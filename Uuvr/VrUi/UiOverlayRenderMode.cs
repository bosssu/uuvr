using UnityEngine;

namespace Uuvr.VrUi;

public class UiOverlayRenderMode: UuvrBehaviour
{
#if CPP
    public UiOverlayRenderMode(System.IntPtr pointer) : base(pointer)
    {
    }
#endif

    private Camera? _uiSceneCamera;
    private bool _cameraBuilt;

    protected override void OnSettingChanged()
    {
        base.OnSettingChanged();
        // HDRP: secondary overlay cameras frequently wipe the XR frame to skybox even when
        // clearColorMode is set to None. Keep disabled on HDRP entirely.
        if (UiCameraSetup.IsHdrp())
        {
            SetOverlayActive(false);
            return;
        }

        var wantOverlay =
            ModConfiguration.Instance.PreferredUiRenderMode.Value == ModConfiguration.UiRenderMode.OverlayCamera;
        SetOverlayActive(wantOverlay);
    }

    private void Awake()
    {
        // Do not create the camera until actually needed (avoids HDRP skybox wipe on spawn).
        if (UiCameraSetup.IsHdrp())
        {
            Debug.Log("[UUVR] UI Overlay camera skipped on HDRP (causes skybox wipe).");
            gameObject.SetActive(false);
            return;
        }

        EnsureCamera();
        SetOverlayActive(
            ModConfiguration.Instance.PreferredUiRenderMode.Value == ModConfiguration.UiRenderMode.OverlayCamera);
    }

    private void EnsureCamera()
    {
        if (_cameraBuilt) return;
        _cameraBuilt = true;

        _uiSceneCamera = Create<UuvrPoseDriver>(transform).gameObject.AddComponent<Camera>();
        VrCamera.VrCamera.IgnoredCameras.Add(_uiSceneCamera);
        UiCameraSetup.ConfigureOverlayCamera(_uiSceneCamera, LayerHelper.GetVrUiLayer());
        _uiSceneCamera.enabled = false;
        Debug.Log("[UUVR] UI Overlay camera created (disabled until Overlay mode)");
    }

    private void SetOverlayActive(bool active)
    {
        if (active && !UiCameraSetup.IsHdrp())
        {
            EnsureCamera();
        }

        if (_uiSceneCamera != null)
        {
            if (active)
            {
                UiCameraSetup.ConfigureOverlayCamera(_uiSceneCamera, LayerHelper.GetVrUiLayer());
            }
            _uiSceneCamera.enabled = active;
        }

        // Keep this component's GO active so settings still apply; only the camera is toggled.
        if (!UiCameraSetup.IsHdrp())
        {
            gameObject.SetActive(true);
        }
    }
}
