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

public sealed class SemanticColorRuleItem : INotifyPropertyChanged
{
    public string Value { get; init; } = string.Empty;

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
    public string Title => "Color Sync";
    public string Description => "Color matching values (e.g. Yes / No) the same across every visual";
    public string Glyph => "\uE790";

    private ReportManager Report => App.Workspace.Manager;
    private readonly ObservableCollection<SemanticColorRuleItem> _items = new();
    private string _loadedReportPath = string.Empty;
    private bool _subscribed;

    public SemanticColorManagerPage()
    {
        InitializeComponent();
        ValuesListView.ItemsSource = _items;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void OnActivated() => ReloadForReport();

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            App.Log("SemanticColorManagerPage loaded.");
            App.Workspace.Changed -= Workspace_Changed;
            App.Workspace.Changed += Workspace_Changed;
            _subscribed = true;
            ReloadForReport();
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

    private void Workspace_Changed(object? sender, EventArgs e) => ReloadForReport();

    private void ReloadForReport()
    {
        var hasReport = App.Workspace.HasReport;
        var reportPath = hasReport ? App.Workspace.ReportPath : string.Empty;

        ReportPathText.Text = hasReport ? App.Workspace.ReportName : "No report loaded";

        // Rules are user input keyed by report; only reload when the report changed.
        if (!string.Equals(reportPath, _loadedReportPath, StringComparison.OrdinalIgnoreCase))
        {
            _loadedReportPath = reportPath;
            LoadRules();
        }

        UpdateEmptyStates();
        UpdateHistoryButtons();
    }

    private void LoadRules()
    {
        _items.Clear();

        if (!App.Workspace.HasReport) return;

        var palette = SemanticColorService.DefaultPalette;
        var stored = SemanticColorRuleStore.Load(App.Workspace.ReportPath);
        var index = 0;

        foreach (var rule in stored)
        {
            if (string.IsNullOrWhiteSpace(rule.Value)) continue;

            var item = new SemanticColorRuleItem { Value = rule.Value };
            var hex = string.IsNullOrWhiteSpace(rule.Hex)
                ? palette[index % palette.Length]
                : rule.Hex;
            index++;

            item.SetColor(ParseHex(hex));
            _items.Add(item);
        }
    }

    private void SaveRules()
    {
        if (!App.Workspace.HasReport) return;

        SemanticColorRuleStore.Save(App.Workspace.ReportPath, _items
            .Where(i => !string.IsNullOrWhiteSpace(i.Value))
            .Select(i => new SemanticColorRule { Value = i.Value, Hex = i.Hex }));
    }

    private void UpdateEmptyStates()
    {
        var hasReport = App.Workspace.HasReport;
        var hasItems = _items.Count > 0;

        ListCard.Visibility = hasReport && hasItems ? Visibility.Visible : Visibility.Collapsed;
        NoReportPanel.Visibility = hasReport ? Visibility.Collapsed : Visibility.Visible;
        NoValuesPanel.Visibility = hasReport && !hasItems ? Visibility.Visible : Visibility.Collapsed;

        if (ApplyButton != null) ApplyButton.IsEnabled = hasReport && hasItems;
        if (AddValueButton != null) AddValueButton.IsEnabled = hasReport;

        SummaryText.Text = hasItems
            ? $"{Plural(_items.Count, "value")} · not applied automatically"
            : string.Empty;
    }

    private void UpdateHistoryButtons()
    {
        var history = App.Workspace.History;
        if (UndoButton != null) UndoButton.IsEnabled = history?.CanUndo == true;
        if (RedoButton != null) RedoButton.IsEnabled = history?.CanRedo == true;
    }

    private void Apply_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Workspace.HasReport)
        {
            ToastService.Show("Open a report first.", ToastSeverity.Warning);
            return;
        }

        var rules = _items
            .Where(i => !string.IsNullOrWhiteSpace(i.Value) && !string.IsNullOrWhiteSpace(i.Hex))
            .Select(i => new SemanticColorRule { Value = i.Value, Hex = i.Hex })
            .ToList();

        if (rules.Count == 0)
        {
            ToastService.Show("Add at least one value with a color first.", ToastSeverity.Informational);
            return;
        }

        try
        {
            var result = App.Workspace.ApplyEditWithResult(
                m => new SemanticColorService().ApplyRules(m, rules),
                managerWritesInternally: true);

            if (result.FilesWritten == 0)
            {
                ToastService.Show("Every matching value already has this color — nothing to change.", ToastSeverity.Informational);
            }
            else
            {
                var parts = new List<string> { Plural(result.SelectorsChanged, "color") };
                if (result.SelectorsCreated > 0) parts.Add($"{result.SelectorsCreated} added");
                ToastService.Show(
                    $"Updated {string.Join(" and ", parts)} across {Plural(result.VisualsChanged, "visual")}.",
                    ToastSeverity.Success);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Semantic color apply failed: {ex}");
            ToastService.Show($"Could not apply colors: {ex.Message}", ToastSeverity.Error);
        }
    }

    private async void Swatch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SemanticColorRuleItem item)
        {
            return;
        }

        var picker = BuildPicker(item.Hex);
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = $"Color for \u201C{item.Value}\u201D",
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
                SaveRules();
            }
        }
        catch (Exception ex)
        {
            App.Log($"Color picker dialog failed: {ex.Message}");
        }
    }

    private async void AddValue_Click(object sender, RoutedEventArgs e)
    {
        if (!App.Workspace.HasReport)
        {
            ToastService.Show("Open a report first.", ToastSeverity.Warning);
            return;
        }

        var valueBox = new TextBox
        {
            Header = "Value(s)",
            PlaceholderText = "e.g. Channel Partner, Enterprise (comma-separated)",
            MinWidth = 320
        };

        var picker = BuildPicker(
            SemanticColorService.DefaultPalette[_items.Count % SemanticColorService.DefaultPalette.Length]);

        var hint = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Text = "Matched as text against every non-table chart, whether or not that value already has a color."
        };

        var panel = new StackPanel { Spacing = 10 };
        panel.Children.Add(valueBox);
        panel.Children.Add(picker);
        panel.Children.Add(hint);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = "Add value",
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

        var parts = (valueBox.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        if (parts.Count == 0)
        {
            ToastService.Show("Type at least one value to add.", ToastSeverity.Warning);
            return;
        }

        var added = 0;
        foreach (var part in parts)
        {
            var value = SemanticColorService.NormalizeRuleValue(part);
            if (value.Length == 0) continue;

            var existing = _items.FirstOrDefault(i =>
                string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase));

            if (existing != null)
            {
                existing.SetColor(picker.Color);
            }
            else
            {
                var item = new SemanticColorRuleItem { Value = value };
                item.SetColor(picker.Color);
                _items.Add(item);
            }

            added++;
        }

        if (added == 0) return;

        SaveRules();
        UpdateEmptyStates();
        ToastService.Show("Added to the list. Click Apply to report to write it.", ToastSeverity.Informational);
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SemanticColorRuleItem item)
        {
            return;
        }

        _items.Remove(item);
        SaveRules();
        UpdateEmptyStates();
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
            SyncColorsFromReport();
            ToastService.Show(message, ToastSeverity.Informational);
        }
        catch (Exception ex)
        {
            ToastService.Show($"Could not restore history: {ex.Message}", ToastSeverity.Error);
        }

        UpdateHistoryButtons();
    }

    /// <summary>
    /// Undo/redo restores the report, not the user's rule list, so pull each rule's
    /// color back from the report to keep the swatches in sync.
    /// </summary>
    private void SyncColorsFromReport()
    {
        if (!App.Workspace.HasReport) return;

        var service = new SemanticColorService();
        foreach (var item in _items)
        {
            var hex = service.GetAppliedColor(Report, item.Value);
            if (!string.IsNullOrEmpty(hex))
            {
                item.SetColor(ParseHex(hex));
            }
        }

        SaveRules();
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
