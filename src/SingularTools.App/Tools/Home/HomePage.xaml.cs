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
        UpdateReportStatus();
    }

    private void HomePage_Loaded(object sender, RoutedEventArgs e)
    {
        PopulateTools();
        Subscribe();
        TryDiscoverReport();
        UpdateReportStatus();
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

    private void Workspace_Changed(object? sender, EventArgs e) => UpdateReportStatus();

    private void PopulateTools()
    {
        if (ToolsGrid == null) return;

        ToolsGrid.ItemsSource = ToolRegistry.Tools
            .Where(t => !string.Equals(t.Id, ToolId, StringComparison.OrdinalIgnoreCase))
            .ToList();
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

        UpdateReportStatus();
    }

    private void UpdateReportStatus()
    {
        if (ReportNameText == null) return;

        if (App.Workspace.HasReport)
        {
            ReportNameText.Text = App.Workspace.ReportName;
            ReportHintText.Text = App.Workspace.ReportPath;
            ReportPathText.Text = App.Workspace.ReportName;
        }
        else
        {
            ReportNameText.Text = "No report loaded";
            ReportHintText.Text = "Open a Power BI project (.pbip) folder to manage its report pages.";
            ReportPathText.Text = "No report loaded";
        }
    }

    private void ToolsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolDescriptor tool)
        {
            App.CurrentMainWindow?.NavigateToTool(tool.Id);
        }
    }
}
