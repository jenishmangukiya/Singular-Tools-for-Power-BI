using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SingularTools.Core;

/// <summary>
/// Project-keyed snapshots of semantic model files, so a model edit can be
/// reverted as a whole. Every model-editing tool (Sort by Column, Object
/// Security) funnels its overwrites through here.
///
/// A backup folder holds a <c>manifest.json</c> plus one copy per touched file;
/// the most recent folder is always the undo target. Snapshots live under
/// <c>%LOCALAPPDATA%\SingularPowerTools\backups</c> and the oldest are pruned.
/// </summary>
public static class ModelBackupStore
{
    private const int MaxBackups = 10;

    public static string BackupRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SingularPowerTools",
        "backups");

    /// <summary>The backup scope used by a model-editing tool.</summary>
    public const string DefaultScope = "default";

    /// <summary>A stable, filesystem-safe key for a project's backup folder.</summary>
    public static string ProjectKey(string projectRoot)
    {
        var full = Path.GetFullPath(projectRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var name = Path.GetFileName(full);
        var safe = new string(name.Select(ch => char.IsLetterOrDigit(ch) || ch is '-' or '_' or '.' ? ch : '_').ToArray());
        if (safe.Length == 0) safe = "project";

        var hash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8];
        return $"{safe}-{hash}";
    }

    public static string BackupFolderFor(string projectRoot, string scope = DefaultScope)
        => Path.Combine(BackupRoot, ProjectKey(projectRoot), scope);

    public static string? LatestBackupFolder(string projectRoot, string scope = DefaultScope)
    {
        var folder = BackupFolderFor(projectRoot, scope);
        if (!Directory.Exists(folder)) return null;

        return Directory.GetDirectories(folder)
                        .Where(d => File.Exists(Path.Combine(d, "manifest.json")))
                        .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                        .LastOrDefault();
    }

    /// <summary>
    /// Creates a timestamped snapshot folder for a project, writes its manifest and
    /// prunes older snapshots. The caller then records each file with
    /// <see cref="BackupFile"/> before overwriting it.
    /// </summary>
    public static string CreateBackupFolder(string projectRoot, string modelFolder, string scope = DefaultScope)
    {
        var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff");
        var dir = Path.Combine(BackupFolderFor(projectRoot, scope), stamp);
        Directory.CreateDirectory(dir);

        var manifest = new JsonObject
        {
            ["createdUtc"] = DateTime.UtcNow.ToString("o"),
            ["projectRoot"] = projectRoot,
            ["modelFolder"] = modelFolder,
            ["files"] = new JsonArray()
        };

        File.WriteAllText(Path.Combine(dir, "manifest.json"), manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        PruneBackups(projectRoot, scope);
        return dir;
    }

    /// <summary>Copies a file into the snapshot and records its original path.</summary>
    public static void BackupFile(string backupFolder, string file)
    {
        var manifestPath = Path.Combine(backupFolder, "manifest.json");
        if (File.Exists(manifestPath) && JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject manifest &&
            manifest["files"] is JsonArray files)
        {
            var stored = Path.GetFileName(file);
            var index = 0;
            while (files.Any(n => string.Equals(n?["fileName"]?.ToString(), stored, StringComparison.OrdinalIgnoreCase)))
            {
                stored = $"{index++:D2}_{Path.GetFileName(file)}";
            }

            File.Copy(file, Path.Combine(backupFolder, stored), overwrite: true);
            files.Add(new JsonObject { ["original"] = file, ["fileName"] = stored });
            File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        }
    }

    /// <summary>Records a file that is about to be deleted rather than overwritten.</summary>
    public static void BackupFileForDeletion(string backupFolder, string file) => BackupFile(backupFolder, file);

    /// <summary>
    /// Records a file this apply creates (and therefore has no previous contents to
    /// restore). Undo deletes it, so creating a role is fully reversible.
    /// </summary>
    public static void RecordCreatedFile(string backupFolder, string file)
    {
        var manifestPath = Path.Combine(backupFolder, "manifest.json");
        if (!File.Exists(manifestPath)) return;

        try
        {
            if (JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject manifest)
            {
                if (manifest["created"] is not JsonArray created)
                {
                    created = new JsonArray();
                    manifest["created"] = created;
                }

                created.Add(file);
                File.WriteAllText(manifestPath, manifest.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            }
        }
        catch
        {
            // A manifest we cannot extend just means this one undo cannot delete.
        }
    }

    /// <summary>Restores the files captured by the most recent apply. Returns how many were written.</summary>
    public static int RestoreLatestBackup(string projectRoot, out string? restoredFrom, string scope = DefaultScope)
    {
        restoredFrom = null;
        var folder = LatestBackupFolder(projectRoot, scope);
        if (folder == null) return 0;

        var manifestPath = Path.Combine(folder, "manifest.json");
        if (!File.Exists(manifestPath)) return 0;

        var restored = 0;
        try
        {
            if (JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject manifest &&
                manifest["files"] is JsonArray files)
            {
                foreach (var entry in files)
                {
                    if (entry is not JsonObject node) continue;

                    var original = node["original"]?.ToString();
                    var stored = node["fileName"]?.ToString();
                    if (string.IsNullOrEmpty(original) || string.IsNullOrEmpty(stored)) continue;

                    var source = Path.Combine(folder, stored);
                    if (!File.Exists(source)) continue;

                    var dir = Path.GetDirectoryName(original);
                    if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

                    File.Copy(source, original, overwrite: true);
                    restored++;
                }
            }

            if (restored > 0 || HasCreatedFiles(manifestPath))
            {
                // Remove files this snapshot created so an undo of "create role"
                // does not leave an orphan behind.
                DeleteCreatedFiles(manifestPath);

                restoredFrom = folder;
                Directory.Delete(folder, recursive: true);
                RemoveIfEmpty(Path.GetDirectoryName(folder));
            }
        }
        catch
        {
            return restored;
        }

        return restored;
    }

    private static bool HasCreatedFiles(string manifestPath)
    {
        try
        {
            return File.Exists(manifestPath)
                   && System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath)) is JsonObject manifest
                   && manifest["created"] is JsonArray created
                   && created.Count > 0;
        }
        catch
        {
            return false;
        }
    }

    private static void DeleteCreatedFiles(string manifestPath)
    {
        try
        {
            if (System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath)) is not JsonObject manifest ||
                manifest["created"] is not JsonArray created)
            {
                return;
            }

            foreach (var entry in created)
            {
                var path = entry?.ToString();
                if (string.IsNullOrEmpty(path)) continue;
                try { if (File.Exists(path)) File.Delete(path); } catch { }
            }
        }
        catch
        {
        }
    }

    private static void PruneBackups(string projectRoot, string scope)
    {
        try
        {
            var folder = BackupFolderFor(projectRoot, scope);
            if (!Directory.Exists(folder)) return;

            var all = Directory.GetDirectories(folder)
                               .OrderBy(d => Path.GetFileName(d), StringComparer.Ordinal)
                               .ToList();

            while (all.Count > MaxBackups)
            {
                try { Directory.Delete(all[0], recursive: true); } catch { }
                all.RemoveAt(0);
            }
        }
        catch
        {
        }
    }

    /// <summary>Removes a backup key folder once its last snapshot is gone.</summary>
    private static void RemoveIfEmpty(string? folder)
    {
        try
        {
            if (!string.IsNullOrEmpty(folder) && Directory.Exists(folder) &&
                !Directory.EnumerateFileSystemEntries(folder).Any())
            {
                Directory.Delete(folder, recursive: false);
            }
        }
        catch
        {
        }
    }
}
