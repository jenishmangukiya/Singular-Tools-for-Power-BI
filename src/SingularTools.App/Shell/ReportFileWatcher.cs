using System;
using System.IO;
using Microsoft.UI.Dispatching;

namespace SingularTools_App.Shell;

/// <summary>
/// Watches a report's on-disk definition folder for external changes (typically a
/// save from Power BI Desktop) and raises a single debounced callback once the
/// writes settle. Callbacks are marshalled to the UI thread.
/// </summary>
internal sealed class ReportFileWatcher : IDisposable
{
    private const int DebounceMs = 600;

    private readonly Action _onChanged;
    private FileSystemWatcher? _watcher;
    private DispatcherQueueTimer? _debounce;

    public ReportFileWatcher(Action onChanged)
    {
        _onChanged = onChanged;
    }

    public void Watch(string directory)
    {
        Stop();

        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return;
        }

        try
        {
            _watcher = new FileSystemWatcher(directory)
            {
                IncludeSubdirectories = true,
                Filter = "*.json",
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024,
                EnableRaisingEvents = true
            };

            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Deleted += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.Error += OnWatcherError;
        }
        catch (Exception ex)
        {
            App.Log($"ReportFileWatcher could not watch '{directory}': {ex.Message}");
        }
    }

    public void Stop()
    {
        if (_watcher != null)
        {
            try
            {
                _watcher.EnableRaisingEvents = false;
                _watcher.Changed -= OnFileEvent;
                _watcher.Created -= OnFileEvent;
                _watcher.Deleted -= OnFileEvent;
                _watcher.Renamed -= OnFileEvent;
                _watcher.Error -= OnWatcherError;
                _watcher.Dispose();
            }
            catch { }
            _watcher = null;
        }

        _debounce?.Stop();
        _debounce = null;
    }

    public void Dispose() => Stop();

    private void OnFileEvent(object sender, FileSystemEventArgs e) => RestartDebounce();

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        // Buffer overflow or the folder disappeared: force a full re-sync.
        App.Log($"ReportFileWatcher error: {e.GetException().Message}");
        RestartDebounce();
    }

    private void RestartDebounce()
    {
        var dispatcher = App.CurrentMainWindow?.DispatcherQueue;
        if (dispatcher == null)
        {
            return;
        }

        if (!dispatcher.HasThreadAccess)
        {
            dispatcher.TryEnqueue(RestartDebounce);
            return;
        }

        if (_debounce == null)
        {
            _debounce = dispatcher.CreateTimer();
            _debounce.IsRepeating = false;
            _debounce.Tick += (_, _) =>
            {
                _debounce?.Stop();
                _debounce = null;
                try
                {
                    _onChanged();
                }
                catch (Exception ex)
                {
                    App.Log($"ReportFileWatcher callback failed: {ex.Message}");
                }
            };
        }

        _debounce.Stop();
        _debounce.Interval = TimeSpan.FromMilliseconds(DebounceMs);
        _debounce.Start();
    }
}
