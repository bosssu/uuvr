using System;
using System.Collections;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;

#if MODERN
using Uuvr.VrCamera;
#endif

#if CPP
using BepInEx.Unity.IL2CPP.Utils;
#endif

namespace Uuvr.VrUi.PatchModes;

/// <summary>
/// Captures the flat game view into the VR UI RenderTexture.
/// On HDRP this is the primary UI path: Screen Space Overlay UI still composites to the game view,
/// while default uGUI shaders will NOT draw into a secondary HD camera (CanvasRedirect fails).
/// </summary>
public class ScreenMirrorPatchMode : UuvrBehaviour, VrUiPatchMode
{
#if CPP
    public ScreenMirrorPatchMode(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    private CommandBuffer? _commandBuffer;
    private Camera? _clearCamera;
    private RenderTexture _targetTexture;
    private Coroutine? _endOfFrameCoroutine;
    private MethodInfo? _captureScreenshotIntoRt;
    private bool _loggedCapturePath;
    private int _captureFrames;
    private bool _loggedUiEmptyFallback;

    /// <summary>
    /// Chroma-key clear color for UI-only capture. Magenta is rare in game UI; black key
    /// incorrectly removes dark text/panels.
    /// </summary>
    public static readonly Color UiChromaKeyColor = new Color(1f, 0f, 1f, 1f);

    /// <summary>
    /// When true: skip the pre-clear camera (full mono game view, including scene).
    /// When false: clear to chroma key so capture is ideally Overlay UI only.
    /// </summary>
    public bool SkipClearCamera { get; set; }

    /// <summary>
    /// True after automatic fallback to full game-view capture (UI was empty with clear).
    /// </summary>
    public bool UsingFullCaptureFallback { get; private set; }

    protected override void OnEnable()
    {
        base.OnEnable();

        _loggedUiEmptyFallback = false;
        UsingFullCaptureFallback = false;

        // When clearing for UI-only, stop mirroring XR eyes into the game view so the clear
        // + Overlay UI dominate. Full-capture fallback re-enables mirror.
        ApplyClearAndMirrorPolicy();

        _endOfFrameCoroutine = this.StartCoroutine(EndOfFrameCoroutine());
        Debug.Log(
            $"[UUVR] ScreenMirror/game-view capture enabled " +
            $"(skipClear={SkipClearCamera}, uiOnlyCapture={!SkipClearCamera}, HDRP={UiCameraSetup.IsHdrp()})");
    }

    /// <summary>
    /// Force full mono game-view capture (scene + UI). Used when chroma clear yields no UI.
    /// Restores visible UI at the cost of mono content under the panel.
    /// </summary>
    public void ForceFullGameViewCapture(string reason)
    {
        if (UsingFullCaptureFallback && SkipClearCamera) return;

        UsingFullCaptureFallback = true;
        SkipClearCamera = true;
        ApplyClearAndMirrorPolicy();
        if (!_loggedUiEmptyFallback)
        {
            _loggedUiEmptyFallback = true;
            Debug.LogWarning(
                $"[UUVR] UI-only capture empty — falling back to full game-view mirror. {reason}");
        }
    }

    private void ApplyClearAndMirrorPolicy()
    {
        var uiOnly = !SkipClearCamera;
        // Mirror left eye when we need full game view (includes UI baked into scene buffer).
        // When UI-only, disable device view so clear color is not overwritten by stereo eyes.
        SetXrMirror(!uiOnly);

        if (uiOnly)
        {
            EnsureClearCamera();
            if (_clearCamera != null)
            {
                _clearCamera.backgroundColor = UiChromaKeyColor;
                UiCameraSetup.ConfigureCaptureCamera(_clearCamera, UiChromaKeyColor);
                _clearCamera.depth = -100;
                _clearCamera.cullingMask = 0;
                _clearCamera.targetTexture = null;
                _clearCamera.stereoTargetEye = StereoTargetEyeMask.None;
                _clearCamera.enabled = true;
            }
        }
        else if (_clearCamera != null)
        {
            _clearCamera.enabled = false;
        }
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        SetXrMirror(true);
        if (_endOfFrameCoroutine != null)
        {
            StopCoroutine(_endOfFrameCoroutine);
            _endOfFrameCoroutine = null;
        }
        if (_clearCamera != null)
        {
            _clearCamera.enabled = false;
        }
        Reset();
        Debug.Log("[UUVR] ScreenMirror/game-view capture disabled");
    }

    private void Awake()
    {
        var screenCaptureType =
            Type.GetType("UnityEngine.ScreenCapture, UnityEngine.ScreenCaptureModule") ??
            Type.GetType("UnityEngine.ScreenCapture, UnityEngine");
        if (screenCaptureType != null)
        {
            _captureScreenshotIntoRt = screenCaptureType.GetMethod(
                "CaptureScreenshotIntoRenderTexture",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(RenderTexture) },
                null);
        }

        if (_captureScreenshotIntoRt == null)
        {
            Debug.LogWarning("[UUVR] CaptureScreenshotIntoRenderTexture not found; will use CommandBuffer blit only.");
        }
    }

    private void EnsureClearCamera()
    {
        if (_clearCamera != null) return;

        var go = new GameObject("UuvrMirrorClearCamera");
        go.transform.SetParent(transform, false);
        _clearCamera = go.AddComponent<Camera>();
        VrCamera.VrCamera.IgnoredCameras.Add(_clearCamera);

        _clearCamera.stereoTargetEye = StereoTargetEyeMask.None;
        _clearCamera.depth = -100;
        _clearCamera.cullingMask = 0;
        _clearCamera.clearFlags = CameraClearFlags.SolidColor;
        _clearCamera.backgroundColor = UiChromaKeyColor;
        _clearCamera.allowHDR = false;
        _clearCamera.allowMSAA = false;
        _clearCamera.targetTexture = null;
        _clearCamera.enabled = false;

#if MODERN
        var additionalData = AdditionalCameraData.Create(_clearCamera);
        additionalData?.SetAllowXrRendering(false);
#endif
        // HDRP: Color clear to magenta chroma key (not black — preserves dark UI).
        UiCameraSetup.ConfigureCaptureCamera(_clearCamera, UiChromaKeyColor);
        _clearCamera.depth = -100;
        _clearCamera.cullingMask = 0;
        _clearCamera.targetTexture = null;
        _clearCamera.stereoTargetEye = StereoTargetEyeMask.None;
        _clearCamera.enabled = false;
    }

    public void SetUpTargetTexture(RenderTexture targetTexture)
    {
        _targetTexture = targetTexture;

        if (!enabled) return;
        Reset();
        _commandBuffer = CreateCommandBuffer();
        _commandBuffer.name = "UUVR UI";
        _commandBuffer.Blit(BuiltinRenderTextureType.CameraTarget, targetTexture);
    }
    
    private static CommandBuffer CreateCommandBuffer()
    {
        var commandBufferType = typeof(CommandBuffer);
        var constructor = commandBufferType.GetConstructor(Type.EmptyTypes);
        return (CommandBuffer)constructor.Invoke(null);
    }

    private void Reset()
    {
        if (_commandBuffer == null) return;
        _commandBuffer.Dispose();
        _commandBuffer = null;
    }

    private void SetXrMirror(bool mirror)
    {
        try
        {
            var xrSettingsType =
                Type.GetType("UnityEngine.XR.XRSettings, UnityEngine.XRModule") ??
                Type.GetType("UnityEngine.XR.XRSettings, UnityEngine.VRModule") ??
                Type.GetType("UnityEngine.VR.VRSettings, UnityEngine");

            if (xrSettingsType == null) return;

            var gameViewProp = xrSettingsType.GetProperty("gameViewRenderMode");
            if (gameViewProp != null && gameViewProp.CanWrite)
            {
                // None=0 when capturing UI-only; LeftEye=1 when restoring mirror.
                gameViewProp.SetValue(null, mirror ? 1 : 0, null);
            }

            var showDeviceView = xrSettingsType.GetProperty("showDeviceView");
            showDeviceView?.SetValue(null, mirror, null);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UUVR] SetXrMirror({mirror}) failed: {e.Message}");
        }
    }

    private IEnumerator EndOfFrameCoroutine()
    {
        var wait = new WaitForEndOfFrame();
        while (true)
        {
            yield return wait;

            if (!enabled || _targetTexture == null) continue;

            if (Screen.width > 0 && Screen.height > 0 &&
                (Screen.width != _targetTexture.width || Screen.height != _targetTexture.height))
            {
                // Parent resizes RT; just rebuild command buffer binding.
                SetUpTargetTexture(_targetTexture);
            }

            var captured = false;

            // Prefer ScreenCapture — works better with Overlay UI under SRP/HDRP.
            if (_captureScreenshotIntoRt != null)
            {
                try
                {
                    _captureScreenshotIntoRt.Invoke(null, new object[] { _targetTexture });
                    captured = true;
                    _captureFrames++;
                    if (!_loggedCapturePath)
                    {
                        _loggedCapturePath = true;
                        Debug.Log("[UUVR] UI capture: ScreenCapture.CaptureScreenshotIntoRenderTexture");
                    }
                    else if (_captureFrames == 60)
                    {
                        Debug.Log("[UUVR] UI capture still running (60 frames) — if HMD has no UI, check desktop window content / XR game view.");
                    }
                }
                catch (Exception e)
                {
                    if (!_loggedCapturePath)
                    {
                        Debug.LogWarning($"[UUVR] CaptureScreenshotIntoRenderTexture failed: {e.InnerException?.Message ?? e.Message}");
                        _loggedCapturePath = true;
                    }
                }
            }

            if (!captured && _commandBuffer != null)
            {
                try
                {
                    Graphics.ExecuteCommandBuffer(_commandBuffer);
                    if (!_loggedCapturePath)
                    {
                        _loggedCapturePath = true;
                        Debug.Log("[UUVR] UI capture: CommandBuffer Blit(CameraTarget)");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[UUVR] CommandBuffer blit failed: {e.Message}");
                }
            }
        }
    }
}
