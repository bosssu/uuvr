using System.ComponentModel;
using BepInEx.Configuration;
using UnityEngine;

namespace Uuvr;

public class ModConfiguration
{
    public static ModConfiguration Instance;
    
    public enum CameraTrackingMode
    {
        [Description("Absolute")]
        Absolute,
        [Description("Relative matrix")]
        RelativeMatrix,
#if MODERN
        // TODO: could add this for legacy too.
        [Description("Relative Transform")]
        RelativeTransform,
#endif
        [Description("Child")]
        Child,
    }

#if MODERN
    public enum VrApi
    {
        [Description("OpenVR")]
        OpenVr,
        [Description("OpenXR")]
        OpenXr,
    }
#endif
    
    public enum ScreenSpaceCanvasType
    {
        [Description("None")]
        None,
        [Description("Not rendering to texture")]
        NotToTexture,
        [Description("All")]
        All,
    }

    public enum UiRenderMode
    {
        [Description("Overlay camera (draws on top of everything)")]
        OverlayCamera,
        [Description("In world (can be occluded)")]
        InWorld,
    }

    public enum UiPatchMode
    {
        [Description("Don't touch UI")]
        None,
        [Description("Mirror flat screen (game not mirrored)")]
        Mirror,
        [Description("Patch Canvas objects")]
        CanvasRedirect,
    }

    public enum GameProfileMode
    {
        [Description("Once (apply profile only until Applied Profile Id matches)")]
        Once,
        [Description("Always (re-apply profile every launch)")]
        Always,
        [Description("Never (do not apply bundled profiles)")]
        Never,
    }

    public readonly ConfigFile Config;
    public readonly ConfigEntry<CameraTrackingMode> CameraTracking;
    public readonly ConfigEntry<bool> RelativeCameraSetStereoView;
    public readonly ConfigEntry<int> VrCameraDepth;
    public readonly ConfigEntry<int> VrUiLayerOverride;
    public readonly ConfigEntry<bool> AlignCameraToHorizon;
    public readonly ConfigEntry<float> CameraPositionOffsetX;
    public readonly ConfigEntry<float> CameraPositionOffsetY;
    public readonly ConfigEntry<float> CameraPositionOffsetZ;
    public readonly ConfigEntry<bool> OverrideDepth;
    public readonly ConfigEntry<bool> DisableDepthOfField;
    public readonly ConfigEntry<bool> PhysicsMatchHeadsetRefreshRate;
    public readonly ConfigEntry<UiPatchMode> PreferredUiPatchMode;
    public readonly ConfigEntry<UiRenderMode> PreferredUiRenderMode;
    public readonly ConfigEntry<ScreenSpaceCanvasType> ScreenSpaceCanvasTypesToPatch;
    public readonly ConfigEntry<float> UiScale;
    public readonly ConfigEntry<bool> UiAutoAlign;
    public readonly ConfigEntry<float> UiAutoAlignInterval;
    public readonly ConfigEntry<float> UiAutoAlignMaxDistance;
    public readonly ConfigEntry<bool> UiFaceCamera;
    public readonly ConfigEntry<bool> UiCaptureFlipX;
    public readonly ConfigEntry<string> ForcedGameFixId;
    public readonly ConfigEntry<bool> ApplyGameProfile;
    public readonly ConfigEntry<GameProfileMode> ProfileMode;
    public readonly ConfigEntry<string> AppliedProfileId;
    
#if MODERN
    public readonly ConfigEntry<VrApi> PreferredVrApi;
#endif

    public ModConfiguration(ConfigFile config)
    {
        Instance = this;

        Config = config;
        
#if MODERN
        PreferredVrApi = config.Bind(
            "General",
            "Preferred VR APi",
            VrApi.OpenXr,
            "VR API to use. Depending on the game, some APIs might be unavailable, so UUVR will fall back to one that works.");
#endif

        CameraTracking = config.Bind(
            "Camera",
            "Camera Tracking Mode",
#if LEGACY
            CameraTrackingMode.RelativeMatrix,
#else
            CameraTrackingMode.RelativeTransform,
#endif
            "Defines how camera tracking is done. Relative is usually preferred, but not all games support it. Changing this might require restarting the level.");
        
        RelativeCameraSetStereoView = config.Bind(
            "Relative Camera",
            "Use SetStereoView for Relative Camera",
            false,
            "Some games are better with this on, some are better with this off. Just try it and see which one is better. Changing this might require restarting the level.");
        
        AlignCameraToHorizon = config.Bind(
            "Camera",
            "Align To Horizon",
            false,
            "Prevents pitch and roll changes on the camera, allowing only yaw changes.");

        CameraPositionOffsetX = config.Bind(
            "Camera",
            "Camera Position Offset X",
            0f,
            "Changes position of tracked VR cameras");

        CameraPositionOffsetY = config.Bind(
            "Camera",
            "Camera Position Offset Y",
            0f,
            "Changes position of tracked VR cameras");
        
        CameraPositionOffsetZ = config.Bind(
            "Camera",
            "Camera Position Offset Z",
            0f,
            "Changes position of tracked VR cameras");

        OverrideDepth = config.Bind(
            "Camera",
            "Override Depth",
            false,
            "In some games, the VR camera won't display anything unless we override the camera depth value.");
        
        VrCameraDepth = config.Bind(
            "Camera Depth",
            "Depth Value",
            1,
            new ConfigDescription(
                "Requires enabling 'Override Depth'. Range is -100 to 100, but you should try to find the lowest value that fixes visibility.",
                new AcceptableValueRange<int>(-100, 100)));

        // Child tracking + head-locked World Space UI often sits outside the game's DoF focus
        // plane, so HDRP Gaussian DoF softens the whole menu. Disabling DoF is the stable fix.
        DisableDepthOfField = config.Bind(
            "Camera",
            "Disable Depth Of Field",
            true,
            "Disables Depth of Field (Gaussian/physical) on SRP Volume profiles. " +
            "Use when head-locked UI looks soft/blurred. Scene soft-focus is also removed.");
        
        PhysicsMatchHeadsetRefreshRate = config.Bind(
            "General",
            "Force physics rate to match headset refresh rate",
            false,
            "Can help fix jiterriness in games that rely a lot on physics. Might break a lot of games too.");

        // CanvasRedirect: non-HDRP → capture camera; HDRP → World Space canvases (stereo-safe).
        // Mirror: full game-view capture plate (may flatten stereo under the panel).
        PreferredUiPatchMode = config.Bind(
            "UI",
            "UI Patch Mode",
            UiPatchMode.CanvasRedirect,
            "CanvasRedirect recommended (HDRP converts menus to World Space for real stereo). Mirror pastes a flat game-view plate into both eyes.");
        
        VrUiLayerOverride = config.Bind(
            "UI",
            "VR UI Layer Override",
            -1,
            new ConfigDescription(
                "Layer to use for VR UI. By default (value -1) UUVR falls back to an unused (unnamed) layer.",
                new AcceptableValueRange<int>(-1, 31)));

        ScreenSpaceCanvasTypesToPatch = config.Bind(
            "UI",
            "Screen-space UI elements to patch",
            ScreenSpaceCanvasType.All,
            "Which screen-space canvases CanvasRedirect should move onto the VR UI capture camera. Use All if menus are still missing in the headset.");
        
        PreferredUiRenderMode = config.Bind(
            "UI",
            "Preferred UI Plane Render Mode",
            // InWorld: UI quad drawn by the game VR camera (safer on HDRP — Overlay camera defaulted to Sky clear and wiped the scene).
            // OverlayCamera: dedicated top camera; requires HDRP clearColorMode=None (configured in UiCameraSetup).
            UiRenderMode.InWorld,
            "How to render the VR UI Plane. Prefer InWorld on HDRP if Overlay washes the scene to skybox; use Overlay when InWorld is culled/occluded.");

        UiScale = config.Bind(
            "UI",
            "UI Scale",
            1f,
            new ConfigDescription(
                "World-space UI size multiplier (1 = default ~1.05m wide panel). Larger = bigger menus in the headset.",
                new AcceptableValueRange<float>(0.4f, 3f)));

        UiAutoAlign = config.Bind(
            "UI",
            "UI Auto Align",
            true,
            "Keep world-space UI in front of the active VR camera. Re-picks the camera after teleports/cutscenes so the panel does not freeze at the old location.");

        UiAutoAlignInterval = config.Bind(
            "UI",
            "UI Auto Align Interval",
            0.25f,
            new ConfigDescription(
                "Seconds between re-picking the active VR camera (UI still tracks every frame). Lower = faster recovery after teleports.",
                new AcceptableValueRange<float>(0f, 5f)));

        UiAutoAlignMaxDistance = config.Bind(
            "UI",
            "UI Auto Align Max Distance",
            0.45f,
            new ConfigDescription(
                "Log (and ensure snap) when UI is farther than this many meters from the ideal head-forward pose after a camera jump.",
                new AcceptableValueRange<float>(0.1f, 5f)));

        // World Space plane placed with the same rotation as the HMD faces away from the
        // player; without a 180° yaw you look at the back of the canvas (L/R mirrored).
        UiFaceCamera = config.Bind(
            "UI",
            "UI Face Camera",
            true,
            "HDRP World Space only: yaw the canvas 180° so the panel faces the HMD. " +
            "Has no effect on non-HDRP ScreenSpaceCamera capture (use UI Capture Flip X).");

        // Non-HDRP CanvasRedirect draws UI into a RT then onto a head-locked quad (Y=180).
        // That plate commonly appears L/R mirrored; negative scale.x un-mirrors the texture.
        UiCaptureFlipX = config.Bind(
            "UI",
            "UI Capture Flip X",
            true,
            "Non-HDRP capture plane only: flip the VR UI quad horizontally (fixes mirrored menus). " +
            "Does not apply to HDRP World Space canvases.");

        // --- Game fix framework (L0 profile + L1 optional code) ---
        ForcedGameFixId = config.Bind(
            "GameFix",
            "Forced Game Id",
            "",
            "Force a GameProfiles id (e.g. swpt, bloodyspell). Empty = auto-detect from product/process name.");

        ApplyGameProfile = config.Bind(
            "GameFix",
            "Apply Profile",
            true,
            "When a game is matched, apply the bundled GameProfiles/{id}.cfg overrides.");

        ProfileMode = config.Bind(
            "GameFix",
            "Profile Mode",
            GameProfileMode.Once,
            "Once: apply only until Applied Profile Id matches (keeps your later edits). Always: re-apply every launch. Never: skip L0.");

        AppliedProfileId = config.Bind(
            "GameFix",
            "Applied Profile Id",
            "",
            "Internal marker for Profile Mode=Once. Clear this value to re-apply the bundled profile on next launch.");
    }
}
