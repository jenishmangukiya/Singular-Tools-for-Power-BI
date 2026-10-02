using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using SingularTools.Core;
using SingularTools.Core.Models;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.ReportPagesManager;

public class PageItemViewModel : INotifyPropertyChanged
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string Subtitle { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public string OrderIndexText => $"#{OrderIndex + 1}";
    public bool IsActive { get; set; }
    public bool IsHidden { get; set; }
    public string SizeText { get; set; } = "1920x1080";

    public Visibility ActiveBadgeVisibility => IsActive ? Visibility.Visible : Visibility.Collapsed;
    public Visibility HiddenBadgeVisibility => IsHidden ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Crossed-eye marker ahead of the name; falls back while the rename box owns the row.</summary>
    public Visibility HiddenEyeVisibility => IsHidden && !_isEditing ? Visibility.Visible : Visibility.Collapsed;

    private bool _isEditing;
    public bool IsEditing
    {
        get => _isEditing;
        set
        {
            if (_isEditing == value) return;
            _isEditing = value;
            Raise(nameof(IsEditing));
            Raise(nameof(NameVisibility));
            Raise(nameof(EditVisibility));
            Raise(nameof(HiddenEyeVisibility));
        }
    }

    private string _editName = string.Empty;
    public string EditName
    {
        get => _editName;
        set
        {
            if (_editName == value) return;
            _editName = value;
            Raise(nameof(EditName));
        }
    }

    public Visibility NameVisibility => IsEditing ? Visibility.Collapsed : Visibility.Visible;
    public Visibility EditVisibility => IsEditing ? Visibility.Visible : Visibility.Collapsed;

    private double _gripOpacity = 0.5;
    public double GripOpacity
    {
        get => _gripOpacity;
        set
        {
            if (Math.Abs(_gripOpacity - value) < 0.01) return;
            _gripOpacity = value;
            Raise(nameof(GripOpacity));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class ReportPagesManagerPage : Page, IToolPage
{
    public string ToolId => "report-pages-manager";
    public string Title => "Pages Manager";
    public string Description => "Search, reorder, rename and organize report pages";
    public string Glyph => "\uE8A9";

    private ReportManager _reportManager => App.Workspace.Manager;
    private readonly ObservableCollection<PageItemViewModel> _items = new();
    private bool _suppressNavigation;
    private bool _pendingRefresh;

    /// <summary>True while the Go-to-page toggle is on: selection drives Desktop instead of editing.</summary>
    public bool IsGoToPageMode => GoToPageBtn?.IsChecked == true;

    /// <summary>Go-to-page drives Desktop itself, so the focus-regain UIA save is skipped.</summary>
    public bool SkipAutoPowerBiSync => IsGoToPageMode;

    public ReportPagesManagerPage()
    {
        InitializeComponent();
        PagesListView.ItemsSource = _items;

        Loaded += ReportPagesManagerPage_Loaded;
        Unloaded += ReportPagesManagerPage_Unloaded;
    }

    private void ReportPagesManagerPage_Unloaded(object sender, RoutedEventArgs e)
    {
        App.Workspace.Changed -= Workspace_Changed;
    }

    private void Workspace_Changed(object? sender, EventArgs e)
    {
        // Don't tear down an open inline editor; refresh once it commits.
        if (_items.Any(i => i.IsEditing))
        {
            _pendingRefresh = true;
            return;
        }

        RefreshList();
        UpdateReportStatus();
        UpdateHistoryButtons();
    }

    public void OnActivated()
    {
        RefreshList();
        UpdateReportStatus();
        FocusSearchBox();
    }

    public void FocusSearchBox()
    {
        try
        {
            SearchBox?.Focus(FocusState.Programmatic);
        }
        catch { }
    }

    private void ReportPagesManagerPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("ReportPagesManagerPage loaded.");
            App.Workspace.Changed -= Workspace_Changed;
            App.Workspace.Changed += Workspace_Changed;
            UpdateReportStatus();
            RefreshList();
            FocusSearchBox();
        }
        catch (Exception ex)
        {
            App.Log($"Error in ReportPagesManagerPage_Loaded: {ex}");
        }
    }

    private void UpdateReportStatus()
    {
        if (ReportPathText == null) return;
        ReportPathText.Text = App.Workspace.HasReport ? App.Workspace.ReportName : "No report loaded";
    }

    public void RefreshList()
    {
        _suppressNavigation = true;
        _items.Clear();

        foreach (var p in _reportManager.Pages)
        {
            _items.Add(new PageItemViewModel
            {
                Id = p.Id,
                DisplayName = p.DisplayName,
                Subtitle = $"ID: {p.Id.Substring(0, Math.Min(8, p.Id.Length))}...",
                OrderIndex = p.OrderIndex,
                IsActive = p.IsActive,
                IsHidden = p.IsHidden,
                SizeText = p.FormattedSize
            });
        }

        PageCountText.Text = _reportManager.Pages.Count > 0
            ? $"{_reportManager.Pages.Count} pages"
            : "0 pages";

        if (_items.Count > 0 && PagesListView.SelectedItem == null)
        {
            PagesListView.SelectedIndex = 0;
        }

        _suppressNavigation = false;
        UpdateEmptyStates();
        WireGripCursors();
    }

    /// <summary>Gives every realized drag grip the hand cursor (idempotent per container).</summary>
    private void WireGripCursors()
    {
        foreach (var item in _items)
        {
            if (PagesListView.ContainerFromItem(item) is not DependencyObject container) continue;
            if (FindDescendant<StackPanel>(container, "GripHandle") is not StackPanel grip) continue;

            if (grip.Tag as string != "cursor-wired")
            {
                grip.Tag = "cursor-wired";
                grip.PointerMoved += (_, _) => NativeInput.SetHandCursor();
                grip.PointerExited += (_, _) => NativeInput.SetArrowCursor();
            }

            grip.Visibility = IsGoToPageMode ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    private void UpdateEmptyStates()
    {
        bool hasReport = !string.IsNullOrEmpty(_reportManager.ReportFolderPath);
        bool hasItems = _items.Count > 0;

        ListCard.Visibility = hasReport && hasItems ? Visibility.Visible : Visibility.Collapsed;
        NoReportPanel.Visibility = hasReport ? Visibility.Collapsed : Visibility.Visible;

        if (hasReport && !hasItems)
        {
            NoResultsText.Text = "This report has no pages.";
            NoResultsPanel.Visibility = Visibility.Visible;
        }
        else
        {
            NoResultsPanel.Visibility = Visibility.Collapsed;
        }
    }

    private void ExecuteSort(SortMode mode, string message)
    {
        if (IsGoToPageMode) return;

        App.Workspace.ApplyEdit(m => m.SortPages(mode));
        RefreshList();
        ShowStatus(InfoBarSeverity.Success, message);
    }

    private void ReloadPages()
    {
        App.Workspace.Reload();
        RefreshList();
        UpdateHistoryButtons();
        ShowStatus(InfoBarSeverity.Informational, "Reloaded pages from the report definition.");
    }

    private void UpdateHistoryButtons()
    {
        var history = App.Workspace.History;
        if (UndoButton != null) UndoButton.IsEnabled = !IsGoToPageMode && history?.CanUndo == true;
        if (RedoButton != null) RedoButton.IsEnabled = !IsGoToPageMode && history?.CanRedo == true;
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var history = App.Workspace.History;
        if (IsGoToPageMode || history == null || !history.CanUndo) return;
        ApplySnapshot(history.Undo(), "Undid the last change.");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        var history = App.Workspace.History;
        if (IsGoToPageMode || history == null || !history.CanRedo) return;
        ApplySnapshot(history.Redo(), "Redid the last change.");
    }

    private void ApplySnapshot(string? snapshot, string message)
    {
        if (string.IsNullOrEmpty(snapshot))
        {
            UpdateHistoryButtons();
            return;
        }

        try
        {
            App.Workspace.ApplyEdit(m => m.RestoreFromSnapshot(snapshot), managerWritesInternally: true, syncFirst: false);
            RefreshList();
            ShowStatus(InfoBarSeverity.Informational, message);
        }
        catch (Exception ex)
        {
            ShowStatus(InfoBarSeverity.Error, $"Could not restore history: {ex.Message}", autoDismiss: false);
        }

        UpdateHistoryButtons();
    }

    private static void ShowStatus(InfoBarSeverity severity, string message, bool autoDismiss = true)
    {
        var toastSeverity = severity switch
        {
            InfoBarSeverity.Success => ToastSeverity.Success,
            InfoBarSeverity.Warning => ToastSeverity.Warning,
            InfoBarSeverity.Error => ToastSeverity.Error,
            _ => ToastSeverity.Informational
        };

        // Non-dismissing messages just stay on screen longer.
        ToastService.Show(message, toastSeverity, autoDismiss ? null : 8000);
    }

    private void SearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        var query = sender.Text?.Trim() ?? string.Empty;
        if (query.Length == 0)
        {
            sender.ItemsSource = null;
            sender.IsSuggestionListOpen = false;
            return;
        }

        sender.ItemsSource = _items
            .Where(p => p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .Take(20)
            .ToList();
    }

    private void SearchBox_SuggestionChosen(AutoSuggestBox sender, AutoSuggestBoxSuggestionChosenEventArgs args)
    {
        if (args.SelectedItem is PageItemViewModel page)
        {
            sender.Text = page.DisplayName;
        }
    }

    private void SearchBox_QuerySubmitted(AutoSuggestBox sender, AutoSuggestBoxQuerySubmittedEventArgs args)
    {
        var queryText = args.QueryText?.Trim() ?? string.Empty;

        var page = args.ChosenSuggestion as PageItemViewModel
                   ?? _items.FirstOrDefault(p => string.Equals(p.DisplayName, queryText, StringComparison.OrdinalIgnoreCase))
                   ?? _items.FirstOrDefault(p => p.DisplayName.Contains(queryText, StringComparison.OrdinalIgnoreCase));

        // Close the dropdown explicitly before moving focus / clearing text.
        // Relying on the control's automatic dismissal leaves the popup open,
        // so it would only close on a second click.
        sender.IsSuggestionListOpen = false;
        sender.ItemsSource = null;
        sender.Text = string.Empty;

        if (page == null)
        {
            return;
        }

        RevealPageInList(page);
    }

    /// <summary>Selects and scrolls to a page in the list without changing the active page.</summary>
    private void RevealPageInList(PageItemViewModel page)
    {
        var wasSuppressed = _suppressNavigation;
        _suppressNavigation = true;
        try
        {
            PagesListView.SelectedItem = page;
            PagesListView.ScrollIntoView(page);
            PagesListView.Focus(FocusState.Programmatic);
        }
        finally
        {
            _suppressNavigation = wasSuppressed;
        }
    }

    private void ClearSearch_Click(object sender, RoutedEventArgs e)
    {
        SearchBox.Text = string.Empty;
        FocusSearchBox();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => ReloadPages();

    private void PagesListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        var selected = PagesListView.SelectedItem as PageItemViewModel;

        // While an inline rename editor is open, let it own the keyboard.
        if (_items.Any(i => i.IsEditing)) return;

        if (IsGoToPageMode)
        {
            // In go-to-page mode the list is read-only: only navigation keys act.
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                if (PagesListView.SelectedItem is PageItemViewModel nav)
                {
                    NavigateToOpenReport(nav);
                }
                e.Handled = true;
            }
            return;
        }

        if (isCtrl && e.Key == Windows.System.VirtualKey.Z)
        {
            Undo_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (isCtrl && e.Key == Windows.System.VirtualKey.Y)
        {
            Redo_Click(sender, e);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            TriggerPrimaryAction();
            e.Handled = true;
            return;
        }

        if (selected == null) return;

        if (isCtrl && e.Key == Windows.System.VirtualKey.D)
        {
            DuplicatePage(selected.Id);
            e.Handled = true;
            return;
        }

        if (isCtrl && e.Key == Windows.System.VirtualKey.F)
        {
            FocusSearchBox();
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.F2)
        {
            BeginRename(selected);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            DeletePage(selected.Id);
            e.Handled = true;
            return;
        }

    }

    private void PagesListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        if (IsGoToPageMode) return;

        var orderedIds = _items.Select(i => i.Id).ToList();
        if (orderedIds.Count > 0)
        {
            App.Workspace.ApplyEdit(m => m.ReorderPages(orderedIds));
            RefreshList();
            ShowStatus(InfoBarSeverity.Success, "Page order updated via drag-and-drop.");
        }
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        if (sender is Button btn && btn.Tag is string id)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null)
            {
                PagesListView.SelectedItem = item;
            }
        }
    }

    private void DuplicatePage(string id)
    {
        if (IsGoToPageMode) return;
        var newPage = App.Workspace.ApplyEditWithResult(m => m.DuplicatePage(id), managerWritesInternally: true);
        if (newPage != null)
        {
            RefreshList();
            ShowStatus(InfoBarSeverity.Success, $"Duplicated page as '{newPage.DisplayName}'.");

            var newItem = _items.FirstOrDefault(i => i.Id == newPage.Id);
            if (newItem != null)
            {
                PagesListView.SelectedItem = newItem;
                PagesListView.ScrollIntoView(newItem);
            }
        }
    }

    private async void DeletePage(string id)
    {
        if (IsGoToPageMode) return;
        if (_reportManager.Pages.Count <= 1)
        {
            ShowStatus(InfoBarSeverity.Warning, "Cannot delete the only remaining page in the report.", autoDismiss: false);
            return;
        }

        var pageToDelete = _reportManager.Pages.FirstOrDefault(p => p.Id == id);
        var pageName = pageToDelete?.DisplayName ?? id;

        var confirmed = await ConfirmDeleteAsync(pageName);
        if (!confirmed) return;

        // If the page being removed is the one Power BI currently has open, move
        // Power BI onto another page first so it doesn't fail applying the
        // external change with "ActivePageName not found".
        if (string.Equals(_reportManager.ActivePageId, id, StringComparison.OrdinalIgnoreCase))
        {
            var fallback = _reportManager.Pages.FirstOrDefault(p => !string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));
            if (fallback != null)
            {
                PowerBiNavigator.TryGoToPage(fallback.DisplayName, out _);
            }
        }

        if (App.Workspace.ApplyEditWithResult(m => m.DeletePage(id), managerWritesInternally: true))
        {
            RefreshList();
            ShowStatus(InfoBarSeverity.Success, $"Deleted page '{pageName}'.");
        }
    }

    private async System.Threading.Tasks.Task<bool> ConfirmDeleteAsync(string pageName)
    {
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = XamlRoot,
                Title = "Delete page?",
                Content = $"'{pageName}' and its folder will be removed from the report. You can undo this with Ctrl+Z.",
                PrimaryButtonText = "Delete",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close
            };

            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary;
        }
        catch (Exception ex)
        {
            App.Log($"Confirm delete dialog failed: {ex.Message}");
            return false;
        }
    }

    private void TriggerPrimaryAction()
    {
        var selected = PagesListView.SelectedItem as PageItemViewModel;
        if (selected == null && _items.Count > 0)
        {
            selected = _items[0];
        }

        if (selected == null || selected.IsEditing) return;

        if (IsGoToPageMode)
        {
            NavigateToOpenReport(selected);
            return;
        }

        ActivatePage(selected);
    }

    /// <summary>Sets the given page as the active page, persists it and (optionally) navigates the open report.</summary>
    private void ActivatePage(PageItemViewModel page)
    {
        if (page == null) return;

        App.Workspace.ApplyEdit(m => m.SetActivePage(page.Id));
        RefreshList();
        ReselectId(page.Id);
        ShowStatus(InfoBarSeverity.Success, $"Active page set to '{page.DisplayName}'.");

        if (GoToPageBtn?.IsChecked == true)
        {
            NavigateToOpenReport(page);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item != null) MoveUp(item.Id);
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item != null) MoveDown(item.Id);
    }

    private PageItemViewModel? GetContextItem(object sender)
    {
        if (sender is FrameworkElement fe)
        {
            if (fe.DataContext is PageItemViewModel vm) return vm;
            if (fe.Tag is string id) return _items.FirstOrDefault(i => i.Id == id);
        }
        return PagesListView.SelectedItem as PageItemViewModel;
    }

    private void ContextMenu_SetActive_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item == null) return;

        App.Workspace.ApplyEdit(m => m.SetActivePage(item.Id));
        RefreshList();
        ShowStatus(InfoBarSeverity.Success, $"Active page set to '{item.DisplayName}'.");
    }

    private void ContextMenu_Rename_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item != null) BeginRename(item);
    }

    private void ContextMenu_Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item != null) DuplicatePage(item.Id);
    }

    private void ContextMenu_ToggleHide_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item == null) return;
        App.Workspace.ApplyEdit(m => m.TogglePageVisibility(item.Id));
        RefreshList();
        ShowStatus(InfoBarSeverity.Success, "Updated page visibility.");
    }

    private void ContextMenu_MoveToTop_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item != null) MoveToTop(item.Id);
    }

    private void ContextMenu_MoveToBottom_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item != null) MoveToBottom(item.Id);
    }

    private void ContextMenu_Delete_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        var item = GetContextItem(sender);
        if (item != null) DeletePage(item.Id);
    }

    private void MoveUp(string id)
    {
        if (IsGoToPageMode) return;
        App.Workspace.ApplyEdit(m => m.MovePageUp(id));
        RefreshList();
        ReselectId(id);
        ShowStatus(InfoBarSeverity.Success, "Moved page up.");
    }

    private void MoveDown(string id)
    {
        if (IsGoToPageMode) return;
        App.Workspace.ApplyEdit(m => m.MovePageDown(id));
        RefreshList();
        ReselectId(id);
        ShowStatus(InfoBarSeverity.Success, "Moved page down.");
    }

    private void MoveToTop(string id)
    {
        if (IsGoToPageMode) return;
        App.Workspace.ApplyEdit(m => m.MovePageToTop(id));
        RefreshList();
        ReselectId(id);
        ShowStatus(InfoBarSeverity.Success, "Moved page to top.");
    }

    private void MoveToBottom(string id)
    {
        if (IsGoToPageMode) return;
        App.Workspace.ApplyEdit(m => m.MovePageToBottom(id));
        RefreshList();
        ReselectId(id);
        ShowStatus(InfoBarSeverity.Success, "Moved page to bottom.");
    }

    private void ReselectId(string id)
    {
        var target = _items.FirstOrDefault(i => i.Id == id);
        if (target != null)
        {
            PagesListView.SelectedItem = target;
            PagesListView.ScrollIntoView(target);
        }
    }

    private void BeginRename(PageItemViewModel item)
    {
        if (item == null || IsGoToPageMode) return;

        foreach (var other in _items)
        {
            if (!ReferenceEquals(other, item)) other.IsEditing = false;
        }

        item.EditName = item.DisplayName;
        item.IsEditing = true;
        PagesListView.SelectedItem = item;
        PagesListView.ScrollIntoView(item);
        PagesListView.UpdateLayout();

        if (!TryFocusRenameBox(item))
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                PagesListView.UpdateLayout();
                if (!TryFocusRenameBox(item))
                {
                    App.Log("Rename: could not focus the inline editor.");
                }
            });
        }
    }

    private bool TryFocusRenameBox(PageItemViewModel item)
    {
        if (PagesListView.ContainerFromItem(item) is not DependencyObject container) return false;

        var box = FindDescendant<TextBox>(container, "RenameBox");
        if (box == null || box.Visibility != Visibility.Visible) return false;

        box.Focus(FocusState.Programmatic);
        box.SelectAll();
        return true;
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PageItemViewModel vm)
        {
            vm.GripOpacity = 1.0;
        }
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PageItemViewModel vm)
        {
            vm.GripOpacity = 0.5;
        }
    }

    private static T? FindDescendant<T>(DependencyObject root, string? name) where T : FrameworkElement
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match && (name == null || match.Name == name)) return match;

            var nested = FindDescendant<T>(child, name);
            if (nested != null) return nested;
        }
        return null;
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string id) return;
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item == null) return;

        if (e.Key == Windows.System.VirtualKey.Enter)
        {
            CommitRename(item, tb.Text);
            PagesListView.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            item.EditName = item.DisplayName;
            item.IsEditing = false;
            PagesListView.Focus(FocusState.Programmatic);
            e.Handled = true;
        }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb || tb.Tag is not string id) return;
        var item = _items.FirstOrDefault(i => i.Id == id);
        if (item != null) CommitRename(item, tb.Text);
    }

    private void CommitRename(PageItemViewModel item, string? enteredText = null)
    {
        if (item == null || !item.IsEditing) return;
        item.IsEditing = false;

        var newName = (enteredText ?? item.EditName)?.Trim() ?? string.Empty;
        if (string.IsNullOrEmpty(newName) || string.Equals(newName, item.DisplayName, StringComparison.Ordinal))
        {
            item.EditName = item.DisplayName;
            return;
        }

        App.Workspace.ApplyEdit(m => m.RenamePage(item.Id, newName));
        item.DisplayName = newName;
        RefreshList();
        ReselectId(item.Id);
        ShowStatus(InfoBarSeverity.Success, $"Renamed page to '{newName}'.");

        if (_pendingRefresh)
        {
            _pendingRefresh = false;
            RefreshList();
            UpdateReportStatus();
            UpdateHistoryButtons();
        }
    }

    private void SortAz_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Ascending, "Sorted pages A-Z");
    private void SortZa_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Descending, "Sorted pages Z-A");
    private void SortNatural_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Natural, "Naturally sorted pages");
    private void SortReverse_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Reverse, "Reversed page order");

    private void GoToPage_Click(object sender, RoutedEventArgs e)
    {
        ApplyGoToPageMode();

        if (GoToPageBtn.IsChecked == true)
        {
            ShowStatus(InfoBarSeverity.Informational, "Go-to-page is ON. Editing is disabled; selecting a page navigates the open Power BI report.");
            var selected = PagesListView.SelectedItem as PageItemViewModel;
            if (selected != null)
            {
                NavigateToOpenReport(selected);
            }
        }
        else
        {
            ShowStatus(InfoBarSeverity.Informational, "Go-to-page is OFF.");
        }
    }

    /// <summary>Disables every editing affordance while Go-to-page is on, and restores them when it turns off.</summary>
    private void ApplyGoToPageMode()
    {
        var mode = IsGoToPageMode;

        SortButton.IsEnabled = !mode;
        UpdateHistoryButtons();

        PagesListView.CanDragItems = !mode;
        PagesListView.CanReorderItems = !mode;
        PagesListView.AllowDrop = !mode;

        // SearchBox and ReloadButton deliberately stay enabled in both modes.
        foreach (var item in _items)
        {
            if (PagesListView.ContainerFromItem(item) is DependencyObject container)
            {
                if (FindDescendant<Button>(container, "OverflowBtn") is Button overflow)
                {
                    overflow.IsEnabled = !mode;
                }

                if (FindDescendant<Button>(container, "DeleteBtn") is Button del)
                {
                    del.IsEnabled = !mode;
                }

                if (FindDescendant<Grid>(container, "RowGrid") is Grid row)
                {
                    if (mode)
                    {
                        // Drop the right-click menu; keep it in Tag to restore later.
                        if (row.Tag is not MenuFlyout && row.ContextFlyout is MenuFlyout original)
                        {
                            row.Tag = original;
                        }
                        row.ContextFlyout = null;
                    }
                    else if (row.Tag is MenuFlyout saved)
                    {
                        row.ContextFlyout = saved;
                        row.Tag = null;
                    }
                }
            }
        }

        WireGripCursors();
        App.Log($"Go-to-page mode {(mode ? "enabled" : "disabled")}.");
    }

    private void PagesListView_ContextRequested(UIElement sender, ContextRequestedEventArgs e)
    {
        if (IsGoToPageMode)
        {
            e.Handled = true;
        }
    }

    private void PagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation || GoToPageBtn?.IsChecked != true)
        {
            return;
        }

        var selected = PagesListView.SelectedItem as PageItemViewModel;
        if (selected == null)
        {
            return;
        }

        NavigateToOpenReport(selected);
    }

    private void NavigateToOpenReport(PageItemViewModel page)
    {
        if (page == null) return;

        if (PowerBiNavigator.TryGoToPage(page.DisplayName, out var error))
        {
            ShowStatus(InfoBarSeverity.Success, $"Went to '{page.DisplayName}' in the open Power BI report.");
        }
        else
        {
            ShowStatus(InfoBarSeverity.Warning, $"Could not go to '{page.DisplayName}': {error}");
        }
    }

    private void GoToHome_Click(object sender, RoutedEventArgs e)
    {
        App.CurrentMainWindow?.NavigateToTool("home");
    }
}
