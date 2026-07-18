using HarmonyLib;
using UnityEngine;

namespace Uuvr.GameFixes;

/// <summary>Empty lifecycle defaults + helpers for L1 game fixes.</summary>
public abstract class UuvrGameFixBase : IUuvrGameFix
{
    public abstract string Id { get; }

    protected GameFixContext Ctx { get; private set; }
    protected Harmony Harmony { get; private set; }

    public virtual void OnBootstrap(GameFixContext ctx)
    {
        Ctx = ctx;
        Log("OnBootstrap");
    }

    public virtual void OnCoreReady(UuvrCore core)
    {
        Log("OnCoreReady");
    }

    public virtual void OnSceneLoaded(string sceneName, int buildIndex)
    {
        // default: no-op
    }

    public virtual void OnUnload()
    {
        if (Harmony != null)
        {
            try
            {
                Harmony.UnpatchSelf();
            }
            catch
            {
                // ignore
            }

            Harmony = null;
        }

        Log("OnUnload");
    }

    protected Harmony EnsureHarmony(string suffix = "main")
    {
        if (Harmony == null && Ctx != null)
        {
            Harmony = Ctx.CreateHarmony(suffix);
        }

        return Harmony;
    }

    /// <summary>Patch Harmony attributes on this fix type only (not the whole assembly).</summary>
    protected void PatchSelf()
    {
        var h = EnsureHarmony();
        if (h == null) return;
        h.PatchAll(GetType());
    }

    protected void Log(string message)
    {
        Debug.Log("[UUVR] GameFix[" + Id + "]: " + message);
    }
}
