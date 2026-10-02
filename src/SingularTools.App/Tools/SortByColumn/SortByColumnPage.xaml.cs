using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using SingularTools.Core;
using SingularTools_App.Shell;
using Windows.System;

namespace SingularTools_App.Tools.SortByColumn;

/// <summary>One base/order column pair shown as a row with an on/off switch.</summary>
public sealed class PairRowViewModel : INotifyPropertyChanged
{
    public PairRowViewModel(SortByColumnPair pair) => Pair = pair;

    public SortByColumnPair Pair { get; }

    public string BaseColumn => Pair.BaseColumn;
    public string OrderColumn => Pair.OrderColumn;
    public string Key => Pair.Key;
    public bool AlreadyApplied => Pair.AlreadyApplied;

    public string StatusText => "Applied";
    public Visibility StatusVisibility => Pair.AlreadyApplied ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>DAX calculated columns can create a circular dependency, so they are left to Power BI.</summary>
    public bool IsUnsupported => Pair.IsCalculatedPair;

    public bool IsToggleEnabled => !IsUnsupported;

    public Visibility HintVisibility => IsUnsupported ? Visibility.Visible : Visibility.Collapsed;

    private bool _isSelected = true;

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            Raise(nameof(IsSelected));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A table card: the pairs that belong to one semantic model table.</summary>
public sealed class TableGroupViewModel : INotifyPropertyChanged
{
    public TableGroupViewModel(string tableName) => TableName = tableName;

    public string TableName { get; }
    public ObservableCollection<PairRowViewModel> Rows { get; } = new();

    public string Summary => $"{Rows.Count(r => r.IsSelected)} of {Rows.Count} selected";

    public void Add(PairRowViewModel row)
    {
        row.PropertyChanged += (_, _) => Raise(nameof(Summary));
        Rows.Add(row);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A column that ends with the suffix but has no base column to pair with.</summary>
public sealed class UnmatchedViewModel
{
    public UnmatchedViewModel(SortByColumnUnmatched model) => Model = model;

    public SortByColumnUnmatched Model { get; }
    public string Display => $"{Model.TableName} \u00B7 {Model.ColumnName}";
    public string Reason => Model.Reason;
}

public sealed partial class SortByColumnPage : Page, IToolPage
{
    public string ToolId => "sort-by-column";
    public string Title => "Sort by Column";
    public string Description => "Point text columns at their order columns so they sort logically";
    public string Glyph => "\uE8CB";

    public ObservableCollection<TableGroupViewModel> Tables { get; } = new();
    public ObservableCollection<UnmatchedViewModel> Unmatched { get; } = new();

    private string? _manualAnchor;
    private string _projectRoot = string.Empty;
    private string _modelFolder = string.Empty;
    private SemanticModel? _model;
    private Action? _emptyAction;
    private bool _subscribed;
    private bool _suppressSelectionSave;

    public SortByColumnPage()
    {
        InitializeComponent();
        Loaded += SortByColumnPage_Loaded;
        Unloaded += SortByColumnPage_Unloaded;
    }

    // ---- Lifecycle --------------------------------------------------------

    public void OnActivated() => RefreshAll();

    private void SortByColumnPage_Loaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Subscribe();
            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"SortByColumnPage load failed: {ex}");
        }
    }

    private void SortByColumnPage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (!_subscribed) return;
        App.Workspace.Changed -= Workspace_Changed;
        _subscribed = false;
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Workspace.Changed += Workspace_Changed;
        _subscribed = true;
    }

    private void Workspace_Changed(object? sender, EventArgs e)
    {
        // A new report was opened elsewhere; follow it instead of any manual pick.
        _manualAnchor = null;
        RefreshAll();
    }

    // ---- Refresh ----------------------------------------------------------

    private void RefreshAll()
    {
        var anchor = ResolveAnchor();
        _projectRoot = ResolveProjectRoot(anchor);
        _modelFolder = SortByColumnService.DiscoverModelFolder(anchor) ?? string.Empty;
        _model = _modelFolder.Length > 0 ? SortByColumnService.LoadModel(_modelFolder) : null;

        UpdateHeader();

        if (_model == null)
        {
            ClearTables();
            ShowEmpty(
                "\uE8CB",
                "No semantic model found",
                "This feature edits the TMDL of a PBIP semantic model. Open a .pbip project, or choose the folder that holds it.",
                "Choose project folder\u2026",
                () => ChooseFolder());
            return;
        }

        if (!_model.HasTmdlDefinition)
        {
            ClearTables();
            ShowEmpty(
                "\uE783",
                "This model uses model.bim",
                "Sort by Column edits TMDL models (definition/tables/*.tmdl). The model at this path is stored as a single model.bim file, which is left untouched.");
            return;
        }

        var suffix = LoadSuffix();
        SuffixBox.Text = suffix;

        if (suffix.Length == 0)
        {
            ClearTables();
            ShowEmpty(
                "\uE8CB",
                "Set your order column suffix",
                "Type the ending shared by your order columns (for example _ord) above, then press Enter. Columns named like <name>_ord are paired with <name>.");
            return;
        }

        BuildPlan(suffix);
    }

    private string ResolveAnchor()
    {
        if (_manualAnchor != null) return _manualAnchor;
        return App.Workspace.HasReport ? App.Workspace.ReportPath : string.Empty;
    }

    private static string ResolveProjectRoot(string anchor)
    {
        if (string.IsNullOrWhiteSpace(anchor)) return string.Empty;

        // A model folder picked directly: the project lives one level up.
        if (anchor.EndsWith(".SemanticModel", StringComparison.OrdinalIgnoreCase))
        {
            var parent = System.IO.Directory.GetParent(anchor)?.FullName;
            if (!string.IsNullOrEmpty(parent)) return parent;
        }

        return ReportConfigStore.ResolveProjectRoot(anchor);
    }

    private void BuildPlan(string suffix)
    {
        var plan = SortByColumnService.BuildPlan(_model!, suffix);
        var excluded = new HashSet<string>(LoadExcluded(), StringComparer.OrdinalIgnoreCase);

        _suppressSelectionSave = true;
        Tables.Clear();

        foreach (var group in plan.Pairs
                     .GroupBy(p => p.TableName, StringComparer.Ordinal)
                     .OrderBy(g => g.Key, StringComparer.OrdinalIgnoreCase))
        {
            var table = new TableGroupViewModel(group.Key);
            foreach (var pair in group.OrderBy(p => p.BaseColumn, StringComparer.OrdinalIgnoreCase))
            {
                var row = new PairRowViewModel(pair)
                {
                    IsSelected = !pair.IsCalculatedPair && !excluded.Contains(pair.Key)
                };
                table.Add(row);
                row.PropertyChanged += Row_PropertyChanged;
            }

            Tables.Add(table);
        }

        _suppressSelectionSave = false;

        Unmatched.Clear();
        foreach (var item in plan.Unmatched
                     .OrderBy(u => u.TableName, StringComparer.OrdinalIgnoreCase)
                     .ThenBy(u => u.ColumnName, StringComparer.OrdinalIgnoreCase))
        {
            Unmatched.Add(new UnmatchedViewModel(item));
        }

        UnmatchedExpander.Visibility = Unmatched.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        UnmatchedExpander.Header = $"Not matched ({Unmatched.Count})";

        if (plan.Pairs.Count > 0)
        {
            HideEmpty();
            TablesScroll.Visibility = Visibility.Visible;
        }
        else
        {
            ClearTables();
            ShowEmpty(
                "\uE946",
                "No matching order columns",
                $"No columns end with \"{suffix}\", or none has a base column to sort. Check the suffix above.");
        }

        UpdatePlanSummary();
    }

    private void ClearTables()
    {
        _suppressSelectionSave = true;
        Tables.Clear();
        Unmatched.Clear();
        _suppressSelectionSave = false;
        TablesScroll.Visibility = Visibility.Collapsed;
        UnmatchedExpander.Visibility = Visibility.Collapsed;
    }

    private void UpdateHeader()
    {
        if (_model == null)
        {
            ModelNameText.Text = "No semantic model found";
            ModelHintText.Text = string.IsNullOrEmpty(_projectRoot)
                ? "Open a Power BI project (.pbip) to sort its tables."
                : $"No .SemanticModel folder next to {_projectRoot}.";
            PowerBiWarning.Visibility = Visibility.Collapsed;
            return;
        }

        ModelNameText.Text = _model.Name;
        ModelHintText.Text = _model.HasTmdlDefinition
            ? $"{_model.Tables.Count} tables \u00B7 {_model.FolderPath}"
            : _model.FolderPath;
        PowerBiWarning.Visibility = _model.HasTmdlDefinition ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlanSummary()
    {
        var rows = Tables.SelectMany(t => t.Rows).ToList();
        var toApply = rows.Count(r => r.IsSelected && !r.IsUnsupported && !r.AlreadyApplied);
        var applied = rows.Count(r => r.AlreadyApplied);
        var unsupported = rows.Count(r => r.IsUnsupported);

        ApplyButtonText.Text = toApply > 0 ? $"Apply sort orders ({toApply})" : "Apply sort orders";
        ApplyButton.IsEnabled = toApply > 0;
        UndoButton.IsEnabled = HasBackup();

        if (rows.Count == 0)
        {
            PlanSummaryText.Text = string.Empty;
            return;
        }

        var summary = $"{toApply} to apply \u00B7 {applied} already set";
        if (unsupported > 0) summary += $" \u00B7 {unsupported} calculated (skipped)";
        PlanSummaryText.Text = summary;
    }

    // ---- Selection persistence -------------------------------------------

    private void Row_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_suppressSelectionSave || e.PropertyName != nameof(PairRowViewModel.IsSelected)) return;

        SaveSelection();
        UpdatePlanSummary();
    }

    private void SaveSelection()
    {
        if (string.IsNullOrEmpty(_projectRoot)) return;

        try
        {
            var excluded = Tables.SelectMany(t => t.Rows)
                                 .Where(r => !r.IsUnsupported && !r.IsSelected)
                                 .Select(r => r.Key)
                                 .ToList();

            var config = ReportConfigStore.Load(_projectRoot);
            config.SetSortByColumnExcluded(excluded);
            ReportConfigStore.Save(_projectRoot, config);
        }
        catch (Exception ex)
        {
            App.Log($"Saving sort-by selection failed: {ex.Message}");
        }
    }

    private string LoadSuffix()
    {
        if (string.IsNullOrEmpty(_projectRoot)) return string.Empty;
        try
        {
            return ReportConfigStore.Load(_projectRoot).GetSortByColumnSuffix();
        }
        catch
        {
            return string.Empty;
        }
    }

    private List<string> LoadExcluded()
    {
        if (string.IsNullOrEmpty(_projectRoot)) return new List<string>();
        try
        {
            return ReportConfigStore.Load(_projectRoot).GetSortByColumnExcluded();
        }
        catch
        {
            return new List<string>();
        }
    }

    private bool HasBackup()
    {
        if (string.IsNullOrEmpty(_projectRoot)) return false;
        return SortByColumnService.LatestBackupFolder(_projectRoot) != null;
    }

    // ---- Commands ---------------------------------------------------------

    private void SuffixBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter)
        {
            e.Handled = true;
            ApplySuffix();
        }
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => ApplySuffix();

    private void ApplySuffix()
    {
        var suffix = SuffixBox.Text.Trim();
        if (suffix.Length == 0)
        {
            ToastService.Show("Enter the order column suffix first (for example _ord).", ToastSeverity.Warning);
            return;
        }

        if (!string.IsNullOrEmpty(_projectRoot))
        {
            try
            {
                var config = ReportConfigStore.Load(_projectRoot);
                config.SetSortByColumnSuffix(suffix);
                ReportConfigStore.Save(_projectRoot, config);
            }
            catch (Exception ex)
            {
                App.Log($"Saving sort-by suffix failed: {ex.Message}");
            }
        }

        RefreshAll();
    }

    private void TableAll_Click(object sender, RoutedEventArgs e) => SetTable(sender, true);

    private void TableNone_Click(object sender, RoutedEventArgs e) => SetTable(sender, false);

    private void SetTable(object sender, bool selected)
    {
        if ((sender as FrameworkElement)?.DataContext is not TableGroupViewModel table) return;

        _suppressSelectionSave = true;
        foreach (var row in table.Rows) row.IsSelected = selected;
        _suppressSelectionSave = false;

        SaveSelection();
        UpdatePlanSummary();
    }

    private async void ApplyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_model == null) return;

        var selected = Tables.SelectMany(t => t.Rows)
                             .Where(r => r.IsSelected && !r.IsUnsupported && !r.AlreadyApplied)
                             .Select(r => r.Pair)
                             .ToList();

        if (selected.Count == 0)
        {
            ToastService.Show("Nothing to apply — every selected pair is already set.", ToastSeverity.Informational);
            return;
        }

        var confirmed = await ConfirmAsync(
            "Apply sort by column?",
            $"This writes {selected.Count} change(s) directly to the model files in {_model.FolderPath}. " +
            "If the report is open in Power BI Desktop, close it first or your edits may be overwritten.",
            "Apply");
        if (!confirmed) return;

        try
        {
            var result = SortByColumnService.Apply(_model, selected, createBackup: true, projectRoot: _projectRoot);
            App.Workspace.NotifyModelFilesChanged();

            // Power BI Desktop has the old model in memory: click "Apply external
            // changes" and confirm the "Overwrite your unsaved edits" prompt so the
            // sort orders we just wrote are picked up without the author doing it.
            var powerBiRunning = await Task.Run(() => PowerBiPublisher.TryApplyExternalChangesInPowerBi());

            if (result.ColumnsChanged == 0)
            {
                ToastService.Show("Nothing changed — the model already matched.", ToastSeverity.Informational);
            }
            else if (powerBiRunning)
            {
                ToastService.Show(
                    $"Sorted {result.ColumnsChanged} column(s) across {result.FilesWritten} table file(s) and applied the external change in Power BI.",
                    ToastSeverity.Success);
            }
            else
            {
                ToastService.Show(
                    $"Sorted {result.ColumnsChanged} column(s) across {result.FilesWritten} table file(s). Reload the model in Power BI to pick it up.",
                    ToastSeverity.Success);
            }

            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"Sort by column apply failed: {ex}");
            ToastService.Show($"Could not apply: {ex.Message}", ToastSeverity.Error);
        }
    }

    private async void UndoButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_projectRoot)) return;

        var confirmed = await ConfirmAsync(
            "Undo last apply?",
            "The model files changed by the last apply will be restored from the backup.",
            "Restore");
        if (!confirmed) return;

        try
        {
            var restored = SortByColumnService.RestoreLatestBackup(_projectRoot, out _);
            App.Workspace.NotifyModelFilesChanged();

            // The restore rewrites the model on disk, so tell Power BI Desktop to
            // pick it up the same way an apply does.
            var powerBiRunning = false;
            if (restored > 0)
            {
                powerBiRunning = await Task.Run(() => PowerBiPublisher.TryApplyExternalChangesInPowerBi());
            }

            if (restored <= 0)
            {
                ToastService.Show("No backup to restore.", ToastSeverity.Informational);
            }
            else if (powerBiRunning)
            {
                ToastService.Show(
                    $"Restored {restored} model file(s) and applied the external change in Power BI.",
                    ToastSeverity.Success);
            }
            else
            {
                ToastService.Show(
                    $"Restored {restored} model file(s). Reload the model in Power BI to pick it up.",
                    ToastSeverity.Success);
            }

            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"Sort by column restore failed: {ex}");
            ToastService.Show($"Could not restore: {ex.Message}", ToastSeverity.Error);
        }
    }

    private void EmptyActionButton_Click(object sender, RoutedEventArgs e) => _emptyAction?.Invoke();

    private async void ChooseFolder()
    {
        try
        {
            var path = await ReportPicker.PickReportFolderAsync();
            if (string.IsNullOrEmpty(path)) return;

            _manualAnchor = path;
            RefreshAll();
        }
        catch (Exception ex)
        {
            App.Log($"Sort by column folder pick failed: {ex}");
        }
    }

    // ---- Empty state ------------------------------------------------------

    private void ShowEmpty(string glyph, string title, string message, string? actionText = null, Action? action = null)
    {
        EmptyIcon.Glyph = glyph;
        EmptyTitle.Text = title;
        EmptyMessage.Text = message;

        var hasAction = actionText != null && action != null;
        EmptyActionButton.Content = actionText ?? string.Empty;
        EmptyActionButton.Visibility = hasAction ? Visibility.Visible : Visibility.Collapsed;
        _emptyAction = hasAction ? action : null;

        EmptyPanel.Visibility = Visibility.Visible;
        UpdatePlanSummary();
    }

    private void HideEmpty() => EmptyPanel.Visibility = Visibility.Collapsed;

    private async Task<bool> ConfirmAsync(string title, string message, string primaryText)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            PrimaryButtonText = primaryText,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
