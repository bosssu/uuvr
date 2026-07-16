using UnityEngine;
using UnityEngine.Rendering;
using Uuvr.VrUi.PatchModes;

namespace Uuvr.VrUi;

public class VrUiManager : UuvrBehaviour
{
#if CPP
    public VrUiManager(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    private RenderTexture? _uiTexture;
    private GameObject? _vrUiQuad;
    private GameObject? _uiContainer;
    private CanvasRedirectPatchMode? _canvasRedirectPatchMode;
    private ScreenMirrorPatchMode? _screenMirrorPatchMode;
    private FollowTarget? _worldRenderModeFollowTarget;
    private UiOverlayRenderMode? _uiOverlayRenderMode;
    private Material? _uiQuadMaterial;
    private MeshFilter? _quadMeshFilter;
    private Renderer? _quadRenderer;
    private VrUiHdrpPresenter? _hdrpPresenter;
    private Camera? _desktopUiCamera;
    private Camera? _xrUiOverlayCamera;
    private bool _useGameViewCapture;
    private bool _useWorldSpaceUi;
    private bool _loggedMask;
    private int _frames;

    private void Start()
    {
        SetUpUi();
        OnSettingChanged();
        Create<VrUiCursor>(transform);
    }

    protected override void OnSettingChanged()
    {
        base.OnSettingChanged();
        if (_vrUiQuad == null || _uiContainer == null) return;

        var patchMode = ModConfiguration.Instance.PreferredUiPatchMode.Value;
        var hdrp = UiCameraSetup.IsHdrp();
        var renderMode = ModConfiguration.Instance.PreferredUiRenderMode.Value;

        // HDRP + CanvasRedirect: real World Space canvases (stereo-safe). No mono game-view plate.
        // HDRP + Mirror: full game-view capture + eye blit (UI visible; flattens under panel).
        // Non-HDRP CanvasRedirect: capture-camera path as original.
        _useWorldSpaceUi = patchMode == ModConfiguration.UiPatchMode.CanvasRedirect && hdrp;
        _useGameViewCapture = patchMode == ModConfiguration.UiPatchMode.Mirror;
        var useCanvasRedirect = patchMode == ModConfiguration.UiPatchMode.CanvasRedirect;

        if (_useWorldSpaceUi)
        {
            Debug.Log(
                "[UUVR] HDRP CanvasRedirect → World Space UI (no game-view capture, no eye mono blit).");
        }

        // World-space HDRP UI uses VR UI layer (event/desktop cameras). Otherwise layer 0 /
        // overlay layer as before.
        var displayLayer = _useWorldSpaceUi
            ? LayerHelper.GetVrUiLayer()
            : (!hdrp && renderMode == ModConfiguration.UiRenderMode.OverlayCamera
                ? LayerHelper.GetVrUiLayer()
                : 0);
        ApplyQuadLayerAndFacing(displayLayer);

        if (_uiOverlayRenderMode != null)
        {
            if (hdrp || _useWorldSpaceUi)
            {
                _uiOverlayRenderMode.gameObject.SetActive(false);
            }
            else
            {
                _uiOverlayRenderMode.gameObject.SetActive(
                    renderMode == ModConfiguration.UiRenderMode.OverlayCamera);
            }
        }

        if (_worldRenderModeFollowTarget != null)
        {
            _worldRenderModeFollowTarget.enabled = true;
        }

        if (_screenMirrorPatchMode != null)
        {
            // Full mono capture only for explicit Mirror mode — never chroma-clear UI-only
            // (that path produced a flat plate without menus on this HDRP title).
            _screenMirrorPatchMode.enabled = false;
            _screenMirrorPatchMode.SkipClearCamera = true;
            _screenMirrorPatchMode.enabled = _useGameViewCapture;
        }

        if (_canvasRedirectPatchMode != null)
        {
            _canvasRedirectPatchMode.SetWorldSpaceParent(
                _useWorldSpaceUi ? _uiContainer.transform : null);
            _canvasRedirectPatchMode.enabled = useCanvasRedirect;
        }

        // Presenter only when mirroring flat capture onto eyes (HDRP Mirror).
        if (_hdrpPresenter != null)
        {
            _hdrpPresenter.enabled = _useGameViewCapture && hdrp;
            _hdrpPresenter.DisableKeying = _useGameViewCapture;
        }

        // Hide placeholder quad when world-space canvases are the UI, or when not using mirror plate.
        if (_quadRenderer != null)
        {
            _quadRenderer.enabled = _useGameViewCapture && !hdrp;
        }

        ApplyQuadScale(_useGameViewCapture);
        if (_useWorldSpaceUi)
        {
            // UI must NOT be drawn by the main TAA'd VR camera — head-locked panels smear.
            // Draw UI on a separate XR overlay camera with TAA off + clearColorMode=None.
            SetVrCamerasUiLayer(displayLayer, include: false);
            EnsureXrUiOverlayCamera(displayLayer, enable: true);
            EnsureDesktopUiCamera(displayLayer, enable: false);
        }
        else
        {
            EnsureXrUiOverlayCamera(displayLayer, enable: false);
            EnsureVrCamerasSeeLayer(displayLayer);
            EnsureDesktopUiCamera(displayLayer, enable: false);
        }
        UpdateFollowTarget(forceReparent: true);

        // Sync world-space distances with plane placement.
        if (_useWorldSpaceUi)
        {
            CanvasRedirect.WorldSpaceDistance = GetPlaneLocalPosition().z;
            // Base ~1.05m wide at ~1.2m; multiplied by UI Scale config.
            var uiScale = 1f;
            try
            {
                uiScale = Mathf.Clamp(ModConfiguration.Instance.UiScale.Value, 0.4f, 3f);
            }
            catch
            {
                // config not ready
            }

            CanvasRedirect.WorldSpaceWidthMeters = 1.05f * uiScale;
        }

        Debug.Log(
            $"[UUVR] UI settings: patch={patchMode}, render={renderMode}, " +
            $"displayLayer={displayLayer}, gameViewCapture={_useGameViewCapture}, " +
            $"worldSpaceUi={_useWorldSpaceUi}, HDRP={hdrp}");
    }

    private void ApplyQuadLayerAndFacing(int layer)
    {
        if (_vrUiQuad == null) return;

        _vrUiQuad.layer = layer;
        _vrUiQuad.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
        _vrUiQuad.transform.localPosition = GetPlaneLocalPosition();
    }

    private Vector3 GetPlaneLocalPosition()
    {
        var dist = 1.5f;
        var cam = VrCamera.VrCamera.HighestDepthVrCamera?.ParentCamera;
        if (cam != null)
        {
            dist = Mathf.Max(cam.nearClipPlane + 0.35f, 1.2f);
            dist = Mathf.Min(dist, cam.farClipPlane * 0.5f);
        }
        return Vector3.forward * dist;
    }

    private void ApplyQuadScale(bool flipYForCapture)
    {
        if (_vrUiQuad == null || _uiTexture == null) return;

        var aspect = (float)_uiTexture.height / Mathf.Max(1, _uiTexture.width);
        var uiScale = 1f;
        try
        {
            uiScale = Mathf.Clamp(ModConfiguration.Instance.UiScale.Value, 0.4f, 3f);
        }
        catch
        {
            // config not ready
        }

        var quadWidth = 1.6f * uiScale;
        var y = quadWidth * aspect;
        if (flipYForCapture) y = -y;
        _vrUiQuad.transform.localScale = new Vector3(quadWidth, y, 1f);
    }

    private void SetUpUi()
    {
        _uiTexture = new RenderTexture(Mathf.Max(Screen.width, 16), Mathf.Max(Screen.height, 16), 24, RenderTextureFormat.ARGB32)
        {
            name = "UuvrUiTexture",
            antiAliasing = 1,
            useMipMap = false,
            autoGenerateMips = false,
        };
        _uiTexture.Create();

        _uiContainer = new GameObject("VrUiContainer")
        {
            transform = { parent = transform }
        };

        _vrUiQuad = GameObject.CreatePrimitive(PrimitiveType.Quad);
        Destroy(_vrUiQuad.GetComponent("Collider"));
        _vrUiQuad.name = "VrUiQuad";
        _vrUiQuad.transform.SetParent(_uiContainer.transform, false);
        ApplyQuadLayerAndFacing(0);
        ApplyQuadScale(false);

        _quadMeshFilter = _vrUiQuad.GetComponent<MeshFilter>();
        _quadRenderer = _vrUiQuad.GetComponent<Renderer>();
        _uiQuadMaterial = UiMaterialHelper.CreateUiQuadMaterial(_uiTexture);
        if (_uiQuadMaterial.HasProperty("_UnlitColor"))
            _uiQuadMaterial.SetColor("_UnlitColor", Color.white);
        if (_uiQuadMaterial.HasProperty("_EmissiveColor"))
            _uiQuadMaterial.SetColor("_EmissiveColor", new Color(0.25f, 0.25f, 0.25f));
        _quadRenderer.material = _uiQuadMaterial;
        _quadRenderer.shadowCastingMode = ShadowCastingMode.Off;
        _quadRenderer.receiveShadows = false;
        _quadRenderer.allowOcclusionWhenDynamic = false;
        // Placeholder plate off by default on HDRP (world-space UI or optional Mirror).
        _quadRenderer.enabled = false;

        _canvasRedirectPatchMode = gameObject.AddComponent<CanvasRedirectPatchMode>();
        _canvasRedirectPatchMode.enabled = false;
        _canvasRedirectPatchMode.SetUpTargetTexture(_uiTexture);
        _canvasRedirectPatchMode.SetWorldSpaceParent(_uiContainer.transform);

        _screenMirrorPatchMode = gameObject.AddComponent<ScreenMirrorPatchMode>();
        _screenMirrorPatchMode.enabled = false;
        _screenMirrorPatchMode.SkipClearCamera = true;
        _screenMirrorPatchMode.SetUpTargetTexture(_uiTexture);

        _uiOverlayRenderMode = Create<UiOverlayRenderMode>(transform);
        _worldRenderModeFollowTarget = _uiContainer.AddComponent<FollowTarget>();
        _worldRenderModeFollowTarget.enabled = true;

        // HDRP Mirror mode only: blit full game-view capture onto XR eyes.
        if (UiCameraSetup.IsHdrp())
        {
            _hdrpPresenter = gameObject.AddComponent<VrUiHdrpPresenter>();
            _hdrpPresenter.SetUiTexture(_uiTexture);
            _hdrpPresenter.enabled = false;
        }

        Debug.Log($"[UUVR] VrUiManager setup complete. RT={_uiTexture.width}x{_uiTexture.height}");
    }

    private void Update()
    {
        if (_uiTexture == null) SetUpUi();

        if (_uiTexture != null && Screen.width > 0 && Screen.height > 0 &&
            (Screen.width != _uiTexture.width || Screen.height != _uiTexture.height))
        {
            ResizeUiTexture(Screen.width, Screen.height);
        }
    }

    private void LateUpdate()
    {
        _frames++;
        var layer = _vrUiQuad != null ? _vrUiQuad.layer : 0;

        if (_useWorldSpaceUi)
        {
            var uiLayer = LayerHelper.GetVrUiLayer();
            // Games often reset culling masks — keep UI off the TAA'd main cam.
            SetVrCamerasUiLayer(uiLayer, include: false);
            if (_xrUiOverlayCamera == null || !_xrUiOverlayCamera.enabled)
            {
                EnsureXrUiOverlayCamera(uiLayer, enable: true);
            }
            else
            {
                // Light re-assert without full HDRP reconfigure / log spam.
                _xrUiOverlayCamera.cullingMask = 1 << uiLayer;
                _xrUiOverlayCamera.clearFlags = CameraClearFlags.Depth;
                SyncXrUiOverlayCameraPose();
            }
        }
        else
        {
            EnsureVrCamerasSeeLayer(layer);
        }
        UpdateFollowTarget(forceReparent: false);

        if (_vrUiQuad != null)
        {
            _vrUiQuad.transform.localRotation = Quaternion.Euler(0f, 180f, 0f);
            _vrUiQuad.transform.localPosition = GetPlaneLocalPosition();
            if (_useWorldSpaceUi)
            {
                CanvasRedirect.WorldSpaceDistance = _vrUiQuad.transform.localPosition.z;
            }

            // Show capture plate only for non-HDRP mirror / non-world-space paths.
            if (_quadRenderer != null)
            {
                var showPlate = _useGameViewCapture && !UiCameraSetup.IsHdrp();
                _quadRenderer.enabled = showPlate;
            }
        }

        if (!UiCameraSetup.IsHdrp() && !_useWorldSpaceUi)
        {
            DrawUiPlaneExplicit();
        }

        if (!_loggedMask && VrCamera.VrCamera.HighestDepthVrCamera?.ParentCamera != null && _frames > 10)
        {
            _loggedMask = true;
            var cam = VrCamera.VrCamera.HighestDepthVrCamera.ParentCamera;
            var bit = 1 << layer;
            var tracking = ModConfiguration.Instance.CameraTracking.Value;
            // Layers other than UI (bit 31) — if only UI bit is set, scene cannot draw.
            var mask = cam.cullingMask;
            var nonUiBits = mask & ~(1 << LayerHelper.GetVrUiLayer());
            Debug.Log(
                $"[UUVR] UI plane debug: parent={_uiContainer?.transform.parent?.name}, " +
                $"layer={layer}, camMaskHasLayer={((mask & bit) != 0)}, " +
                $"cullingMask=0x{mask:X8}, nonUiLayers={nonUiBits != 0}, " +
                $"tracking={tracking}, cam='{cam.name}', pos={cam.transform.position}, " +
                $"near={cam.nearClipPlane}, far={cam.farClipPlane}, " +
                $"planeZ={_vrUiQuad?.transform.localPosition.z}, " +
                $"worldSpaceUi={_useWorldSpaceUi}, gameViewCapture={_useGameViewCapture}, " +
                $"xrUiOverlay={(_xrUiOverlayCamera != null && _xrUiOverlayCamera.enabled)}, " +
                $"mainSeesUi={((mask & (1 << LayerHelper.GetVrUiLayer())) != 0)}");
        }
    }

#if MODERN
    protected override void OnBeginFrameRendering()
    {
        if (_useWorldSpaceUi)
        {
            SyncXrUiOverlayCameraPose();
        }
    }
#endif

    protected override void OnBeforeRender()
    {
        if (_useWorldSpaceUi)
        {
            SyncXrUiOverlayCameraPose();
        }
    }

    private void DrawUiPlaneExplicit()
    {
        if (_uiQuadMaterial == null || _quadMeshFilter == null || _vrUiQuad == null) return;
        var mesh = _quadMeshFilter.sharedMesh;
        if (mesh == null) return;

        var matrix = _vrUiQuad.transform.localToWorldMatrix;
        var layer = _vrUiQuad.layer;

        foreach (var cam in VrCamera.VrCamera.VrCameras)
        {
            if (cam == null || !cam.isActiveAndEnabled) continue;
            if (cam.cullingMask == 0) continue;
            Graphics.DrawMesh(mesh, matrix, _uiQuadMaterial, layer, cam);
        }
    }

    private void ResizeUiTexture(int width, int height)
    {
        if (_uiTexture == null) return;

        _uiTexture.Release();
        _uiTexture.width = width;
        _uiTexture.height = height;
        _uiTexture.Create();

        if (_uiQuadMaterial != null)
        {
            _uiQuadMaterial.mainTexture = _uiTexture;
            if (_uiQuadMaterial.HasProperty("_MainTex")) _uiQuadMaterial.SetTexture("_MainTex", _uiTexture);
            if (_uiQuadMaterial.HasProperty("_BaseMap")) _uiQuadMaterial.SetTexture("_BaseMap", _uiTexture);
            if (_uiQuadMaterial.HasProperty("_BaseColorMap")) _uiQuadMaterial.SetTexture("_BaseColorMap", _uiTexture);
            if (_uiQuadMaterial.HasProperty("_UnlitColorMap")) _uiQuadMaterial.SetTexture("_UnlitColorMap", _uiTexture);
        }

        _canvasRedirectPatchMode?.SetUpTargetTexture(_uiTexture);
        _screenMirrorPatchMode?.SetUpTargetTexture(_uiTexture);
        _hdrpPresenter?.SetUiTexture(_uiTexture);
        ApplyQuadScale(_useGameViewCapture);
    }

    private static void EnsureVrCamerasSeeLayer(int layer)
    {
        var bit = 1 << layer;
        foreach (var cam in VrCamera.VrCamera.VrCameras)
        {
            if (cam == null) continue;
            if ((cam.cullingMask & bit) == 0)
            {
                cam.cullingMask |= bit;
            }
        }

        var main = Camera.main;
        if (main != null && (main.cullingMask & bit) == 0)
        {
            main.cullingMask |= bit;
        }
    }

    /// <summary>
    /// Include or exclude the VR UI layer on scene/VR cameras (not our overlay cameras).
    /// </summary>
    private void SetVrCamerasUiLayer(int layer, bool include)
    {
        var bit = 1 << layer;
        foreach (var cam in VrCamera.VrCamera.VrCameras)
        {
            if (cam == null) continue;
            if (cam == _xrUiOverlayCamera || cam == _desktopUiCamera) continue;
            if (include)
            {
                cam.cullingMask |= bit;
            }
            else
            {
                cam.cullingMask &= ~bit;
            }
        }

        var main = Camera.main;
        if (main != null && main != _xrUiOverlayCamera && main != _desktopUiCamera)
        {
            if (include)
            {
                main.cullingMask |= bit;
            }
            else
            {
                main.cullingMask &= ~bit;
            }
        }
    }

    /// <summary>
    /// XR stereo UI camera: UI layer only, no TAA, clear color None.
    /// Must stay in IgnoredCameras so VrCameraManager does not wrap it.
    /// </summary>
    private void EnsureXrUiOverlayCamera(int uiLayer, bool enable)
    {
        if (!enable)
        {
            if (_xrUiOverlayCamera != null) _xrUiOverlayCamera.enabled = false;
            return;
        }

        if (_xrUiOverlayCamera == null)
        {
            var go = new GameObject("UuvrXrUiOverlayCamera");
            go.transform.SetParent(transform, false);
            _xrUiOverlayCamera = go.AddComponent<Camera>();
            VrCamera.VrCamera.IgnoredCameras.Add(_xrUiOverlayCamera);
            Debug.Log("[UUVR] XR UI overlay camera created (UI layer, no TAA)");
        }

        var ok = UiCameraSetup.ConfigureWorldSpaceUiOverlayCamera(_xrUiOverlayCamera, uiLayer);
        if (!ok)
        {
            // Fall back: put UI back on main cameras rather than lose menus entirely.
            _xrUiOverlayCamera.enabled = false;
            SetVrCamerasUiLayer(uiLayer, include: true);
            Debug.LogWarning(
                "[UUVR] XR UI overlay setup failed (clear None). UI stays on main camera (may smear).");
            return;
        }

        _xrUiOverlayCamera.enabled = true;
        SyncXrUiOverlayCameraPose();
    }

    private void SyncXrUiOverlayCameraPose()
    {
        if (_xrUiOverlayCamera == null || !_xrUiOverlayCamera.enabled) return;

        var vr = VrCamera.VrCamera.ResolveCameraInUse()
                 ?? VrCamera.VrCamera.HighestDepthVrCamera?.ParentCamera;
        if (vr == null) return;

        var t = _xrUiOverlayCamera.transform;
        t.SetPositionAndRotation(vr.transform.position, vr.transform.rotation);
        _xrUiOverlayCamera.fieldOfView = vr.fieldOfView;
        _xrUiOverlayCamera.nearClipPlane = Mathf.Max(0.01f, vr.nearClipPlane);
        _xrUiOverlayCamera.farClipPlane = Mathf.Min(50f, Mathf.Max(vr.farClipPlane, 50f));
        _xrUiOverlayCamera.orthographic = vr.orthographic;
        if (vr.orthographic)
        {
            _xrUiOverlayCamera.orthographicSize = vr.orthographicSize;
        }

        _xrUiOverlayCamera.stereoTargetEye = StereoTargetEyeMask.Both;
        _xrUiOverlayCamera.cullingMask = 1 << LayerHelper.GetVrUiLayer();
        _xrUiOverlayCamera.clearFlags = CameraClearFlags.Depth;
        _xrUiOverlayCamera.depth = Mathf.Max(vr.depth + 10f, 100f);
    }

    /// <summary>
    /// Non-XR camera that only draws the VR UI layer onto the game view (desktop monitor).
    /// HDRP: clearColorMode=None so we do not wipe the frame to skybox.
    /// </summary>
    private void EnsureDesktopUiCamera(int uiLayer, bool enable)
    {
        if (!enable)
        {
            if (_desktopUiCamera != null) _desktopUiCamera.enabled = false;
            return;
        }

        if (_desktopUiCamera == null)
        {
            var go = new GameObject("UuvrDesktopUiCamera");
            go.transform.SetParent(transform, false);
            _desktopUiCamera = go.AddComponent<Camera>();
            VrCamera.VrCamera.IgnoredCameras.Add(_desktopUiCamera);
            Debug.Log("[UUVR] Desktop UI camera created (UI layer only, non-XR)");
        }

        // Dedicated desktop pass — do NOT use ConfigureOverlayCamera (that sets XR Both +
        // can leave HDRP clearColorMode=Sky, which wipes the game view to skybox+UI only).
        UiCameraSetup.ConfigureDesktopUiCamera(_desktopUiCamera, uiLayer);
        _desktopUiCamera.enabled = true;
    }

    private void UpdateFollowTarget(bool forceReparent)
    {
        if (_uiContainer == null) return;

        // CRITICAL: never parent VrUiContainer under a scene camera.
        // Scene loads destroy that camera → container dies → worldSpaceParent=null →
        // canvases get DontDestroyOnLoad-orphaned and game UI refs NRE (Scene.Start / CheckOnGUI).
        // Keep container under this DDOL VrUiManager and follow the HMD with FollowTarget.
        if (_uiContainer.transform.parent != transform)
        {
            _uiContainer.transform.SetParent(transform, false);
            Debug.Log("[UUVR] VrUiContainer re-parented under UUVR (scene-safe, not under camera)");
        }

        // Follow the camera actually used for VR (Child mode = child cam). Re-resolve so
        // teleports/disabled cameras do not leave VrUiContainer at the old world pose.
        Transform? followT = null;
        try
        {
            if (ModConfiguration.Instance.UiAutoAlign.Value)
            {
                followT = VrCamera.VrCamera.ResolvePoseTransform();
            }
        }
        catch
        {
            // config not ready
        }

        if (followT == null)
        {
            followT = VrCamera.VrCamera.HighestDepthVrCamera?.CameraInUse?.transform
                      ?? VrCamera.VrCamera.HighestDepthVrCamera?.ParentCamera?.transform;
        }

        if (_worldRenderModeFollowTarget != null)
        {
            if (followT != null)
            {
                _worldRenderModeFollowTarget.Target = followT;
                _worldRenderModeFollowTarget.LocalPosition = Vector3.zero;
                _worldRenderModeFollowTarget.LocalRotation = Quaternion.identity;
                _worldRenderModeFollowTarget.enabled = true;
            }
            else
            {
                _worldRenderModeFollowTarget.Target = null;
            }
        }

        if (forceReparent || followT != null)
        {
            ApplyQuadLayerAndFacing(_vrUiQuad != null ? _vrUiQuad.layer : 0);
            ApplyQuadScale(_useGameViewCapture);
        }

        if (_useWorldSpaceUi)
        {
            _canvasRedirectPatchMode?.SetWorldSpaceParent(_uiContainer.transform);
        }
    }
}
