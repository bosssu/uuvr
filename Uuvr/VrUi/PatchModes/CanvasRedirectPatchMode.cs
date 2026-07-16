using System.Collections.Generic;
using System.Linq;
using UnityEngine;
using UnityEngine.UI;

namespace Uuvr.VrUi.PatchModes;

public class CanvasRedirectPatchMode : UuvrBehaviour, VrUiPatchMode
{
#if CPP
    public CanvasRedirectPatchMode(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    private readonly List<string> _ignoredCanvases = new()
    {
        "unityexplorer",
        "universelib",
    };

    private Camera? _uiCaptureCamera;
    private Transform? _worldSpaceParent;
    private int _loggedCanvasCount = -1;
    private int _patchedCount;

    /// <summary>
    /// Parent for HDRP world-space canvases (VrUiContainer under HMD). Optional on non-HDRP.
    /// </summary>
    public void SetWorldSpaceParent(Transform? parent)
    {
        _worldSpaceParent = parent;
        foreach (var redirect in FindObjectsOfType<CanvasRedirect>())
        {
            if (redirect != null) redirect.SetWorldSpaceParent(parent);
        }
    }

    protected override void OnSettingChanged()
    {
        base.OnSettingChanged();
        if (_uiCaptureCamera != null && !UiCameraSetup.IsHdrp())
        {
            _uiCaptureCamera.cullingMask = 1 << LayerHelper.GetVrUiLayer();
        }
    }

    private void Start()
    {
        OnSettingChanged();
    }

    protected override void OnEnable()
    {
        base.OnEnable();
        if (UiCameraSetup.IsHdrp())
        {
            // No secondary HD capture camera — world-space canvases render with the game VR camera.
            if (_uiCaptureCamera != null) _uiCaptureCamera.enabled = false;
            Debug.Log("[UUVR] CanvasRedirect enabled (HDRP World Space path — no capture camera)");
            return;
        }

        if (_uiCaptureCamera != null)
        {
            _uiCaptureCamera.enabled = true;
        }
        Debug.Log("[UUVR] CanvasRedirect enabled (ScreenSpaceCamera capture path)");
    }

    protected override void OnDisable()
    {
        base.OnDisable();
        if (_uiCaptureCamera != null)
        {
            _uiCaptureCamera.enabled = false;
        }
    }

    private void Awake()
    {
        if (UiCameraSetup.IsHdrp())
        {
            Debug.Log("[UUVR] CanvasRedirect: HDRP mode — will convert Overlay/SSC canvases to World Space.");
            return;
        }

        var go = new GameObject("VrUiCaptureCamera");
        go.transform.SetParent(transform, false);
        _uiCaptureCamera = go.AddComponent<Camera>();
        VrCamera.VrCamera.IgnoredCameras.Add(_uiCaptureCamera);
        UiCameraSetup.ConfigureCaptureCamera(_uiCaptureCamera);
        _uiCaptureCamera.cullingMask = 1 << LayerHelper.GetVrUiLayer();
        _uiCaptureCamera.enabled = false;
        Debug.Log($"[UUVR] CanvasRedirect capture camera ready (disabled), layer mask={_uiCaptureCamera.cullingMask}");
    }

    public void SetUpTargetTexture(RenderTexture targetTexture)
    {
        if (_uiCaptureCamera == null) return;
        _uiCaptureCamera.targetTexture = targetTexture;
        UiCameraSetup.ConfigureCaptureCamera(_uiCaptureCamera);
        _uiCaptureCamera.cullingMask = 1 << LayerHelper.GetVrUiLayer();
        _uiCaptureCamera.enabled = enabled && !UiCameraSetup.IsHdrp();
    }

    private void Update()
    {
        if (!enabled) return;
        if (!UiCameraSetup.IsHdrp() && _uiCaptureCamera == null) return;

        try
        {
            var registry = GraphicRegistry.instance;
            if (registry == null) return;

            var keys =
#if CPP
                registry.m_Graphics.keys;
#else
                registry.m_Graphics.Keys;
#endif

            var count = 0;
            _patchedCount = 0;
            foreach (var canvas in keys)
            {
                count++;
                PatchCanvas(canvas);
            }

            // Also scan all root canvases (some HDRP UIs skip GraphicRegistry briefly).
            if (UiCameraSetup.IsHdrp())
            {
                foreach (var canvas in FindObjectsOfType<Canvas>())
                {
                    if (canvas == null || !canvas.isRootCanvas) continue;
                    count++;
                    PatchCanvas(canvas);
                }
            }

            if (count != _loggedCanvasCount)
            {
                _loggedCanvasCount = count;
                Debug.Log(
                    $"[UUVR] CanvasRedirect: tracking ~{count} canvases, " +
                    $"HDRP={UiCameraSetup.IsHdrp()}, parent={(_worldSpaceParent != null ? _worldSpaceParent.name : "null")}");
            }
        }
        catch (System.Exception e)
        {
            Debug.LogWarning($"[UUVR] CanvasRedirect scan failed: {e.Message}");
        }

        if (_uiCaptureCamera != null && !UiCameraSetup.IsHdrp())
        {
            _uiCaptureCamera.transform.localPosition = Vector3.right * 10;
        }
    }

    private void PatchCanvas(Canvas canvas)
    {
        if (canvas == null) return;
        if (canvas.renderMode == RenderMode.WorldSpace && canvas.GetComponent<CanvasRedirect>() == null)
        {
            // Already world-space from game — leave alone.
            return;
        }

        if (!canvas.isRootCanvas)
        {
            PatchCanvas(canvas.rootCanvas);
            return;
        }

        if (_ignoredCanvases.Any(ignoredCanvas => canvas.name.ToLower().Contains(ignoredCanvas.ToLower())))
        {
            return;
        }

        if (canvas.GetComponent<CanvasRedirect>() != null)
        {
            _patchedCount++;
            return;
        }

        CanvasRedirect.Create(canvas, _uiCaptureCamera, _worldSpaceParent);
        _patchedCount++;
    }
}
