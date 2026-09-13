using System;
using System.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.Home;

public sealed partial class HomePage : Page, IToolPage
{
    public string ToolId => "home";
    public string Title => "Home";
    public string Description => "Getting started with Singular Tools";
    public string Glyph => "\uE80F";

    public HomePage()
    {
        InitializeComponent();
        _ = BrandAssets.ApplyAsync(HomeLogo);
        Loaded += (_, _) => PopulateTools();
    }

    public void OnActivated()
    {
        PopulateTools();
    }

    private void PopulateTools()
    {
        if (ToolsGrid == null) return;

        ToolsGrid.ItemsSource = ToolRegistry.Tools
            .Where(t => !string.Equals(t.Id, ToolId, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void ToolsGrid_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is ToolDescriptor tool)
        {
            App.CurrentMainWindow?.NavigateToTool(tool.Id);
        }
    }
}
