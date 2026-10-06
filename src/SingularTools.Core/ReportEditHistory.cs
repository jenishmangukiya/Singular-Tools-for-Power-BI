using System;
using System.Collections.Generic;
using System.IO;

namespace SingularTools.Core;

/// <summary>
/// Snapshot-based undo/redo for report edits. Before the first edit a baseline snapshot is
/// captured; after every edit the resulting state is captured too. Undo/redo simply restores
/// the relevant snapshot, which makes any edit — reorder, rename, hide, duplicate, delete —
/// fully reversible.
/// </summary>
/// <remarks>
/// The snapshot covers the pages directory plus any extra files the caller nominates. Report-level
/// filters live in <c>definition/report.json</c>, outside the pages directory, and are edited by
/// the Field Repair tool; without including them here a repair would survive an Undo and leave
/// the report half-reverted.
///
/// Layout inside each snapshot: the pages directory under <c>pages/</c>, and each extra file under
/// <c>extras/</c> at its path relative to the report root. A snapshot written before this layout
/// existed has the pages contents at its root, which <see cref="ReportManager.RestoreFromSnapshot"/>
/// still understands.
/// </remarks>
public sealed class ReportEditHistory : IDisposable
{
    /// <summary>Sub-folder holding the pages directory copy inside a snapshot.</summary>
    internal const string PagesFolderName = "pages";

    /// <summary>Sub-folder holding nominated outside-the-pages files inside a snapshot.</summary>
    internal const string ExtrasFolderName = "extras";

    private readonly List<string> _snapshots = new();
    private readonly int _limit;
    private readonly string _root;
    private string _pagesDirectory = string.Empty;
    private string _reportRoot = string.Empty;
    private List<string> _extraFiles = new();
    private int _index = -1;

    public ReportEditHistory(int limit = 30)
    {
        _limit = Math.Max(2, limit);
        _root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "SingularTools",
            "history",
            Guid.NewGuid().ToString("N"));

        TryDeleteDirectory(_root);
    }

    public bool CanUndo => _index > 0;

    public bool CanRedo => _index >= 0 && _index < _snapshots.Count - 1;

    public int UndoCount => Math.Max(0, _index);

    public int RedoCount => Math.Max(0, _snapshots.Count - 1 - _index);

    /// <summary>
    /// Captures the baseline state. Call after loading a report.
    /// </summary>
    /// <param name="pagesDirectory">The report's <c>definition/pages</c> folder.</param>
    /// <param name="extraFiles">
    /// Files outside the pages directory that edits may touch, so undo can restore them too.
    /// Paths are stored relative to the report root derived from <paramref name="pagesDirectory"/>.
    /// </param>
    public void Reset(string pagesDirectory, IEnumerable<string>? extraFiles = null)
    {
        _pagesDirectory = pagesDirectory ?? string.Empty;
        _reportRoot = DeriveReportRoot(_pagesDirectory);
        _extraFiles = (extraFiles ?? Enumerable.Empty<string>())
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        foreach (var snapshot in _snapshots)
        {
            TryDeleteDirectory(snapshot);
        }
        _snapshots.Clear();
        _index = -1;

        if (!Directory.Exists(_pagesDirectory))
        {
            return;
        }

        AddSnapshot();
    }

    /// <summary>Captures the current state after an edit, discarding any redo branch.</summary>
    public void Commit()
    {
        if (!Directory.Exists(_pagesDirectory))
        {
            return;
        }

        if (_index < 0)
        {
            // Re-baseline with the same extras, or the report-level files would silently fall
            // out of undo coverage from the first commit onwards.
            Reset(_pagesDirectory, _extraFiles);
            return;
        }

        // Drop the redo branch that is no longer reachable.
        for (int i = _snapshots.Count - 1; i > _index; i--)
        {
            TryDeleteDirectory(_snapshots[i]);
            _snapshots.RemoveAt(i);
        }

        AddSnapshot();
    }

    /// <summary>Moves back one step and returns the snapshot directory to restore, if any.</summary>
    public string? Undo()
    {
        if (!CanUndo)
        {
            return null;
        }

        _index--;
        return _snapshots[_index];
    }

    /// <summary>Moves forward one step and returns the snapshot directory to restore, if any.</summary>
    public string? Redo()
    {
        if (!CanRedo)
        {
            return null;
        }

        _index++;
        return _snapshots[_index];
    }

    public void Dispose()
    {
        foreach (var snapshot in _snapshots)
        {
            TryDeleteDirectory(snapshot);
        }
        _snapshots.Clear();
        _index = -1;
    }

    private void AddSnapshot()
    {
        var target = Path.Combine(_root, _snapshots.Count.ToString("D4"));
        TryDeleteDirectory(target);

        // Pages first, then any nominated outside-the-pages files at their report-relative path.
        CopyDirectory(_pagesDirectory, Path.Combine(target, PagesFolderName));
        CopyExtras(target);

        _snapshots.Add(target);
        _index = _snapshots.Count - 1;

        while (_snapshots.Count > _limit)
        {
            TryDeleteDirectory(_snapshots[0]);
            _snapshots.RemoveAt(0);
            _index--;
        }
    }

    /// <summary>Copies each nominated file into <c>extras/</c>, keeping its report-relative path.</summary>
    private void CopyExtras(string snapshotRoot)
    {
        if (_extraFiles.Count == 0 || string.IsNullOrEmpty(_reportRoot)) return;

        foreach (var file in _extraFiles)
        {
            if (!File.Exists(file)) continue;

            var relative = Path.GetRelativePath(_reportRoot, file);
            if (relative.StartsWith("..", StringComparison.Ordinal)) continue;

            var destination = Path.Combine(snapshotRoot, ExtrasFolderName, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(file, destination, overwrite: true);
        }
    }

    /// <summary>
    /// Derives the report folder from <c>&lt;report&gt;/definition/pages</c>. Returns an empty
    /// string when the path does not have that shape, which disables extras rather than
    /// guessing a root and copying files to the wrong place.
    /// </summary>
    private static string DeriveReportRoot(string pagesDirectory)
    {
        if (string.IsNullOrWhiteSpace(pagesDirectory)) return string.Empty;

        try
        {
            var definition = Directory.GetParent(pagesDirectory);
            var reportRoot = definition?.Parent;
            return reportRoot?.FullName ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            File.Copy(file, Path.Combine(destinationDir, Path.GetFileName(file)), overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            CopyDirectory(subDir, Path.Combine(destinationDir, Path.GetFileName(subDir)));
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
        }
    }
}
