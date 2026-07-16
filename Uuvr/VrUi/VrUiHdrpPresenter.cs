using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.Rendering;
using Uuvr.VrUi.PatchModes;

namespace Uuvr.VrUi;

/// <summary>
/// HDRP + XR UI: composite a game-view UI capture onto each eye as a centered panel.
/// UI-only path: clear game view to magenta, chroma-key it out, keep stereo under transparent pixels.
/// If that yields almost no UI pixels, fall back to full capture (UI visible; mono under panel).
/// </summary>
public class VrUiHdrpPresenter : UuvrBehaviour
{
#if CPP
    public VrUiHdrpPresenter(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    /// <summary>Panel width as fraction of eye texture width.</summary>
    public float PanelWidthFraction { get; set; } = 0.48f;

    /// <summary>Max panel height as fraction of eye texture height.</summary>
    public float PanelMaxHeightFraction { get; set; } = 0.48f;

    /// <summary>Flip UI vertically (game-view capture is often upside-down in XR).</summary>
    public bool FlipVertical { get; set; } = true;

    /// <summary>
    /// Max RGB distance (0–255 scale per channel, summed / 3) to chroma key for transparency.
    /// Higher = more aggressive keying (more holes). ~40–70 is typical for magenta clear.
    /// </summary>
    public float ChromaKeyTolerance { get; set; } = 55f;

    /// <summary>If opaque fraction after keying is below this, treat capture as empty UI.</summary>
    public float MinOpaqueFraction { get; set; } = 0.004f;

    /// <summary>
    /// When true, skip chroma key and draw the flipped capture as-is (Mirror mode: menus visible).
    /// </summary>
    public bool DisableKeying { get; set; }

    private RenderTexture? _uiTexture;
    private RenderTexture? _keySourceRt;
    private Texture2D? _keyedCpuTex;
    private Material? _drawMaterial;
    private bool _logged;
    private int _frameLogBudget = 3;
    private int _emptyUiFrames;
    private bool _useUnkeyedFallback;
    private ScreenMirrorPatchMode? _screenMirror;

    private static bool _resolved;
    private static Type? _subsystemManagerType;
    private static Type? _xrDisplayType;
    private static MethodInfo? _getInstances;
    private static MethodInfo? _getRenderPassCount;
    private static MethodInfo? _getRenderTextureForRenderPass;
    private static PropertyInfo? _displayRunning;

    private const int KeyWidth = 640;
    private const int KeyHeight = 360;

    public void SetUiTexture(RenderTexture texture)
    {
        _uiTexture = texture;
    }

    protected override void OnEnable()
    {
        base.OnEnable();
#if MODERN
        RenderPipelineManager.endFrameRendering += OnEndFrameRendering;
#endif
        ResolveXrApi();
        EnsureDrawMaterial();
        _screenMirror = GetComponent<ScreenMirrorPatchMode>();
        _emptyUiFrames = 0;
        _useUnkeyedFallback = false;
        Debug.Log("[UUVR] HDRP UI presenter enabled (XR eye panel, magenta chroma-key composite)");
    }

    protected override void OnDisable()
    {
#if MODERN
        RenderPipelineManager.endFrameRendering -= OnEndFrameRendering;
#endif
        base.OnDisable();
    }

    private void OnDestroy()
    {
        if (_keySourceRt != null)
        {
            _keySourceRt.Release();
            Destroy(_keySourceRt);
            _keySourceRt = null;
        }
        if (_keyedCpuTex != null)
        {
            Destroy(_keyedCpuTex);
            _keyedCpuTex = null;
        }
        if (_drawMaterial != null)
        {
            Destroy(_drawMaterial);
            _drawMaterial = null;
        }
    }

    private void OnEndFrameRendering(ScriptableRenderContext context, Camera[] cameras)
    {
        if (_uiTexture == null || !_uiTexture.IsCreated()) return;

        RebuildKeyedTexture();

        var source = _useUnkeyedFallback ? (Texture)_keySourceRt! : (Texture)_keyedCpuTex!;
        if (source == null) return;

        var blits = TryBlitToXrRenderPasses(source);
        if (blits > 0 && (!_logged || _frameLogBudget > 0))
        {
            Debug.Log(
                $"[UUVR] HDRP UI composite → {blits} XR eye(s) " +
                $"(panel={PanelWidthFraction:P0}, flipY={FlipVertical}, " +
                $"chromaTol={ChromaKeyTolerance:F0}, unkeyedFallback={_useUnkeyedFallback})");
            _logged = true;
            _frameLogBudget--;
        }
    }

    /// <summary>
    /// Downscale capture, chroma-key magenta clear to alpha=0. Dark UI is preserved (unlike black key).
    /// </summary>
    private void RebuildKeyedTexture()
    {
        if (_uiTexture == null) return;

        if (_keySourceRt == null || !_keySourceRt.IsCreated())
        {
            if (_keySourceRt != null) _keySourceRt.Release();
            _keySourceRt = new RenderTexture(KeyWidth, KeyHeight, 0, RenderTextureFormat.ARGB32)
            {
                name = "UuvrUiKeySource",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
            _keySourceRt.Create();
        }

        if (FlipVertical)
        {
            Graphics.Blit(_uiTexture, _keySourceRt, new Vector2(1f, -1f), new Vector2(0f, 1f));
        }
        else
        {
            Graphics.Blit(_uiTexture, _keySourceRt);
        }

        // Full-capture / Mirror: skip expensive CPU keying; draw flipped source directly.
        if (_useUnkeyedFallback || DisableKeying)
        {
            _useUnkeyedFallback = true;
            return;
        }

        if (_keyedCpuTex == null || _keyedCpuTex.width != KeyWidth || _keyedCpuTex.height != KeyHeight)
        {
            if (_keyedCpuTex != null) Destroy(_keyedCpuTex);
            _keyedCpuTex = new Texture2D(KeyWidth, KeyHeight, TextureFormat.RGBA32, false)
            {
                name = "UuvrUiKeyed",
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
            };
        }

        var prev = RenderTexture.active;
        RenderTexture.active = _keySourceRt;
        _keyedCpuTex.ReadPixels(new Rect(0, 0, KeyWidth, KeyHeight), 0, 0, false);
        RenderTexture.active = prev;

        var pixels = _keyedCpuTex.GetPixels32();
        // Magenta key in 0–255
        const int keyR = 255;
        const int keyG = 0;
        const int keyB = 255;
        var tol = ChromaKeyTolerance;
        var opaque = 0;

        for (var i = 0; i < pixels.Length; i++)
        {
            var c = pixels[i];
            // Average absolute channel distance to magenta (handles slight HDR/sRGB drift).
            var dr = Mathf.Abs(c.r - keyR);
            var dg = Mathf.Abs(c.g - keyG);
            var db = Mathf.Abs(c.b - keyB);
            var dist = (dr + dg + db) / 3f;

            // Also treat pure black / very dark clear leftovers as transparent (some paths clear black).
            var lum = (c.r * 30 + c.g * 59 + c.b * 11) / 100;
            var isBlackClear = lum <= 6 && c.r < 12 && c.g < 12 && c.b < 12;

            if (dist <= tol || isBlackClear)
            {
                c.a = 0;
                c.r = c.g = c.b = 0;
            }
            else
            {
                opaque++;
                // Capture is usually opaque RGB; force readable alpha for blend.
                if (c.a < 8) c.a = 255;
            }

            pixels[i] = c;
        }

        _keyedCpuTex.SetPixels32(pixels);
        _keyedCpuTex.Apply(false, false);

        var fraction = opaque / (float)Mathf.Max(1, pixels.Length);
        if (fraction < MinOpaqueFraction)
        {
            _emptyUiFrames++;
            // A few empty frames during boot is OK; then fall back so UI is visible again.
            if (_emptyUiFrames >= 5)
            {
                EnterUnkeyedFallback(
                    $"opaque fraction {fraction:P2} after chroma key (need UI in capture)");
            }
        }
        else
        {
            _emptyUiFrames = 0;
        }
    }

    private void EnterUnkeyedFallback(string reason)
    {
        if (_useUnkeyedFallback) return;
        _useUnkeyedFallback = true;

        if (_screenMirror == null)
            _screenMirror = GetComponent<ScreenMirrorPatchMode>();

        _screenMirror?.ForceFullGameViewCapture(reason);
        Debug.LogWarning(
            $"[UUVR] Chroma-keyed UI panel empty — drawing unkeyed capture (UI restored; stereo under panel may flatten). {reason}");
    }

    private int TryBlitToXrRenderPasses(Texture ui)
    {
        ResolveXrApi();
        if (_getInstances == null || _xrDisplayType == null || _getRenderPassCount == null ||
            _getRenderTextureForRenderPass == null)
        {
            return 0;
        }

        if (ui == null) return 0;

        try
        {
            var listType = typeof(List<>).MakeGenericType(_xrDisplayType);
            var list = Activator.CreateInstance(listType);
            _getInstances.MakeGenericMethod(_xrDisplayType).Invoke(null, new[] { list });

            var countProp = listType.GetProperty("Count");
            var indexer = listType.GetProperty("Item");
            if (countProp == null || indexer == null) return 0;

            var displayCount = (int)countProp.GetValue(list, null);
            if (displayCount == 0) return 0;

            var totalBlits = 0;
            for (var d = 0; d < displayCount; d++)
            {
                var display = indexer.GetValue(list, new object[] { d });
                if (display == null) continue;

                if (_displayRunning != null)
                {
                    var running = _displayRunning.GetValue(display, null);
                    if (running is bool b && !b) continue;
                }

                var passCount = (int)_getRenderPassCount.Invoke(display, null);
                for (var p = 0; p < passCount; p++)
                {
                    var rt = _getRenderTextureForRenderPass.Invoke(display, new object[] { p }) as RenderTexture;
                    if (rt == null) continue;

                    DrawCenteredPanelAlpha(rt, ui);
                    totalBlits++;
                }
            }

            return totalBlits;
        }
        catch (Exception e)
        {
            if (!_logged)
            {
                _logged = true;
                Debug.LogWarning($"[UUVR] XR eye composite failed: {e.InnerException?.Message ?? e.Message}");
            }
            return 0;
        }
    }

    /// <summary>
    /// Alpha-composite UI panel over the stereo eye buffer (does not replace the whole eye).
    /// Unkeyed fallback draws opaque-ish so UI is readable again.
    /// </summary>
    private void DrawCenteredPanelAlpha(RenderTexture eye, Texture ui)
    {
        if (eye == null || ui == null) return;

        var prev = RenderTexture.active;
        try
        {
            RenderTexture.active = eye;
            GL.PushMatrix();
            GL.LoadPixelMatrix(0, eye.width, eye.height, 0);

            var texAspect = (float)ui.width / Mathf.Max(1, ui.height);
            var maxW = eye.width * Mathf.Clamp(PanelWidthFraction, 0.15f, 1f);
            var maxH = eye.height * Mathf.Clamp(PanelMaxHeightFraction, 0.15f, 1f);

            float panelW = maxW;
            float panelH = panelW / texAspect;
            if (panelH > maxH)
            {
                panelH = maxH;
                panelW = panelH * texAspect;
            }

            var x = (eye.width - panelW) * 0.5f;
            var y = (eye.height - panelH) * 0.5f;
            var screenRect = new Rect(x, y, panelW, panelH);
            var uv = new Rect(0f, 0f, 1f, 1f);

            // Full-capture fallback: slight transparency so world isn't a hard flat plate edge.
            var tint = _useUnkeyedFallback
                ? new Color(1f, 1f, 1f, 0.92f)
                : Color.white;

            EnsureDrawMaterial();
            if (_drawMaterial != null)
            {
                Graphics.DrawTexture(screenRect, ui, uv, 0, 0, 0, 0, tint, _drawMaterial);
            }
            else
            {
                Graphics.DrawTexture(screenRect, ui, uv, 0, 0, 0, 0, tint);
            }

            GL.PopMatrix();
        }
        finally
        {
            RenderTexture.active = prev;
        }
    }

    private void EnsureDrawMaterial()
    {
        if (_drawMaterial != null) return;

        var shader =
            Shader.Find("UI/Default") ??
            Shader.Find("Sprites/Default") ??
            Shader.Find("Unlit/Transparent") ??
            Shader.Find("Hidden/Internal-GUITexture") ??
            Shader.Find("Hidden/BlitCopy");

        if (shader == null)
        {
            Debug.LogWarning("[UUVR] No alpha-capable draw shader; panel may fully occlude.");
            return;
        }

        _drawMaterial = new Material(shader)
        {
            name = "UuvrUiPanelDraw",
            hideFlags = HideFlags.HideAndDontSave,
        };
        if (_drawMaterial.HasProperty("_Mode")) _drawMaterial.SetFloat("_Mode", 2f);
        if (_drawMaterial.HasProperty("_SrcBlend"))
            _drawMaterial.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
        if (_drawMaterial.HasProperty("_DstBlend"))
            _drawMaterial.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
        if (_drawMaterial.HasProperty("_ZWrite")) _drawMaterial.SetInt("_ZWrite", 0);

        Debug.Log($"[UUVR] UI panel draw shader: {shader.name}");
    }

    private static void ResolveXrApi()
    {
        if (_resolved) return;
        _resolved = true;

        _subsystemManagerType =
            Type.GetType("UnityEngine.SubsystemManager, UnityEngine.SubsystemsModule") ??
            Type.GetType("UnityEngine.SubsystemManager, UnityEngine");

        _xrDisplayType =
            Type.GetType("UnityEngine.XR.XRDisplaySubsystem, UnityEngine.XRModule") ??
            Type.GetType("UnityEngine.XR.XRDisplaySubsystem, UnityEngine");

        if (_subsystemManagerType != null)
        {
            foreach (var m in _subsystemManagerType.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (m.Name != "GetInstances" || !m.IsGenericMethodDefinition) continue;
                if (m.GetParameters().Length == 1)
                {
                    _getInstances = m;
                    break;
                }
            }
        }

        if (_xrDisplayType != null)
        {
            _getRenderPassCount = _xrDisplayType.GetMethod("GetRenderPassCount", Type.EmptyTypes);
            _getRenderTextureForRenderPass = _xrDisplayType.GetMethod(
                "GetRenderTextureForRenderPass", new[] { typeof(int) });
            _displayRunning = _xrDisplayType.GetProperty("running");
        }

        Debug.Log(
            $"[UUVR] XR API resolve: displayType={_xrDisplayType != null}, " +
            $"getInstances={_getInstances != null}, getPassCount={_getRenderPassCount != null}, " +
            $"getPassRT={_getRenderTextureForRenderPass != null}");
    }
}
