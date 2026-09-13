using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
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

    private static string SelectionPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SingularPowerTools",
        "publishing-manager.json");

    private readonly ObservableCollection<WorkspaceItemViewModel> _items = new();
    private CancellationTokenSource? _cts;
    private bool _isBusy;

    public ReportPublishingManagerPage()
    {
        InitializeComponent();
        WorkspacesListView.ItemsSource = _items;

        Loaded += ReportPublishingManagerPage_Loaded;
        Unloaded += ReportPublishingManagerPage_Unloaded;
    }

    private void ReportPublishingManagerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        SaveSelection();
    }

    private void ReportPublishingManagerPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("ReportPublishingManagerPage loaded.");
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
        UpdateReportStatus();
        UpdateEmptyStates();
    }

    private void UpdateReportStatus()
    {
        if (ReportNameText == null) return;

        if (PowerBiDetector.FindActivePowerBiWindow(out _, out var title, out _))
        {
            var reportName = PowerBiDetector.ExtractReportName(title);
            ReportNameText.Text = string.IsNullOrEmpty(reportName) ? title : reportName;
            ReportHintText.Text = "Detect the workspaces you can publish to, then publish to several at once.";
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
            NoWorkspacesTitle.Text = "No workspaces detected";
            NoWorkspacesHint.Text = "Click 'Detect workspaces' to read the list from Power BI Desktop.";
        }
        else
        {
            NoWorkspacesTitle.Text = "Power BI Desktop is not running";
            NoWorkspacesHint.Text = "Open the report in Power BI Desktop first, then detect its workspaces.";
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

    private HashSet<string> LoadSavedSelection()
    {
        try
        {
            if (!File.Exists(SelectionPath)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var json = File.ReadAllText(SelectionPath);
            var list = JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
            return new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveSelection()
    {
        try
        {
            var dir = Path.GetDirectoryName(SelectionPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var selected = _items.Where(i => i.IsSelected).Select(i => i.Name).ToList();
            File.WriteAllText(SelectionPath, JsonSerializer.Serialize(selected));
        }
        catch (Exception ex)
        {
            App.Log($"Save publishing selection failed: {ex.Message}");
        }
    }
}
