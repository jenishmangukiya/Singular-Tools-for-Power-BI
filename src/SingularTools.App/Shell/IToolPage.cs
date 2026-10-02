using System;

namespace SingularTools_App.Shell;

/// <summary>
/// Contract implemented by every tool hosted in the Singular Tools shell.
/// Adding a new tool only requires implementing this interface and registering
/// it in <see cref="ToolRegistry"/>.
/// </summary>
public interface IToolPage
{
    /// <summary>Stable identifier used for navigation and persistence.</summary>
    string ToolId { get; }

    /// <summary>Short label shown in the navigation rail.</summary>
    string Title { get; }

    /// <summary>One-line description shown as the navigation tooltip.</summary>
    string Description { get; }

    /// <summary>Segoe Fluent Icons glyph for the navigation rail.</summary>
    string Glyph { get; }

    /// <summary>
    /// Called when the tool becomes the active tool. Tools should refresh their
    /// data and focus their primary input here.
    /// </summary>
    void OnActivated();

    /// <summary>
    /// When true, the shell must not auto-flush Power BI Desktop via UIA on
    /// focus regain while this tool is active (the tool itself drives Desktop).
    /// </summary>
    bool SkipAutoPowerBiSync => false;
}

/// <summary>
/// Broad job a tool belongs to. Drives the grouping of the Home launcher's tool
/// grid, so the six tools read as a few related clusters rather than one flat list.
/// </summary>
public enum ToolCategory
{
    /// <summary>Shaping the report itself: its pages and how values are colored.</summary>
    ReportStructure,

    /// <summary>Getting the report out to workspaces.</summary>
    Deployment,

    /// <summary>Working on the semantic model (TMDL) behind the report.</summary>
    SemanticModel
}

/// <summary>What a tool needs before it can do anything useful, so Home can say so up front.</summary>
public enum ToolRequirement
{
    /// <summary>Nothing — the tool works with or without an open report.</summary>
    None,

    /// <summary>A report must be open. <see cref="ReportWorkspace.HasReport"/>.</summary>
    Report,

    /// <summary>A sibling semantic model must exist. <see cref="ReportWorkspace.HasSemanticModel"/>.</summary>
    SemanticModel
}

/// <summary>Describes a tool so the shell can build navigation without referencing the page type directly.</summary>
public sealed class ToolDescriptor
{
    public ToolDescriptor(
        string id,
        string title,
        string description,
        string glyph,
        Type pageType,
        ToolCategory category = ToolCategory.ReportStructure,
        ToolRequirement requirement = ToolRequirement.None)
    {
        Id = id;
        Title = title;
        Description = description;
        Glyph = glyph;
        PageType = pageType;
        Category = category;
        Requirement = requirement;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Glyph { get; }
    public Type PageType { get; }

    /// <summary>Grouping used by the Home launcher.</summary>
    public ToolCategory Category { get; }

    /// <summary>What the tool needs to be usable, surfaced as a badge on its Home card.</summary>
    public ToolRequirement Requirement { get; }
}
