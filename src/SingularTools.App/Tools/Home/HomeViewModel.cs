using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.UI.Xaml;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.Home;

/// <summary>
/// One tool rendered as a card on the Home launcher. Carries the truth about
/// whether the tool can actually do anything yet, so the card can say so before
/// the author clicks into an empty page.
/// </summary>
public sealed class ToolCardViewModel
{
    private const string PagesManagerToolId = "report-pages-manager";

    public ToolCardViewModel(ToolDescriptor tool, bool hasReport, bool hasSemanticModel, int pageCount)
    {
        Id = tool.Id;
        Title = tool.Title;
        Description = tool.Description;
        Glyph = tool.Glyph;

        IsReady = tool.Requirement switch
        {
            ToolRequirement.Report => hasReport,
            ToolRequirement.SemanticModel => hasSemanticModel,
            _ => true
        };

        // A live count is more useful than a static pill, but only stat we can
        // read without touching disk is the page list already held in memory.
        if (IsReady && string.Equals(tool.Id, PagesManagerToolId, StringComparison.Ordinal) && hasReport)
        {
            BadgeText = pageCount == 1 ? "1 page" : $"{pageCount} pages";
        }
        else if (!IsReady)
        {
            BadgeText = tool.Requirement == ToolRequirement.SemanticModel
                ? "Needs a semantic model"
                : "Needs a report";
        }
        else
        {
            BadgeText = string.Empty;
        }
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Glyph { get; }

    /// <summary>Badge copy, or empty when the card needs no badge.</summary>
    public string BadgeText { get; }

    public Visibility BadgeVisibility =>
        string.IsNullOrEmpty(BadgeText) ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>False when the tool's requirement is unmet; the card is still clickable.</summary>
    public bool IsReady { get; }

    /// <summary>Dims only the icon tile when the tool isn't usable yet, keeping text legible.</summary>
    public double IconTileOpacity => IsReady ? 1.0 : 0.4;
}

/// <summary>A titled group of tool cards on the Home launcher.</summary>
public sealed class ToolSectionViewModel
{
    public ToolSectionViewModel(string title, IReadOnlyList<ToolCardViewModel> cards)
    {
        Title = title;
        Cards = cards;
    }

    public string Title { get; }
    public IReadOnlyList<ToolCardViewModel> Cards { get; }
}

/// <summary>Builds the Home launcher's grouped tool grid from the registry.</summary>
public sealed class HomeViewModel
{
    /// <summary>
    /// Section order and titles — the single place the launcher's grouping is defined.
    /// A category with no tools is dropped, so this list can safely name categories
    /// before any tool uses them.
    /// </summary>
    private static readonly (ToolCategory Category, string Title)[] SectionOrder =
    {
        (ToolCategory.ReportStructure, "Report structure"),
        (ToolCategory.Deployment, "Deployment"),
        (ToolCategory.SemanticModel, "Semantic model")
    };

    public IReadOnlyList<ToolSectionViewModel> BuildSections(
        string excludeToolId,
        bool hasReport,
        bool hasSemanticModel,
        int pageCount)
    {
        return SectionOrder
            .Select(section => new ToolSectionViewModel(
                section.Title,
                ToolRegistry.Tools
                    .Where(t => t.Category == section.Category
                                && !string.Equals(t.Id, excludeToolId, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new ToolCardViewModel(t, hasReport, hasSemanticModel, pageCount))
                    .ToList()))
            .Where(section => section.Cards.Count > 0)
            .ToList();
    }
}
