using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using SingularTools.Core;
using SingularTools.Core.Models;
using SingularTools_App.Shell;
using Windows.UI;

namespace SingularTools_App.Tools.SemanticColorManager;

/// <summary>One editable Color Sync rule shown in the list.</summary>
public sealed class SemanticColorRuleItem : INotifyPropertyChanged
{
    private string _value = string.Empty;
    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            Raise(nameof(Value));
        }
    }

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

    private SemanticColorScope _scope = SemanticColorScope.Report;
    public SemanticColorScope Scope
    {
        get => _scope;
        set
        {
            if (_scope == value) return;
            _scope = value;
            Raise(nameof(Scope));
            Raise(nameof(ScopeGlyph));
        }
    }

    public List<string> PageIds { get; set; } = new();

    private string _scopeText = "All pages";
    public string ScopeText
    {
        get => _scopeText;
        set
        {
            if (_scopeText == value) return;
            _scopeText = value;
            Raise(nameof(ScopeText));
        }
    }

    private bool _hasMissingPages;
    public bool HasMissingPages
    {
        get => _hasMissingPages;
        set
        {
            if (_hasMissingPages == value) return;
            _hasMissingPages = value;
            Raise(nameof(HasMissingPages));
            Raise(nameof(MissingVisibility));
        }
    }

    public Visibility MissingVisibility => _hasMissingPages ? Visibility.Visible : Visibility.Collapsed;

    public string ScopeGlyph => _scope == SemanticColorScope.Pages ? "\uE8A9" : "\uE774";

    public void SetColor(Color color)
    {
        Hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
        SwatchBrush = new SolidColorBrush(color);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A page that can be chosen as a Color Sync target.</summary>
public sealed class ColorSyncPageOption
{
    public string Id { get; init; } = string.Empty;
    public string DisplayName { get; init; } = string.Empty;
    public bool IsMissing { get; init; }
}

/// <summary>A section of the grouped rule list ("Entire report" / "Specific pages").</summary>
public sealed class SemanticColorRuleGroup : ObservableCollection<SemanticColorRuleItem>
{
    public SemanticColorRuleGroup(string title, IReadOnlyList<SemanticColorRuleItem> items)
        : base(items)
    {
        Title = title;
        Summary = items.Count == 1 ? "1 value" : $"{items.Count} values";
    }

    public string Title { get; }
    public string Summary { get; }
}

public sealed partial class SemanticColorManagerPage : Page, IToolPage
{
    public string ToolId => "semantic-color-manager";
    public string Title => "Color Sync";
    public string Description => "Color matching values the same across the report or selected pages";
    public string Glyph => "\uE790";

    private ReportManager Report => App.Workspace.Manager;

    private readonly ObservableCollection<SemanticColorRuleItem> _items = new();
    private readonly ObservableCollection<SemanticColorRuleGroup> _groups = new();
    private List<ColorSyncPageOption> _pageOptions = new();
    private string _loadedReportPath = string.Empty;
    private bool _subscribed;

    // Fluent UI "eyedropper" (20px) path, used for the Color picker button.
    private const string EyedropperPath =
        "M17.2465 2.75409C16.2228 1.7304 14.563 1.73041 13.5393 2.75411L12.5 3.7935L12.1489 3.44243C11.5631 2.85663 10.6134 2.85663 10.0276 3.44242L9.44178 4.0282C8.856 4.61399 8.85599 5.56373 9.44178 6.14952L9.79287 6.50061L3.43946 12.854C3.15816 13.1353 3.00012 13.5169 3.00012 13.9147V14.4162L2.03559 16.6745C1.67987 17.5073 2.52597 18.3463 3.35577 17.9835L5.60452 17.0005H6.08591C6.48373 17.0005 6.86526 16.8424 7.14657 16.5611L13.5 10.2077L13.8533 10.5611C14.4391 11.1468 15.3888 11.1468 15.9746 10.5611L16.5604 9.97528C17.1462 9.38949 17.1462 8.43974 16.5604 7.85396L16.2071 7.50061L17.2465 6.46119C18.2701 5.4375 18.2701 3.77778 17.2465 2.75409ZM14.2464 3.46121C14.8796 2.82804 15.9062 2.82803 16.5394 3.4612C17.1725 4.09436 17.1725 5.12092 16.5394 5.75409L15.1464 7.14706C14.9511 7.34232 14.9511 7.6589 15.1464 7.85416L15.8533 8.56106C16.0486 8.75633 16.0486 9.07291 15.8533 9.26817L15.2675 9.85396C15.0723 10.0492 14.7557 10.0492 14.5604 9.85396L10.1489 5.44241C9.95363 5.24715 9.95363 4.93057 10.1489 4.73531L10.7347 4.14952C10.9299 3.95426 11.2465 3.95426 11.4418 4.14953L12.1464 4.85416C12.2402 4.94793 12.3674 5.00061 12.5 5.00061C12.6326 5.00061 12.7598 4.94793 12.8535 4.85416L14.2464 3.46121ZM12.7929 9.50062L6.43946 15.854C6.34569 15.9478 6.21852 16.0005 6.08591 16.0005H5.50001C5.43108 16.0005 5.36289 16.0147 5.29973 16.0423L2.95522 17.0673L3.95994 14.7149C3.98645 14.6528 4.00012 14.586 4.00012 14.5185V13.9147C4.00012 13.7821 4.0528 13.6549 4.14657 13.5611L10.5 7.20772L12.7929 9.50062Z";

    public SemanticColorManagerPage()
    {
        InitializeComponent();

        var grouped = new CollectionViewSource { IsSourceGrouped = true, Source = _groups };
        RulesListView.ItemsSource = grouped.View;

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

        BuildPageOptions();

        // Rules are user input keyed by report; only reload when the report changed.
        if (!string.Equals(reportPath, _loadedReportPath, StringComparison.OrdinalIgnoreCase))
        {
            _loadedReportPath = reportPath;
            LoadRules();
        }
        else
        {
            foreach (var item in _items) UpdateScopeText(item);
            RebuildGroups();
        }

        UpdateEmptyStates();
        UpdateHistoryButtons();
    }

    private void BuildPageOptions()
    {
        if (!App.Workspace.HasReport)
        {
            _pageOptions = new List<ColorSyncPageOption>();
            return;
        }

        _pageOptions = Report.Pages
            .Select(p => new ColorSyncPageOption
            {
                Id = p.Id,
                DisplayName = string.IsNullOrWhiteSpace(p.DisplayName) ? p.Id : p.DisplayName,
                IsMissing = false
            })
            .ToList();
    }

    private void LoadRules()
    {
        _items.Clear();

        if (!App.Workspace.HasReport)
        {
            RebuildGroups();
            return;
        }

        var palette = SemanticColorService.DefaultPalette;
        var stored = ReportConfigStore.Load(App.Workspace.ReportPath).GetColorSyncRules();
        var index = 0;

        foreach (var rule in stored)
        {
            if (string.IsNullOrWhiteSpace(rule.Value)) continue;

            var item = new SemanticColorRuleItem
            {
                Value = rule.Value,
                PageIds = rule.PageIds?.ToList() ?? new List<string>()
            };
            item.Scope = rule.Scope;

            var hex = string.IsNullOrWhiteSpace(rule.Hex)
                ? palette[index % palette.Length]
                : rule.Hex;
            index++;

            item.SetColor(ParseHex(hex));
            UpdateScopeText(item);
            _items.Add(item);
        }

        RebuildGroups();
    }

    private void RebuildGroups()
    {
        _groups.Clear();

        var reportRules = _items.Where(i => i.Scope == SemanticColorScope.Report).ToList();
        var pageRules = _items.Where(i => i.Scope == SemanticColorScope.Pages).ToList();

        if (reportRules.Count > 0) _groups.Add(new SemanticColorRuleGroup("Entire report", reportRules));
        if (pageRules.Count > 0) _groups.Add(new SemanticColorRuleGroup("Specific pages", pageRules));
    }

    private void UpdateScopeText(SemanticColorRuleItem item)
    {
        if (item.Scope != SemanticColorScope.Pages)
        {
            item.ScopeText = "All pages";
            item.HasMissingPages = false;
            return;
        }

        var ids = item.PageIds ?? new List<string>();
        if (ids.Count == 0)
        {
            item.ScopeText = "No pages selected";
            item.HasMissingPages = false;
            return;
        }

        var names = new List<string>();
        var missing = 0;

        foreach (var id in ids)
        {
            var option = _pageOptions.FirstOrDefault(p =>
                string.Equals(p.Id, id, StringComparison.OrdinalIgnoreCase));

            if (option == null)
            {
                missing++;
                names.Add(ShortId(id));
            }
            else
            {
                names.Add(option.DisplayName);
            }
        }

        var text = names.Count <= 2 ? string.Join(", ", names) : $"{names.Count} pages";
        if (missing > 0) text += $" \u00B7 {missing} missing";

        item.ScopeText = text;
        item.HasMissingPages = missing > 0;
    }

    private void SaveRules()
    {
        if (!App.Workspace.HasReport) return;

        var config = ReportConfigStore.Load(App.Workspace.ReportPath);
        config.SetColorSyncRules(_items
            .Where(i => !string.IsNullOrWhiteSpace(i.Value))
            .Select(ToRule));

        if (!ReportConfigStore.Save(App.Workspace.ReportPath, config))
        {
            App.Log("Could not write singular-tools.json to the report project folder.");
            ToastService.Show("Could not save values to the report project folder.", ToastSeverity.Warning);
        }
    }

    private static SemanticColorRule ToRule(SemanticColorRuleItem item) => new()
    {
        Value = item.Value,
        Hex = item.Hex,
        Scope = item.Scope,
        PageIds = item.Scope == SemanticColorScope.Pages
            ? item.PageIds.ToList()
            : new List<string>()
    };

    private void UpdateEmptyStates()
    {
        var hasReport = App.Workspace.HasReport;
        var hasItems = _items.Count > 0;

        ListCard.Visibility = hasReport && hasItems ? Visibility.Visible : Visibility.Collapsed;
        NoReportPanel.Visibility = hasReport ? Visibility.Collapsed : Visibility.Visible;
        NoValuesPanel.Visibility = hasReport && !hasItems ? Visibility.Visible : Visibility.Collapsed;

        if (ApplyButton != null) ApplyButton.IsEnabled = hasReport && hasItems;
        if (AddValueButton != null) AddValueButton.IsEnabled = hasReport;

        if (!hasItems)
        {
            SummaryText.Text = string.Empty;
            return;
        }

        var report = _items.Count(i => i.Scope == SemanticColorScope.Report);
        var pages = _items.Count - report;
        SummaryText.Text = $"All pages: {report} \u00B7 Page-specific: {pages}";
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
            .Select(ToRule)
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

        var result = await ShowRuleEditorAsync(null);
        if (result == null) return;

        var added = 0;
        foreach (var raw in result.Values)
        {
            var value = SemanticColorService.NormalizeRuleValue(raw);
            if (value.Length == 0) continue;

            var existing = _items.FirstOrDefault(i =>
                string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase));

            var item = existing ?? new SemanticColorRuleItem { Value = value };
            ApplyEditorResult(item, result);
            UpdateScopeText(item);

            if (existing == null) _items.Add(item);
            added++;
        }

        if (added == 0)
        {
            ToastService.Show("Type at least one value to add.", ToastSeverity.Warning);
            return;
        }

        SaveRules();
        RebuildGroups();
        UpdateEmptyStates();
        ToastService.Show("Saved. Click Apply to report to write it.", ToastSeverity.Informational);
    }

    private async void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SemanticColorRuleItem item)
        {
            return;
        }

        var result = await ShowRuleEditorAsync(item);
        if (result == null) return;

        var value = SemanticColorService.NormalizeRuleValue(result.Values.FirstOrDefault() ?? item.Value);
        if (value.Length == 0)
        {
            ToastService.Show("Enter a value.", ToastSeverity.Warning);
            return;
        }

        var clash = _items.FirstOrDefault(i =>
            !ReferenceEquals(i, item) &&
            string.Equals(i.Value, value, StringComparison.OrdinalIgnoreCase));

        if (clash != null)
        {
            ToastService.Show($"\u201C{value}\u201D already exists in the list.", ToastSeverity.Informational);
            return;
        }

        item.Value = value;
        ApplyEditorResult(item, result);
        UpdateScopeText(item);

        SaveRules();
        RebuildGroups();
        UpdateEmptyStates();
    }

    private void DeleteRule_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.DataContext is not SemanticColorRuleItem item)
        {
            return;
        }

        _items.Remove(item);
        SaveRules();
        RebuildGroups();
        UpdateEmptyStates();
    }

    private static void ApplyEditorResult(SemanticColorRuleItem item, RuleEditResult result)
    {
        item.SetColor(result.Color);
        item.Scope = result.Scope;
        item.PageIds = result.PageIds.ToList();
    }

    private async Task<RuleEditResult?> ShowRuleEditorAsync(SemanticColorRuleItem? existing)
    {
        var themeColors = ReportThemeService.GetDataColors(App.Workspace.ReportPath);
        var palette = (themeColors.Count > 0 ? themeColors : SemanticColorService.DefaultPalette)
            .Take(16)
            .ToList();

        using var picker = new ScreenPickService();

        var valueBox = new TextBox
        {
            Header = "Value(s)",
            PlaceholderText = "e.g. Channel Partner, Enterprise (comma-separated)",
            MinWidth = 340,
            Text = existing?.Value ?? string.Empty
        };

        var textPickerButton = new Button
        {
            Content = "Text picker",
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(8, 0, 0, 0)
        };
        ToolTipService.SetToolTip(textPickerButton, "Grab text from any tooltip on screen (Ctrl + Left click)");

        var statusText = new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Visibility = Visibility.Collapsed
        };

        var valueRow = new Grid();
        valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        valueRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        valueBox.HorizontalAlignment = HorizontalAlignment.Stretch;
        Grid.SetColumn(valueBox, 0);
        Grid.SetColumn(textPickerButton, 1);
        valueRow.Children.Add(valueBox);
        valueRow.Children.Add(textPickerButton);

        var initialHex = existing?.Hex ?? palette[_items.Count % palette.Count];
        var selectedColor = ParseHex(initialHex);

        var selectionStroke = new SolidColorBrush(Color.FromArgb(255, 0, 120, 215));
        var defaultStroke = new SolidColorBrush(Color.FromArgb(60, 128, 128, 128));

        var previewBorder = new Border
        {
            Width = 28,
            Height = 28,
            CornerRadius = new CornerRadius(4),
            BorderBrush = defaultStroke,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(selectedColor),
            VerticalAlignment = VerticalAlignment.Center
        };

        var chips = new List<(Border Fill, FontIcon Check, string Hex)>();

        var chipGrid = new VariableSizedWrapGrid
        {
            Orientation = Orientation.Horizontal,
            MaximumRowsOrColumns = 8,
            ItemWidth = 30,
            ItemHeight = 30,
            VerticalAlignment = VerticalAlignment.Center
        };

        void SetSelectedColor(Color color)
        {
            selectedColor = color;
            previewBorder.Background = new SolidColorBrush(color);

            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            foreach (var chip in chips)
            {
                var selected = string.Equals(chip.Hex, hex, StringComparison.OrdinalIgnoreCase);
                chip.Fill.BorderBrush = selected ? selectionStroke : defaultStroke;
                chip.Fill.BorderThickness = new Thickness(selected ? 2 : 1);
                chip.Check.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        foreach (var hex in palette)
        {
            var key = hex.ToUpperInvariant();
            var chipColor = ParseHex(hex);

            var fill = new Border
            {
                Width = 22,
                Height = 22,
                CornerRadius = new CornerRadius(4),
                Background = new SolidColorBrush(chipColor),
                BorderBrush = defaultStroke,
                BorderThickness = new Thickness(1)
            };

            var check = new FontIcon
            {
                Glyph = "\uE73E",
                FontSize = 13,
                Foreground = ContrastBrush(chipColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Visibility = Visibility.Collapsed
            };

            var chipContent = new Grid { Width = 30, Height = 30 };
            chipContent.Children.Add(fill);
            chipContent.Children.Add(check);

            var chipButton = new Button
            {
                Padding = new Thickness(0),
                Width = 30,
                Height = 30,
                MinWidth = 0,
                MinHeight = 0,
                BorderThickness = new Thickness(0),
                Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
                Content = chipContent,
                Tag = key
            };
            ToolTipService.SetToolTip(chipButton, key);
            chipButton.Click += (s, _) =>
            {
                if (s is Button button && button.Tag is string value) SetSelectedColor(ParseHex(value));
            };

            chips.Add((fill, check, key));
            chipGrid.Children.Add(chipButton);
        }

        var paletteBox = new Border
        {
            CornerRadius = new CornerRadius(6),
            BorderBrush = defaultStroke,
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromArgb(16, 128, 128, 128)),
            Padding = new Thickness(6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = chipGrid
        };

        var customPicker = BuildPicker(initialHex);
        customPicker.ColorChanged += (_, args) => SetSelectedColor(args.NewColor);

        var eyedropperIcon = new PathIcon
        {
            Data = (Geometry)XamlBindingHelper.ConvertValue(typeof(Geometry), EyedropperPath),
            Width = 16,
            Height = 16,
            VerticalAlignment = VerticalAlignment.Center
        };
        var eyedropperContent = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        eyedropperContent.Children.Add(eyedropperIcon);
        eyedropperContent.Children.Add(new TextBlock
        {
            Text = "Color picker",
            VerticalAlignment = VerticalAlignment.Center
        });

        var eyedropperButton = new Button
        {
            Content = eyedropperContent,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        ToolTipService.SetToolTip(eyedropperButton, "Pick a color from anywhere on screen");

        var customPanel = new StackPanel { Spacing = 8 };
        customPanel.Children.Add(customPicker);
        customPanel.Children.Add(eyedropperButton);

        var customFlyout = new Flyout { Content = customPanel };

        eyedropperButton.Click += (_, _) =>
        {
            customFlyout.Hide();

            if (picker.IsActive)
            {
                picker.Stop();
                ResetPickers();
                return;
            }

            picker.StartColorSample();
            statusText.Text = "Move anywhere \u2014 a magnifier follows the cursor. Left click to pick, Esc to cancel.";
            statusText.Visibility = Visibility.Visible;
            textPickerButton.Content = "Stop picking";
        };

        var customButton = new Button
        {
            Content = "Custom\u2026",
            VerticalAlignment = VerticalAlignment.Center,
            Flyout = customFlyout
        };

        SetSelectedColor(selectedColor);

        void ResetPickers()
        {
            textPickerButton.Content = "Text picker";
            statusText.Text = string.Empty;
            statusText.Visibility = Visibility.Collapsed;
        }

        picker.TextPicked += text =>
        {
            var current = (valueBox.Text ?? string.Empty).Trim();
            valueBox.Text = current.Length == 0 ? text : current + ", " + text;
            ResetPickers();
            ToastService.Show($"Grabbed \u201C{text}\u201D.", ToastSeverity.Success);
        };

        picker.ColorPreview += color => SetSelectedColor(color);

        picker.ColorPicked += color =>
        {
            SetSelectedColor(color);
            customPicker.Color = color;
            ResetPickers();

            var hex = $"#{color.R:X2}{color.G:X2}{color.B:X2}";
            try
            {
                var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
                package.SetText(hex);
                Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            }
            catch
            {
                // Clipboard can be busy; the color is still applied.
            }

            ToastService.Show($"Picked {hex} (copied to clipboard).", ToastSeverity.Success);
        };

        picker.Cancelled += ResetPickers;

        textPickerButton.Click += (_, _) =>
        {
            if (picker.IsActive)
            {
                picker.Stop();
                ResetPickers();
                return;
            }

            picker.StartTooltipTextPick();
            textPickerButton.Content = "Stop picking";
            statusText.Text = "Hover any tooltip on screen, then Ctrl + Left click to grab it. Esc to cancel.";
            statusText.Visibility = Visibility.Visible;
        };

        var colorRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center
        };
        colorRow.Children.Add(new TextBlock { Text = "Color", VerticalAlignment = VerticalAlignment.Center });
        colorRow.Children.Add(previewBorder);
        colorRow.Children.Add(paletteBox);
        colorRow.Children.Add(customButton);

        var reportRadio = new RadioButton { Content = "Entire report" };
        var pagesRadio = new RadioButton { Content = "Selected pages" };
        var targets = new RadioButtons();
        targets.Items.Add(reportRadio);
        targets.Items.Add(pagesRadio);
        targets.SelectedIndex = existing?.Scope == SemanticColorScope.Pages ? 1 : 0;
        targets.MaxColumns = 2;

        // List current pages, plus any stale ids already on the rule so they are preserved.
        var options = new List<ColorSyncPageOption>(_pageOptions);
        if (existing != null)
        {
            foreach (var id in existing.PageIds ?? new List<string>())
            {
                if (options.All(o => !string.Equals(o.Id, id, StringComparison.OrdinalIgnoreCase)))
                {
                    options.Add(new ColorSyncPageOption
                    {
                        Id = id,
                        DisplayName = $"{ShortId(id)} (missing page)",
                        IsMissing = true
                    });
                }
            }
        }

        var pageList = new ListView
        {
            SelectionMode = ListViewSelectionMode.Multiple,
            ItemsSource = options,
            DisplayMemberPath = nameof(ColorSyncPageOption.DisplayName),
            MaxHeight = 170,
            MinWidth = 340,
            Margin = new Thickness(0, 0, 14, 0)
        };

        if (existing != null)
        {
            var existingPageIds = existing.PageIds ?? new List<string>();
            foreach (var option in options)
            {
                if (existingPageIds.Any(id => string.Equals(id, option.Id, StringComparison.OrdinalIgnoreCase)))
                {
                    pageList.SelectedItems.Add(option);
                }
            }
        }

        var selectAll = new Button { Content = "Select all" };
        selectAll.Click += (_, _) => pageList.SelectAll();
        var clear = new Button { Content = "Clear" };
        clear.Click += (_, _) => pageList.SelectedItems.Clear();

        var pageButtons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        pageButtons.Children.Add(selectAll);
        pageButtons.Children.Add(clear);

        var pagesPanel = new StackPanel { Spacing = 6 };
        pagesPanel.Children.Add(pageList);
        pagesPanel.Children.Add(pageButtons);
        pagesPanel.Children.Add(new TextBlock
        {
            FontSize = 12,
            Opacity = 0.75,
            TextWrapping = TextWrapping.Wrap,
            Text = "Only the selected pages will be recolored."
        });

        void SyncScope() =>
            pagesPanel.Visibility = targets.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

        targets.SelectionChanged += (_, _) => SyncScope();
        SyncScope();

        var errorText = new TextBlock
        {
            FontSize = 12,
            Visibility = Visibility.Collapsed,
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 196, 43, 28))
        };

        var panel = new StackPanel { Spacing = 12, MinWidth = 360 };
        panel.Children.Add(valueRow);
        panel.Children.Add(statusText);
        panel.Children.Add(colorRow);
        panel.Children.Add(new TextBlock { Text = "Apply to" });
        panel.Children.Add(targets);
        panel.Children.Add(pagesPanel);
        panel.Children.Add(errorText);

        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = existing == null ? "Add value" : "Edit value",
            Content = new ScrollViewer { Content = panel, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = existing == null ? "Add" : "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary
        };

        dialog.PrimaryButtonClick += (_, args) =>
        {
            var text = (valueBox.Text ?? string.Empty).Trim();
            if (text.Length == 0)
            {
                args.Cancel = true;
                errorText.Text = "Enter at least one value.";
                errorText.Visibility = Visibility.Visible;
                return;
            }

            if (targets.SelectedIndex == 1 && pageList.SelectedItems.Count == 0)
            {
                args.Cancel = true;
                errorText.Text = "Select at least one page.";
                errorText.Visibility = Visibility.Visible;
            }
        };

        try
        {
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return null;
            }
        }
        catch (Exception ex)
        {
            App.Log($"Color sync dialog failed: {ex.Message}");
            return null;
        }

        var scope = targets.SelectedIndex == 1 ? SemanticColorScope.Pages : SemanticColorScope.Report;

        var pageIds = scope == SemanticColorScope.Pages
            ? pageList.SelectedItems
                .Cast<ColorSyncPageOption>()
                .Select(o => o.Id)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList()
            : new List<string>();

        var values = (valueBox.Text ?? string.Empty)
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

        return new RuleEditResult
        {
            Values = values,
            Color = selectedColor,
            Scope = scope,
            PageIds = pageIds
        };
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
    /// color back from the report (within its scope) to keep the swatches in sync.
    /// </summary>
    private void SyncColorsFromReport()
    {
        if (!App.Workspace.HasReport) return;

        var service = new SemanticColorService();
        foreach (var item in _items)
        {
            var hex = service.GetAppliedColor(Report, ToRule(item));
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

    private static string ShortId(string id) =>
        string.IsNullOrEmpty(id) ? "?" : id.Length <= 6 ? id : id.Substring(0, 6);

    private static string Plural(int count, string noun) =>
        count == 1 ? $"{count} {noun}" : $"{count} {noun}s";

    /// <summary>Picks a readable check-mark color (black or white) for a given swatch.</summary>
    private static SolidColorBrush ContrastBrush(Color color)
    {
        var luminance = (0.299 * color.R + 0.587 * color.G + 0.114 * color.B) / 255.0;
        return new SolidColorBrush(luminance > 0.55
            ? Color.FromArgb(255, 0, 0, 0)
            : Color.FromArgb(255, 255, 255, 255));
    }

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

    private sealed class RuleEditResult
    {
        public List<string> Values { get; init; } = new();
        public Color Color { get; init; }
        public SemanticColorScope Scope { get; init; }
        public List<string> PageIds { get; init; } = new();
    }
}
