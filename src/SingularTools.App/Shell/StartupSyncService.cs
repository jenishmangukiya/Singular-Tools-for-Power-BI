using System;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;
using SingularTools.Core;

namespace SingularTools_App.Shell;

/// <summary>What the startup sync decided to do, so the caller can react.</summary>
internal sealed class StartupSyncResult
{
    /// <summary>True when the startup handshake ran a save pass against Desktop.</summary>
    public bool RanSave { get; init; }

    /// <summary>True when Desktop actually wrote a file (it had unsaved edits).</summary>
    public bool WroteFile { get; init; }

    /// <summary>True when a Power BI Desktop report was open and reachable.</summary>
    public bool PowerBiAvailable { get; init; }

    /// <summary>True when the wait hit the hard cap before a write/quiescence signal.</summary>
    public bool TimedOut { get; init; }
}

/// <summary>
/// One-shot startup handshake: on the very first activation, flush Power BI
/// Desktop's unsaved edits to disk and wait until the tools' watchers have settled,
/// so the hosted tools render current data rather than a stale report.
///
/// It never blocks the UI thread and never throws; a Power BI Desktop that is not
/// running (or a multi-report state) is a fast no-op, and a wedged wait is capped.
/// </summary>
internal sealed class StartupSyncService
{
    /// <summary>Hard cap: the overlay always comes down, even if nothing signals.</summary>
    private static readonly TimeSpan HardTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Quiet period after a watcher event before we call the save settled.</summary>
    private static readonly TimeSpan QuietPeriod = TimeSpan.FromMilliseconds(1000);

    /// <summary>
    /// Runs the startup handshake. Returns what happened; the caller decides whether
    /// to show an overlay. <paramref name="hasReport"/> is re-checked via the
    /// provided open callback so discovery stays in one place.
    /// </summary>
    public async Task<StartupSyncResult> RunAsync(Func<bool> ensureReportOpen)
    {
        try
        {
            // 1. Power BI Desktop running? If not, there is nothing to flush.
            if (!PowerBiPublisher.IsPowerBiRunning())
            {
                App.Log("Startup sync: Power BI Desktop is not running — skipping.");
                return new StartupSyncResult { RanSave = false, PowerBiAvailable = false };
            }

            // 2. More than one report open: the guard owns the UI, so do not guess.
            if (PowerBiDetector.CountOpenPowerBiReports() > 1)
            {
                App.Log("Startup sync: multiple reports open — skipping save.");
                return new StartupSyncResult { RanSave = false, PowerBiAvailable = true };
            }

            // 3. Make sure a report is open so the watchers are armed.
            if (!ensureReportOpen())
            {
                App.Log("Startup sync: no report loaded — skipping save.");
                return new StartupSyncResult { RanSave = false, PowerBiAvailable = true };
            }

            // 4. Watch for the watchers settling, then click Save.
            var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            var quiet = CreateQuietTimer(settled);

            EventHandler onChanged = (_, _) =>
            {
                quiet.Stop();
                quiet.Start();
            };

            App.Workspace.ExternalChangeDetected += onChanged;
            App.Workspace.ModelChanged += onChanged;

            try
            {
                App.Log("Startup sync: asking Power BI Desktop to save.");
                var wroteFile = await App.Workspace.RequestPowerBiSaveAsync().ConfigureAwait(true);
                App.Log($"Startup sync: save pass finished (wroteFile={wroteFile}).");

                var timedOut = false;
                if (wroteFile)
                {
                    // Start counting only now, so the quiet period begins after the
                    // click and covers the watcher's own ~600ms debounce. Each watcher
                    // event restarts it, so we proceed once the writes have settled.
                    quiet.Start();
                    var finished = await Task.WhenAny(settled.Task, Task.Delay(HardTimeout)).ConfigureAwait(true);
                    timedOut = finished != settled.Task;
                    App.Log(timedOut
                        ? "Startup sync: wait timed out — proceeding anyway."
                        : "Startup sync: watchers settled.");
                }
                else
                {
                    // Desktop had nothing unsaved: the on-disk state is already current.
                    App.Log("Startup sync: nothing to save — disk already current.");
                }

                quiet.Stop();

                return new StartupSyncResult
                {
                    RanSave = true,
                    WroteFile = wroteFile,
                    PowerBiAvailable = true,
                    TimedOut = timedOut
                };
            }
            finally
            {
                App.Workspace.ExternalChangeDetected -= onChanged;
                App.Workspace.ModelChanged -= onChanged;
                quiet.Stop();
            }
        }
        catch (Exception ex)
        {
            App.Log($"Startup sync failed: {ex}");
            return new StartupSyncResult { RanSave = false, PowerBiAvailable = false };
        }
    }

    /// <summary>
    /// A one-shot timer that resolves <paramref name="settled"/> after the quiet
    /// period, so a burst of watcher events coalesces into a single settle.
    /// </summary>
    private static DispatcherQueueTimer CreateQuietTimer(TaskCompletionSource<bool> settled)
    {
        var dispatcher = App.CurrentMainWindow?.DispatcherQueue
            ?? throw new InvalidOperationException("No dispatcher available for the startup timer.");

        var timer = dispatcher.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = QuietPeriod;
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            settled.TrySetResult(true);
        };

        return timer;
    }
}
