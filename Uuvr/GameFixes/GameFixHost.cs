using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

namespace Uuvr.GameFixes;

/// <summary>
/// L0: match game + apply GameProfiles/*.cfg
/// L1: optional IUuvrGameFix with same id
/// </summary>
public static class GameFixHost
{
    public static string ResolvedGameId { get; private set; }
    public static IUuvrGameFix ActiveFix { get; private set; }
    public static GameFixContext Context { get; private set; }

    private static bool _bootstrapped;

    public static void Bootstrap()
    {
        if (_bootstrapped) return;
        _bootstrapped = true;

        var mod = ModConfiguration.Instance;
        if (mod == null)
        {
            Debug.LogWarning("[UUVR] GameFixHost: ModConfiguration missing");
            return;
        }

        var modFolder = UuvrPlugin.ModFolderPath ?? "";
        var profilesDir = Path.Combine(modFolder, "GameProfiles");
        var manifestPath = Path.Combine(profilesDir, "profiles.manifest");

        var product = "";
        try { product = Application.productName ?? ""; } catch { /* */ }

        var process = GameIdResolver.GetProcessName();
        var gameDir = GameIdResolver.GetGameDirectoryName();

        Debug.Log(
            "[UUVR] GameFix: product='" + product + "' process='" + process +
            "' dir='" + gameDir + "' forced='" + (mod.ForcedGameFixId.Value ?? "") + "'");

        var manifest = GameProfileManifest.Load(manifestPath);
        var resolved = GameIdResolver.Resolve(
            mod.ForcedGameFixId.Value,
            product,
            process,
            gameDir,
            manifest);

        if (resolved == null || string.IsNullOrEmpty(resolved.GameId))
        {
            Debug.Log("[UUVR] GameFix: none matched (core only)");
            ResolvedGameId = "";
            return;
        }

        ResolvedGameId = resolved.GameId;
        Debug.Log(
            "[UUVR] GameId=" + ResolvedGameId +
            " (source=" + resolved.Source + ", profile=" + resolved.ProfileFileName + ")");

        Context = new GameFixContext(
            ResolvedGameId,
            product,
            process,
            modFolder,
            mod);

        // --- L0 profile ---
        if (mod.ApplyGameProfile.Value)
        {
            ApplyProfileIfNeeded(mod, profilesDir, resolved.ProfileFileName, ResolvedGameId);
        }
        else
        {
            Debug.Log("[UUVR] GameProfile: Apply Profile=false (skipped)");
        }

        // --- L1 code fix (optional) ---
        ActiveFix = CreateCodeFix(ResolvedGameId);
        if (ActiveFix != null)
        {
            try
            {
                ActiveFix.OnBootstrap(Context);
                Debug.Log("[UUVR] GameFix: activated " + ActiveFix.GetType().Name);
            }
            catch (Exception e)
            {
                Debug.LogError("[UUVR] GameFix OnBootstrap failed: " + e);
                ActiveFix = null;
            }
        }
        else
        {
            Debug.Log("[UUVR] GameFix: none (profile-only)");
        }
    }

    public static void OnCoreReady(UuvrCore core)
    {
        if (ActiveFix == null) return;
        try
        {
            ActiveFix.OnCoreReady(core);
        }
        catch (Exception e)
        {
            Debug.LogError("[UUVR] GameFix OnCoreReady failed: " + e);
        }
    }

    public static void OnSceneLoaded(string sceneName, int buildIndex)
    {
        if (ActiveFix == null) return;
        try
        {
            ActiveFix.OnSceneLoaded(sceneName, buildIndex);
        }
        catch (Exception e)
        {
            Debug.LogError("[UUVR] GameFix OnSceneLoaded failed: " + e);
        }
    }

    public static void OnUnload()
    {
        if (ActiveFix == null) return;
        try
        {
            ActiveFix.OnUnload();
        }
        catch (Exception e)
        {
            Debug.LogError("[UUVR] GameFix OnUnload failed: " + e);
        }
        finally
        {
            ActiveFix = null;
        }
    }

    private static void ApplyProfileIfNeeded(
        ModConfiguration mod,
        string profilesDir,
        string profileFileName,
        string gameId)
    {
        var mode = mod.ProfileMode.Value;
        if (mode == ModConfiguration.GameProfileMode.Never)
        {
            Debug.Log("[UUVR] GameProfile: mode=Never (skipped)");
            return;
        }

        if (mode == ModConfiguration.GameProfileMode.Once)
        {
            var applied = mod.AppliedProfileId.Value ?? "";
            if (string.Equals(applied, gameId, StringComparison.OrdinalIgnoreCase))
            {
                Debug.Log(
                    "[UUVR] GameProfile: mode=Once, already applied id='" + applied +
                    "' (clear Applied Profile Id to re-apply)");
                return;
            }
        }

        var path = Path.Combine(profilesDir, profileFileName ?? (gameId + ".cfg"));
        var keys = GameProfileApplier.ApplyFile(path, mod);
        Debug.Log(
            "[UUVR] GameProfile: applied " + Path.GetFileName(path) +
            " (mode=" + mode + ", keys=" + keys + ")");

        // Marker for Once; Always also records last applied id for diagnostics.
        try
        {
            mod.AppliedProfileId.Value = gameId;
            mod.Config.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UUVR] Failed to write Applied Profile Id: " + e.Message);
        }
    }

    private static IUuvrGameFix CreateCodeFix(string gameId)
    {
        if (string.IsNullOrEmpty(gameId)) return null;

        Type[] types;
        try
        {
            types = Assembly.GetExecutingAssembly().GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            types = ex.Types;
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UUVR] GameFix type scan failed: " + e.Message);
            return null;
        }

        if (types == null) return null;

        Type bestType = null;
        var bestPriority = int.MinValue;

        for (var i = 0; i < types.Length; i++)
        {
            var t = types[i];
            if (t == null || t.IsAbstract || !typeof(IUuvrGameFix).IsAssignableFrom(t)) continue;

            var attrs = t.GetCustomAttributes(typeof(UuvrGameFixAttribute), false);
            if (attrs == null || attrs.Length == 0) continue;

            var attr = attrs[0] as UuvrGameFixAttribute;
            if (attr == null) continue;
            if (!string.Equals(attr.Id, gameId, StringComparison.OrdinalIgnoreCase)) continue;

            if (bestType == null || attr.Priority > bestPriority)
            {
                bestType = t;
                bestPriority = attr.Priority;
            }
        }

        if (bestType == null) return null;

        try
        {
            return Activator.CreateInstance(bestType) as IUuvrGameFix;
        }
        catch (Exception e)
        {
            Debug.LogError("[UUVR] Failed to create GameFix " + bestType.Name + ": " + e.Message);
            return null;
        }
    }
}
