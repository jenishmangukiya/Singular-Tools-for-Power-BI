using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SingularTools.Core;

/// <summary>
/// Reads and writes <see cref="WorkspaceCache"/> for the current machine, plus a
/// one-time migration from the per-tool selection files this replaced.
///
/// Lives under <c>%LOCALAPPDATA%\SingularPowerTools</c> because the list of
/// reachable workspaces is a property of the machine and its Power BI sign-in,
/// not of any one report. Writes are atomic, and skipped when nothing changed.
/// </summary>
public static class WorkspaceCacheStore
{
    public const string FileName = "workspaces.json";

    private const string LegacyPublishingManagerFile = "publishing-manager.json";
    private const string LegacyGroupWorkspacesFile = "publishing-groups-workspaces.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Folder shared by every machine-scoped file the app keeps.</summary>
    public static string ResolveDataFolder()
        => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SingularPowerTools");

    public static string ResolveCachePath() => Path.Combine(ResolveDataFolder(), FileName);

    /// <summary>
    /// Loads the cache, or an empty one when it is missing or unreadable. A corrupt
    /// file must never stop a tool from opening.
    /// </summary>
    public static WorkspaceCache Load(string? path = null)
    {
        var cachePath = path ?? ResolveCachePath();

        try
        {
            if (!File.Exists(cachePath)) return WorkspaceCache.Empty();

            return FromJson(File.ReadAllText(cachePath));
        }
        catch
        {
            return WorkspaceCache.Empty();
        }
    }

    /// <summary>Parses a cache document. Never throws; returns empty on bad input.</summary>
    public static WorkspaceCache FromJson(string? json)
    {
        var cache = WorkspaceCache.Empty();
        if (string.IsNullOrWhiteSpace(json)) return cache;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return cache;

            if (root["names"] is JsonArray names)
            {
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var node in names)
                {
                    var name = node?.ToString();
                    if (!string.IsNullOrWhiteSpace(name) && seen.Add(name))
                    {
                        cache.Names.Add(name);
                    }
                }
            }

            var stamp = root["lastDetectedUtc"]?.ToString();
            if (!string.IsNullOrWhiteSpace(stamp)
                && DateTimeOffset.TryParse(stamp, null,
                       System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                cache.LastDetectedUtc = parsed;
            }
        }
        catch
        {
            return WorkspaceCache.Empty();
        }

        return cache;
    }

    /// <summary>
    /// Records a successful detection: replaces the list and stamps the time.
    /// Returns false only when the file could not be written.
    /// </summary>
    public static bool SaveDetected(IEnumerable<string> names, DateTimeOffset? detectedUtc = null, string? path = null)
    {
        var cache = new WorkspaceCache
        {
            Names = Normalize(names),
            LastDetectedUtc = detectedUtc ?? DateTimeOffset.UtcNow
        };

        return Save(cache, path);
    }

    /// <summary>Atomically writes the cache. Skips the write when nothing changed.</summary>
    public static bool Save(WorkspaceCache cache, string? path = null)
    {
        if (cache == null) return false;

        var cachePath = path ?? ResolveCachePath();

        try
        {
            cache.Names = Normalize(cache.Names);
            var json = ToJson(cache);

            if (File.Exists(cachePath)
                && string.Equals(File.ReadAllText(cachePath), json, StringComparison.Ordinal))
            {
                return true;
            }

            var directory = Path.GetDirectoryName(cachePath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = cachePath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, cachePath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToJson(WorkspaceCache cache)
    {
        var names = new JsonArray();
        foreach (var name in cache.Names)
        {
            names.Add(name);
        }

        var root = new JsonObject
        {
            ["names"] = names,
            ["lastDetectedUtc"] = cache.LastDetectedUtc?.ToUniversalTime().ToString("O")
        };

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>
    /// Seeds a cache from the selection files written by earlier versions, so an
    /// author who already told the app which workspaces they use does not have to
    /// detect again. Read-only: the legacy files are left on disk untouched, so a
    /// rollback to an older build still finds them.
    ///
    /// Returns null when there is nothing to migrate or a cache already exists.
    /// </summary>
    public static WorkspaceCache? TryMigrateLegacySelection(string? folder = null)
    {
        var dataFolder = folder ?? ResolveDataFolder();

        try
        {
            if (File.Exists(Path.Combine(dataFolder, FileName))) return null;

            var names = new List<string>();
            names.AddRange(ReadLegacyNames(Path.Combine(dataFolder, LegacyPublishingManagerFile)));
            names.AddRange(ReadLegacyNames(Path.Combine(dataFolder, LegacyGroupWorkspacesFile)));

            names = Normalize(names);
            if (names.Count == 0) return null;

            return new WorkspaceCache
            {
                Names = names,
                // Unknown: the legacy files only recorded names, so leave the
                // timestamp unset and let the UI say "not detected yet".
                LastDetectedUtc = null
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Reads a legacy file holding a flat array of workspace names.</summary>
    private static IEnumerable<string> ReadLegacyNames(string path)
    {
        if (!File.Exists(path)) return Array.Empty<string>();

        try
        {
            var list = JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path));
            return list ?? new List<string>();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>Trims, drops blanks and removes duplicates while keeping order.</summary>
    private static List<string> Normalize(IEnumerable<string>? names)
    {
        var result = new List<string>();
        if (names == null) return result;

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var name in names)
        {
            var trimmed = name?.Trim();
            if (!string.IsNullOrEmpty(trimmed) && seen.Add(trimmed))
            {
                result.Add(trimmed);
            }
        }

        return result;
    }
}
