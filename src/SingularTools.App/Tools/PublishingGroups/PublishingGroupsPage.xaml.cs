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
using Microsoft.UI.Xaml.Input;
using SingularTools.Core;
using SingularTools.Core.Models;
using SingularTools_App.Shell;
using Windows.System;

namespace SingularTools_App.Tools.PublishingGroups;

/// <summary>One saved publishing group shown in the left-hand list.</summary>
public sealed class GroupViewModel : INotifyPropertyChanged
{
    public GroupViewModel(PublishingGroup model) => Model = model;

    public PublishingGroup Model { get; }

    public string Name
    {
        get => Model.Name;
        set
        {
            if (string.Equals(Model.Name, value, StringComparison.Ordinal)) return;
            Model.Name = value;
            Raise(nameof(Name));
        }
    }

    private int _visibleCount;
    private int _totalCount;

    public void SetCounts(int visibleCount, int totalCount)
    {
        if (_visibleCount == visibleCount && _totalCount == totalCount) return;
        _visibleCount = visibleCount;
        _totalCount = totalCount;
        Raise(nameof(Summary));
    }
    public string Summary
    {
        get
        {
            if (_totalCount == 0) return "No pages";
            return $"{_visibleCount} of {_totalCount} pages visible";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>One report page row in the visibility editor.</summary>
public sealed class GroupPageViewModel : INotifyPropertyChanged
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public int OrderIndex { get; init; }
    public string OrderIndexText => $"#{OrderIndex + 1}";

    /// <summary>True when the page is hidden in the report itself (author setting).</summary>
    public bool IsHiddenInReport { get; init; }
    public Visibility HiddenInReportVisibility => IsHiddenInReport ? Visibility.Visible : Visibility.Collapsed;

    private bool _isVisible = true;

    /// <summary>Whether this group keeps the page visible.</summary>
    public bool IsVisible
    {
        get => _isVisible;
        set
        {
            if (_isVisible == value) return;
            _isVisible = value;
            Raise(nameof(IsVisible));
        }
    }
    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class PublishingGroupsPage : Page, IToolPage
{
    public string ToolId => "publishing-groups";
    public string Title => "Publishing Groups";
    public string Description => "Choose which pages each workspace sees when you publish";
    public string Glyph => "\uE724";

    /// <summary>How long to wait after swapping visibility so Power BI Desktop can reload it.</summary>
    private const int VisibilitySettleMs = 900;

    private static string SavedWorkspacesPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SingularPowerTools",
        "publishing-groups-workspaces.json");

    private readonly ObservableCollection<GroupViewModel> _groups = new();
    private readonly ObservableCollection<GroupPageViewModel> _pages = new();

    private GroupViewModel? _current;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private bool _suppressToggle;
    private bool _suppressNameEdit;
    private bool _suppressSelection;
    private bool _subscribed;

    public PublishingGroupsPage()
    {
        InitializeComponent();
        GroupsListView.ItemsSource = _groups;
        PagesListView.ItemsSource = _pages;

        Loaded += PublishingGroupsPage_Loaded;
        Unloaded += PublishingGroupsPage_Unloaded;
    }

    // ---- Lifecycle --------------------------------------------------------

    private void PublishingGroupsPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("PublishingGroupsPage loaded.");
            Subscribe();
            RefreshAll();
            UpdateReportStatus();
        }
        catch (Exception ex)
        {
            App.Log($"Error in PublishingGroupsPage_Loaded: {ex}");
        }
    }

    private void PublishingGroupsPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            App.Workspace.Changed -= Workspace_Changed;
            _subscribed = false;
        }
    }

    public void OnActivated()
    {
        RefreshAll();
        UpdateReportStatus();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Workspace.Changed += Workspace_Changed;
        _subscribed = true;
    }

    private void Workspace_Changed(object? sender, EventArgs e)
    {
        // While publishing we swap page visibility on purpose; ignore the
        // refresh those writes trigger so the running operation is not torn down.
        if (_isBusy) return;

        RefreshAll();
        UpdateReportStatus();
    }

    // ---- State refresh ----------------------------------------------------

    private void RefreshAll()
    {
        var previousName = _current?.Name;

        _groups.Clear();
        if (App.Workspace.HasReport)
        {
            foreach (var group in ReportConfigStore.Load(App.Workspace.ReportPath).GetPublishingGroups())
            {
                _groups.Add(new GroupViewModel(group));
            }
        }

        GroupViewModel? target = null;
        if (!string.IsNullOrEmpty(previousName))
        {
            target = _groups.FirstOrDefault(g => string.Equals(g.Name, previousName, StringComparison.OrdinalIgnoreCase));
        }
        target ??= _groups.FirstOrDefault();

        _suppressSelection = true;
        GroupsListView.SelectedItem = target;
        _suppressSelection = false;

        _current = target;
        ShowEditor(target);
        UpdateGroupCount();
        UpdateEmptyStates();
    }

    private void ShowEditor(GroupViewModel? group)
    {
        if (group == null)
        {
            _suppressToggle = true;
            _pages.Clear();
            _suppressToggle = false;
            UpdateEmptyStates();
            return;
        }

        _suppressNameEdit = true;
        GroupNameBox.Text = group.Name;
        _suppressNameEdit = false;

        RefreshPages();
        UpdateEmptyStates();
    }

    private void RefreshPages()
    {
        _suppressToggle = true;
        _pages.Clear();

        var visible = _current == null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(_current.Model.VisiblePageIds, StringComparer.OrdinalIgnoreCase);

        if (App.Workspace.HasReport)
        {
            int index = 0;
            foreach (var page in App.Workspace.Manager.Pages)
            {
                _pages.Add(new GroupPageViewModel
                {
                    Id = page.Id,
                    DisplayName = page.DisplayName,
                    OrderIndex = index++,
                    IsHiddenInReport = page.IsHidden,
                    IsVisible = visible.Contains(page.Id)
                });
            }
        }

        _suppressToggle = false;
        UpdateVisibilitySummary();
    }

    private void UpdateEmptyStates()
    {
        var hasReport = App.Workspace.HasReport;
        var hasGroup = _current != null;

        NoReportPanel.Visibility = hasReport ? Visibility.Collapsed : Visibility.Visible;
        NoGroupPanel.Visibility = hasReport && !hasGroup ? Visibility.Visible : Visibility.Collapsed;
        EditorPanel.Visibility = hasReport && hasGroup ? Visibility.Visible : Visibility.Collapsed;

        GroupsListView.Visibility = _groups.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        NoGroupsPanel.Visibility = _groups.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        NoGroupsText.Text = hasReport
            ? "No groups yet. Create one to choose which pages a workspace will see."
            : "Open a report to create publishing groups.";

        NewGroupButton.IsEnabled = hasReport && !_isBusy;
        PublishButton.IsEnabled = !_isBusy && hasGroup && _pages.Count > 0;
        ShowAllButton.IsEnabled = !_isBusy && hasGroup && _pages.Count > 0;
        HideAllButton.IsEnabled = !_isBusy && hasGroup && _pages.Count > 0;
    }

    private void UpdateVisibilitySummary()
    {
        var total = _pages.Count;
        var visible = _pages.Count(p => p.IsVisible);

        VisibilitySummaryText.Text = total == 0
            ? "No pages"
            : $"{total} pages · {visible} visible · {total - visible} hidden";

        _current?.SetCounts(visible, total);

        PublishButton.IsEnabled = !_isBusy && _current != null && total > 0;
        ShowAllButton.IsEnabled = PublishButton.IsEnabled;
        HideAllButton.IsEnabled = PublishButton.IsEnabled;
    }

    private void UpdateGroupCount()
    {
        GroupCountText.Text = _groups.Count switch
        {
            0 => "No groups",
            1 => "1 group",
            _ => $"{_groups.Count} groups"
        };
    }

    private void UpdateReportStatus()
    {
        if (ReportNameText == null) return;

        if (App.Workspace.HasReport)
        {
            ReportNameText.Text = App.Workspace.ReportName;
            ReportHintText.Text = "Choose which pages each workspace will see, then publish the group.";
            ReportStatusGlyph.Glyph = "\uE73E";
        }
        else
        {
            ReportNameText.Text = "No report loaded";
            ReportHintText.Text = "Open a report to set up which pages each workspace will see.";
            ReportStatusGlyph.Glyph = "\uE7BA";
        }
    }

    private void SetBusy(bool busy, bool allowCancel = false)
    {
        _isBusy = busy;
        NewGroupButton.IsEnabled = !busy && App.Workspace.HasReport;
        PublishButton.IsEnabled = !busy && _current != null && _pages.Count > 0;
        ShowAllButton.IsEnabled = PublishButton.IsEnabled;
        HideAllButton.IsEnabled = PublishButton.IsEnabled;
        GroupsListView.IsEnabled = !busy;
        CancelButton.Visibility = busy && allowCancel ? Visibility.Visible : Visibility.Collapsed;
    }

    // ---- Group list -------------------------------------------------------

    private void GroupsListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;

        _current = GroupsListView.SelectedItem as GroupViewModel;
        ShowEditor(_current);
    }

    private static GroupViewModel? GroupFrom(object sender)
        => (sender as FrameworkElement)?.DataContext as GroupViewModel;

    private async void NewGroupButton_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Workspace.HasReport)
        {
            ToastService.Show("Open a report first.", ToastSeverity.Warning);
            return;
        }

        var name = await PromptForNameAsync("New group", "Name this group", SuggestGroupName());
        if (name == null) return;

        var group = new PublishingGroup
        {
            Name = UniqueName(name),
            // A new group starts with every page visible.
            VisiblePageIds = App.Workspace.Manager.Pages.Select(p => p.Id).ToList()
        };

        _groups.Add(new GroupViewModel(group));
        SaveGroups();

        _current = _groups[^1];
        _suppressSelection = true;
        GroupsListView.SelectedItem = _current;
        _suppressSelection = false;

        ShowEditor(_current);
        UpdateGroupCount();
        UpdateEmptyStates();

        ToastService.Show($"Created group '{_current.Name}'.", ToastSeverity.Success);
    }

    private async void GroupRename_Click(object sender, RoutedEventArgs e)
    {
        var group = GroupFrom(sender);
        if (group == null) return;

        var name = await PromptForNameAsync("Rename group", "Group name", group.Name);
        if (name == null) return;

        if (!string.Equals(name, group.Name, StringComparison.Ordinal)
            && _groups.Any(g => !ReferenceEquals(g, group) && string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            ToastService.Show($"Another group is already called '{name}'.", ToastSeverity.Warning);
        }

        group.Name = name;
        SaveGroups();

        if (ReferenceEquals(group, _current))
        {
            _suppressNameEdit = true;
            GroupNameBox.Text = name;
            _suppressNameEdit = false;
        }
    }

    private void GroupDuplicate_Click(object sender, RoutedEventArgs e)
    {
        var group = GroupFrom(sender);
        if (group == null) return;

        var copy = new PublishingGroup
        {
            Name = UniqueName($"{group.Name} copy"),
            VisiblePageIds = new List<string>(group.Model.VisiblePageIds)
        };

        _groups.Add(new GroupViewModel(copy));
        SaveGroups();

        _current = _groups[^1];
        _suppressSelection = true;
        GroupsListView.SelectedItem = _current;
        _suppressSelection = false;

        ShowEditor(_current);
        UpdateGroupCount();
        UpdateEmptyStates();

        ToastService.Show($"Duplicated as '{_current.Name}'.", ToastSeverity.Success);
    }

    private async void GroupDelete_Click(object sender, RoutedEventArgs e)
    {
        var group = GroupFrom(sender);
        if (group == null) return;

        var confirmed = await ConfirmAsync(
            "Delete this group?",
            $"'{group.Name}' will be removed. The report itself is not changed.");
        if (!confirmed) return;

        _groups.Remove(group);
        SaveGroups();

        var next = _groups.FirstOrDefault();
        _current = next;
        _suppressSelection = true;
        GroupsListView.SelectedItem = next;
        _suppressSelection = false;

        ShowEditor(next);
        UpdateGroupCount();
        UpdateEmptyStates();

        ToastService.Show("Group deleted.", ToastSeverity.Informational);
    }

    // ---- Editor -----------------------------------------------------------

    private void GroupNameBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (_suppressNameEdit || _current == null) return;

        var name = GroupNameBox.Text.Trim();
        if (string.IsNullOrEmpty(name)) return;

        _current.Name = name;
        SaveGroups();
    }

    private void PageVisibility_Toggled(object sender, RoutedEventArgs e)
    {
        if (sender is not ToggleSwitch toggle) return;

        // The switch also fires when the list recycles a container for a page
        // flushed by Show/Hide all. Without a page behind it, revert the visual
        // so it can never look like an unsaved change.
        if (_suppressToggle || toggle.DataContext is not GroupPageViewModel page)
        {
            if (toggle.IsOn)
            {
                _suppressToggle = true;
                toggle.IsOn = false;
                _suppressToggle = false;
            }

            return;
        }

        page.IsVisible = toggle.IsOn;
        if (_current == null) return;

        PersistCurrentVisibility();
        UpdateVisibilitySummary();
    }

    private void ShowAllButton_Click(object sender, RoutedEventArgs e) => SetAllPagesVisible(true);

    private void HideAllButton_Click(object sender, RoutedEventArgs e) => SetAllPagesVisible(false);

    private void SetAllPagesVisible(bool visible)
    {
        if (_current == null) return;

        _suppressToggle = true;
        foreach (var page in _pages)
        {
            page.IsVisible = visible;
        }

        _suppressToggle = false;

        PersistCurrentVisibility();
        RefreshPages();
    }

    private void PersistCurrentVisibility()
    {
        if (_current == null) return;

        _current.Model.VisiblePageIds = _pages.Where(p => p.IsVisible).Select(p => p.Id).ToList();
        SaveGroups();
        _current.SetCounts(_pages.Count(p => p.IsVisible), _pages.Count);
    }

    private void GoToHome_Click(object sender, RoutedEventArgs e)
        => App.CurrentMainWindow?.NavigateToTool("home");

    // ---- Publishing -------------------------------------------------------

    private async void PublishButton_Click(object sender, RoutedEventArgs e)
    {
        var group = _current;
        if (group == null) return;

        if (!App.Workspace.HasReport)
        {
            ToastService.Show("Open a report first.", ToastSeverity.Warning);
            return;
        }

        var pages = App.Workspace.Manager.Pages;
        var existing = new HashSet<string>(pages.Select(p => p.Id), StringComparer.OrdinalIgnoreCase);
        var visibleIds = new HashSet<string>(group.Model.VisiblePageIds.Where(existing.Contains), StringComparer.OrdinalIgnoreCase);
        var hiddenCount = pages.Count(p => !visibleIds.Contains(p.Id));
        var missingCount = group.Model.VisiblePageIds.Count(id => !existing.Contains(id));

        var workspaces = await PickWorkspacesAsync(group.Name, hiddenCount, missingCount);
        if (workspaces == null) return;
        if (workspaces.Count == 0)
        {
            ToastService.Show("Choose at least one workspace to publish to.", ToastSeverity.Warning);
            return;
        }

        // Remember the report's own visibility so it can be put back afterwards.
        var originalVisibility = App.Workspace.Manager.CaptureVisibility();
        var hiddenByPageId = pages.ToDictionary(
            p => p.Id,
            p => !visibleIds.Contains(p.Id),
            StringComparer.OrdinalIgnoreCase);

        _cts = new CancellationTokenSource();
        var token = _cts.Token;
        SetBusy(true, allowCancel: true);

        int succeeded = 0;
        try
        {
            App.Workspace.ApplyTransientEdit(m => m.SetPageVisibility(hiddenByPageId));
            ToastService.Show(
                hiddenCount > 0
                    ? $"Prepared '{group.Name}' — {hiddenCount} page(s) hidden for this publish."
                    : $"Prepared '{group.Name}' — all pages visible.",
                ToastSeverity.Informational);

            // Give Power BI Desktop a moment to reload the swapped visibility.
            await Task.Delay(VisibilitySettleMs, token);

            foreach (var workspace in workspaces)
            {
                token.ThrowIfCancellationRequested();

                var result = await Task.Run(() => PowerBiPublisher.PublishToWorkspace(workspace, token), token);

                if (result.Success)
                {
                    succeeded++;
                    ToastService.Show($"Published to '{workspace}'.", ToastSeverity.Success);
                }
                else
                {
                    ToastService.Show($"'{workspace}': {result.Error ?? "Publish failed."}", ToastSeverity.Error);
                }
            }

            ToastService.Show(
                $"Finished: {succeeded} of {workspaces.Count} workspace(s) succeeded.",
                succeeded == workspaces.Count ? ToastSeverity.Success : ToastSeverity.Warning);
        }
        catch (OperationCanceledException)
        {
            ToastService.Show("Publishing cancelled.", ToastSeverity.Informational);
        }
        catch (Exception ex)
        {
            App.Log($"Publishing group publish failed: {ex}");
            ToastService.Show($"Publishing failed: {ex.Message}", ToastSeverity.Error);
        }
        finally
        {
            // Always put the report's own visibility back, even on failure.
            try
            {
                App.Workspace.ApplyTransientEdit(m => m.SetPageVisibility(originalVisibility));
            }
            catch (Exception ex)
            {
                App.Log($"Restoring page visibility failed: {ex}");
                ToastService.Show("Could not restore page visibility — check the report in Power BI Desktop.", ToastSeverity.Error);
            }

            SetBusy(false);
            _cts?.Dispose();
            _cts = null;
            UpdateVisibilitySummary();
        }
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        ToastService.Show("Cancelling after the current workspace…", ToastSeverity.Informational);
    }

    // ---- Workspace picker -------------------------------------------------

    private sealed class WorkspaceChoice
    {
        public string Name { get; set; } = string.Empty;
        public bool IsChecked { get; set; }
    }

    /// <summary>
    /// Shows the workspace picker and returns the chosen names, or null when the
    /// author cancels. Workspaces can be detected from Power BI Desktop or typed
    /// by hand, which also works when Desktop is not signed in.
    /// </summary>
    private async Task<List<string>?> PickWorkspacesAsync(string groupName, int hiddenCount, int missingCount)
    {
        var choices = new List<WorkspaceChoice>();
        foreach (var name in LoadSavedWorkspaces())
        {
            choices.Add(new WorkspaceChoice { Name = name, IsChecked = true });
        }

        var listPanel = new StackPanel { Spacing = 2 };
        var emptyHint = new TextBlock
        {
            Text = "No workspaces yet — detect them, or type a name below.",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        };

        void Rebuild()
        {
            listPanel.Children.Clear();
            foreach (var choice in choices.OrderBy(c => c.Name, StringComparer.OrdinalIgnoreCase))
            {
                var box = new CheckBox { Content = choice.Name, IsChecked = choice.IsChecked };
                box.Checked += (_, _) => choice.IsChecked = true;
                box.Unchecked += (_, _) => choice.IsChecked = false;
                listPanel.Children.Add(box);
            }

            emptyHint.Visibility = choices.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        var addBox = new TextBox
        {
            PlaceholderText = "Type a workspace name",
            Width = 240
        };

        var addButton = new Button { Content = "Add" };
        void AddTyped()
        {
            var typed = addBox.Text.Trim();
            if (string.IsNullOrEmpty(typed)) return;

            var existing = choices.FirstOrDefault(c => string.Equals(c.Name, typed, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.IsChecked = true;
            }
            else
            {
                choices.Add(new WorkspaceChoice { Name = typed, IsChecked = true });
            }

            addBox.Text = string.Empty;
            Rebuild();
        }

        addButton.Click += (_, _) => AddTyped();
        addBox.KeyDown += (_, args) =>
        {
            if (args.Key == VirtualKey.Enter) AddTyped();
        };

        var progress = new ProgressRing { IsActive = false, Width = 18, Height = 18 };
        var detectButton = new Button { Content = "Detect workspaces" };
        detectButton.Click += async (_, _) =>
        {
            detectButton.IsEnabled = false;
            progress.IsActive = true;
            try
            {
                var names = await Task.Run(() => PowerBiPublisher.DetectWorkspaces());
                int added = 0;
                foreach (var name in names)
                {
                    if (choices.Any(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase))) continue;
                    choices.Add(new WorkspaceChoice { Name = name, IsChecked = true });
                    added++;
                }

                Rebuild();
                ToastService.Show(
                    names.Count == 0 ? "No workspaces were found in the publish dialog." : $"Found {names.Count} workspace(s) ({added} new).",
                    names.Count > 0 ? ToastSeverity.Success : ToastSeverity.Warning);
            }
            catch (Exception ex)
            {
                App.Log($"Detect workspaces failed: {ex}");
                ToastService.Show($"Could not read workspaces: {ex.Message}", ToastSeverity.Error);
            }
            finally
            {
                detectButton.IsEnabled = true;
                progress.IsActive = false;
            }
        };

        var scroller = new ScrollViewer { Content = listPanel, MaxHeight = 220 };
        ScrollViewer.SetVerticalScrollBarVisibility(scroller, ScrollBarVisibility.Auto);

        var detectRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        detectRow.Children.Add(detectButton);
        detectRow.Children.Add(progress);

        var addRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        addRow.Children.Add(addBox);
        addRow.Children.Add(addButton);

        var root = new StackPanel { Spacing = 10, MinWidth = 340 };
        root.Children.Add(new TextBlock
        {
            Text = $"Choose the workspaces to publish '{groupName}' to.",
            TextWrapping = TextWrapping.Wrap
        });

        var notes = new List<string>();
        if (hiddenCount > 0) notes.Add($"{hiddenCount} page(s) will be hidden in the published copy");
        if (missingCount > 0) notes.Add($"{missingCount} page(s) in this group no longer exist and will be ignored");
        notes.Add("Hidden pages leave the page list — they are not access-controlled");

        root.Children.Add(new TextBlock
        {
            Text = string.Join(".\n", notes) + ".",
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        });
        root.Children.Add(detectRow);
        root.Children.Add(scroller);
        root.Children.Add(emptyHint);
        root.Children.Add(addRow);

        Rebuild();

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Publish '{groupName}'",
            Content = root,
            PrimaryButtonText = "Publish",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        var result = await dialog.ShowAsync();
        if (result != ContentDialogResult.Primary) return null;

        // Include a name that was typed but not yet added.
        var pending = addBox.Text.Trim();
        if (!string.IsNullOrEmpty(pending)
            && !choices.Any(c => string.Equals(c.Name, pending, StringComparison.OrdinalIgnoreCase)))
        {
            choices.Add(new WorkspaceChoice { Name = pending, IsChecked = true });
        }

        var selected = choices
            .Where(c => c.IsChecked && !string.IsNullOrWhiteSpace(c.Name))
            .Select(c => c.Name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        SaveSavedWorkspaces(selected);
        return selected;
    }

    // ---- Dialogs & persistence -------------------------------------------

    private async Task<string?> PromptForNameAsync(string title, string label, string initial)
    {
        try
        {
            var box = new TextBox
            {
                Header = label,
                Text = initial,
                MaxWidth = 320,
                PlaceholderText = "Group name"
            };

            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = box,
                PrimaryButtonText = "Save",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Primary
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Primary) return null;

            var name = box.Text.Trim();
            return string.IsNullOrEmpty(name) ? null : name;
        }
        catch (Exception ex)
        {
            App.Log($"Prompt for name failed: {ex.Message}");
            return null;
        }
    }

    private async Task<bool> ConfirmAsync(string title, string message)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = title,
                Content = message,
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            App.Log($"Confirm dialog failed: {ex.Message}");
            return false;
        }
    }

    private string SuggestGroupName()
    {
        for (int i = 1; i < 1000; i++)
        {
            var candidate = $"Group {i}";
            if (!_groups.Any(g => string.Equals(g.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return "Group";
    }

    private string UniqueName(string baseName)
    {
        if (!_groups.Any(g => string.Equals(g.Name, baseName, StringComparison.OrdinalIgnoreCase)))
        {
            return baseName;
        }

        for (int i = 2; i < 1000; i++)
        {
            var candidate = $"{baseName} {i}";
            if (!_groups.Any(g => string.Equals(g.Name, candidate, StringComparison.OrdinalIgnoreCase)))
            {
                return candidate;
            }
        }

        return baseName;
    }

    private bool SaveGroups()
    {
        if (!App.Workspace.HasReport) return false;

        try
        {
            var config = ReportConfigStore.Load(App.Workspace.ReportPath);
            config.SetPublishingGroups(_groups.Select(g => g.Model));
            if (!ReportConfigStore.Save(App.Workspace.ReportPath, config))
            {
                ToastService.Show("Could not save publishing groups.", ToastSeverity.Error);
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            App.Log($"Saving publishing groups failed: {ex.Message}");
            ToastService.Show($"Could not save publishing groups: {ex.Message}", ToastSeverity.Error);
            return false;
        }
    }

    private static List<string> LoadSavedWorkspaces()
    {
        try
        {
            if (!File.Exists(SavedWorkspacesPath)) return new List<string>();
            var json = File.ReadAllText(SavedWorkspacesPath);
            return JsonSerializer.Deserialize<List<string>>(json) ?? new List<string>();
        }
        catch
        {
            return new List<string>();
        }
    }

    private static void SaveSavedWorkspaces(List<string> names)
    {
        try
        {
            var dir = Path.GetDirectoryName(SavedWorkspacesPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(SavedWorkspacesPath, JsonSerializer.Serialize(names, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Best-effort convenience only.
        }
    }
}
