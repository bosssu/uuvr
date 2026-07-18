using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.SceneManagement;
using Uuvr.GameFixes;
using Uuvr.VrCamera;
using Uuvr.VrTogglers;
using Uuvr.VrUi;

namespace Uuvr;

public class UuvrCore: MonoBehaviour
{
#if CPP
    public UuvrCore(IntPtr pointer) : base(pointer)
    {
    }
#endif

    private readonly KeyboardKey _toggleVrKey = new (KeyboardKey.KeyCode.F3);
    private float _originalFixedDeltaTime;
    
    private VrUiManager? _vrUi;
    private PropertyInfo? _refreshRateProperty;
    private VrTogglerManager? _vrTogglerManager;
    private bool _sceneHooked;

    public static void Create()
    {
        new GameObject("UUVR").AddComponent<UuvrCore>();
    }

    private void Awake()
    {
        DontDestroyOnLoad(gameObject);
        gameObject.AddComponent<VrCameraManager>();
        
        // TODO: Emulate input.   
        // UuvrBehaviour.Create<UuvrInput>(transform);
    }

    private void OnDestroy()
    {
        UnhookSceneLoaded();
        GameFixHost.OnUnload();
        Debug.Log("UUVR has been destroyed. This shouldn't have happened. Recreating...");
        
        Create();
    }

    private void Start()
    {
        var xrDeviceType = Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.XRModule") ??
                           Type.GetType("UnityEngine.XR.XRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine.VRModule") ??
                           Type.GetType("UnityEngine.VR.VRDevice, UnityEngine");

        _refreshRateProperty = xrDeviceType?.GetProperty("refreshRate");
        
        _vrUi = UuvrBehaviour.Create<VrUiManager>(transform);

        _vrTogglerManager = new VrTogglerManager();

        SetPositionTrackingEnabled(false);

        HookSceneLoaded();
        GameFixHost.OnCoreReady(this);
    }

    private void HookSceneLoaded()
    {
        if (_sceneHooked) return;
        try
        {
            SceneManager.sceneLoaded += OnUnitySceneLoaded;
            _sceneHooked = true;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UUVR] Failed to hook SceneManager.sceneLoaded: " + e.Message);
        }
    }

    private void UnhookSceneLoaded()
    {
        if (!_sceneHooked) return;
        try
        {
            SceneManager.sceneLoaded -= OnUnitySceneLoaded;
        }
        catch
        {
            // ignore
        }

        _sceneHooked = false;
    }

    private void OnUnitySceneLoaded(Scene scene, LoadSceneMode mode)
    {
        GameFixHost.OnSceneLoaded(scene.name, scene.buildIndex);
    }

    private void Update()
    {
        if (_toggleVrKey.UpdateIsDown()) _vrTogglerManager?.ToggleVr();
        UpdatePhysicsRate();
        DepthOfFieldDisabler.Tick();
    }

    private void UpdatePhysicsRate()
    {
        if (_originalFixedDeltaTime == 0)
        {
            _originalFixedDeltaTime = Time.fixedDeltaTime;
        }

        if (_refreshRateProperty == null) return;

        var headsetRefreshRate = (float)_refreshRateProperty.GetValue(null, null);
        if (headsetRefreshRate <= 0) return;

        if (ModConfiguration.Instance.PhysicsMatchHeadsetRefreshRate.Value)
        {
            Time.fixedDeltaTime = 1f / (float) _refreshRateProperty.GetValue(null, null);
        }
        else
        {
            Time.fixedDeltaTime = _originalFixedDeltaTime;
        }
    }

    private static void SetPositionTrackingEnabled(bool positionTrackingEnabled)
    {
        var inputTrackingType = 
            Type.GetType("UnityEngine.XR.InputTracking, UnityEngine.XRModule") ??
            Type.GetType("UnityEngine.XR.InputTracking, UnityEngine.VRModule") ??
            Type.GetType("UnityEngine.VR.InputTracking, UnityEngine.VRModule") ??
            Type.GetType("UnityEngine.VR.InputTracking, UnityEngine");

        if (inputTrackingType != null)
        {
            var disablePositionalTrackingProperty = inputTrackingType.GetProperty("disablePositionalTracking");
            if (disablePositionalTrackingProperty != null)
            {
                disablePositionalTrackingProperty.SetValue(null, !positionTrackingEnabled, null);
            }
            else
            {
                Debug.LogWarning("Failed to get property disablePositionalTracking");
            }
        }
        else
        {
            Debug.LogWarning("Failed to get type UnityEngine.XR.InputTracking");
        }
    }
}
