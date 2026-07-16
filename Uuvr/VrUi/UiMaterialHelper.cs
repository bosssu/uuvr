using UnityEngine;

namespace Uuvr.VrUi;

/// <summary>
/// Builds a material for the VR UI quad that works across Built-in / URP / HDRP.
/// On HDRP, prefer HDRP/Unlit — Sprites/Default may exist but often does not draw correctly.
/// </summary>
public static class UiMaterialHelper
{
    private static readonly string[] BuiltInFirst =
    {
        "Sprites/Default",
        "UI/Default",
        "Unlit/Transparent",
        "Unlit/Texture",
        "Legacy Shaders/Transparent/Diffuse",
        "Universal Render Pipeline/Unlit",
        "HDRP/Unlit",
    };

    private static readonly string[] HdrpFirst =
    {
        "HDRP/Unlit",
        "Hidden/HDRP/Unlit",
        "Unlit/Transparent",
        "Unlit/Texture",
        "UI/Default",
        "Sprites/Default",
        "Universal Render Pipeline/Unlit",
    };

    public static Material CreateUiQuadMaterial(Texture texture)
    {
        var candidates = UiCameraSetup.IsHdrp() ? HdrpFirst : BuiltInFirst;

        Shader? shader = null;
        string? usedName = null;

        foreach (var name in candidates)
        {
            var found = Shader.Find(name);
            if (found == null) continue;
            shader = found;
            usedName = name;
            break;
        }

        if (shader == null)
        {
            Debug.LogError("[UUVR] No suitable UI quad shader found. VR UI plane will be invisible.");
            shader = Shader.Find("Hidden/InternalErrorShader");
            if (shader == null)
            {
                return new Material(Shader.Find("Diffuse") ?? Shader.Find("Standard"));
            }
            usedName = "Hidden/InternalErrorShader";
        }

        Debug.Log($"[UUVR] VR UI quad shader: {usedName} (HDRP={UiCameraSetup.IsHdrp()})");

        var material = new Material(shader)
        {
            mainTexture = texture,
            renderQueue = 5000,
            color = Color.white,
        };

        TrySetTexture(material, "_MainTex", texture);
        TrySetTexture(material, "_BaseMap", texture);
        TrySetTexture(material, "_BaseColorMap", texture);
        TrySetTexture(material, "_UnlitColorMap", texture);
        TrySetTexture(material, "_MainTexMap", texture);
        TrySetTexture(material, "_EmissiveColorMap", texture);

        TrySetColor(material, "_Color", Color.white);
        TrySetColor(material, "_BaseColor", Color.white);
        TrySetColor(material, "_UnlitColor", Color.white);
        TrySetColor(material, "_EmissiveColor", Color.white);

        // HDRP: use Opaque so a solid plane is always visible (transparent+empty RT = "no UI").
        // Game-view capture is usually opaque RGB.
        var transparent = !UiCameraSetup.IsHdrp();
        if (material.HasProperty("_SurfaceType"))
        {
            material.SetFloat("_SurfaceType", transparent ? 1f : 0f);
        }
        if (material.HasProperty("_Surface"))
        {
            material.SetFloat("_Surface", transparent ? 1f : 0f);
        }
        if (material.HasProperty("_ZWrite"))
        {
            material.SetFloat("_ZWrite", transparent ? 0f : 1f);
        }
        if (material.HasProperty("_AlphaCutoffEnable"))
        {
            material.SetFloat("_AlphaCutoffEnable", 0f);
        }

        // Make double-sided so the plane is visible from both eyes / offsets.
        if (material.HasProperty("_DoubleSidedEnable"))
        {
            material.SetFloat("_DoubleSidedEnable", 1f);
        }
        if (material.HasProperty("_CullMode"))
        {
            material.SetFloat("_CullMode", 0f); // Both
        }

        return material;
    }

    private static void TrySetTexture(Material material, string property, Texture texture)
    {
        if (material.HasProperty(property))
        {
            material.SetTexture(property, texture);
        }
    }

    private static void TrySetColor(Material material, string property, Color color)
    {
        if (material.HasProperty(property))
        {
            material.SetColor(property, color);
        }
    }
}
