using System.Collections.Generic;

namespace SingularTools_App.Shell;

/// <summary>
/// Central registry of every tool available in the app. To add a future tool,
/// create a Page implementing <see cref="IToolPage"/> and append a descriptor
/// here — the shell picks it up automatically.
/// </summary>
public static class ToolRegistry
{
    public static IReadOnlyList<ToolDescriptor> Tools { get; } = new List<ToolDescriptor>
    {
        new(
            id: "home",
            title: "Home",
            description: "Getting started with Singular Tools",
            glyph: "\uE80F", // Home
            pageType: typeof(Tools.Home.HomePage)),
        new(
            id: "report-pages-manager",
            title: "Pages Manager",
            description: "Search, reorder, rename and organize report pages",
            glyph: "\uE8A9", // Page
            pageType: typeof(Tools.ReportPagesManager.ReportPagesManagerPage)),
        new(
            id: "report-publishing-manager",
            title: "Multi-Workspace Publish",
            description: "Publish a report to multiple Power BI workspaces at once",
            glyph: "\uE724", // Send
            pageType: typeof(Tools.ReportPublishingManager.ReportPublishingManagerPage)),
        new(
            id: "semantic-color-manager",
            title: "Color Sync",
            description: "Keep matching values (e.g. Yes / No) the same color across every visual",
            glyph: "\uE790", // Color
            pageType: typeof(Tools.SemanticColorManager.SemanticColorManagerPage)),
        new(
            id: "publishing-groups",
            title: "Publishing Groups",
            description: "Choose which pages each workspace sees when you publish",
            glyph: "\uE724", // Send
            pageType: typeof(Tools.PublishingGroups.PublishingGroupsPage))
    };
}
