using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SingularTools.Core;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.ReportPublishingManager;

public enum PublishStatus
{
    Pending,
    Publishing,
    Success,
    Failed,
    Skipped
}

public sealed class WorkspaceItemViewModel : INotifyPropertyChanged
{
    public string Name { get; set; } = string.Empty;

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    private PublishStatus _status = PublishStatus.Pending;
    public PublishStatus Status => _status;

    public string StatusText { get; private set; } = string.Empty;
    public string StatusGlyph { get; private set; } = string.Empty;

    public Visibility StatusVisibility => _status == PublishStatus.Pending ? Visibility.Collapsed : Visibility.Visible;

    public void SetStatus(PublishStatus status, string text, string glyph)
    {
        _status = status;
        StatusText = text;
        StatusGlyph = glyph;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusText)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusGlyph)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(StatusVisibility)));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed partial class ReportPublishingManagerPage : Page, IToolPage
{
    public string ToolId => "report-publishing-manager";
    public string Title => "Multi-Workspace Publish";
    public string Description => "Publish a report to multiple Power BI workspaces at once";
    public string Glyph => "\uE724";

    private readonly ObservableCollection<WorkspaceItemViewModel> _items = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _subscribed;
    private WorkspaceCache _cache = WorkspaceCache.Empty();

    /// <summary>Report whose saved selection is currently applied, so an edit or
    /// external save to the same report does not overwrite unsaved ticks.</summary>
    private string _selectionReportPath = string.Empty;

    public ReportPublishingManagerPage()
    {
        InitializeComponent();
        WorkspacesListView.ItemsSource = _items;

        Loaded += ReportPublishingManagerPage_Loaded;
        Unloaded += ReportPublishingManagerPage_Unloaded;
    }

    private void ReportPublishingManagerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            App.Workspace.Changed -= Workspace_Changed;
            _subscribed = false;
        }

        SaveSelection();
    }

    private void ReportPublishingManagerPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("ReportPublishingManagerPage loaded.");
            Subscribe();
            LoadCachedWorkspaces();
            UpdateReportStatus();
            UpdateEmptyStates();
        }
        catch (Exception ex)
        {
            App.Log($"Error in ReportPublishingManagerPage_Loaded: {ex}");
        }
    }

    public void OnActivated()
    {
        Subscribe();
        LoadCachedWorkspaces();
        UpdateReportStatus();
        UpdateEmptyStates();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Workspace.Changed += Workspace_Changed;
        _subscribed = true;
    }

    /// <summary>
    /// The saved ticks belong to the report, so a different report in the shared
    /// session means a different pre-selection for the same cached workspace list.
    /// Edits to the same report are ignored so unsaved ticks survive.
    /// </summary>
    private void Workspace_Changed(object? sender, EventArgs e)
    {
        if (_isBusy) return;

        var path = App.Workspace.HasReport ? App.Workspace.ReportPath : string.Empty;
        if (string.Equals(path, _selectionReportPath, StringComparison.OrdinalIgnoreCase))
        {
            UpdateReportStatus();
            return;
        }

        _selectionReportPath = path;
        ApplySavedSelection();
        UpdateReportStatus();
    }

    /// <summary>Re-ticks the current list from the current report's saved selection.</summary>
    private void ApplySavedSelection()
    {
        var saved = LoadSavedSelection();
        foreach (var item in _items)
        {
            item.IsSelected = saved.Contains(item.Name);
        }

        UpdateSummary();
        if (!_isBusy) PublishButton.IsEnabled = _items.Any(i => i.IsSelected);
    }

    /// <summary>
    /// Populates the list from the machine-level cache so the tool opens ready to
    /// publish. Detection stays available but is no longer a prerequisite.
    /// </summary>
    private void LoadCachedWorkspaces()
    {
        _cache = WorkspaceCacheStore.Load();

        // First run after an upgrade: seed from the per-tool selection files the
        // older builds wrote, so existing users keep their list.
        if (_cache.Names.Count == 0)
        {
            var migrated = WorkspaceCacheStore.TryMigrateLegacySelection();
            if (migrated != null && migrated.Names.Count > 0)
            {
                _cache = migrated;
                WorkspaceCacheStore.Save(_cache);
                App.Log($"Migrated {migrated.Names.Count} workspace name(s) from the legacy selection file.");
            }
        }

        WorkspaceDetectPresenter.Apply(DetectButton, LastDetectedText, _cache);

        _selectionReportPath = App.Workspace.HasReport ? App.Workspace.ReportPath : string.Empty;

        if (_cache.Names.Count > 0 && _items.Count == 0)
        {
            PopulateWorkspaces(_cache.Names);
        }
    }

    private void UpdateReportStatus()
    {
        if (ReportNameText == null) return;

        if (PowerBiDetector.FindActivePowerBiWindow(out _, out var title, out _))
        {
            var reportName = PowerBiDetector.ExtractReportName(title);
            ReportNameText.Text = string.IsNullOrEmpty(reportName) ? title : reportName;
            ReportHintText.Text = App.Workspace.HasReport
                ? "Detect the workspaces you can publish to, then publish to several at once."
                : "Open this report in Singular Tools to remember your workspace selection, then publish to several at once.";
            ReportStatusGlyph.Glyph = "\uE73E";
        }
        else
        {
            ReportNameText.Text = "Power BI Desktop is not running";
            ReportHintText.Text = "Open the report in Power BI Desktop, then detect workspaces to publish to.";
            ReportStatusGlyph.Glyph = "\uE7BA";
        }
    }

    private void UpdateEmptyStates()
    {
        var pbiRunning = PowerBiDetector.FindActivePowerBiWindow(out _, out _, out _);
        var hasItems = _items.Count > 0;

        ListCard.Visibility = hasItems ? Visibility.Visible : Visibility.Collapsed;
        NoWorkspacesPanel.Visibility = hasItems ? Visibility.Collapsed : Visibility.Visible;

        if (pbiRunning)
        {
            NoWorkspacesTitle.Text = "No workspaces yet";
            NoWorkspacesHint.Text = _cache.HasBeenDetected
                ? "The last detection returned no workspaces. Redetect once Power BI Desktop has finished loading."
                : "Click 'Detect workspaces' to read the list from Power BI Desktop.";
        }
        else
        {
            NoWorkspacesTitle.Text = "Power BI Desktop is not running";
            NoWorkspacesHint.Text = _cache.Names.Count > 0
                ? "The workspaces below were detected earlier. Open your report and redetect to refresh them."
                : "Open the report in Power BI Desktop first, then detect its workspaces.";
        }

        UpdateSummary();
    }

    private void UpdateSummary()
    {
        if (SummaryText == null) return;

        var selected = _items.Count(i => i.IsSelected);
        var finished = _items.Count(i => i.Status is PublishStatus.Success or PublishStatus.Failed or PublishStatus.Skipped);
        var succeeded = _items.Count(i => i.Status == PublishStatus.Success);

        if (_items.Count == 0)
        {
            SummaryText.Text = "No workspaces";
        }
        else if (finished > 0)
        {
            SummaryText.Text = $"{succeeded} published · {selected} selected";
        }
        else
        {
            SummaryText.Text = $"{_items.Count} workspaces · {selected} selected";
        }
    }

    private void SetBusy(bool busy, bool allowCancel = false)
    {
        _isBusy = busy;
        DetectButton.IsEnabled = !busy;
        SelectAllButton.IsEnabled = !busy;
        ClearButton.IsEnabled = !busy;
        PublishButton.IsEnabled = !busy && _items.Any(i => i.IsSelected);
        CancelButton.Visibility = busy && allowCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Detect workspaces -----------------------------------------------

    private async void DetectButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            SetBusy(true);
            var names = await Task.Run(() => PowerBiPublisher.DetectWorkspaces());

            if (names.Count > 0)
            {
                _cache = WorkspaceDetectPresenter.Merge(names, _cache);
                WorkspaceCacheStore.Save(_cache);
                WorkspaceDetectPresenter.Apply(DetectButton, LastDetectedText, _cache);
            }

            PopulateWorkspaces(names);
            UpdateEmptyStates();
            UpdateSummary();
            SetBusy(false);

            ToastService.Show(names.Count > 0
                ? $"Found {names.Count} workspace(s)."
                : "No workspaces were found in the publish dialog.", names.Count > 0 ? ToastSeverity.Success : ToastSeverity.Warning);
        }
        catch (Exception ex)
        {
            SetBusy(false);
            App.Log($"Detect workspaces failed: {ex}");
            ToastService.Show($"Could not read workspaces: {ex.Message}", ToastSeverity.Error);
        }
    }

    private void PopulateWorkspaces(IReadOnlyList<string> names)
    {
        var saved = LoadSavedSelection();

        var existing = new HashSet<string>(_items.Where(i => i.IsSelected).Select(i => i.Name), StringComparer.OrdinalIgnoreCase);
        _items.Clear();

        foreach (var name in names)
        {
            _items.Add(new WorkspaceItemViewModel
            {
                Name = name,
                IsSelected = saved.Contains(name) || existing.Contains(name)
            });
        }
    }

    // ---- Publish ---------------------------------------------------------

    private async void PublishButton_Click(object sender, RoutedEventArgs e)
    {
        var selected = _items.Where(i => i.IsSelected).ToList();
        if (selected.Count == 0)
        {
            ToastService.Show("Select at least one workspace to publish to.", ToastSeverity.Warning);
            return;
        }

        var confirmed = await ConfirmPublishAsync(selected.Count);
        if (!confirmed) return;

        SaveSelection();

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true, allowCancel: true);

        int succeeded = 0;
        try
        {
            foreach (var item in selected)
            {
                token.ThrowIfCancellationRequested();
                item.SetStatus(PublishStatus.Publishing, "Publishing…", "\uE895");

                var result = await Task.Run(() => PowerBiPublisher.PublishToWorkspace(item.Name, token), token);

                if (result.Success)
                {
                    item.SetStatus(PublishStatus.Success, "Published", "\uE73E");
                    succeeded++;
                    ToastService.Show($"Published to '{item.Name}'.", ToastSeverity.Success);
                }
                else
                {
                    item.SetStatus(PublishStatus.Failed, result.Error ?? "Failed", "\uE711");
                    ToastService.Show($"'{item.Name}': {result.Error}", ToastSeverity.Error);
                }

                UpdateSummary();
            }

            ToastService.Show(
                $"Publishing finished: {succeeded} of {selected.Count} workspace(s) succeeded.",
                succeeded == selected.Count ? ToastSeverity.Success : ToastSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            foreach (var item in selected.Where(i => i.Status == PublishStatus.Publishing))
            {
                item.SetStatus(PublishStatus.Skipped, "Skipped", "\uE756");
            }
            ToastService.Show("Publishing cancelled.", ToastSeverity.Informational);
        }
        catch (Exception ex)
        {
            App.Log($"Publish loop failed: {ex}");
            ToastService.Show($"Publishing failed: {ex.Message}", ToastSeverity.Error);
        }
        finally
        {
            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            UpdateSummary();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ToastService.Show("Cancelling after the current workspace…", ToastSeverity.Informational);
    }

    private async Task<bool> ConfirmPublishAsync(int count)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Publish to workspaces?",
                Content = $"The open report will be published to {count} workspace(s). Existing reports with the same name will be replaced automatically.",
                PrimaryButtonText = "Publish",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            App.Log($"Confirm publish dialog failed: {ex.Message}");
            return false;
        }
    }

    // ---- Selection helpers -----------------------------------------------

    private void SelectAllButton_Click(object sender, RoutedEventArgs e)
    {
        var anyUnselected = _items.Any(i => !i.IsSelected);
        foreach (var item in _items)
        {
            item.IsSelected = anyUnselected;
        }
        UpdateSummary();
        if (!_isBusy) PublishButton.IsEnabled = _items.Any(i => i.IsSelected);
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        foreach (var item in _items)
        {
            item.IsSelected = false;
        }
        UpdateSummary();
        if (!_isBusy) PublishButton.IsEnabled = false;
    }

    private void WorkspaceCheckBox_Changed(object sender, RoutedEventArgs e)
    {
        UpdateSummary();
        if (!_isBusy) PublishButton.IsEnabled = _items.Any(i => i.IsSelected);
    }

    private void GoToHome_Click(object sender, RoutedEventArgs e)
    {
        App.CurrentMainWindow?.NavigateToTool("home");
    }

    // ---- Persistence -----------------------------------------------------

    /// <summary>
    /// The report's pre-ticked workspaces, stored with the report in
    /// <c>singular-tools.json</c>. Multi-workspace publishing is report specific,
    /// so the selection travels with the report rather than the machine.
    /// </summary>
    private HashSet<string> LoadSavedSelection()
    {
        if (!App.Workspace.HasReport) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            var names = ReportConfigStore.Load(App.Workspace.ReportPath).GetPublishWorkspaces();
            return new HashSet<string>(names, StringComparer.OrdinalIgnoreCase);
        }
        catch (Exception ex)
        {
            App.Log($"Load publishing selection failed: {ex.Message}");
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveSelection()
    {
        if (!App.Workspace.HasReport) return;

        try
        {
            var config = ReportConfigStore.Load(App.Workspace.ReportPath);
            config.SetPublishWorkspaces(_items.Where(i => i.IsSelected).Select(i => i.Name));

            if (!ReportConfigStore.Save(App.Workspace.ReportPath, config))
            {
                App.Log("Could not save publishing selection to singular-tools.json.");
            }
        }
        catch (Exception ex)
        {
            App.Log($"Save publishing selection failed: {ex.Message}");
        }
    }
}
