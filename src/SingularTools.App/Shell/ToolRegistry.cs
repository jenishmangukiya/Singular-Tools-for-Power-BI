using System.Collections.Generic;

namespace SingularTools_App.Shell;

/// <summary>
/// Central registry of every tool available in the app. To add a future tool,
/// create a Page implementing <see cref="IToolPage"/> and append a descriptor
/// here — the shell picks it up automatically, and the Home launcher files it
/// under the section matching <see cref="ToolDescriptor.Category"/>.
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
            pageType: typeof(Tools.Home.HomePage),
            category: ToolCategory.ReportStructure,
            requirement: ToolRequirement.None),
        new(
            id: "report-pages-manager",
            title: "Pages Manager",
            description: "Search, reorder, rename and organize report pages",
            glyph: "\uE8A9", // Page
            pageType: typeof(Tools.ReportPagesManager.ReportPagesManagerPage),
            category: ToolCategory.ReportStructure,
            requirement: ToolRequirement.Report),
        new(
            id: "semantic-color-manager",
            title: "Color Sync",
            description: "Keep matching values (e.g. Yes / No) the same color across every visual",
            glyph: "\uE790", // Color
            pageType: typeof(Tools.SemanticColorManager.SemanticColorManagerPage),
            category: ToolCategory.ReportStructure,
            requirement: ToolRequirement.Report),
        new(
            id: "field-repair",
            title: "Field Repair",
            description: "Find and repair visuals and filters broken by a renamed field",
            glyph: "\uE945", // Repair
            pageType: typeof(Tools.FieldRepair.FieldRepairPage),
            category: ToolCategory.ReportStructure,
            requirement: ToolRequirement.SemanticModel),
        new(
            id: "report-publishing-manager",
            title: "Multi-Workspace Publish",
            description: "Publish a report to multiple Power BI workspaces at once",
            glyph: "\uE724", // Send
            pageType: typeof(Tools.ReportPublishingManager.ReportPublishingManagerPage),
            category: ToolCategory.Deployment,
            requirement: ToolRequirement.Report),
        new(
            id: "publishing-groups",
            title: "Publishing Groups",
            description: "Choose which pages each workspace sees when you publish",
            glyph: "\uE8F1", // BulletedList
            pageType: typeof(Tools.PublishingGroups.PublishingGroupsPage),
            category: ToolCategory.Deployment,
            requirement: ToolRequirement.Report),
        new(
            id: "sort-by-column",
            title: "Sort by Column",
            description: "Point text columns at their order columns so they sort logically",
            glyph: "\uE8CB", // Sort
            pageType: typeof(Tools.SortByColumn.SortByColumnPage),
            category: ToolCategory.SemanticModel,
            requirement: ToolRequirement.SemanticModel),
        new(
            id: "object-security",
            title: "Object Security",
            description: "Manage object-level security (OLS) roles for tables and columns",
            glyph: "\uE72E", // Lock
            pageType: typeof(Tools.ObjectSecurity.ObjectSecurityPage),
            category: ToolCategory.SemanticModel,
            requirement: ToolRequirement.SemanticModel)
    };
}
