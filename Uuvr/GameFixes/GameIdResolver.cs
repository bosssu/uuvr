using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using UnityEngine;

namespace Uuvr.GameFixes;

public sealed class GameIdResolveResult
{
    public string GameId;
    public string ProfileFileName;
    public string Source;
}

public static class GameIdResolver
{
    public static GameIdResolveResult Resolve(
        string forcedId,
        string productName,
        string processName,
        string gameDirName,
        IList<GameProfileEntry> manifest)
    {
        productName = productName ?? "";
        processName = processName ?? "";
        gameDirName = gameDirName ?? "";
        forcedId = forcedId != null ? forcedId.Trim() : "";

        if (forcedId.Length > 0)
        {
            var forced = forcedId.ToLowerInvariant();
            var profile = FindProfileFile(forced, manifest) ?? (forced + ".cfg");
            return new GameIdResolveResult
            {
                GameId = forced,
                ProfileFileName = profile,
                Source = "Forced Game Id",
            };
        }

        if (manifest != null)
        {
            for (var i = 0; i < manifest.Count; i++)
            {
                var e = manifest[i];
                if (e == null || string.IsNullOrEmpty(e.Id)) continue;

                if (MatchesProcess(e.ProcessEquals, processName) ||
                    MatchesProcess(e.ProcessEquals, gameDirName) ||
                    MatchesProduct(e.ProductContains, productName) ||
                    MatchesProduct(e.ProductContains, gameDirName))
                {
                    return new GameIdResolveResult
                    {
                        GameId = e.Id,
                        ProfileFileName = e.ProfileFile,
                        Source = "manifest",
                    };
                }
            }
        }

        return null;
    }

    public static string GetProcessName()
    {
        try
        {
            return Process.GetCurrentProcess().ProcessName ?? "";
        }
        catch
        {
            return "";
        }
    }

    public static string GetGameDirectoryName()
    {
        try
        {
            var data = Application.dataPath;
            if (string.IsNullOrEmpty(data)) return "";
            // .../GameName/GameName_Data  or  .../GameName/Data
            var parent = Directory.GetParent(data);
            return parent != null ? parent.Name : "";
        }
        catch
        {
            return "";
        }
    }

    private static string FindProfileFile(string id, IList<GameProfileEntry> manifest)
    {
        if (manifest == null) return null;
        for (var i = 0; i < manifest.Count; i++)
        {
            var e = manifest[i];
            if (e != null && e.Id == id) return e.ProfileFile;
        }

        return null;
    }

    private static bool MatchesProcess(string[] names, string processName)
    {
        if (names == null || string.IsNullOrEmpty(processName)) return false;
        for (var i = 0; i < names.Length; i++)
        {
            if (string.Equals(names[i], processName, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    private static bool MatchesProduct(string[] needles, string productName)
    {
        if (needles == null || string.IsNullOrEmpty(productName)) return false;
        for (var i = 0; i < needles.Length; i++)
        {
            var n = needles[i];
            if (string.IsNullOrEmpty(n)) continue;
            if (productName.IndexOf(n, StringComparison.OrdinalIgnoreCase) >= 0)
                return true;
        }

        return false;
    }
}
