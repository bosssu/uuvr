using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace Uuvr.GameFixes;

/// <summary>
/// Applies INI-style profile fragments onto already-bound BepInEx ConfigEntries.
/// </summary>
public static class GameProfileApplier
{
    public static int ApplyFile(string profilePath, ModConfiguration modConfig)
    {
        if (string.IsNullOrEmpty(profilePath) || !File.Exists(profilePath))
        {
            Debug.LogWarning("[UUVR] GameProfile file missing: " + profilePath);
            return 0;
        }

        Dictionary<string, string> pairs;
        try
        {
            pairs = ParseIni(File.ReadAllLines(profilePath));
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UUVR] GameProfile parse failed: " + e.Message);
            return 0;
        }

        var map = BuildEntryMap(modConfig);
        var applied = 0;

        foreach (var kv in pairs)
        {
            ConfigEntryBase entry;
            if (!map.TryGetValue(kv.Key, out entry) || entry == null)
            {
                Debug.LogWarning("[UUVR] GameProfile unknown key (skip): " + kv.Key);
                continue;
            }

            // Never let a profile rewrite framework control keys mid-apply.
            if (IsFrameworkControlKey(entry.Definition.Section, entry.Definition.Key))
            {
                continue;
            }

            try
            {
                entry.SetSerializedValue(kv.Value);
                applied++;
            }
            catch (Exception e)
            {
                Debug.LogWarning(
                    "[UUVR] GameProfile failed to set " + kv.Key + "='" + kv.Value + "': " + e.Message);
            }
        }

        try
        {
            modConfig.Config.Save();
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UUVR] GameProfile Config.Save failed: " + e.Message);
        }

        return applied;
    }

    private static bool IsFrameworkControlKey(string section, string key)
    {
        if (!string.Equals(section, "GameFix", StringComparison.OrdinalIgnoreCase)) return false;
        return string.Equals(key, "Forced Game Id", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "Apply Profile", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "Profile Mode", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(key, "Applied Profile Id", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Map "Section|Key" -> ConfigEntryBase from public fields on ModConfiguration.</summary>
    public static Dictionary<string, ConfigEntryBase> BuildEntryMap(ModConfiguration modConfig)
    {
        var map = new Dictionary<string, ConfigEntryBase>(StringComparer.OrdinalIgnoreCase);
        if (modConfig == null) return map;

        var fields = typeof(ModConfiguration).GetFields(BindingFlags.Instance | BindingFlags.Public);
        for (var i = 0; i < fields.Length; i++)
        {
            var field = fields[i];
            var value = field.GetValue(modConfig);
            var entry = value as ConfigEntryBase;
            if (entry == null) continue;

            var def = entry.Definition;
            if (def == null) continue;
            var k = def.Section + "|" + def.Key;
            map[k] = entry;
        }

        return map;
    }

    /// <summary>Minimal INI: [Section] then Key = Value. #/; comments.</summary>
    public static Dictionary<string, string> ParseIni(string[] lines)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var section = "";

        for (var i = 0; i < lines.Length; i++)
        {
            var raw = lines[i];
            if (raw == null) continue;
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line[0] == '#' || line[0] == ';') continue;

            if (line[0] == '[' && line[line.Length - 1] == ']')
            {
                section = line.Substring(1, line.Length - 2).Trim();
                continue;
            }

            var eq = line.IndexOf('=');
            if (eq <= 0) continue;
            var key = line.Substring(0, eq).Trim();
            var val = line.Substring(eq + 1).Trim();
            if (key.Length == 0) continue;
            // strip inline comments after value for simple cases
            var hash = val.IndexOf(" #", StringComparison.Ordinal);
            if (hash >= 0) val = val.Substring(0, hash).Trim();

            var mapKey = section + "|" + key;
            result[mapKey] = val;
        }

        return result;
    }
}
