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
using Windows.ApplicationModel.DataTransfer;

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

    /// <summary>Ids of the pages a drag started with, so the moved block can be reselected.</summary>
    private List<string> _dragIds = new();

    /// <summary>The row the current drag was grabbed from, kept visible while the rest dim.</summary>
    private string _dragSourceId = string.Empty;

    /// <summary>Selected rows dimmed during a multi-item drag, with their original opacity.</summary>
    private readonly List<(UIElement Element, double Opacity)> _liftedRows = new();

    /// <summary>Which direction a batch move should take the current selection.</summary>
    private enum PageMoveMode
    {
        Up,
        Down,
        Top,
        Bottom
    }

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
        ClearLiftedRows();

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
        UpdateSelectionChrome();
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
        if (IsGoToPageMode) return;
        ApplySnapshot(undo: true, "Undid the last change.");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        ApplySnapshot(undo: false, "Redid the last change.");
    }

    /// <summary>
    /// Steps the shared history and refreshes the list.
    /// </summary>
    /// <remarks>
    /// Goes through <see cref="ReportWorkspace.Undo"/>/<see cref="ReportWorkspace.Redo"/> rather
    /// than <c>ApplyEdit</c>: ApplyEdit records the restore as a new edit, which truncates the
    /// redo branch and leaves Redo permanently disabled.
    /// </remarks>
    private void ApplySnapshot(bool undo, string message)
    {
        try
        {
            var moved = undo ? App.Workspace.Undo() : App.Workspace.Redo();
            if (moved)
            {
                RefreshList();
                ShowStatus(InfoBarSeverity.Informational, message);
            }
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

    private void PagesListView_DragItemsStarting(object sender, DragItemsStartingEventArgs e)
    {
        var selection = SelectedIdsInOrder();
        var dragged = e.Items.OfType<PageItemViewModel>().Select(i => i.Id).ToList();

        // A multi-selection drag starts on one of its rows, so the whole set moves and
        // is represented as one lift. Falls back to what the control reported.
        _dragIds = selection.Count > 1
                   && !string.IsNullOrEmpty(_dragSourceId)
                   && selection.Contains(_dragSourceId, StringComparer.OrdinalIgnoreCase)
            ? selection
            : dragged;

        LiftSelectedRows();
    }

    private void PagesListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // Restore any rows dimmed for the drag before the list is rebuilt (or left as-is
        // when the drop landed outside the list).
        ClearLiftedRows();

        if (IsGoToPageMode) return;

        // A drop outside the list is not a reorder; leave the order alone.
        if (args.DropResult != DataPackageOperation.Move)
        {
            _dragIds.Clear();
            _dragSourceId = string.Empty;
            return;
        }

        var orderedIds = _items.Select(i => i.Id).ToList();
        if (orderedIds.Count > 0)
        {
            var moved = _dragIds.ToList();
            App.Workspace.ApplyEdit(m => m.ReorderPages(orderedIds));
            RefreshList();

            if (moved.Count > 0)
            {
                SelectIds(moved);
                ShowStatus(InfoBarSeverity.Success,
                    moved.Count == 1 ? "Moved 1 page." : $"Moved {moved.Count} pages.");
            }
            else
            {
                ShowStatus(InfoBarSeverity.Success, "Page order updated via drag-and-drop.");
            }
        }

        _dragIds.Clear();
        _dragSourceId = string.Empty;
    }

    /// <summary>
    /// Hides every selected row except the one under the pointer, so a multi-page drag
    /// reads as the whole selection leaving the list and lifting into the drag visual,
    /// instead of only the grabbed row moving while the rest sit in place.
    /// </summary>
    private void LiftSelectedRows()
    {
        ClearLiftedRows();

        if (_dragIds.Count <= 1) return;

        foreach (var item in _items)
        {
            if (!_dragIds.Contains(item.Id, StringComparer.OrdinalIgnoreCase)) continue;

            // Keep the grabbed row visible: WinUI captures the drag visual from it.
            if (string.Equals(item.Id, _dragSourceId, StringComparison.OrdinalIgnoreCase)) continue;

            if (PagesListView.ContainerFromItem(item) is not UIElement container) continue;

            _liftedRows.Add((container, container.Opacity));
            container.Opacity = 0;
        }
    }

    /// <summary>Puts the opacity of rows dimmed for a drag back the way it was.</summary>
    private void ClearLiftedRows()
    {
        foreach (var (element, opacity) in _liftedRows)
        {
            element.Opacity = opacity;
        }

        _liftedRows.Clear();
    }

    private void OverflowButton_Click(object sender, RoutedEventArgs e)
    {
        if (IsGoToPageMode) return;
        if (sender is Button btn && btn.Tag is string id)
        {
            var item = _items.FirstOrDefault(i => i.Id == id);
            if (item != null && !PagesListView.SelectedItems.Contains(item))
            {
                // Only select the row when it is not already part of a multi-selection,
                // so opening its menu cannot destroy the selection being acted on.
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

    private void MoveUp_Click(object sender, RoutedEventArgs e) => MoveFromMenu(sender, PageMoveMode.Up);

    private void MoveDown_Click(object sender, RoutedEventArgs e) => MoveFromMenu(sender, PageMoveMode.Down);

    // ---- Batch move (toolbar dropdown + selection-aware context menu) ----

    private void MoveSelectedUp_Click(object sender, RoutedEventArgs e) => MoveSelection(PageMoveMode.Up);

    private void MoveSelectedDown_Click(object sender, RoutedEventArgs e) => MoveSelection(PageMoveMode.Down);

    private void MoveSelectedToTop_Click(object sender, RoutedEventArgs e) => MoveSelection(PageMoveMode.Top);

    private void MoveSelectedToBottom_Click(object sender, RoutedEventArgs e) => MoveSelection(PageMoveMode.Bottom);

    /// <summary>
    /// Moves the whole selection when the clicked row is part of it, otherwise moves
    /// just that row. Shared by the row context menu and the per-row overflow flyout.
    /// </summary>
    private void MoveFromMenu(object sender, PageMoveMode mode)
    {
        if (IsGoToPageMode) return;

        var item = GetContextItem(sender);
        if (item == null) return;

        var selection = SelectedIdsInOrder();
        if (selection.Count > 1 && selection.Contains(item.Id, StringComparer.OrdinalIgnoreCase))
        {
            MoveSelection(mode);
            return;
        }

        switch (mode)
        {
            case PageMoveMode.Up: MoveUp(item.Id); break;
            case PageMoveMode.Down: MoveDown(item.Id); break;
            case PageMoveMode.Top: MoveToTop(item.Id); break;
            case PageMoveMode.Bottom: MoveToBottom(item.Id); break;
        }
    }

    /// <summary>Moves every selected page as one block, then keeps that block selected.</summary>
    private void MoveSelection(PageMoveMode mode)
    {
        if (IsGoToPageMode) return;

        var ids = SelectedIdsInOrder();
        if (ids.Count == 0) return;

        App.Workspace.ApplyEdit(m =>
        {
            switch (mode)
            {
                case PageMoveMode.Up: m.MovePagesUp(ids); break;
                case PageMoveMode.Down: m.MovePagesDown(ids); break;
                case PageMoveMode.Top: m.MovePagesToTop(ids); break;
                case PageMoveMode.Bottom: m.MovePagesToBottom(ids); break;
            }
        });

        RefreshList();
        SelectIds(ids);
        ShowStatus(InfoBarSeverity.Success,
            ids.Count == 1 ? "Moved 1 page." : $"Moved {ids.Count} pages.");
    }

    /// <summary>The currently selected page ids, in report order.</summary>
    private List<string> SelectedIdsInOrder()
    {
        var selected = new HashSet<PageItemViewModel>(PagesListView.SelectedItems.OfType<PageItemViewModel>());
        return _items.Where(i => selected.Contains(i)).Select(i => i.Id).ToList();
    }

    /// <summary>Selects the pages with the given ids without triggering navigation.</summary>
    private void SelectIds(IEnumerable<string> ids)
    {
        var wanted = new HashSet<string>(ids, StringComparer.OrdinalIgnoreCase);

        var wasSuppressed = _suppressNavigation;
        _suppressNavigation = true;
        try
        {
            PagesListView.SelectedItems.Clear();

            PageItemViewModel? first = null;
            foreach (var item in _items)
            {
                if (!wanted.Contains(item.Id)) continue;
                PagesListView.SelectedItems.Add(item);
                first ??= item;
            }

            if (first != null) PagesListView.ScrollIntoView(first);
        }
        finally
        {
            _suppressNavigation = wasSuppressed;
        }

        UpdateSelectionChrome();
    }

    /// <summary>Keeps the Move button and the list hint in step with the selection.</summary>
    private void UpdateSelectionChrome()
    {
        var count = PagesListView?.SelectedItems?.Count ?? 0;
        var total = _items.Count;

        if (MoveButton != null)
        {
            MoveButton.IsEnabled = !IsGoToPageMode && count > 0;
        }

        if (MoveButtonText != null)
        {
            MoveButtonText.Text = count > 0 ? $"Move ({count})" : "Move";
        }

        if (ListHintText == null) return;

        if (IsGoToPageMode)
        {
            ListHintText.Text = "Select a page to navigate the open report";
        }
        else if (count > 1)
        {
            ListHintText.Text = $"{count} of {total} pages selected \u00B7 drag to move them together";
        }
        else
        {
            ListHintText.Text = "Drag rows to reorder \u00B7 Ctrl/Shift-click to select multiple";
        }
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

    private void ContextMenu_MoveToTop_Click(object sender, RoutedEventArgs e) => MoveFromMenu(sender, PageMoveMode.Top);

    private void ContextMenu_MoveToBottom_Click(object sender, RoutedEventArgs e) => MoveFromMenu(sender, PageMoveMode.Bottom);

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

    /// <summary>
    /// Remembers which row a press landed on, so a later drag knows which row keeps
    /// its drag visual and which of the selection should dim away.
    /// </summary>
    private void Row_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PageItemViewModel vm)
        {
            _dragSourceId = vm.Id;
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

        // Editing works on one page at a time; Go-to-page only ever navigates one.
        PagesListView.SelectionMode = mode ? ListViewSelectionMode.Single : ListViewSelectionMode.Extended;

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
        UpdateSelectionChrome();
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
        UpdateSelectionChrome();

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
