using System;
using System.IO;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Uuvr.VrUi;

/// <summary>
/// World Space VR UI: orthographic event camera (always on the UI front face), software
/// cursor, motion-vector kill. Negative canvas scale.x still flips EventSystem mouse X
/// so hits match the cursor; with positive scale, correct camera side alone keeps L/R correct.
/// </summary>
public class VrUiWorldInput : UuvrBehaviour
{
#if CPP
    public VrUiWorldInput(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    private Canvas? _canvas;
    private RectTransform? _canvasRect;
    private Camera? _eventCamera;
    private RectTransform? _cursorRect;
    private RawImage? _cursorImage;
    private Texture2D? _cursorTexture;
    private UuvrUiBaseInput? _baseInput;
    private bool _loggedReady;
    private bool _loggedInputFlip;
    private int _motionVectorFrames;

    public static VrUiWorldInput Ensure(Canvas canvas)
    {
        var existing = canvas.GetComponent<VrUiWorldInput>();
        if (existing != null) return existing;
        return canvas.gameObject.AddComponent<VrUiWorldInput>();
    }

    private void Start()
    {
        _canvas = GetComponent<Canvas>();
        _canvasRect = GetComponent<RectTransform>();
        EnsureEventSystemAndInput();
        EnsureGraphicRaycaster();
        EnsureEventCamera();
        EnsureCursor();
        ApplyMotionVectorKill();
        Debug.Log($"[UUVR] VrUiWorldInput ready on '{name}' (eventCam + cursor + flipped-X input when needed)");
    }

    protected override void OnBeforeRender()
    {
        if (_canvas == null || !_canvas.isActiveAndEnabled) return;
        SyncEventCameraToCanvas();
        if ((_motionVectorFrames++ % 30) == 0)
        {
            ApplyMotionVectorKill();
        }
    }

    private void LateUpdate()
    {
        if (_canvas == null || _eventCamera == null) return;

        if (_canvas.worldCamera != _eventCamera)
        {
            _canvas.worldCamera = _eventCamera;
        }

        SyncInputFlip();
        UpdateCursor();
        EnsureEventSystemAndInput();
    }

    private bool NeedsMouseFlipX()
    {
        // Negative X scale un-mirrors World Space UI after Y=180, but also mirrors
        // GraphicRaycaster hits relative to the physical mouse / visible cursor.
        return _canvasRect != null && _canvasRect.lossyScale.x < 0f;
    }

    private void SyncInputFlip()
    {
        var flip = NeedsMouseFlipX();
        if (_baseInput != null)
        {
            _baseInput.FlipX = flip;
        }

        if (flip && !_loggedInputFlip)
        {
            _loggedInputFlip = true;
            Debug.Log("[UUVR] UI pointer: flipping mouse X for EventSystem (canvas scale.x < 0)");
        }
    }

    private void EnsureEventSystemAndInput()
    {
        EventSystem es;
        if (EventSystem.current != null)
        {
            es = EventSystem.current;
        }
        else
        {
            var go = new GameObject("UuvrEventSystem");
            DontDestroyOnLoad(go);
            es = go.AddComponent<EventSystem>();
            go.AddComponent<StandaloneInputModule>();
            Debug.Log("[UUVR] Created EventSystem + StandaloneInputModule for World Space UI clicks");
        }

        if (_baseInput == null)
        {
            _baseInput = es.gameObject.GetComponent<UuvrUiBaseInput>();
            if (_baseInput == null)
            {
                _baseInput = es.gameObject.AddComponent<UuvrUiBaseInput>();
            }
        }

        // Route StandaloneInputModule through our BaseInput so raycasts use flipped X.
        var module = es.GetComponent<StandaloneInputModule>();
        if (module != null)
        {
            TrySetInputOverride(module, _baseInput);
        }

        SyncInputFlip();
    }

    private static void TrySetInputOverride(StandaloneInputModule module, BaseInput input)
    {
        // Unity 2019+: inputOverride property
        try
        {
            var prop = typeof(StandaloneInputModule).GetProperty(
                "inputOverride",
                BindingFlags.Instance | BindingFlags.Public);
            if (prop != null && prop.CanWrite)
            {
                prop.SetValue(module, input, null);
                return;
            }

            // Older: m_InputOverride / m_BaseInput private field
            var flags = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
            var field =
                typeof(BaseInputModule).GetField("m_InputOverride", flags) ??
                typeof(BaseInputModule).GetField("m_BaseInput", flags);
            field?.SetValue(module, input);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UUVR] Failed to set inputOverride: {e.Message}");
        }
    }

    private void EnsureGraphicRaycaster()
    {
        if (_canvas == null) return;
        if (_canvas.GetComponent<GraphicRaycaster>() == null)
        {
            _canvas.gameObject.AddComponent<GraphicRaycaster>();
        }
    }

    private void EnsureEventCamera()
    {
        if (_eventCamera != null) return;

        var go = new GameObject("UuvrUiEventCamera");
        go.transform.SetParent(transform, false);
        _eventCamera = go.AddComponent<Camera>();
        VrCamera.VrCamera.IgnoredCameras.Add(_eventCamera);

        _eventCamera.enabled = false; // raycasts only
        _eventCamera.orthographic = true;
        _eventCamera.nearClipPlane = 0.01f;
        _eventCamera.farClipPlane = 10f;
        _eventCamera.clearFlags = CameraClearFlags.Nothing;
        _eventCamera.cullingMask = 0;
        _eventCamera.stereoTargetEye = StereoTargetEyeMask.None;
        _eventCamera.depth = -1000;
        _eventCamera.allowHDR = false;
        _eventCamera.allowMSAA = false;

#if MODERN
        try
        {
            var additional = VrCamera.AdditionalCameraData.Create(_eventCamera);
            additional?.SetAllowXrRendering(false);
        }
        catch
        {
            // optional
        }
#endif

        if (_canvas != null)
        {
            _canvas.worldCamera = _eventCamera;
        }
    }

    private void SyncEventCameraToCanvas()
    {
        if (_eventCamera == null || _canvasRect == null) return;

        var corners = new Vector3[4];
        _canvasRect.GetWorldCorners(corners);
        var bl = corners[0];
        var tl = corners[1];
        var tr = corners[2];
        var center = (bl + tr) * 0.5f;
        var up = (tl - bl).normalized;
        var right = (corners[3] - bl).normalized;

        // Plane normal from corner winding. uGUI faces -transform.forward; we MUST place
        // the event camera on that front side. Looking from the back mirrors screen X
        // (cursor left/right inverted + raycasts miss buttons under the visual cursor).
        var normal = Vector3.Cross(right, up);
        if (normal.sqrMagnitude < 1e-8f)
        {
            normal = -_canvasRect.forward;
        }
        else
        {
            normal.Normalize();
        }

        // Prefer the side the canvas actually faces (front of the UI).
        var faceDir = -_canvasRect.forward;
        if (Vector3.Dot(normal, faceDir) < 0f)
        {
            normal = -normal;
        }

        var height = Vector3.Distance(bl, tl);
        var width = Vector3.Distance(bl, corners[3]);
        if (height < 1e-4f || width < 1e-4f) return;

        var dist = Mathf.Max(0.15f, height);
        // Sit in front of the UI, look at the panel (toward -normal).
        _eventCamera.transform.SetPositionAndRotation(
            center + normal * dist,
            Quaternion.LookRotation(-normal, up));

        _eventCamera.orthographic = true;
        _eventCamera.orthographicSize = height * 0.5f;
        if (Screen.height > 0)
        {
            _eventCamera.aspect = (float)Screen.width / Screen.height;
        }

        var canvasAspect = width / height;
        if (_eventCamera.aspect > 0.01f && canvasAspect > _eventCamera.aspect)
        {
            _eventCamera.orthographicSize = width * 0.5f / _eventCamera.aspect;
        }

        _eventCamera.nearClipPlane = 0.01f;
        _eventCamera.farClipPlane = dist + height + 1f;

        if (_canvas != null)
        {
            _canvas.worldCamera = _eventCamera;
        }

        if (!_loggedReady)
        {
            _loggedReady = true;
            Debug.Log(
                $"[UUVR] UI event camera synced (front-facing): canvas {width:F2}x{height:F2}m, " +
                $"ortho={_eventCamera.orthographicSize:F3}, screen={Screen.width}x{Screen.height}, " +
                $"flipX={NeedsMouseFlipX()}");
        }
    }

    private void EnsureCursor()
    {
        if (_cursorRect != null || _canvasRect == null) return;

        LoadCursorTexture();
        if (_cursorTexture == null) return;

        var go = new GameObject("UuvrUiCursor");
        go.transform.SetParent(_canvasRect, false);
        go.layer = gameObject.layer;

        _cursorRect = go.AddComponent<RectTransform>();
        _cursorRect.pivot = new Vector2(0.12f, 0.88f);
        _cursorRect.anchorMin = new Vector2(0.5f, 0.5f);
        _cursorRect.anchorMax = new Vector2(0.5f, 0.5f);
        _cursorRect.sizeDelta = new Vector2(48f, 48f);
        _cursorRect.SetAsLastSibling();

        _cursorImage = go.AddComponent<RawImage>();
        _cursorImage.texture = _cursorTexture;
        _cursorImage.raycastTarget = false;
        _cursorImage.color = Color.white;

        // Draw on top without participating in raycasts (no GraphicRaycaster).
        var canvas = go.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        canvas.sortingOrder = 32000;
    }

    private void LoadCursorTexture()
    {
        if (_cursorTexture != null) return;
        try
        {
            var path = Path.Combine(UuvrPlugin.ModFolderPath, "Assets", "cursor.bmp");
            if (!File.Exists(path))
            {
                Debug.LogWarning($"[UUVR] Cursor texture missing: {path}");
                return;
            }

            var bytes = File.ReadAllBytes(path);
            var width = bytes[18] + (bytes[19] << 8);
            var height = bytes[22] + (bytes[23] << 8);
            var colors = new Color32[width * height];
            _cursorTexture = new Texture2D(width, height, TextureFormat.BGRA32, false);
            for (var i = 0; i < colors.Length; i++)
            {
                colors[i] = new Color32(
                    bytes[i * 4 + 54],
                    bytes[i * 4 + 55],
                    bytes[i * 4 + 56],
                    bytes[i * 4 + 57]);
            }
            _cursorTexture.SetPixels32(colors);
            _cursorTexture.Apply(false, false);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UUVR] Failed to load cursor: {e.Message}");
        }
    }

    private void UpdateCursor()
    {
        if (_cursorRect == null || _canvasRect == null || _eventCamera == null) return;

        Cursor.visible = false;
        Cursor.lockState = CursorLockMode.None;

        // Same pointer position EventSystem uses (includes X flip when needed).
        var mouse = GetPointerScreenPosition();
        if (RectTransformUtility.ScreenPointToLocalPointInRectangle(
                _canvasRect,
                mouse,
                _eventCamera,
                out var local))
        {
            _cursorRect.anchoredPosition = local;

            // Parent may be negatively scaled; keep the arrow graphic un-mirrored.
            var scale = _canvasRect.lossyScale;
            var sx = scale.x < 0f ? -1f : 1f;
            var sy = scale.y < 0f ? -1f : 1f;
            _cursorRect.localScale = new Vector3(sx, sy, 1f);

            if (_cursorImage != null)
            {
                _cursorImage.enabled = true;
            }
        }
    }

    /// <summary>Screen position after optional X flip — shared by cursor and BaseInput.</summary>
    private Vector2 GetPointerScreenPosition()
    {
        var raw = GetRawMousePosition();
        if (NeedsMouseFlipX() && Screen.width > 0)
        {
            raw.x = Screen.width - 1f - raw.x;
        }
        return raw;
    }

    private static Vector2 GetRawMousePosition()
    {
        try
        {
            var inputType =
                Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule") ??
                Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ??
                Type.GetType("UnityEngine.Input, UnityEngine");
            var prop = inputType?.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
            {
                var v = (Vector3)prop.GetValue(null, null);
                return new Vector2(v.x, v.y);
            }
        }
        catch
        {
            // fall through
        }

        return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
    }

    private void ApplyMotionVectorKill()
    {
        try
        {
            var prop = typeof(CanvasRenderer).GetProperty(
                "motionVectorGenerationMode",
                BindingFlags.Instance | BindingFlags.Public);
            if (prop == null || !prop.CanWrite) return;

            var enumType = prop.PropertyType;
            object forceNoMotion;
            try
            {
                forceNoMotion = Enum.Parse(enumType, "ForceNoMotion");
            }
            catch
            {
                forceNoMotion = Enum.ToObject(enumType, 2);
            }

            var renderers = GetComponentsInChildren<CanvasRenderer>(true);
            foreach (var cr in renderers)
            {
                if (cr == null) continue;
                prop.SetValue(cr, forceNoMotion, null);
            }
        }
        catch
        {
            // optional
        }
    }

    private void OnDestroy()
    {
        if (_eventCamera != null)
        {
            Destroy(_eventCamera.gameObject);
            _eventCamera = null;
        }
        if (_cursorTexture != null)
        {
            Destroy(_cursorTexture);
            _cursorTexture = null;
        }
    }
}

/// <summary>
/// Feeds StandaloneInputModule a mouse position that can flip X to match negatively-scaled World Space UI.
/// </summary>
public class UuvrUiBaseInput : BaseInput
{
#if CPP
    public UuvrUiBaseInput(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    public bool FlipX;

    public override Vector2 mousePosition
    {
        get
        {
            var m = GetRawMouse();
            if (FlipX && Screen.width > 0)
            {
                m.x = Screen.width - 1f - m.x;
            }
            return m;
        }
    }

    private static Vector2 GetRawMouse()
    {
        try
        {
            var inputType =
                Type.GetType("UnityEngine.Input, UnityEngine.InputLegacyModule") ??
                Type.GetType("UnityEngine.Input, UnityEngine.CoreModule") ??
                Type.GetType("UnityEngine.Input, UnityEngine");
            var prop = inputType?.GetProperty("mousePosition", BindingFlags.Public | BindingFlags.Static);
            if (prop != null)
            {
                var v = (Vector3)prop.GetValue(null, null);
                return new Vector2(v.x, v.y);
            }
        }
        catch
        {
            // fall through
        }

        return new Vector2(Screen.width * 0.5f, Screen.height * 0.5f);
    }
}
