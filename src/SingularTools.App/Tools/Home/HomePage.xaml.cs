using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SingularTools.Core;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.Home;

public sealed partial class HomePage : Page, IToolPage
{
    public string ToolId => "home";
    public string Title => "Home";
    public string Description => "Getting started with Singular Tools";
    public string Glyph => "\uE80F";

    private readonly HomeViewModel _viewModel = new();
    private bool _subscribed;

    public HomePage()
    {
        InitializeComponent();
        _ = BrandAssets.ApplyAsync(HomeLogo);
        Loaded += HomePage_Loaded;
        Unloaded += HomePage_Unloaded;
    }

    public void OnActivated()
    {
        PopulateTools();
        UpdateReportBand();
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        PopulateTools();
        Subscribe();
        TryDiscoverReport();
        UpdateReportBand();
    }

    private void HomePage_Unloaded(object sender, RoutedEventArgs e)
    {
        if (_subscribed)
        {
            App.Workspace.Changed -= Workspace_Changed;
            _subscribed = false;
        }
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        App.Workspace.Changed += Workspace_Changed;
        _subscribed = true;
    }

    private void Workspace_Changed(object? sender, EventArgs e)
    {
        PopulateTools();
        UpdateReportBand();
    }

    /// <summary>
    /// Rebuilds the grouped card grid. Cheap enough to do wholesale: there are only
    /// a handful of tools, and re-projecting keeps every card's readiness badge in
    /// step with the current report.
    /// </summary>
    private void PopulateTools()
    {
        if (SectionsRepeater == null) return;

        SectionsRepeater.ItemsSource = _viewModel.BuildSections(
            excludeToolId: ToolId,
            hasReport: App.Workspace.HasReport,
            hasSemanticModel: App.Workspace.HasSemanticModel,
            pageCount: App.Workspace.Manager.Pages.Count);
    }

    private void UpdateReportBand()
    {
        if (ReportNameText == null) return;

        var hasReport = App.Workspace.HasReport;
        var pages = App.Workspace.Manager.Pages;

        if (hasReport)
        {
            ReportNameText.Text = App.Workspace.ReportName;
            ReportPathText.Text = App.Workspace.ReportPath;
        }
        else
        {
            ReportNameText.Text = "No report loaded";
            ReportPathText.Text = "Open a Power BI project (.pbip) folder to get started.";
        }

        UpdateReportButton(hasReport);

        SetChip(PagesChip, PagesChipText, hasReport,
            pages.Count == 1 ? "1 page" : $"{pages.Count} pages");
        SetChip(ModelChip, ModelChipText, hasReport,
            App.Workspace.HasSemanticModel ? "Semantic model linked" : "No semantic model");

        // Always shown: it tells the author whether live sync with Desktop is possible.
        SetChip(DesktopChip, DesktopChipText, visible: true,
            PowerBiPublisher.IsPowerBiRunning()
                ? "Power BI Desktop running"
                : "Power BI Desktop not detected");
    }

    /// <summary>
    /// When Power BI Desktop launched us from its External Tools ribbon, the open
    /// report is pinned to that model: Power BI owns it, so offering to switch it
    /// would only produce a second, unsynced session. The button is disabled rather
    /// than hidden so the band keeps its layout, and the tooltip says why.
    /// </summary>
    private void UpdateReportButton(bool hasReport)
    {
        if (App.LaunchedFromPowerBi)
        {
            OpenReportButton.IsEnabled = false;
            OpenReportButtonText.Text = "Report set by Power BI";
            ToolTipService.SetToolTip(
                OpenReportButton,
                "Opened from Power BI Desktop, so the report can't be switched here.");
            return;
        }

        OpenReportButton.IsEnabled = true;
        OpenReportButtonText.Text = hasReport ? "Switch report" : "Open report";
        ToolTipService.SetToolTip(OpenReportButton, null);
    }

    private static void SetChip(Border chip, TextBlock text, bool visible, string value)
    {
        chip.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        text.Text = value;
    }

    private void TryDiscoverReport()
    {
        if (App.Workspace.HasReport) return;

        try
        {
            var discovered = ReportManager.DiscoverReportFolder();
            if (discovered != null)
            {
                App.Workspace.OpenReport(discovered);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Home report discovery failed: {ex.Message}");
        }
    }

    private async void OpenReportButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var path = await ReportPicker.PickReportFolderAsync();
            if (string.IsNullOrEmpty(path)) return;

            if (App.Workspace.OpenReport(path))
            {
                ToastService.Show($"Opened '{App.Workspace.ReportName}'.", ToastSeverity.Success);
            }
            else
            {
                ToastService.Show("That folder does not contain definition/pages/pages.json.", ToastSeverity.Error);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Open report failed: {ex}");
            ToastService.Show($"Could not open report: {ex.Message}", ToastSeverity.Error);
        }

        PopulateTools();
        UpdateReportBand();
    }

    private void ToolCard_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string toolId })
        {
            App.CurrentMainWindow?.NavigateToTool(toolId);
        }
    }
}
