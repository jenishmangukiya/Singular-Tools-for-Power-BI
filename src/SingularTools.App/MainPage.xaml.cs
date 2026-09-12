using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SingularTools.Core;
using SingularTools.Core.Models;
using Windows.Storage.Pickers;

namespace SingularTools_App;

public class PageItemViewModel
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
}

public sealed partial class MainPage : Page
{
    private readonly ReportManager _reportManager = new();
    private readonly ObservableCollection<PageItemViewModel> _items = new();
    private string _filterText = string.Empty;
    private bool _suppressNavigation;

    public MainPage()
    {
        InitializeComponent();
        PagesListView.ItemsSource = _items;

        Loaded += MainPage_Loaded;
    }

    public void FocusSearchBox()
    {
        try
        {
            if (SearchBox != null)
            {
                SearchBox.Focus(FocusState.Programmatic);
                SearchBox.SelectAll();
            }
        }
        catch { }
    }

    private void MainPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("MainPage_Loaded triggered.");
            // Try auto-discovering PBIP report in current or parent directory
            var discovered = ReportManager.DiscoverReportFolder();
            if (discovered != null)
            {
                LoadReportFolder(discovered);
            }
            else
            {
                StatusMessageText.Text = "No PBIP report detected. Click 'Open Report' to select a report folder.";
            }

            FocusSearchBox();
        }
        catch (Exception ex)
        {
            App.Log($"Error in MainPage_Loaded: {ex}");
        }
    }

    public void LoadReportFolder(string path)
    {
        try
        {
            if (_reportManager.LoadReport(path))
            {
                ReportPathText.Text = Path.GetFileName(_reportManager.ReportFolderPath);
                StatusMessageText.Text = $"Loaded {_reportManager.Pages.Count} pages from {Path.GetFileName(_reportManager.ReportFolderPath)}";
                RefreshList();
            }
            else
            {
                StatusMessageText.Text = "Selected folder does not contain definition/pages/pages.json";
            }
        }
        catch (Exception ex)
        {
            StatusMessageText.Text = $"Error: {ex.Message}";
        }
    }

    public void RefreshList()
    {
        _suppressNavigation = true;
        _items.Clear();

        var query = _filterText.Trim();

        // Check if command mode
        if (query.StartsWith(">"))
        {
            HandleCommandQuery(query);
            _suppressNavigation = false;
            return;
        }

        var filtered = _reportManager.Pages
            .Where(p => string.IsNullOrWhiteSpace(query) ||
                        p.DisplayName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                        p.Id.Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();

        foreach (var p in filtered)
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

        PageCountText.Text = $"{_items.Count} of {_reportManager.Pages.Count} pages";

        if (_items.Count > 0 && PagesListView.SelectedItem == null)
        {
            PagesListView.SelectedIndex = 0;
        }

        _suppressNavigation = false;
    }

    private void HandleCommandQuery(string query)
    {
        var cmd = query.TrimStart('>').Trim().ToLowerInvariant();

        var commands = new List<(string name, string desc, Action action)>
        {
            ("duplicate", "Clone the currently selected page", () => DuplicateSelected()),
            ("delete", "Delete the currently selected page", () => DeleteSelected()),
            ("sort az", "Sort all report pages in alphabetical order (A-Z)", () => ExecuteSort(SortMode.Ascending, "Sorted pages A-Z")),
            ("sort za", "Sort all report pages in reverse alphabetical order (Z-A)", () => ExecuteSort(SortMode.Descending, "Sorted pages Z-A")),
            ("sort natural", "Sort pages naturally by numbers (Page 1, 2, 10)", () => ExecuteSort(SortMode.Natural, "Naturally sorted pages")),
            ("sort reverse", "Invert the current order of pages", () => ExecuteSort(SortMode.Reverse, "Reversed page order")),
            ("save", "Save current page configuration to pages.json", () => SaveChanges()),
            ("reload", "Reload report pages from disk", () => ReloadPages())
        };

        var matched = commands.Where(c => string.IsNullOrWhiteSpace(cmd) || c.name.Contains(cmd)).ToList();

        int idx = 0;
        foreach (var c in matched)
        {
            _items.Add(new PageItemViewModel
            {
                Id = $"cmd:{c.name}",
                DisplayName = $"> {c.name}",
                Subtitle = c.desc,
                OrderIndex = idx++,
                IsActive = false,
                IsHidden = false,
                SizeText = "Command"
            });
        }

        PageCountText.Text = $"{matched.Count} commands";
        if (_items.Count > 0)
        {
            PagesListView.SelectedIndex = 0;
        }
    }

    private void ExecuteCommand(string cmdId)
    {
        if (cmdId == "cmd:duplicate") DuplicateSelected();
        else if (cmdId == "cmd:delete") DeleteSelected();
        else if (cmdId == "cmd:sort az") ExecuteSort(SortMode.Ascending, "Sorted pages A-Z");
        else if (cmdId == "cmd:sort za") ExecuteSort(SortMode.Descending, "Sorted pages Z-A");
        else if (cmdId == "cmd:sort natural") ExecuteSort(SortMode.Natural, "Naturally sorted pages");
        else if (cmdId == "cmd:sort reverse") ExecuteSort(SortMode.Reverse, "Reversed page order");
        else if (cmdId == "cmd:save") SaveChanges();
        else if (cmdId == "cmd:reload") ReloadPages();

        SearchBox.Text = string.Empty;
    }

    private void ExecuteSort(SortMode mode, string message)
    {
        _reportManager.SortPages(mode);
        _reportManager.SaveChanges();
        StatusMessageText.Text = $"{message} & saved to pages.json";
        RefreshList();
    }

    private void SaveChanges()
    {
        _reportManager.SaveChanges();
        StatusMessageText.Text = "✓ Saved page order and settings to pages.json";
    }

    private void ReloadPages()
    {
        _reportManager.Reload();
        StatusMessageText.Text = "Reloaded pages from report definition.";
        RefreshList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _filterText = SearchBox.Text;
        RefreshList();
    }

    private void SearchBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Down)
        {
            PagesListView.Focus(FocusState.Programmatic);
            if (PagesListView.SelectedIndex < _items.Count - 1)
            {
                PagesListView.SelectedIndex++;
            }
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            TriggerPrimaryAction();
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Escape)
        {
            // Close or clear
            if (!string.IsNullOrEmpty(SearchBox.Text))
            {
                SearchBox.Text = string.Empty;
                e.Handled = true;
            }
        }
    }

    private void PagesListView_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        var isAlt = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Menu) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var isCtrl = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;
        var isShift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) == Windows.UI.Core.CoreVirtualKeyStates.Down;

        var selected = PagesListView.SelectedItem as PageItemViewModel;
        if (selected == null) return;

        if (isCtrl && e.Key == Windows.System.VirtualKey.D)
        {
            DuplicatePage(selected.Id);
            e.Handled = true;
            return;
        }

        if (e.Key == Windows.System.VirtualKey.Delete)
        {
            DeletePage(selected.Id);
            e.Handled = true;
            return;
        }

        if (isAlt && e.Key == Windows.System.VirtualKey.Up)
        {
            MoveUp(selected.Id);
            e.Handled = true;
        }
        else if (isAlt && e.Key == Windows.System.VirtualKey.Down)
        {
            MoveDown(selected.Id);
            e.Handled = true;
        }
        else if (isCtrl && isShift && e.Key == Windows.System.VirtualKey.Up)
        {
            MoveToTop(selected.Id);
            e.Handled = true;
        }
        else if (isCtrl && isShift && e.Key == Windows.System.VirtualKey.Down)
        {
            MoveToBottom(selected.Id);
            e.Handled = true;
        }
        else if (e.Key == Windows.System.VirtualKey.Enter)
        {
            TriggerPrimaryAction();
            e.Handled = true;
        }
    }

    private void PagesListView_DragItemsCompleted(ListViewBase sender, DragItemsCompletedEventArgs args)
    {
        // Re-sync items order from _items collection back to ReportManager
        var orderedIds = _items.Where(i => !i.Id.StartsWith("cmd:")).Select(i => i.Id).ToList();
        if (orderedIds.Count > 0)
        {
            _reportManager.ReorderPages(orderedIds);
            _reportManager.SaveChanges();
            StatusMessageText.Text = "✓ Page order updated via drag-and-drop (saved to pages.json)";
            RefreshList();
        }
    }

    private void DuplicateSelected()
    {
        var selected = PagesListView.SelectedItem as PageItemViewModel;
        if (selected != null && !selected.Id.StartsWith("cmd:"))
        {
            DuplicatePage(selected.Id);
        }
        else if (_items.Count > 0 && !_items[0].Id.StartsWith("cmd:"))
        {
            DuplicatePage(_items[0].Id);
        }
    }

    private void DeleteSelected()
    {
        var selected = PagesListView.SelectedItem as PageItemViewModel;
        if (selected != null && !selected.Id.StartsWith("cmd:"))
        {
            DeletePage(selected.Id);
        }
    }

    private void DuplicatePage(string id)
    {
        var newPage = _reportManager.DuplicatePage(id);
        if (newPage != null)
        {
            StatusMessageText.Text = $"✓ Duplicated page as '{newPage.DisplayName}' (saved to pages.json)";
            RefreshList();

            var newItem = _items.FirstOrDefault(i => i.Id == newPage.Id);
            if (newItem != null)
            {
                PagesListView.SelectedItem = newItem;
                PagesListView.ScrollIntoView(newItem);
            }
        }
    }

    private void DeletePage(string id)
    {
        if (_reportManager.Pages.Count <= 1)
        {
            StatusMessageText.Text = "Cannot delete the only remaining page in the report.";
            return;
        }

        var pageToDelete = _reportManager.Pages.FirstOrDefault(p => p.Id == id);
        var pageName = pageToDelete?.DisplayName ?? id;

        if (_reportManager.DeletePage(id))
        {
            StatusMessageText.Text = $"✓ Deleted page '{pageName}'";
            RefreshList();
        }
    }

    private void TriggerPrimaryAction()
    {
        var selected = PagesListView.SelectedItem as PageItemViewModel;
        if (selected == null && _items.Count > 0)
        {
            selected = _items[0];
        }

        if (selected == null) return;

        if (selected.Id.StartsWith("cmd:"))
        {
            ExecuteCommand(selected.Id);
            return;
        }

        // Set as active page
        _reportManager.SetActivePage(selected.Id);
        _reportManager.SaveChanges();
        StatusMessageText.Text = $"Active page set to '{selected.DisplayName}' (saved to pages.json)";
        RefreshList();

        // Optionally navigate the open Power BI report to this page
        if (GoToPageBtn?.IsChecked == true)
        {
            NavigateToOpenReport(selected);
        }
    }

    private void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            MoveUp(id);
        }
    }

    private void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            MoveDown(id);
        }
    }

    private void MoveToTop_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            MoveToTop(id);
        }
    }

    private void SetActive_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _reportManager.SetActivePage(id);
            _reportManager.SaveChanges();
            StatusMessageText.Text = $"Active page set to '{_reportManager.Pages.First(p => p.Id == id).DisplayName}'";
            RefreshList();
        }
    }

    private void ToggleHide_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            _reportManager.TogglePageVisibility(id);
            _reportManager.SaveChanges();
            StatusMessageText.Text = $"Updated visibility for page";
            RefreshList();
        }
    }

    private void Duplicate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            DuplicatePage(id);
        }
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn && btn.Tag is string id)
        {
            DeletePage(id);
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
        var item = GetContextItem(sender);
        if (item != null)
        {
            _reportManager.SetActivePage(item.Id);
            _reportManager.SaveChanges();
            StatusMessageText.Text = $"Active page set to '{item.DisplayName}'";
            RefreshList();
        }
    }

    private void ContextMenu_Duplicate_Click(object sender, RoutedEventArgs e)
    {
        var item = GetContextItem(sender);
        if (item != null)
        {
            DuplicatePage(item.Id);
        }
    }

    private void ContextMenu_ToggleHide_Click(object sender, RoutedEventArgs e)
    {
        var item = GetContextItem(sender);
        if (item != null)
        {
            _reportManager.TogglePageVisibility(item.Id);
            _reportManager.SaveChanges();
            StatusMessageText.Text = "Updated visibility for page";
            RefreshList();
        }
    }

    private void ContextMenu_MoveToTop_Click(object sender, RoutedEventArgs e)
    {
        var item = GetContextItem(sender);
        if (item != null)
        {
            MoveToTop(item.Id);
        }
    }

    private void ContextMenu_MoveToBottom_Click(object sender, RoutedEventArgs e)
    {
        var item = GetContextItem(sender);
        if (item != null)
        {
            MoveToBottom(item.Id);
        }
    }

    private void ContextMenu_Delete_Click(object sender, RoutedEventArgs e)
    {
        var item = GetContextItem(sender);
        if (item != null)
        {
            DeletePage(item.Id);
        }
    }

    private void MoveUp(string id)
    {
        _reportManager.MovePageUp(id);
        _reportManager.SaveChanges();
        StatusMessageText.Text = $"Moved page up (order saved)";
        RefreshList();
        ReselectId(id);
    }

    private void MoveDown(string id)
    {
        _reportManager.MovePageDown(id);
        _reportManager.SaveChanges();
        StatusMessageText.Text = $"Moved page down (order saved)";
        RefreshList();
        ReselectId(id);
    }

    private void MoveToTop(string id)
    {
        _reportManager.MovePageToTop(id);
        _reportManager.SaveChanges();
        StatusMessageText.Text = $"Moved page to top (order saved)";
        RefreshList();
        ReselectId(id);
    }

    private void MoveToBottom(string id)
    {
        _reportManager.MovePageToBottom(id);
        _reportManager.SaveChanges();
        StatusMessageText.Text = $"Moved page to bottom (order saved)";
        RefreshList();
        ReselectId(id);
    }

    private void ReselectId(string id)
    {
        var target = _items.FirstOrDefault(i => i.Id == id);
        if (target != null)
        {
            PagesListView.SelectedItem = target;
        }
    }

    private void SortAz_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Ascending, "Sorted pages A-Z");
    private void SortZa_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Descending, "Sorted pages Z-A");
    private void SortNatural_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Natural, "Naturally sorted pages");
    private void SortReverse_Click(object sender, RoutedEventArgs e) => ExecuteSort(SortMode.Reverse, "Reversed page order");
    private void Save_Click(object sender, RoutedEventArgs e) => SaveChanges();

    private void GoToPage_Click(object sender, RoutedEventArgs e)
    {
        if (GoToPageBtn.IsChecked == true)
        {
            StatusMessageText.Text = "Go-to-page is ON. Selecting a page now navigates the open Power BI report.";
            var selected = PagesListView.SelectedItem as PageItemViewModel;
            if (selected != null && !selected.Id.StartsWith("cmd:"))
            {
                NavigateToOpenReport(selected);
            }
        }
        else
        {
            StatusMessageText.Text = "Go-to-page is OFF.";
        }
    }

    private void PagesListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressNavigation || GoToPageBtn?.IsChecked != true)
        {
            return;
        }

        var selected = PagesListView.SelectedItem as PageItemViewModel;
        if (selected == null || selected.Id.StartsWith("cmd:"))
        {
            return;
        }

        NavigateToOpenReport(selected);
    }

    private void NavigateToOpenReport(PageItemViewModel page)
    {
        if (page == null || page.Id.StartsWith("cmd:")) return;

        if (PowerBiNavigator.TryGoToPage(page.DisplayName, out var error))
        {
            StatusMessageText.Text = $"→ Went to '{page.DisplayName}' in the open Power BI report";
        }
        else
        {
            StatusMessageText.Text = $"Could not go to '{page.DisplayName}': {error}";
        }
    }

    private async void OpenFolderButton_Click(object sender, RoutedEventArgs e)
    {
        var folderPicker = new FolderPicker();
        folderPicker.SuggestedStartLocation = PickerLocationId.DocumentsLibrary;
        folderPicker.FileTypeFilter.Add("*");

        var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.CurrentMainWindow);
        WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

        var folder = await folderPicker.PickSingleFolderAsync();
        if (folder != null)
        {
            LoadReportFolder(folder.Path);
        }
    }
}
