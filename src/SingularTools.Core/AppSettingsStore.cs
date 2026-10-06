using System;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SingularTools.Core;

/// <summary>
/// Reads and writes the machine-level <see cref="AppSettings"/>
/// (<c>settings.json</c>) beside the workspace cache.
///
/// The file holds preferences that belong to this install rather than to any one
/// report, so it lives under <c>%LOCALAPPDATA%\SingularTools</c>. Writes are
/// atomic, and skipped when nothing changed. A corrupt or missing file must never
/// stop the app starting, so loading always falls back to defaults.
/// </summary>
public static class AppSettingsStore
{
    public const string FileName = "settings.json";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Full path of the settings file for the current machine.</summary>
    public static string ResolveSettingsPath() =>
        Path.Combine(WorkspaceCacheStore.ResolveDataFolder(), FileName);

    /// <summary>Loads the settings, or defaults when missing / unreadable.</summary>
    public static AppSettings Load(string? path = null)
    {
        var settingsPath = path ?? ResolveSettingsPath();

        try
        {
            if (!File.Exists(settingsPath)) return AppSettings.Empty();
            return FromJson(File.ReadAllText(settingsPath));
        }
        catch
        {
            return AppSettings.Empty();
        }
    }

    /// <summary>Parses a settings document. Never throws; returns defaults on bad input.</summary>
    public static AppSettings FromJson(string? json)
    {
        var settings = AppSettings.Empty();
        if (string.IsNullOrWhiteSpace(json)) return settings;

        try
        {
            if (JsonNode.Parse(json) is not JsonObject root) return settings;

            var theme = root["theme"]?.ToString();
            if (!string.IsNullOrWhiteSpace(theme)) settings.Theme = ParseTheme(theme);
        }
        catch
        {
            return AppSettings.Empty();
        }

        return settings;
    }

    /// <summary>Atomically writes the settings. Skips the write when nothing changed.</summary>
    public static bool Save(AppSettings settings, string? path = null)
    {
        if (settings == null) return false;

        var settingsPath = path ?? ResolveSettingsPath();

        try
        {
            var json = ToJson(settings);

            if (File.Exists(settingsPath)
                && string.Equals(File.ReadAllText(settingsPath), json, StringComparison.Ordinal))
            {
                return true;
            }

            var directory = Path.GetDirectoryName(settingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = settingsPath + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, settingsPath, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    public static string ToJson(AppSettings settings)
    {
        var root = new JsonObject
        {
            ["theme"] = ThemeToString(settings.Theme)
        };

        return root.ToJsonString(WriteOptions);
    }

    /// <summary>Maps a stored string to a theme; anything unrecognised falls back to System.</summary>
    public static AppTheme ParseTheme(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "light" => AppTheme.Light,
        "dark" => AppTheme.Dark,
        _ => AppTheme.System
    };

    /// <summary>Serializes a theme to its stable stored string.</summary>
    public static string ThemeToString(AppTheme theme) => theme switch
    {
        AppTheme.Light => "light",
        AppTheme.Dark => "dark",
        _ => "system"
    };
}
