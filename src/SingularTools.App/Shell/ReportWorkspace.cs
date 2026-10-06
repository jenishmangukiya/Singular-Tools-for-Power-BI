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
    private readonly ReportFileWatcher _modelWatcher;
    private readonly PowerBiSyncService _powerBiSync = new();
    private string _signature = string.Empty;
    private string _modelSignature = string.Empty;
    private string _modelFolder = string.Empty;
    private DispatcherQueueTimer? _retryTimer;
    private int _retryCount;

    public ReportWorkspace()
    {
        _watcher = new ReportFileWatcher(OnExternalChange);
        _modelWatcher = new ReportFileWatcher(OnModelExternalChange, "*.tmdl");
    }

    public ReportManager Manager { get; } = new();

    public ReportEditHistory? History => _history;

    public bool HasReport => !string.IsNullOrEmpty(Manager.ReportFolderPath);

    public string ReportName => HasReport ? Path.GetFileName(Manager.ReportFolderPath) : string.Empty;

    public string ReportPath => Manager.ReportFolderPath;

    /// <summary>
    /// True when a sibling semantic model (<c>.SemanticModel</c> holding TMDL tables)
    /// was found for the open report. Resolved once when the report is opened or
    /// reloaded, so the Home launcher can show model-dependent tools' state without
    /// touching disk on every refresh.
    /// </summary>
    public bool HasSemanticModel => !string.IsNullOrEmpty(_modelFolder);

    /// <summary>Raised whenever the active report is opened, reloaded or edited.</summary>
    public event EventHandler? Changed;

    /// <summary>Raised when an external change was detected and pulled into the session.</summary>
    public event EventHandler? ExternalChangeDetected;

    /// <summary>
    /// Raised when the semantic model's TMDL files changed on disk (typically a save
    /// from Power BI Desktop, or a model edit by another tool). Unlike
    /// <see cref="Changed"/>, this does not touch page state or undo history.
    /// </summary>
    public event EventHandler? ModelChanged;

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
        WatchModel();
        RaiseChanged();
        return true;
    }

    /// <summary>Re-reads the current report from disk and restarts undo history.</summary>
    public void Reload()
    {
        Manager.Reload();
        ResetHistory();
        _signature = ComputeSignature();
        WatchModel();
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
    /// Steps the shared history back one edit and restores it. Returns false when there is
    /// nothing to undo.
    /// </summary>
    /// <remarks>
    /// Tools must call this rather than <c>ApplyEdit(m =&gt; m.RestoreFromSnapshot(...))</c>.
    /// ApplyEdit commits the result as a new edit, and committing after an undo is precisely what
    /// discards the redo branch — so routing an undo through it leaves Redo permanently disabled.
    /// Pairing the history step with the restore here is what makes that mistake impossible.
    /// </remarks>
    public bool Undo()
    {
        var snapshot = _history?.Undo();
        if (string.IsNullOrEmpty(snapshot)) return false;

        RestoreSnapshot(snapshot);
        return true;
    }

    /// <summary>Steps the shared history forward one edit and restores it. Returns false when
    /// there is nothing to redo.</summary>
    /// <inheritdoc cref="Undo"/>
    public bool Redo()
    {
        var snapshot = _history?.Redo();
        if (string.IsNullOrEmpty(snapshot)) return false;

        RestoreSnapshot(snapshot);
        return true;
    }

    /// <summary>
    /// Restores a history snapshot without recording it as a new edit.
    /// </summary>
    /// <remarks>
    /// Deliberately not routed through <see cref="ApplyEditCore"/>: that calls
    /// <c>Commit()</c>, and a commit is "a new edit happened from here", which truncates the redo
    /// branch. Restoring a snapshot *is* the undo, so it must leave the history index alone.
    ///
    /// The signature is recomputed because the restore rewrites files on disk; without that the
    /// watcher would read our own write as an external change, reload, and reset history — losing
    /// the very undo/redo the author is using.
    /// </remarks>
    private void RestoreSnapshot(string snapshot)
    {
        Manager.RestoreFromSnapshot(snapshot);

        _signature = ComputeSignature();
        RaiseChanged();

        // Power BI is showing the pre-undo report, so let it pick the restore up like any edit.
        if (HasReport)
        {
            _powerBiSync.RequestApply();
        }
    }

    /// <summary>
    /// Asks Power BI Desktop to save its open report so the author's unsaved edits
    /// reach the .pbip project folder. Called when the app regains focus: the tools
    /// read that folder, so without this they would show stale data. Coalesced and
    /// off-thread; a save that lands triggers the file watcher to reload us.
    /// </summary>
    public void RequestPowerBiSave()
    {
        if (HasReport)
        {
            _powerBiSync.RequestSave();
        }
    }

    /// <summary>UTC time any Power BI sync pass (save or apply) last finished.</summary>
    public DateTime LastPowerBiSyncUtc => _powerBiSync.LastSaveCompletedUtc;

    /// <summary>
    /// Asks Power BI Desktop to save and returns a task whose result is true when a
    /// file was actually written, so a caller can gate on Desktop's on-disk state
    /// being current (used by the startup flow). False when no report is open.
    /// </summary>
    public Task<bool> RequestPowerBiSaveAsync()
    {
        return HasReport ? _powerBiSync.RequestSaveAsync() : Task.FromResult(false);
    }

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

        // Every user-facing metadata write funnels through here, so let Power BI
        // Desktop pick the change up instead of leaving its "files changed
        // externally" banner for the author to click. Coalesced and off-thread, so
        // it never blocks the edit or the UI.
        if (HasReport)
        {
            _powerBiSync.RequestApply();
        }

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
        _powerBiSync.Stop();
        _watcher.Dispose();
        _modelWatcher.Dispose();
        _retryTimer?.Stop();
        _history?.Dispose();
        _history = null;
    }

    /// <summary>
    /// Marks the on-disk semantic model files as known, so a write this app just
    /// made does not come back as a spurious "model changed in Power BI" event.
    /// Model-editing tools call this immediately after they save.
    /// </summary>
    public void NotifyModelFilesChanged()
    {
        if (!string.IsNullOrEmpty(_modelFolder) && Directory.Exists(_modelFolder))
        {
            _modelSignature = ComputeModelSignature(_modelFolder);
        }
    }

    /// <summary>Starts watching the sibling semantic model (if any) for TMDL changes.</summary>
    private void WatchModel()
    {
        _modelFolder = HasReport
            ? SortByColumnService.DiscoverModelFolder(Manager.ReportFolderPath) ?? string.Empty
            : string.Empty;
        _modelSignature = string.Empty;

        if (string.IsNullOrEmpty(_modelFolder))
        {
            return;
        }

        _modelWatcher.Watch(Path.Combine(_modelFolder, "definition"));
        _modelSignature = ComputeModelSignature(_modelFolder);
    }

    private void OnModelExternalChange()
    {
        if (string.IsNullOrEmpty(_modelFolder) || !Directory.Exists(_modelFolder))
        {
            return;
        }

        var current = ComputeModelSignature(_modelFolder);
        if (string.Equals(current, _modelSignature, StringComparison.Ordinal))
        {
            return;
        }

        _modelSignature = current;
        App.Log("External semantic model change detected.");
        ModelChanged?.Invoke(this, EventArgs.Empty);
    }

    private static string ComputeModelSignature(string modelFolder)
    {
        try
        {
            var definition = Path.Combine(modelFolder, "definition");
            if (!Directory.Exists(definition))
            {
                return string.Empty;
            }

            var builder = new StringBuilder();
            foreach (var file in Directory.EnumerateFiles(definition, "*.tmdl", SearchOption.AllDirectories)
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
            App.Log($"Model signature compute failed: {ex.Message}");
            return string.Empty;
        }
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
        _history.Reset(Manager.PagesDirectoryPath, OutsidePagesEditTargets());
    }

    /// <summary>
    /// Files a tool may edit that live outside the pages directory, so undo can restore them.
    /// </summary>
    /// <remarks>
    /// Report-level filters sit in <c>definition/report.json</c>. Without nominating it here, a
    /// Field Repair repair of a report filter would survive an Undo and leave the report
    /// half-reverted: visuals back to the old field, the filter still pointing at the new one.
    /// </remarks>
    private IEnumerable<string> OutsidePagesEditTargets()
    {
        if (!HasReport) yield break;

        yield return Path.Combine(Manager.ReportFolderPath, "definition", "report.json");
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
