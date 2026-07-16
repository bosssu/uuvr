using System;
using System.Collections.Generic;
using System.Reflection;
using UnityEngine;
using UnityEngine.UI;

namespace Uuvr.VrUi.PatchModes;

/// <summary>
/// Built-in / URP: ScreenSpaceCamera → dedicated UI capture camera → RT on VR plane.
/// HDRP: World Space canvases under the VR UI container (real stereo geometry; no mono blit).
///
/// World-space conversion uses a scale wrapper parent so the Canvas RectTransform keeps
/// localScale = 1 and a stable pixel size (ScrollRect / Mask / LayoutGroup stay intact).
/// </summary>
public class CanvasRedirect : UuvrBehaviour
{
#if CPP
    public CanvasRedirect(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    private Canvas _canvas;
    private Camera? _uiCaptureCamera;
    private Transform? _worldSpaceParent;
    private Transform? _scaleWrapper;
    private bool _isPatched;
    private bool _worldSpaceMode;
    private bool _worldSpaceLayoutInitialized;
    private int _zSanitizeFrame;
    private int _zSanitizeLogged;
    private float _nextForcedAlignUnscaled;
    private float _lastAlignLogUnscaled;
    private float _layoutPixelWidth = 1920f;
    private float _layoutPixelHeight = 1080f;
    private RenderMode _originalRenderMode;
    private Camera? _originalWorldCamera;
    private float _originalPlaneDistance;
    private Vector3 _originalLocalScale;
    private Vector3 _originalLocalPosition;
    private Quaternion _originalLocalRotation;
    private Transform? _originalParent;
    private int _originalSiblingIndex;
    private Vector2 _originalAnchorMin;
    private Vector2 _originalAnchorMax;
    private Vector2 _originalPivot;
    private Vector2 _originalSizeDelta;
    private bool _hadScaler;
    private bool _originalScalerEnabled;
    private CanvasScaler.ScaleMode _originalScalerMode;
    private float _originalScalerFactor = 1f;
    private readonly List<KeyValuePair<Transform, int>> _originalLayers = new();

    /// <summary>Target world width of the UI plane in meters (HDRP world-space path).</summary>
    public static float WorldSpaceWidthMeters { get; set; } = 1.05f;

    /// <summary>Distance in front of parent (meters) for world-space UI.</summary>
    public static float WorldSpaceDistance { get; set; } = 1.2f;

    public static void Create(Canvas canvas, Camera? uiCaptureCamera, Transform? worldSpaceParent)
    {
        var instance = canvas.gameObject.AddComponent<CanvasRedirect>();
        instance._canvas = canvas;
        instance._uiCaptureCamera = uiCaptureCamera;
        instance._worldSpaceParent = worldSpaceParent;
    }

    public static void Create(Canvas canvas, Camera uiCaptureCamera)
    {
        Create(canvas, uiCaptureCamera, worldSpaceParent: null);
    }

    public void SetWorldSpaceParent(Transform? parent)
    {
        _worldSpaceParent = parent;
        if (_isPatched && _worldSpaceMode)
        {
            ApplyWorldSpacePose(updateLayoutOnce: false);
        }
    }

    private void Start()
    {
        OnSettingChanged();
    }

    private void LateUpdate()
    {
        if (!_isPatched || !_worldSpaceMode) return;
        // Pose only on the scale wrapper — never rewrite child RectTransforms.
        ApplyWorldSpacePose(updateLayoutOnce: false);

        // Dynamic list items often keep wrong local Z/rotation after Instantiate/SetParent
        // under a scaled World Space root (z≈20000, tilted). Flat uGUI expects z=0, rot=I.
        if ((_zSanitizeFrame++ % 15) == 0)
        {
            SanitizeChildLocalPose();
            ApplyMotionVectorKill();
        }
    }

    protected override void OnBeforeRender()
    {
        if (!_isPatched || !_worldSpaceMode) return;
        ApplyWorldSpacePose(updateLayoutOnce: false);
    }

#if MODERN
    protected override void OnBeginFrameRendering()
    {
        // Head-locked UI must match the camera after RelativeTransform pose swap;
        // one-frame lag + HDRP TAA = smear while turning the HMD.
        if (!_isPatched || !_worldSpaceMode) return;
        ApplyWorldSpacePose(updateLayoutOnce: false);
    }
#endif

    protected override void OnSettingChanged()
    {
        var shouldPatch = ShouldPatchCanvas();

        if (shouldPatch && !_isPatched)
        {
            Patch();
        }
        else if (!shouldPatch && _isPatched)
        {
            UndoPatch();
        }
        else if (shouldPatch && _isPatched)
        {
            if (_worldSpaceMode)
            {
                ApplyWorldSpacePose(updateLayoutOnce: false);
            }
            else
            {
                ApplyVrUiLayers();
            }
        }
    }

    private bool ShouldPatchCanvas()
    {
        if (ModConfiguration.Instance.PreferredUiPatchMode.Value != ModConfiguration.UiPatchMode.CanvasRedirect)
        {
            return false;
        }

        if (_canvas == null) return false;

        if (!UiCameraSetup.IsHdrp() && _uiCaptureCamera == null) return false;

        var modeToCheck = _isPatched ? _originalRenderMode : _canvas.renderMode;
        var isScreenSpaceCamera = modeToCheck == RenderMode.ScreenSpaceCamera;

        return ModConfiguration.Instance.ScreenSpaceCanvasTypesToPatch.Value switch
        {
            ModConfiguration.ScreenSpaceCanvasType.None => !isScreenSpaceCamera,
            ModConfiguration.ScreenSpaceCanvasType.NotToTexture =>
                !isScreenSpaceCamera ||
                (isScreenSpaceCamera && (_isPatched
                    ? _originalWorldCamera?.targetTexture == null
                    : _canvas.worldCamera?.targetTexture == null)),
            ModConfiguration.ScreenSpaceCanvasType.All => true,
            _ => throw new ArgumentOutOfRangeException()
        };
    }

    private void Patch()
    {
        _originalRenderMode = _canvas.renderMode;
        _originalWorldCamera = _canvas.worldCamera;
        _originalPlaneDistance = _canvas.planeDistance;
        _originalLocalScale = _canvas.transform.localScale;
        _originalLocalPosition = _canvas.transform.localPosition;
        _originalLocalRotation = _canvas.transform.localRotation;
        _originalParent = _canvas.transform.parent;
        _originalSiblingIndex = _canvas.transform.GetSiblingIndex();

        var rect = _canvas.GetComponent<RectTransform>();
        if (rect != null)
        {
            _originalAnchorMin = rect.anchorMin;
            _originalAnchorMax = rect.anchorMax;
            _originalPivot = rect.pivot;
            _originalSizeDelta = rect.sizeDelta;
            // Capture pixel size WHILE still Overlay/SSC (stretch → non-zero rect).
            CaptureLayoutPixelSize(rect);
        }

        var scaler = _canvas.GetComponent<CanvasScaler>();
        _hadScaler = scaler != null;
        if (scaler != null)
        {
            _originalScalerEnabled = scaler.enabled;
            _originalScalerMode = scaler.uiScaleMode;
            _originalScalerFactor = scaler.scaleFactor;
        }

        _worldSpaceMode = UiCameraSetup.IsHdrp();
        _worldSpaceLayoutInitialized = false;

        if (_worldSpaceMode)
        {
            PatchWorldSpace();
        }
        else
        {
            PatchScreenSpaceCamera();
        }

        _isPatched = true;
        Debug.Log(
            $"[UUVR] CanvasRedirect patched '{_canvas.name}' " +
            $"(was {_originalRenderMode} → {(_worldSpaceMode ? "WorldSpace/HDRP" : "ScreenSpaceCamera")}), " +
            $"px={_layoutPixelWidth:F0}x{_layoutPixelHeight:F0}");
    }

    private void PatchScreenSpaceCamera()
    {
        ApplyVrUiLayers();

        _canvas.renderMode = RenderMode.ScreenSpaceCamera;
        _canvas.worldCamera = _uiCaptureCamera;

        if (_originalRenderMode == RenderMode.ScreenSpaceCamera)
        {
            if (_uiCaptureCamera != null && _originalPlaneDistance < _uiCaptureCamera.nearClipPlane)
            {
                _uiCaptureCamera.nearClipPlane = Mathf.Max(0.01f, _originalPlaneDistance - 0.1f);
            }
            if (_uiCaptureCamera != null && _originalPlaneDistance > _uiCaptureCamera.farClipPlane)
            {
                _uiCaptureCamera.farClipPlane = _originalPlaneDistance + 0.1f;
            }
        }
        else
        {
            _canvas.planeDistance = 1f;
        }
    }

    private void PatchWorldSpace()
    {
        var uiLayer = LayerHelper.GetVrUiLayer();
        ApplyLayersRecursive(transform, uiLayer);

        // Capture size again right before mode change (in case Start ran late).
        var rect = _canvas.GetComponent<RectTransform>();
        if (rect != null)
        {
            CaptureLayoutPixelSize(rect);
        }

        _canvas.renderMode = RenderMode.WorldSpace;

        // Disable CanvasScaler: it multiplies root scale and fights our meter-scale wrapper.
        // Layout stays in fixed pixel space via explicit sizeDelta on the canvas.
        var scaler = _canvas.GetComponent<CanvasScaler>();
        if (scaler != null)
        {
            scaler.enabled = false;
        }

        _canvas.overrideSorting = true;
        if (_canvas.sortingOrder < 1000) _canvas.sortingOrder = 5000;

        EnsureScaleWrapper();
        ApplyWorldSpacePose(updateLayoutOnce: true);

        // Soft canvas refresh only — NEVER ForceRebuild every RectTransform
        // (that collapses ScrollRect list items to the top).
        try
        {
            Canvas.ForceUpdateCanvases();
        }
        catch
        {
            // optional
        }

        SanitizeChildLocalPose();
        ApplyMotionVectorKill();

        Debug.Log(
            $"[UUVR] WorldSpace UI '{_canvas.name}' via scale-wrapper " +
            $"(canvas localScale=1, px={_layoutPixelWidth:F0}x{_layoutPixelHeight:F0}, " +
            $"worldW={WorldSpaceWidthMeters:F2}m)");

        VrUi.VrUiWorldInput.Ensure(_canvas);
    }

    /// <summary>
    /// Head-locked World Space UI still moves in world space when the HMD turns.
    /// HDRP TAA smears it unless CanvasRenderers report ForceNoMotion.
    /// </summary>
    private void ApplyMotionVectorKill()
    {
        if (_canvas == null) return;
        try
        {
            var prop = typeof(CanvasRenderer).GetProperty(
                "motionVectorGenerationMode",
                BindingFlags.Instance | BindingFlags.Public);
            if (prop == null || !prop.CanWrite) return;

            object forceNoMotion;
            try
            {
                forceNoMotion = Enum.Parse(prop.PropertyType, "ForceNoMotion");
            }
            catch
            {
                forceNoMotion = Enum.ToObject(prop.PropertyType, 2);
            }

            var renderers = _canvas.GetComponentsInChildren<CanvasRenderer>(true);
            foreach (var cr in renderers)
            {
                if (cr == null) continue;
                prop.SetValue(cr, forceNoMotion, null);
            }
        }
        catch
        {
            // optional — property missing on older Unity
        }
    }

    /// <summary>
    /// Fix dynamic ScrollRect items only. Overlay→WorldSpace Instantiate often leaves huge
    /// local Z and baked world rotation. Do NOT walk the whole canvas (breaks HUD / scene UI).
    /// </summary>
    private void SanitizeChildLocalPose()
    {
        if (_canvas == null) return;

        var fixedCount = 0;
        var scrolls = _canvas.GetComponentsInChildren<ScrollRect>(true);
        if (scrolls != null)
        {
            foreach (var sr in scrolls)
            {
                if (sr == null || sr.content == null) continue;
                fixedCount += SanitizeRectTree(sr.content, fixRotation: true);
            }
        }

        // Extreme Z anywhere under canvas (missing items) without touching intentional rotations.
        fixedCount += SanitizeRectTree(_canvas.transform, fixRotation: false, zOnlyExtreme: true);

        if (fixedCount > 0 && _zSanitizeLogged < 5)
        {
            _zSanitizeLogged++;
            Debug.Log(
                $"[UUVR] Sanitized local Z/rotation on {fixedCount} UI rect(s) under '{_canvas.name}' " +
                $"(ScrollRect content / extreme Z only)");
        }
    }

    private static int SanitizeRectTree(Transform root, bool fixRotation, bool zOnlyExtreme = false)
    {
        if (root == null) return 0;

        const float zListThreshold = 0.5f;
        const float zExtremeThreshold = 10f; // clearly wrong for flat uGUI
        const float rotDotThreshold = 0.99996f; // ~0.5°

        var fixedCount = 0;
        var rects = root.GetComponentsInChildren<RectTransform>(true);
        foreach (var rt in rects)
        {
            if (rt == null) continue;

            var changed = false;
            var zLimit = zOnlyExtreme ? zExtremeThreshold : zListThreshold;

            var lp = rt.localPosition;
            if (Mathf.Abs(lp.z) > zLimit)
            {
                lp.z = 0f;
                rt.localPosition = lp;
                changed = true;
            }

            var ap = rt.anchoredPosition3D;
            if (Mathf.Abs(ap.z) > zLimit)
            {
                ap.z = 0f;
                rt.anchoredPosition3D = ap;
                changed = true;
            }

            if (fixRotation &&
                Mathf.Abs(Quaternion.Dot(rt.localRotation, Quaternion.identity)) < rotDotThreshold)
            {
                rt.localRotation = Quaternion.identity;
                changed = true;
            }

            if (changed) fixedCount++;
        }

        return fixedCount;
    }

    private void CaptureLayoutPixelSize(RectTransform rect)
    {
        var w = Mathf.Abs(rect.rect.width);
        var h = Mathf.Abs(rect.rect.height);

        // Stretch Overlay often reports correct rect; sizeDelta may be 0.
        if (w < 2f)
        {
            w = Mathf.Abs(_originalSizeDelta.x);
        }
        if (h < 2f)
        {
            h = Mathf.Abs(_originalSizeDelta.y);
        }

        var sw = Screen.width > 0 ? Screen.width : 1920f;
        var sh = Screen.height > 0 ? Screen.height : 1080f;
        if (w < 2f) w = sw;
        if (h < 2f) h = sh;

        // Prefer full-screen pixel size when the canvas was stretch-overlay style
        // (anchors nearly full stretch) so secondary panels keep designed proportions.
        var stretchX = Mathf.Abs(_originalAnchorMax.x - _originalAnchorMin.x) > 0.9f;
        var stretchY = Mathf.Abs(_originalAnchorMax.y - _originalAnchorMin.y) > 0.9f;
        if (stretchX && stretchY)
        {
            w = sw;
            h = sh;
        }

        _layoutPixelWidth = w;
        _layoutPixelHeight = h;
    }

    private void EnsureScaleWrapper()
    {
        if (_scaleWrapper != null) return;
        if (_canvas == null) return;

        // Stay in the canvas's scene hierarchy — NEVER DontDestroyOnLoad.
        // Pulling UI under DDOL survives scene loads, breaks Scene.Start / Global.CheckOnGUI,
        // and leaves parent=null ghosts after the VR camera is destroyed.
        var go = new GameObject($"UuvrWs_{_canvas.name}");
        var sceneParent = _originalParent;
        if (sceneParent != null)
        {
            go.transform.SetParent(sceneParent, false);
        }
        // else: scene root (still unloaded with the scene)

        go.layer = _canvas.gameObject.layer;
        _scaleWrapper = go.transform;
        _scaleWrapper.localPosition = Vector3.zero;
        _scaleWrapper.localRotation = Quaternion.identity;
        _scaleWrapper.localScale = Vector3.one;
    }

    /// <param name="updateLayoutOnce">
    /// When true: set canvas anchors/pivot/size once. Never rewrite child layout.
    /// World meters come from the scale wrapper; canvas localScale stays +1.
    /// Pose follows VrUiContainer in world space without parenting under it (scene-safe).
    /// </param>
    private void ApplyWorldSpacePose(bool updateLayoutOnce)
    {
        if (_canvas == null) return;

        EnsureScaleWrapper();
        if (_scaleWrapper == null) return;

        var canvasT = _canvas.transform;
        if (canvasT.parent != _scaleWrapper)
        {
            canvasT.SetParent(_scaleWrapper, false);
        }

        // Canvas stays identity under the wrapper (layout/ScrollRect stay in pixel space).
        canvasT.localPosition = Vector3.zero;
        canvasT.localRotation = Quaternion.identity;
        canvasT.localScale = Vector3.one;

        var rect = _canvas.GetComponent<RectTransform>();
        if (rect != null && (updateLayoutOnce || !_worldSpaceLayoutInitialized))
        {
            // Center root with explicit pixel size — stretch under a plain Transform parent
            // collapses to 0 and piles ScrollRect items at the top.
            rect.pivot = new Vector2(0.5f, 0.5f);
            rect.anchorMin = new Vector2(0.5f, 0.5f);
            rect.anchorMax = new Vector2(0.5f, 0.5f);
            rect.anchoredPosition3D = Vector3.zero;
            rect.sizeDelta = new Vector2(_layoutPixelWidth, _layoutPixelHeight);
            _worldSpaceLayoutInitialized = true;
        }
        else if (rect != null)
        {
            // Keep size stable if something else rewrote it.
            if (Mathf.Abs(rect.rect.width) < 2f || Mathf.Abs(rect.rect.height) < 2f)
            {
                rect.sizeDelta = new Vector2(_layoutPixelWidth, _layoutPixelHeight);
            }
        }

        // Allow UI Scale config (0.4–3× on ~1.05m base) without clamping back to 1.35m.
        var targetW = Mathf.Clamp(WorldSpaceWidthMeters, 0.25f, 4f);
        var scale = Mathf.Clamp(targetW / Mathf.Max(2f, _layoutPixelWidth), 1e-5f, 0.08f);

        // Head-lock pose every frame. Auto-align re-resolves the *active* VR camera so a
        // disabled/stale HighestDepth after teleport cannot freeze the panel in place.
        var autoAlign = true;
        var maxDrift = 0.45f;
        var interval = 0.25f;
        try
        {
            var cfg = ModConfiguration.Instance;
            autoAlign = cfg.UiAutoAlign.Value;
            maxDrift = Mathf.Clamp(cfg.UiAutoAlignMaxDistance.Value, 0.1f, 5f);
            interval = Mathf.Clamp(cfg.UiAutoAlignInterval.Value, 0f, 5f);
        }
        catch
        {
            // config not ready
        }

        Transform? poseSource = null;
        if (autoAlign)
        {
            // Periodic full re-resolve (also clears a sticky wrong HighestDepth).
            if (interval <= 0f || Time.unscaledTime >= _nextForcedAlignUnscaled)
            {
                VrCamera.VrCamera.ResolveBestForUi();
                _nextForcedAlignUnscaled = Time.unscaledTime + Mathf.Max(0.05f, interval);
            }

            poseSource = VrCamera.VrCamera.ResolvePoseTransform();
        }
        else
        {
            var vrCam = VrCamera.VrCamera.HighestDepthVrCamera?.CameraInUse;
            if (vrCam != null) poseSource = vrCam.transform;
        }

        if (poseSource == null && _worldSpaceParent != null)
        {
            poseSource = _worldSpaceParent;
        }

        if (poseSource != null)
        {
            var idealPos = poseSource.TransformPoint(Vector3.forward * WorldSpaceDistance);
            var idealRot = poseSource.rotation;

            // Teleport recovery: if something left the plane behind, snap + log.
            if (autoAlign)
            {
                var drift = Vector3.Distance(_scaleWrapper.position, idealPos);
                if (drift > maxDrift && Time.unscaledTime >= _lastAlignLogUnscaled + 2f)
                {
                    _lastAlignLogUnscaled = Time.unscaledTime;
                    Debug.Log(
                        $"[UUVR] UI auto-align recovery on '{_canvas.name}' " +
                        $"(drift={drift:F2}m → 0, src={poseSource.name})");
                }
            }

            // Always track while we have a pose source (interval only re-picks camera).
            _scaleWrapper.SetPositionAndRotation(idealPos, idealRot);

            // Convert desired world scale into local scale if parent is scaled.
            var parentLossy = _scaleWrapper.parent != null
                ? _scaleWrapper.parent.lossyScale
                : Vector3.one;
            var lx = scale / Mathf.Max(1e-5f, Mathf.Abs(parentLossy.x));
            var ly = scale / Mathf.Max(1e-5f, Mathf.Abs(parentLossy.y));
            var lz = scale / Mathf.Max(1e-5f, Mathf.Abs(parentLossy.z));
            _scaleWrapper.localScale = new Vector3(lx, ly, lz);
        }
        else
        {
            // No HMD anchor yet — keep flat local pose; will snap when parent is set.
            _scaleWrapper.localRotation = Quaternion.identity;
            _scaleWrapper.localPosition = Vector3.forward * WorldSpaceDistance;
            _scaleWrapper.localScale = new Vector3(scale, scale, scale);
        }
    }

    private void ApplyVrUiLayers()
    {
        var uiLayer = LayerHelper.GetVrUiLayer();
        ApplyLayersRecursive(transform, uiLayer);
    }

    private void ApplyLayersRecursive(Transform t, int layer)
    {
        if (t == null) return;
        _originalLayers.Add(new KeyValuePair<Transform, int>(t, t.gameObject.layer));
        t.gameObject.layer = layer;
        for (var i = 0; i < t.childCount; i++)
        {
            ApplyLayersRecursive(t.GetChild(i), layer);
        }
    }

    private void UndoPatch()
    {
        foreach (var pair in _originalLayers)
        {
            if (pair.Key != null)
            {
                pair.Key.gameObject.layer = pair.Value;
            }
        }
        _originalLayers.Clear();

        if (_canvas != null)
        {
            if (_worldSpaceMode)
            {
                if (_originalParent != null)
                {
                    _canvas.transform.SetParent(_originalParent, false);
                    _canvas.transform.SetSiblingIndex(_originalSiblingIndex);
                }
                else
                {
                    _canvas.transform.SetParent(null, false);
                }

                _canvas.transform.localScale = _originalLocalScale;
                _canvas.transform.localPosition = _originalLocalPosition;
                _canvas.transform.localRotation = _originalLocalRotation;

                var rect = _canvas.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.anchorMin = _originalAnchorMin;
                    rect.anchorMax = _originalAnchorMax;
                    rect.pivot = _originalPivot;
                    rect.sizeDelta = _originalSizeDelta;
                }

                var scaler = _canvas.GetComponent<CanvasScaler>();
                if (scaler != null && _hadScaler)
                {
                    scaler.uiScaleMode = _originalScalerMode;
                    scaler.scaleFactor = _originalScalerFactor;
                    scaler.enabled = _originalScalerEnabled;
                }
            }

            _canvas.renderMode = _originalRenderMode;
            _canvas.worldCamera = _originalWorldCamera;
            _canvas.planeDistance = _originalPlaneDistance;
        }

        if (_scaleWrapper != null)
        {
            Destroy(_scaleWrapper.gameObject);
            _scaleWrapper = null;
        }

        _isPatched = false;
        _worldSpaceMode = false;
        _worldSpaceLayoutInitialized = false;
        Debug.Log($"[UUVR] CanvasRedirect undid patch on '{(_canvas != null ? _canvas.name : "?")}'");
    }
}
