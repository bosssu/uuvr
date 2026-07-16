using System;
using System.Reflection;
using UnityEngine;

namespace Uuvr;

/// <summary>
/// Disables Depth of Field on SRP Volume profiles (HDRP/URP) without hard assembly refs.
/// Scans periodically so late-loaded volumes (menus, cutscenes) are covered.
/// </summary>
public static class DepthOfFieldDisabler
{
    private const float ScanIntervalSeconds = 1.5f;

    private static float _nextScanUnscaled;
    private static bool _typesResolved;
    private static Type? _volumeType;
    private static PropertyInfo? _profileProperty;
    private static PropertyInfo? _sharedProfileProperty;
    private static FieldInfo? _componentsField;
    private static PropertyInfo? _componentsProperty;
    private static PropertyInfo? _activeProperty;
    private static int _lastDisabledCount = -1;

    public static void Tick()
    {
        var cfg = ModConfiguration.Instance;
        if (cfg == null || !cfg.DisableDepthOfField.Value) return;

        if (Time.unscaledTime < _nextScanUnscaled) return;
        _nextScanUnscaled = Time.unscaledTime + ScanIntervalSeconds;

        try
        {
            Apply();
        }
        catch (Exception e)
        {
            Debug.LogWarning($"[UUVR] DepthOfField disable scan failed: {e.Message}");
        }
    }

    /// <summary>Force a scan on next Tick (e.g. after config toggle).</summary>
    public static void RequestImmediateScan()
    {
        _nextScanUnscaled = 0f;
    }

    private static void ResolveTypes()
    {
        if (_typesResolved) return;
        _typesResolved = true;

        _volumeType =
            Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core.Runtime") ??
            Type.GetType("UnityEngine.Rendering.Volume, UnityEngine.CoreModule") ??
            Type.GetType("UnityEngine.Rendering.Volume, Unity.RenderPipelines.Core");

        if (_volumeType == null)
        {
            Debug.Log("[UUVR] Volume type not found — Depth of Field disable only works on SRP (HDRP/URP).");
            return;
        }

        _profileProperty = _volumeType.GetProperty("profile", BindingFlags.Instance | BindingFlags.Public);
        _sharedProfileProperty =
            _volumeType.GetProperty("sharedProfile", BindingFlags.Instance | BindingFlags.Public) ??
            _volumeType.GetProperty("profileRef", BindingFlags.Instance | BindingFlags.Public);

        // VolumeProfile.components is usually a List<VolumeComponent> field or property.
        var profileType =
            Type.GetType("UnityEngine.Rendering.VolumeProfile, Unity.RenderPipelines.Core.Runtime") ??
            Type.GetType("UnityEngine.Rendering.VolumeProfile, UnityEngine.CoreModule");

        if (profileType != null)
        {
            _componentsField = profileType.GetField("components", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            _componentsProperty = profileType.GetProperty("components", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        }

        // VolumeComponent.active
        var componentType =
            Type.GetType("UnityEngine.Rendering.VolumeComponent, Unity.RenderPipelines.Core.Runtime") ??
            Type.GetType("UnityEngine.Rendering.VolumeComponent, UnityEngine.CoreModule");
        _activeProperty = componentType?.GetProperty("active", BindingFlags.Instance | BindingFlags.Public);

        Debug.Log(
            $"[UUVR] DepthOfField disabler ready (volume={_volumeType.Name}, " +
            $"activeProp={_activeProperty != null})");
    }

    private static void Apply()
    {
        ResolveTypes();
        if (_volumeType == null) return;

        var volumes = UnityEngine.Object.FindObjectsOfType(_volumeType);
        if (volumes == null || volumes.Length == 0) return;

        var disabled = 0;
        foreach (var vol in volumes)
        {
            if (vol == null) continue;
            disabled += DisableDofOnVolume(vol);
        }

        if (disabled > 0 && disabled != _lastDisabledCount)
        {
            _lastDisabledCount = disabled;
            Debug.Log($"[UUVR] Disabled Depth of Field on {disabled} volume component(s)");
        }
    }

    private static int DisableDofOnVolume(UnityEngine.Object volume)
    {
        var count = 0;
        count += DisableDofOnProfile(GetProfile(volume, _profileProperty));
        count += DisableDofOnProfile(GetProfile(volume, _sharedProfileProperty));
        return count;
    }

    private static object? GetProfile(UnityEngine.Object volume, PropertyInfo? prop)
    {
        if (prop == null || !prop.CanRead) return null;
        try
        {
            return prop.GetValue(volume, null);
        }
        catch
        {
            return null;
        }
    }

    private static int DisableDofOnProfile(object? profile)
    {
        if (profile == null) return 0;

        System.Collections.IEnumerable? components = null;
        try
        {
            if (_componentsField != null)
            {
                components = _componentsField.GetValue(profile) as System.Collections.IEnumerable;
            }

            if (components == null && _componentsProperty != null && _componentsProperty.CanRead)
            {
                components = _componentsProperty.GetValue(profile, null) as System.Collections.IEnumerable;
            }
        }
        catch
        {
            return 0;
        }

        if (components == null) return 0;

        var count = 0;
        foreach (var component in components)
        {
            if (component == null) continue;
            var name = component.GetType().Name;
            if (name.IndexOf("DepthOfField", StringComparison.OrdinalIgnoreCase) < 0 &&
                name.IndexOf("Depth Of Field", StringComparison.OrdinalIgnoreCase) < 0)
            {
                continue;
            }

            if (TrySetActive(component, false))
            {
                count++;
            }
        }

        return count;
    }

    private static bool TrySetActive(object component, bool active)
    {
        try
        {
            if (_activeProperty != null && _activeProperty.CanWrite)
            {
                var current = _activeProperty.GetValue(component, null);
                if (current is bool b && b == active) return false;
                _activeProperty.SetValue(component, active, null);
                return true;
            }

            // Fallback: Behaviour-style enabled
            if (component is Behaviour behaviour)
            {
                if (behaviour.enabled == active) return false;
                behaviour.enabled = active;
                return true;
            }
        }
        catch
        {
            // ignore single component failures
        }

        return false;
    }
}
