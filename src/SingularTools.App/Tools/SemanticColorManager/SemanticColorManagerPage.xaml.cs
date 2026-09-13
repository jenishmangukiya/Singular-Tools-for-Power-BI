using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using SingularTools.Core;
using SingularTools.Core.Models;
using SingularTools_App.Shell;
using Windows.UI;

namespace SingularTools_App.Tools.SemanticColorManager;

public sealed class SemanticColorItem : INotifyPropertyChanged
{
    public string Key { get; init; } = string.Empty;
    public string DisplayValue { get; init; } = string.Empty;
    public string DetailText { get; init; } = string.Empty;
    public string FieldsText { get; init; } = string.Empty;
    public string CurrentColorText { get; init; } = string.Empty;
    public string KindLabel { get; init; } = "Value";
    public bool IsManual { get; init; }
    public SemanticColorManualValue? Manual { get; init; }

    private string _hex = "#118DFF";
    public string Hex
    {
        get => _hex;
        private set
        {
            if (_hex == value) return;
            _hex = value;
            Raise(nameof(Hex));
        }
    }

    private SolidColorBrush _swatchBrush = new(Microsoft.UI.Colors.Gray);
    public SolidColorBrush SwatchBrush
    {
        get => _swatchBrush;
        private set
        {
            _swatchBrush = value;
            Raise(nameof(SwatchBrush));
        }
    }

    public Visibility FieldsVisibility =>
        string.IsNullOrWhiteSpace(FieldsText) ? Visibility.Collapsed : Visibility.Visible;

    public Visibility NewBadgeVisibility =>
        IsManual ? Visibility.Visible : Visibility.Collapsed;

    public void SetColor(Color color)
    {
        Hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SwatchBrush = new SolidColorBrush(color);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed partial class SemanticColorManagerPage : Page, IToolPage
{
    public string ToolId => "semantic-color-manager";
    public string Title => "Semantic Color Manager";
    public string Description => "Keep matching legend values (e.g. Yes / No, 2013) the same color across every visual";
    public string Glyph => "\uE790";

    private ReportManager Report => App.Workspace.Manager;
    private readonly ObservableCollection<SemanticColorItem> _items = new();
    private readonly List<SemanticColorItem> _manuallyAdded = new();
    private SemanticColorScan? _scan;
    private bool _subscribed;

    public SemanticColorManagerPage()
    {
        InitializeComponent();
        ValuesListView.ItemsSource = _items;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void OnActivated()
    {
        RefreshScan();
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("SemanticColorManagerPage loaded.");
            App.Workspace.Changed -= Workspace_Changed;
            App.Workspace.Changed += Workspace_Changed;
            _subscribed = true;
            RefreshScan();
        }
        catch (Exception ex)
        {
            App.Log($"Error in SemanticColorManagerPage_Loaded: {ex}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            App.Workspace.Changed -= Workspace_Changed;
            _subscribed = false;
        }
    }

    private void Workspace_Changed(object? sender, EventArgs e)
    {
        RefreshScan();
    }

    private void RefreshScan()
    {
        _items.Clear();

        var hasReport = App.Workspace.HasReport;
        ReportPathText.Text = hasReport ? App.Workspace.ReportName : "No report loaded";

        if (!hasReport)
        {
            _scan = null;
            SummaryText.Text = string.Empty;
            UpdateEmptyStates(hasReport);
            UpdateHistoryButtons();
            return;
        }

        try
        {
            _scan = new SemanticColorService().Scan(Report);
        }
        catch (Exception ex)
        {
            App.Log($"Semantic color scan failed: {ex}");
            ToastService.Show($"Could not scan report: {ex.Message}", ToastSeverity.Error);
            UpdateEmptyStates(hasReport);
            UpdateHistoryButtons();
            return;
        }

        var palette = SemanticColorService.DefaultPalette;
        var index = 0;

        foreach (var value in _scan.Values)
        {
            var seed = value.CommonColor ?? palette[index % palette.Length];
            index++;

            var item = new SemanticColorItem
            {
                Key = value.Key,
                DisplayValue = string.IsNullOrEmpty(value.DisplayValue) ? "(blank)" : value.DisplayValue,
                KindLabel = value.KindLabel,
                DetailText = BuildDetailText(value),
                FieldsText = BuildFieldsText(value),
                CurrentColorText = BuildCurrentColorText(value)
            };
            item.SetColor(ParseHex(seed));
            _items.Add(item);
        }

        // Keep values the user added by hand until they are written to the report.
        foreach (var manual in _manuallyAdded)
        {
            _items.Add(manual);
        }

        SummaryText.Text = _items.Count == 0
            ? string.Empty
            : $"{Plural(_scan.Values.Count, "item")} · {Plural(_scan.VisualCount, "visual")} scanned";

        UpdateEmptyStates(hasReport);
        UpdateHistoryButtons();
    }

    private static string BuildDetailText(SemanticColorValue value)
    {
        var parts = new List<string> { Plural(value.VisualCount, "visual") };
        if (value.SelectorCount > value.VisualCount)
        {
            parts.Add(Plural(value.SelectorCount, "selector"));
        }
        return string.Join(" · ", parts);
    }

    private static string BuildFieldsText(SemanticColorValue value)
    {
        if (value.Kind == SemanticColorTargetKind.SeriesIdentity)
        {
            return string.IsNullOrEmpty(value.RawValue) ? string.Empty : $"queryRef: {value.RawValue}";
        }

        var fields = value.Fields.Count > 0 ? string.Join(", ", value.Fields) : value.FieldName;
        return string.IsNullOrEmpty(fields) ? string.Empty : $"Fields: {fields}";
    }

    private static string BuildCurrentColorText(SemanticColorValue value)
    {
        if (value.CommonColor != null) return $"Current {value.CommonColor}";
        if (value.HasConflict) return $"Current: mixed ({value.CurrentColors.Count})";
        if (value.HasThemeColor) return "Current: theme color";
        return "Current: not set";
    }

    private void UpdateEmptyStates(bool hasReport)
    {
        var hasItems = _items.Count > 0;

        ListCard.Visibility = hasReport && hasItems ? Visibility.Visible : Visibility.Collapsed;
        NoReportPanel.Visibility = hasReport ? Visibility.Collapsed : Visibility.Visible;
        NoValuesPanel.Visibility = hasReport && !hasItems ? Visibility.Visible : Visibility.Collapsed;

        if (ApplyButton != null) ApplyButton.IsEnabled = hasReport && hasItems;
        if (AddValueButton != null) AddValueButton.IsEnabled = hasReport && _scan?.Fields.Count > 0;
    }

    private void UpdateHistoryButtons()
    {
        var history = App.Workspace.History;
        if (UndoButton != null) UndoButton.IsEnabled = history?.CanUndo == true;
        if (RedoButton != null) RedoButton.IsEnabled = history?.CanRedo == true;
    }

    private void Scan_Click(object sender, RoutedEventArgs e)
    {
        RefreshScan();
        ToastService.Show("Scanned the report for legend and series colors.", ToastSeverity.Informational);
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Workspace.HasReport)
        {
            ToastService.Show("Open a report first.", ToastSeverity.Warning);
            return;
        }

        var assignments = new List<SemanticColorAssignment>();
        foreach (var item in _items)
        {
            if (string.IsNullOrWhiteSpace(item.Hex)) continue;

            if (item.Manual != null)
            {
                assignments.Add(new SemanticColorAssignment { Hex = item.Hex, Manual = item.Manual });
            }
            else
            {
                assignments.Add(new SemanticColorAssignment { Key = item.Key, Hex = item.Hex });
            }
        }

        if (assignments.Count == 0)
        {
            ToastService.Show("No colors to apply.", ToastSeverity.Informational);
            return;
        }

        // The report is about to become the source of truth for these values.
        _manuallyAdded.Clear();

        try
        {
            var result = App.Workspace.ApplyEditWithResult(
                m => new SemanticColorService().Apply(m, assignments),
                managerWritesInternally: true);

            if (result.FilesWritten == 0)
            {
                ToastService.Show("Every matching color already matches — nothing to change.", ToastSeverity.Informational);
            }
            else
            {
                var parts = new List<string> { Plural(result.SelectorsChanged, "selector") };
                if (result.SelectorsCreated > 0) parts.Add($"{result.SelectorsCreated} added");
                ToastService.Show(
                    $"Updated {string.Join(" and ", parts)} across {Plural(result.VisualsChanged, "visual")}.",
                    ToastSeverity.Success);
            }

            RefreshScan();
        }
        catch (Exception ex)
        {
            App.Log($"Semantic color apply failed: {ex}");
            ToastService.Show($"Could not apply colors: {ex.Message}", ToastSeverity.Error);
        }
    }

    private async void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SemanticColorItem item)
        {
            return;
        }

        var picker = BuildPicker(item.Hex);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Color for \u201C{item.DisplayValue}\u201D",
            Content = picker,
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        try
        {
            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            {
                item.SetColor(picker.Color);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Color picker dialog failed: {ex.Message}");
        }
    }

    private async void AddValue_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Workspace.HasReport || _scan == null || _scan.Fields.Count == 0)
        {
            ToastService.Show("Scan a report first.", ToastSeverity.Warning);
            return;
        }

        var fieldCombo = new ComboBox
        {
            Header = "Field",
            ItemsSource = _scan.Fields,
            DisplayMemberPath = "DisplayName",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            MinWidth = 320
        };
        fieldCombo.SelectedIndex = 0;

        var valueBox = new TextBox
        {
            Header = "Value(s)",
            PlaceholderText = "e.g. 2014 or Yes (comma-separated)",
            MinWidth = 320
        };

        var picker = BuildPicker(
            SemanticColorService.DefaultPalette[_items.Count % SemanticColorService.DefaultPalette.Length]);

        var hint = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap
        };

        void SyncFieldMode()
        {
            var field = fieldCombo.SelectedItem as SemanticColorFieldInfo;
            var isMember = field == null || field.Kind == SemanticColorTargetKind.MemberValue;
            valueBox.Visibility = isMember ? Visibility.Visible : Visibility.Collapsed;
            hint.Text = isMember
                ? "A new color selector will be added to every visual that uses this field."
                : "This colors the whole series field using a metadata selector (cartesian charts only).";
        }

        fieldCombo.SelectionChanged += (_, _) => SyncFieldMode();
        SyncFieldMode();

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(fieldCombo);
        panel.Children.Add(valueBox);
        panel.Children.Add(picker);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add legend value",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
        }
        catch (Exception ex)
        {
            App.Log($"Add value dialog failed: {ex.Message}");
            return;
        }

        var selectedField = fieldCombo.SelectedItem as SemanticColorFieldInfo;
        if (selectedField == null) return;

        if (selectedField.Kind == SemanticColorTargetKind.SeriesIdentity)
        {
            if (string.IsNullOrEmpty(selectedField.QueryRef)) return;

            var manual = new SemanticColorManualValue
            {
                Kind = SemanticColorTargetKind.SeriesIdentity,
                FieldName = selectedField.FieldName,
                Entity = selectedField.Entity,
                QueryRef = selectedField.QueryRef
            };

            var item = new SemanticColorItem
            {
                DisplayValue = selectedField.FieldName,
                KindLabel = "Series",
                IsManual = true,
                Manual = manual,
                DetailText = "New · series",
                FieldsText = $"queryRef: {selectedField.QueryRef}",
                CurrentColorText = "Will be added"
            };
            item.SetColor(picker.Color);
            AddManual(item);
        }
        else
        {
            var raw = valueBox.Text ?? string.Empty;
            var parts = raw
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (parts.Count == 0)
            {
                ToastService.Show("Type at least one value to add.", ToastSeverity.Warning);
                return;
            }

            foreach (var value in parts)
            {
                var manual = new SemanticColorManualValue
                {
                    Kind = SemanticColorTargetKind.MemberValue,
                    FieldName = selectedField.FieldName,
                    Entity = selectedField.Entity,
                    TemplateJson = selectedField.TemplateJson,
                    LiteralValue = MakeLiteral(selectedField, value)
                };

                var item = new SemanticColorItem
                {
                    DisplayValue = value,
                    KindLabel = "Value",
                    IsManual = true,
                    Manual = manual,
                    DetailText = $"New · {selectedField.DisplayName}",
                    FieldsText = $"Field: {selectedField.DisplayName}",
                    CurrentColorText = "Will be added"
                };
                item.SetColor(picker.Color);
                AddManual(item);
            }
        }

        ToastService.Show("Added to the list. Click Apply to report to write it.", ToastSeverity.Informational);
    }

    private void AddManual(SemanticColorItem item)
    {
        _manuallyAdded.Add(item);
        _items.Add(item);
        UpdateEmptyStates(App.Workspace.HasReport);
    }

    private static string MakeLiteral(SemanticColorFieldInfo field, string value)
    {
        if (field.IsNumeric)
        {
            return value + field.NumericSuffix;
        }

        return "'" + value.Replace("'", "''") + "'";
    }

    private static ColorPicker BuildPicker(string hex)
    {
        return new ColorPicker
        {
            Color = ParseHex(hex),
            IsAlphaEnabled = false,
            IsHexInputVisible = true,
            IsColorChannelTextInputVisible = false,
            MinWidth = 320
        };
    }

    private void Undo_Click(object sender, RoutedEventArgs e)
    {
        var history = App.Workspace.History;
        if (history == null || !history.CanUndo) return;
        ApplySnapshot(history.Undo(), "Undid the last color change.");
    }

    private void Redo_Click(object sender, RoutedEventArgs e)
    {
        var history = App.Workspace.History;
        if (history == null || !history.CanRedo) return;
        ApplySnapshot(history.Redo(), "Redid the last color change.");
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
            _manuallyAdded.Clear();
            RefreshScan();
            ToastService.Show(message, ToastSeverity.Informational);
        }
        catch (Exception ex)
        {
            ToastService.Show($"Could not restore history: {ex.Message}", ToastSeverity.Error);
        }

        UpdateHistoryButtons();
    }

    private void GoToHome_Click(object sender, RoutedEventArgs e)
    {
        App.CurrentMainWindow?.NavigateToTool("home");
    }

    private static string Plural(int count, string noun) =>
        count == 1 ? $"{count} {noun}" : $"{count} {noun}s";

    private static Color ParseHex(string hex)
    {
        var value = (hex ?? string.Empty).Trim().TrimStart('#');

        if (value.Length == 8) value = value.Substring(2);

        if (value.Length == 6 &&
            uint.TryParse(value, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var parsed))
        {
            return Color.FromArgb(255, (byte)(parsed >> 16), (byte)(parsed >> 8), (byte)parsed);
        }

        return Microsoft.UI.Colors.Gray;
    }
}
