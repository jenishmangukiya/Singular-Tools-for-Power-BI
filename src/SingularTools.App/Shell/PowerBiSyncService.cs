using System;
using System.Threading.Tasks;

namespace SingularTools_App.Shell;

/// <summary>
/// Serializes the two directions of Power BI Desktop sync onto a single background
/// thread so they never overlap:
/// <list type="bullet">
/// <item><b>Save</b> — persist Desktop's unsaved edits when Singular Tools regains
/// focus, so every tool reads current data.</item>
/// <item><b>Apply</b> — tell Desktop to reload files Singular Tools just wrote
/// (clicks the "Apply external changes" banner).</item>
/// </list>
/// Requests are queued; a burst collapses into a single follow-up pass. Blocking UI
/// Automation runs off the UI thread and the worker never runs two passes at once.
/// </summary>
internal sealed class PowerBiSyncService
{
    private readonly object _gate = new();
    private bool _savePending;
    private bool _applyPending;
    private bool _running;
    private bool _stopped;
    private TaskCompletionSource<bool>? _saveCompletion;
    private DateTime _lastSaveCompletedUtc = DateTime.MinValue;

    /// <summary>UTC time the most recent save/apply pass finished, even a no-op one.</summary>
    public DateTime LastSaveCompletedUtc
    {
        get
        {
            lock (_gate)
            {
                return _lastSaveCompletedUtc;
            }
        }
    }

    /// <summary>
    /// Queues a save of Desktop's current report. Returns immediately. Repeated
    /// calls while a pass is pending or running collapse into one follow-up pass.
    /// </summary>
    public void RequestSave()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _savePending = true;
            if (_running) return;
            _running = true;
        }

        _ = Task.Run(RunLoop);
    }

    /// <summary>
    /// Queues a save and returns a task whose result is true when Desktop actually
    /// wrote a file (i.e. it had unsaved changes). Used by the startup flow so tools
    /// only render after Desktop's on-disk state is current, and so a no-op save
    /// does not sit through a watcher wait. Coalesces with any in-flight pass.
    /// </summary>
    public Task<bool> RequestSaveAsync()
    {
        TaskCompletionSource<bool> completion;

        lock (_gate)
        {
            if (_stopped)
            {
                return Task.FromResult(false);
            }

            // One awaited save at a time; extra callers share the same completion.
            completion = _saveCompletion ??= new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            _savePending = true;
            if (_running) return completion.Task;
            _running = true;
        }

        _ = Task.Run(RunLoop);
        return completion.Task;
    }

    /// <summary>
    /// Queues an "apply external changes" pass so Desktop reloads what we wrote.
    /// Returns immediately and coalesces the same way as <see cref="RequestSave"/>.
    /// </summary>
    public void RequestApply()
    {
        lock (_gate)
        {
            if (_stopped) return;
            _applyPending = true;
            if (_running) return;
            _running = true;
        }

        _ = Task.Run(RunLoop);
    }

    /// <summary>Stops accepting new requests; an in-flight pass is allowed to finish.</summary>
    public void Stop()
    {
        TaskCompletionSource<bool>? completion;

        lock (_gate)
        {
            _stopped = true;
            _savePending = false;
            _applyPending = false;
            completion = _saveCompletion;
            _saveCompletion = null;
        }

        // Never leave a startup await hanging on shutdown.
        completion?.TrySetResult(false);
    }

    private void RunLoop()
    {
        while (true)
        {
            bool doSave;
            bool doApply;

            lock (_gate)
            {
                if (_stopped || (!_savePending && !_applyPending))
                {
                    _running = false;
                    return;
                }

                doSave = _savePending;
                doApply = _applyPending;
                _savePending = false;
                _applyPending = false;
            }

            var savedSomething = false;
            try
            {
                if (doApply)
                {
                    // An edit was applied to the report folder: the disk is now the
                    // source of truth, so make Desktop reload it. Skipping a queued
                    // save here is deliberate — saving Desktop's older in-memory
                    // state would clobber the edit we just wrote.
                    PowerBiPublisher.TryApplyExternalChangesInPowerBi();
                }
                else if (doSave)
                {
                    // The author may have edited Desktop without saving. Flush it so
                    // the tools read current data; the file watcher then reloads us.
                    // TrySave returns false when there was nothing to save.
                    savedSomething = PowerBiPublisher.TrySaveOpenReportInPowerBi();
                }
            }
            catch (Exception ex)
            {
                App.Log($"Power BI sync failed: {ex.Message}");
            }
            finally
            {
                // Release anyone awaiting this save. An "apply" pass counts as "done":
                // a save queued behind it would otherwise never report.
                if (doSave || doApply)
                {
                    CompleteSave(savedSomething || doApply);
                    lock (_gate)
                    {
                        _lastSaveCompletedUtc = DateTime.UtcNow;
                    }
                }
            }
        }
    }

    /// <summary>Completes the pending awaited save (if any) and arms a fresh one.</summary>
    private void CompleteSave(bool wroteFile)
    {
        TaskCompletionSource<bool>? completion;
        lock (_gate)
        {
            completion = _saveCompletion;
            _saveCompletion = null;
        }

        completion?.TrySetResult(wroteFile);
    }
}
