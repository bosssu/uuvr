using System;
using System.Reflection;
using UnityEngine;

namespace Uuvr.VrUi;

/// <summary>
/// Configures cameras used only for UI capture / overlay so they work under XR and SRP (URP/HDRP).
/// </summary>
public static class UiCameraSetup
{
    private static Type? _hdAdditionalCameraDataType;
    private static PropertyInfo? _hdXrRenderingProperty;
    private static PropertyInfo? _hdClearColorModeProperty;
    private static PropertyInfo? _hdBackgroundColorProperty;
    private static PropertyInfo? _hdClearDepthProperty;
    private static PropertyInfo? _hdAntialiasingProperty;
    private static PropertyInfo? _hdVolumeLayerMaskProperty;
    private static PropertyInfo? _hdStopNaNsProperty;
    private static bool _hdTypesResolved;
    private static bool? _isHdrp;

    public static bool IsHdrp()
    {
        if (_isHdrp.HasValue) return _isHdrp.Value;
        ResolveHdrpTypes();
        _isHdrp = _hdAdditionalCameraDataType != null;
        return _isHdrp.Value;
    }

    /// <summary>
    /// Capture / pre-clear camera: mono, non-XR, solid clear color.
    /// Prefer a bright chroma key (magenta) for UI-only captures so dark UI is not keyed out.
    /// Does NOT force camera.enabled — caller decides.
    /// </summary>
    public static void ConfigureCaptureCamera(Camera camera, Color? clearColor = null)
    {
        if (camera == null) return;

        var bg = clearColor ?? Color.clear;
        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.depth = -50;
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = bg;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 2000f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;

        DisableXrOnAdditionalData(camera);
        TryConfigureHdrpCamera(
            camera,
            clearModeName: "Color",
            xrRendering: false,
            clearDepth: true,
            backgroundColor: bg,
            disableTemporalAa: false,
            clearVolumeMask: false);
    }

    /// <summary>
    /// Overlay camera: draws only the VR UI layer on top of the scene (can be XR).
    /// HDRP: clearColorMode MUST be None. Default after adding HDAdditionalCameraData is Sky
    /// which wipes the frame to skybox (scene gone, only sky + UI remain).
    /// </summary>
    public static void ConfigureOverlayCamera(Camera camera, int uiLayer)
    {
        if (camera == null) return;

        camera.stereoTargetEye = StereoTargetEyeMask.Both;
        camera.depth = 100;
        camera.clearFlags = CameraClearFlags.Depth;
        camera.cullingMask = 1 << uiLayer;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 100f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;
        camera.backgroundColor = Color.clear;

        TryConfigureHdrpCamera(
            camera,
            clearModeName: "None",
            xrRendering: true,
            clearDepth: true,
            backgroundColor: Color.clear,
            disableTemporalAa: false,
            clearVolumeMask: false);
    }

    /// <summary>
    /// World-space VR UI pass: XR stereo, UI layer only, NO color clear, NO TAA.
    /// Pair with excluding the UI layer from the main VR camera so TAA history never
    /// contains head-locked UI (eliminates smear when turning the HMD).
    /// Returns false if HDRP clearColorMode could not be forced to None (do not enable).
    /// </summary>
    public static bool ConfigureWorldSpaceUiOverlayCamera(Camera camera, int uiLayer)
    {
        if (camera == null) return false;

        camera.stereoTargetEye = StereoTargetEyeMask.Both;
        camera.depth = 100;
        camera.clearFlags = CameraClearFlags.Depth;
        camera.cullingMask = 1 << uiLayer;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 50f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = null;

        return TryConfigureHdrpCamera(
            camera,
            clearModeName: "None",
            xrRendering: true,
            clearDepth: true,
            backgroundColor: Color.clear,
            disableTemporalAa: true,
            clearVolumeMask: true);
    }

    /// <summary>
    /// Game-view (monitor) UI pass only: mono, high depth, UI layer, NEVER clear color to sky.
    /// Used for World Space VR UI so the desktop still sees menus without wiping the scene.
    /// </summary>
    public static void ConfigureDesktopUiCamera(Camera camera, int uiLayer)
    {
        if (camera == null) return;

        camera.stereoTargetEye = StereoTargetEyeMask.None;
        camera.depth = 200;
        camera.clearFlags = CameraClearFlags.Depth;
        camera.cullingMask = 1 << uiLayer;
        camera.nearClipPlane = 0.01f;
        camera.farClipPlane = 50f;
        camera.allowHDR = false;
        camera.allowMSAA = false;
        camera.useOcclusionCulling = false;
        camera.backgroundColor = Color.clear;
        camera.targetTexture = null;

        // xrRendering MUST be false — this is a game-view camera, not an XR eye.
        // clearColorMode MUST be None — Sky (HDRP default) erases geometry and leaves skybox+UI.
        TryConfigureHdrpCamera(
            camera,
            clearModeName: "None",
            xrRendering: false,
            clearDepth: true,
            backgroundColor: Color.clear,
            disableTemporalAa: true,
            clearVolumeMask: true);
    }

    private static void DisableXrOnAdditionalData(Camera camera)
    {
#if MODERN
        try
        {
            var additional = VrCamera.AdditionalCameraData.Create(camera);
            additional?.SetAllowXrRendering(false);
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UUVR] URP AdditionalCameraData XR disable failed: {e.Message}");
        }
#endif
    }

    private static void ResolveHdrpTypes()
    {
        if (_hdTypesResolved) return;
        _hdTypesResolved = true;

        _hdAdditionalCameraDataType =
            Type.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime") ??
            Type.GetType("UnityEngine.Rendering.HighDefinition.HDAdditionalCameraData, Unity.RenderPipelines.HighDefinition.Runtime.dll");

        if (_hdAdditionalCameraDataType == null)
        {
            Debug.Log("[UUVR] HDAdditionalCameraData type not found (not HDRP or stripped).");
            return;
        }

        _hdXrRenderingProperty =
            _hdAdditionalCameraDataType.GetProperty("xrRendering", BindingFlags.Instance | BindingFlags.Public) ??
            _hdAdditionalCameraDataType.GetProperty("allowXRRendering", BindingFlags.Instance | BindingFlags.Public);
        _hdClearColorModeProperty =
            _hdAdditionalCameraDataType.GetProperty("clearColorMode", BindingFlags.Instance | BindingFlags.Public);
        _hdBackgroundColorProperty =
            _hdAdditionalCameraDataType.GetProperty("backgroundColorHDR", BindingFlags.Instance | BindingFlags.Public);
        _hdClearDepthProperty =
            _hdAdditionalCameraDataType.GetProperty("clearDepth", BindingFlags.Instance | BindingFlags.Public);
        _hdAntialiasingProperty =
            _hdAdditionalCameraDataType.GetProperty("antialiasing", BindingFlags.Instance | BindingFlags.Public);
        _hdVolumeLayerMaskProperty =
            _hdAdditionalCameraDataType.GetProperty("volumeLayerMask", BindingFlags.Instance | BindingFlags.Public);
        _hdStopNaNsProperty =
            _hdAdditionalCameraDataType.GetProperty("stopNaNs", BindingFlags.Instance | BindingFlags.Public);

        Debug.Log("[UUVR] HDRP HDAdditionalCameraData available.");
    }

    /// <returns>False when clearColorMode=None was required but could not be applied.</returns>
    private static bool TryConfigureHdrpCamera(
        Camera camera,
        string clearModeName,
        bool xrRendering,
        bool clearDepth,
        Color backgroundColor,
        bool disableTemporalAa,
        bool clearVolumeMask)
    {
        ResolveHdrpTypes();
        if (_hdAdditionalCameraDataType == null) return true; // Built-in pipeline: OK

        try
        {
            // Prefer existing component. Only AddComponent when we can set clearColorMode —
            // default HDAdditionalCameraData is Sky clear, which erases scene geometry.
            var hdData = camera.gameObject.GetComponent(_hdAdditionalCameraDataType);
            var added = false;
            if (hdData == null)
            {
                if (_hdClearColorModeProperty == null || !_hdClearColorModeProperty.CanWrite)
                {
                    Debug.LogWarning(
                        $"[UUVR] Skipping HDAdditionalCameraData on '{camera.name}' " +
                        "(cannot set clearColorMode; default Sky would wipe the scene).");
                    return clearModeName != "None";
                }

                hdData = camera.gameObject.AddComponent(_hdAdditionalCameraDataType);
                added = true;
            }

            if (_hdXrRenderingProperty != null && _hdXrRenderingProperty.CanWrite)
            {
                _hdXrRenderingProperty.SetValue(hdData, xrRendering, null);
            }

            var clearOk = false;
            if (_hdClearColorModeProperty != null && _hdClearColorModeProperty.CanWrite)
            {
                var enumType = _hdClearColorModeProperty.PropertyType;
                if (enumType.IsEnum)
                {
                    object mode;
                    try
                    {
                        mode = Enum.Parse(enumType, clearModeName);
                    }
                    catch
                    {
                        // HDRP: Sky=0, Color=1, None=2
                        var fallback = clearModeName == "None" ? 2 : clearModeName == "Color" ? 1 : 0;
                        mode = Enum.ToObject(enumType, fallback);
                    }
                    _hdClearColorModeProperty.SetValue(hdData, mode, null);
                    clearOk = true;
                    Debug.Log(
                        $"[UUVR] HDRP camera '{camera.name}' clearColorMode={clearModeName}, " +
                        $"xr={xrRendering}, noTAA={disableTemporalAa}");
                }
            }

            if (!clearOk && clearModeName == "None")
            {
                if (added)
                {
                    try
                    {
                        UnityEngine.Object.Destroy(hdData as UnityEngine.Object);
                    }
                    catch
                    {
                        // ignore
                    }

                    Debug.LogWarning(
                        $"[UUVR] Removed HDAdditionalCameraData from '{camera.name}' " +
                        "(failed to force clearColorMode=None).");
                }

                return false;
            }

            if (_hdBackgroundColorProperty != null && _hdBackgroundColorProperty.CanWrite)
            {
                // HDRP backgroundColorHDR is linear; bright key colors still work for chromakey.
                _hdBackgroundColorProperty.SetValue(hdData, backgroundColor, null);
            }

            if (_hdClearDepthProperty != null && _hdClearDepthProperty.CanWrite)
            {
                _hdClearDepthProperty.SetValue(hdData, clearDepth, null);
            }

            if (disableTemporalAa && _hdAntialiasingProperty != null && _hdAntialiasingProperty.CanWrite)
            {
                try
                {
                    var aaType = _hdAntialiasingProperty.PropertyType;
                    object noneMode;
                    try
                    {
                        noneMode = Enum.Parse(aaType, "None");
                    }
                    catch
                    {
                        noneMode = Enum.ToObject(aaType, 0);
                    }

                    _hdAntialiasingProperty.SetValue(hdData, noneMode, null);
                }
                catch (Exception e)
                {
                    Debug.LogWarning($"[UUVR] Failed to disable AA on '{camera.name}': {e.Message}");
                }
            }

            // Avoid motion blur / DoF volumes affecting the UI pass.
            if (clearVolumeMask && _hdVolumeLayerMaskProperty != null && _hdVolumeLayerMaskProperty.CanWrite)
            {
                try
                {
                    // LayerMask is a struct; set via boxed LayerMask with value 0.
                    var mask = new LayerMask { value = 0 };
                    _hdVolumeLayerMaskProperty.SetValue(hdData, mask, null);
                }
                catch
                {
                    // optional
                }
            }

            if (_hdStopNaNsProperty != null && _hdStopNaNsProperty.CanWrite)
            {
                try
                {
                    _hdStopNaNsProperty.SetValue(hdData, false, null);
                }
                catch
                {
                    // optional
                }
            }

            return clearModeName != "None" || clearOk;
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UUVR] HDRP camera setup failed: {e.Message}");
            return false;
        }
    }
}
