using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace Uuvr.GameFixes;

/// <summary>
/// Line format (net35-friendly, no JSON):
/// id|profileFile|productContainsCsv|processEqualsCsv
/// # comments and blank lines ignored
/// </summary>
public sealed class GameProfileEntry
{
    public string Id;
    public string ProfileFile;
    public string[] ProductContains;
    public string[] ProcessEquals;
}

public static class GameProfileManifest
{
    public static List<GameProfileEntry> Load(string manifestPath)
    {
        var list = new List<GameProfileEntry>();
        if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath))
        {
            Debug.LogWarning("[UUVR] GameProfiles manifest missing: " + manifestPath);
            return list;
        }

        string[] lines;
        try
        {
            lines = File.ReadAllLines(manifestPath);
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UUVR] Failed to read manifest: " + e.Message);
            return list;
        }

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i] != null ? lines[i].Trim() : "";
            if (line.Length == 0 || line[0] == '#' || line[0] == ';') continue;

            var parts = line.Split('|');
            if (parts.Length < 2) continue;

            var entry = new GameProfileEntry
            {
                Id = (parts[0] ?? "").Trim().ToLowerInvariant(),
                ProfileFile = (parts[1] ?? "").Trim(),
                ProductContains = SplitCsv(parts.Length > 2 ? parts[2] : ""),
                ProcessEquals = SplitCsv(parts.Length > 3 ? parts[3] : ""),
            };

            if (entry.Id.Length == 0 || entry.ProfileFile.Length == 0) continue;
            list.Add(entry);
        }

        return list;
    }

    private static string[] SplitCsv(string csv)
    {
        if (string.IsNullOrEmpty(csv)) return new string[0];
        var raw = csv.Split(',');
        var list = new List<string>();
        for (var i = 0; i < raw.Length; i++)
        {
            var s = raw[i] != null ? raw[i].Trim() : "";
            if (s.Length > 0) list.Add(s);
        }

        return list.ToArray();
    }
}
