using System;
using System.IO;

namespace SingularTools.Core;

/// <summary>
/// Reads and writes <see cref="ReportConfig"/> (<c>singular-tools.json</c>) for a
/// report. The file lives at the PBIP project root — the folder holding the
/// <c>.pbip</c> — and falls back to the report folder root for standalone reports.
/// Writes are atomic and skipped when the content is unchanged.
/// </summary>
public static class ReportConfigStore
{
    /// <summary>The folder that holds <c>singular-tools.json</c> for the given report folder.</summary>
    public static string ResolveProjectRoot(string reportFolderPath)
    {
        if (string.IsNullOrWhiteSpace(reportFolderPath)) return string.Empty;

        var full = Path.GetFullPath(reportFolderPath);
        var name = Path.GetFileName(full);

        if (name.EndsWith(".Report", StringComparison.OrdinalIgnoreCase))
        {
            var parent = Directory.GetParent(full)?.FullName;
            if (!string.IsNullOrEmpty(parent)) return parent;
        }

        return full;
    }

    public static string ResolveConfigPath(string reportFolderPath)
    {
        var root = ResolveProjectRoot(reportFolderPath);
        return string.IsNullOrEmpty(root) ? string.Empty : Path.Combine(root, ReportConfig.FileName);
    }

    /// <summary>Loads the config, or an empty one when none exists / it cannot be read.</summary>
    public static ReportConfig Load(string reportFolderPath)
    {
        if (string.IsNullOrWhiteSpace(reportFolderPath)) return ReportConfig.Empty();

        try
        {
            var path = ResolveConfigPath(reportFolderPath);
            if (string.IsNullOrEmpty(path) || !File.Exists(path)) return ReportConfig.Empty();

            return ReportConfig.FromJson(File.ReadAllText(path));
        }
        catch
        {
            return ReportConfig.Empty();
        }
    }

    /// <summary>Atomically writes the config. Returns false when it could not be written.</summary>
    public static bool Save(string reportFolderPath, ReportConfig config)
    {
        if (string.IsNullOrWhiteSpace(reportFolderPath) || config == null) return false;

        try
        {
            var path = ResolveConfigPath(reportFolderPath);
            if (string.IsNullOrEmpty(path)) return false;

            var json = config.ToJson();

            if (File.Exists(path) && string.Equals(File.ReadAllText(path), json, StringComparison.Ordinal))
            {
                return true;
            }

            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var temp = path + ".tmp";
            File.WriteAllText(temp, json);
            File.Move(temp, path, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
