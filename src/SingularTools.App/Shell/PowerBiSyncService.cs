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
        lock (_gate)
        {
            _stopped = true;
            _savePending = false;
            _applyPending = false;
        }
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
                    PowerBiPublisher.TrySaveOpenReportInPowerBi();
                }
            }
            catch (Exception ex)
            {
                App.Log($"Power BI sync failed: {ex.Message}");
            }
        }
    }
}
