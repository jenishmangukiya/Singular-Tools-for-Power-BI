using System;
using System.Collections.Generic;
using System.IO;

namespace SingularTools.Core;

/// <summary>
/// Snapshot-based undo/redo for report page edits. Before the first edit a baseline
/// snapshot of the pages directory is captured; after every edit the resulting state
/// is captured too. Undo/redo simply restores the relevant snapshot, which makes any
/// edit — reorder, rename, hide, duplicate, delete — fully reversible.
/// </summary>
public sealed class ReportEditHistory : IDisposable
{
    private readonly List<string> _snapshots = new();
    private readonly int _limit;
    private readonly string _root;
    private string _pagesDirectory = string.Empty;
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

    /// <summary>Captures the baseline state. Call after loading a report.</summary>
    public void Reset(string pagesDirectory)
    {
        _pagesDirectory = pagesDirectory ?? string.Empty;

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
            Reset(_pagesDirectory);
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
        CopyDirectory(_pagesDirectory, target);
        _snapshots.Add(target);
        _index = _snapshots.Count - 1;

        while (_snapshots.Count > _limit)
        {
            TryDeleteDirectory(_snapshots[0]);
            _snapshots.RemoveAt(0);
            _index--;
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
