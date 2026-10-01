using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;

namespace SingularTools.Core;

/// <summary>
/// Reads the data colors of a report's theme so tools can offer the report's own
/// palette instead of a fixed one. Colors come from
/// <c>definition/report.json</c> → <c>themeCollection</c>, resolving either a
/// custom theme (RegisteredResources) or the base theme (SharedResources).
/// </summary>
public static class ReportThemeService
{
    private sealed record CacheEntry(long Stamp, IReadOnlyList<string> Colors);

    private static readonly ConcurrentDictionary<string, CacheEntry> Cache =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the theme's data colors as "#RRGGBB" (empty when none can be read).</summary>
    public static IReadOnlyList<string> GetDataColors(string reportFolderPath)
    {
        if (string.IsNullOrWhiteSpace(reportFolderPath) || !Directory.Exists(reportFolderPath))
        {
            return Array.Empty<string>();
        }

        var reportJsonPath = Path.Combine(reportFolderPath, "definition", "report.json");
        var stamp = File.Exists(reportJsonPath) ? File.GetLastWriteTimeUtc(reportJsonPath).Ticks : 0;

        if (stamp != 0 &&
            Cache.TryGetValue(reportFolderPath, out var entry) &&
            entry.Stamp == stamp)
        {
            return entry.Colors;
        }

        var colors = ReadDataColors(reportFolderPath, reportJsonPath);
        Cache[reportFolderPath] = new CacheEntry(stamp, colors);
        return colors;
    }

    private static IReadOnlyList<string> ReadDataColors(string reportFolderPath, string reportJsonPath)
    {
        try
        {
            string? themeName = null;

            if (File.Exists(reportJsonPath) &&
                JsonNode.Parse(File.ReadAllText(reportJsonPath)) is JsonNode report)
            {
                var collection = report["themeCollection"];
                themeName = collection?["customTheme"]?["name"]?.ToString()
                    ?? collection?["baseTheme"]?["name"]?.ToString();
            }

            var themePath = FindThemeFile(reportFolderPath, themeName);
            if (themePath == null) return Array.Empty<string>();

            var theme = JsonNode.Parse(File.ReadAllText(themePath));
            if (theme?["dataColors"] is not JsonArray dataColors) return Array.Empty<string>();

            var result = new List<string>();
            foreach (var node in dataColors)
            {
                var hex = NormalizeHex(node?.ToString());
                if (hex.Length > 0 && !result.Contains(hex, StringComparer.OrdinalIgnoreCase))
                {
                    result.Add(hex);
                }
            }

            return result;
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static string? FindThemeFile(string reportFolderPath, string? themeName)
    {
        var resources = Path.Combine(reportFolderPath, "StaticResources");
        if (!Directory.Exists(resources)) return null;

        try
        {
            if (!string.IsNullOrWhiteSpace(themeName))
            {
                var named = Directory
                    .EnumerateFiles(resources, themeName + ".json", SearchOption.AllDirectories)
                    .FirstOrDefault();

                if (named != null) return named;
            }

            // Fallback: any base theme on disk.
            return Directory
                .EnumerateFiles(resources, "*.json", SearchOption.AllDirectories)
                .FirstOrDefault(f => f.Replace('\\', '/')
                    .Contains("/BaseThemes/", StringComparison.OrdinalIgnoreCase));
        }
        catch
        {
            return null;
        }
    }

    private static string NormalizeHex(string? value)
    {
        var hex = (value ?? string.Empty).Trim().Trim('\'', '"');
        if (hex.Length == 0) return string.Empty;
        if (hex[0] != '#') hex = "#" + hex;
        return hex.ToUpperInvariant();
    }
}
