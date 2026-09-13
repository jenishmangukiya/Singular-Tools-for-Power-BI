using System;
using System.IO;
using SingularTools.Core;

namespace SingularTools_App.Shell;

/// <summary>
/// Single, app-wide report session shared by every tool page. Owns the active
/// <see cref="ReportManager"/> and its snapshot-based undo/redo history so that
/// opening a report from one page is immediately reflected everywhere.
/// </summary>
public sealed class ReportWorkspace : IDisposable
{
    private ReportEditHistory? _history;

    public ReportManager Manager { get; } = new();

    public ReportEditHistory? History => _history;

    public bool HasReport => !string.IsNullOrEmpty(Manager.ReportFolderPath);

    public string ReportName => HasReport ? Path.GetFileName(Manager.ReportFolderPath) : string.Empty;

    public string ReportPath => Manager.ReportFolderPath;

    /// <summary>Raised whenever the active report is opened, reloaded or edited.</summary>
    public event EventHandler? Changed;

    /// <summary>Loads a report folder and starts a fresh undo history. Returns false for an invalid folder.</summary>
    public bool OpenReport(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Manager.LoadReport(folderPath))
        {
            return false;
        }

        ResetHistory();
        RaiseChanged();
        return true;
    }

    /// <summary>Re-reads the current report from disk and restarts undo history.</summary>
    public void Reload()
    {
        Manager.Reload();
        ResetHistory();
        RaiseChanged();
    }

    /// <summary>Records the current state in undo history after an edit.</summary>
    public void Commit()
    {
        try
        {
            _history?.Commit();
        }
        catch (Exception ex)
        {
            App.Log($"History commit failed: {ex.Message}");
        }
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _history?.Dispose();
        _history = null;
    }

    private void ResetHistory()
    {
        _history?.Dispose();
        _history = new ReportEditHistory();
        _history.Reset(Manager.PagesDirectoryPath);
    }
}
