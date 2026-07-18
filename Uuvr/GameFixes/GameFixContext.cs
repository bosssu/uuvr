using BepInEx.Configuration;
using HarmonyLib;

namespace Uuvr.GameFixes;

public sealed class GameFixContext
{
    public string GameId { get; private set; }
    public string ProductName { get; private set; }
    public string ProcessName { get; private set; }
    public string ModFolderPath { get; private set; }
    public ModConfiguration Config { get; private set; }
    public ConfigFile ConfigFile { get; private set; }

    public GameFixContext(
        string gameId,
        string productName,
        string processName,
        string modFolderPath,
        ModConfiguration config)
    {
        GameId = gameId ?? "";
        ProductName = productName ?? "";
        ProcessName = processName ?? "";
        ModFolderPath = modFolderPath ?? "";
        Config = config;
        ConfigFile = config != null ? config.Config : null;
    }

    /// <summary>Harmony instance isolated from UUVR core patches.</summary>
    public Harmony CreateHarmony(string suffix)
    {
        return new Harmony("uuvr.gamefix." + GameId + "." + (suffix ?? "main"));
    }
}
