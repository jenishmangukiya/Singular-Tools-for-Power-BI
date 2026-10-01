using System;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.UI.Dispatching;
using SingularTools.Core;

namespace SingularTools_App.Shell;

/// <summary>
/// Single, app-wide report session shared by every tool page. Owns the active
/// <see cref="ReportManager"/> and its snapshot-based undo/redo history so that
/// opening a report from one page is immediately reflected everywhere.
///
/// It also watches the report folder so external changes (a save from Power BI
/// Desktop) are pulled back in, and guards writes so a stale in-memory state
/// never silently overwrites those external changes.
/// </summary>
public sealed class ReportWorkspace : IDisposable
{
    private const int MaxSyncRetries = 3;

    private ReportEditHistory? _history;
    private readonly ReportFileWatcher _watcher;
    private string _signature = string.Empty;
    private DispatcherQueueTimer? _retryTimer;
    private int _retryCount;

    public ReportWorkspace()
    {
        _watcher = new ReportFileWatcher(OnExternalChange);
    }

    public ReportManager Manager { get; } = new();

    public ReportEditHistory? History => _history;

    public bool HasReport => !string.IsNullOrEmpty(Manager.ReportFolderPath);

    public string ReportName => HasReport ? Path.GetFileName(Manager.ReportFolderPath) : string.Empty;

    public string ReportPath => Manager.ReportFolderPath;

    /// <summary>Raised whenever the active report is opened, reloaded or edited.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when an external change was detected and pulled into the session.</summary>
    public event EventHandler? ExternalChangeDetected;

    /// <summary>Loads a report folder and starts a fresh undo history. Returns false for an invalid folder.</summary>
    public bool OpenReport(string folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath) || !Manager.LoadReport(folderPath))
        {
            return false;
        }

        ResetHistory();
        _signature = ComputeSignature();
        _watcher.Watch(Path.Combine(Manager.ReportFolderPath, "definition"));
        RaiseChanged();
        return true;
    }

    /// <summary>Re-reads the current report from disk and restarts undo history.</summary>
    public void Reload()
    {
        Manager.Reload();
        ResetHistory();
        _signature = ComputeSignature();
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

    /// <summary>
    /// Applies an in-memory edit, saving it to disk unless the manager already
    /// writes its own files. If the report changed on disk since the last sync,
    /// it is reloaded first so the edit does not clobber the external change.
    /// </summary>
    public void ApplyEdit(Action<ReportManager> edit, bool managerWritesInternally = false, bool syncFirst = true)
    {
        ApplyEditCore(m =>
        {
            edit(m);
            return true;
        }, managerWritesInternally, syncFirst);
    }

    /// <inheritdoc cref="ApplyEdit(Action{ReportManager}, bool, bool)"/>
    public T ApplyEditWithResult<T>(Func<ReportManager, T> edit, bool managerWritesInternally = false, bool syncFirst = true)
        => ApplyEditCore(edit, managerWritesInternally, syncFirst);

    /// <summary>
    /// Applies an in-memory edit and saves it, but deliberately skips the undo
    /// history commit. Use for temporary changes that are reverted within the
    /// same operation (for example swapping page visibility around a publish),
    /// so they never show up as two noisy undo steps.
    /// </summary>
    public void ApplyTransientEdit(Action<ReportManager> edit)
    {
        if (SyncIfStale())
        {
            ToastService.Show("Report changed in Power BI Desktop — reloaded before applying your change.", ToastSeverity.Informational);
        }

        edit(Manager);
        Manager.SaveChanges();

        _signature = ComputeSignature();
        RaiseChanged();
    }

    private T ApplyEditCore<T>(Func<ReportManager, T> edit, bool managerWritesInternally, bool syncFirst)
    {
        if (syncFirst && SyncIfStale())
        {
            ToastService.Show("Report changed in Power BI Desktop — reloaded before applying your change.", ToastSeverity.Informational);
        }

        var result = edit(Manager);

        if (!managerWritesInternally)
        {
            Manager.SaveChanges();
        }

        _signature = ComputeSignature();
        Commit();
        RaiseChanged();
        return result;
    }

    /// <summary>
    /// Reloads the report if the on-disk definition differs from the last synced
    /// state. Returns true when a reload happened (and undo history was reset).
    /// </summary>
    public bool SyncIfStale()
    {
        if (!HasReport)
        {
            return false;
        }

        var current = ComputeSignature();
        if (string.Equals(current, _signature, StringComparison.Ordinal))
        {
            return false;
        }

        return ReloadFromDisk(raiseExternalEvent: false);
    }

    public void RaiseChanged() => Changed?.Invoke(this, EventArgs.Empty);

    public void Dispose()
    {
        _watcher.Dispose();
        _retryTimer?.Stop();
        _history?.Dispose();
        _history = null;
    }

    private void OnExternalChange()
    {
        if (!HasReport)
        {
            return;
        }

        // If nothing actually changed, this was our own write — ignore it.
        var current = ComputeSignature();
        if (string.Equals(current, _signature, StringComparison.Ordinal))
        {
            return;
        }

        ReloadFromDisk(raiseExternalEvent: true);
    }

    private bool ReloadFromDisk(bool raiseExternalEvent)
    {
        try
        {
            Manager.Reload();
            ResetHistory();
            _signature = ComputeSignature();
            _retryCount = 0;
            RaiseChanged();

            if (raiseExternalEvent)
            {
                App.Log($"External report change reloaded ({Manager.Pages.Count} pages).");
                ExternalChangeDetected?.Invoke(this, EventArgs.Empty);
            }

            return true;
        }
        catch (Exception ex)
        {
            // The report may be mid-write (e.g. pages.json briefly missing).
            App.Log($"Report sync failed: {ex.Message}");
            ScheduleRetry();
            return false;
        }
    }

    private void ScheduleRetry()
    {
        if (_retryCount >= MaxSyncRetries) return;
        _retryCount++;

        var dispatcher = App.CurrentMainWindow?.DispatcherQueue;
        if (dispatcher == null) return;

        if (_retryTimer == null)
        {
            _retryTimer = dispatcher.CreateTimer();
            _retryTimer.IsRepeating = false;
            _retryTimer.Tick += OnRetryTick;
        }

        _retryTimer.Stop();
        _retryTimer.Interval = TimeSpan.FromMilliseconds(500 * _retryCount);
        _retryTimer.Start();
    }

    private void OnRetryTick(object? sender, object e)
    {
        _retryTimer?.Stop();
        OnExternalChange();
    }

    private void ResetHistory()
    {
        _history?.Dispose();
        _history = new ReportEditHistory();
        _history.Reset(Manager.PagesDirectoryPath);
    }

    private string ComputeSignature()
    {
        try
        {
            var pagesDir = Manager.PagesDirectoryPath;
            if (string.IsNullOrEmpty(pagesDir) || !Directory.Exists(pagesDir))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(pagesDir, "*", SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                builder.Append(info.FullName).Append('|')
                       .Append(info.LastWriteTimeUtc.Ticks).Append('|')
                       .Append(info.Length).Append('\n');
            }

            return builder.ToString();
        }
        catch (Exception ex)
        {
            App.Log($"Signature compute failed: {ex.Message}");
            return _signature;
        }
    }
}
