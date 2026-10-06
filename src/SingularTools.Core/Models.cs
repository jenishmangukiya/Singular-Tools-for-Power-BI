namespace SingularTools.Core.Models;

public enum SortMode
{
    Ascending,   // A to Z
    Descending,  // Z to A
    Natural,     // Smart alphanumeric (Page 1, Page 2, Page 10)
    Reverse      // Invert current order
}

public class ReportPage
{
    public string Id { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public int OrderIndex { get; set; }
    public bool IsActive { get; set; }
    public bool IsHidden { get; set; }
    public double Width { get; set; } = 1920;
    public double Height { get; set; } = 1080;
    public string DisplayOption { get; set; } = "FitToPage";
    public string FolderPath { get; set; } = string.Empty;
    public string PageJsonPath { get; set; } = string.Empty;

    public string FormattedSize => $"{(int)Width}x{(int)Height}";

    public override string ToString() => $"{DisplayName} (#{OrderIndex + 1}) {(IsActive ? "[Active]" : "")}";
}

public class VisualItemInfo
{
    public string Id { get; set; } = string.Empty;
    public string VisualType { get; set; } = string.Empty;
    public string DisplayTitle { get; set; } = string.Empty;
    public double X { get; set; }
    public double Y { get; set; }
    public double Width { get; set; }
    public double Height { get; set; }
    public int Z { get; set; }

    public string FriendlyType => FormatVisualType(VisualType);

    public static string FormatVisualType(string type)
    {
        if (string.IsNullOrWhiteSpace(type)) return "Visual";
        return type switch
        {
            "clusteredBarChart" => "Clustered Bar Chart",
            "clusteredColumnChart" => "Clustered Column Chart",
            "lineChart" => "Line Chart",
            "areaChart" => "Area Chart",
            "pieChart" => "Pie Chart",
            "donutChart" => "Donut Chart",
            "scatterChart" => "Scatter Chart",
            "card" => "Card",
            "multiRowCard" => "Multi-row Card",
            "tableEx" => "Table",
            "pivotTable" => "Matrix",
            "slicer" => "Slicer",
            "map" => "Map",
            "filledMap" => "Filled Map",
            "gauge" => "Gauge",
            "kpi" => "KPI",
            "textbox" => "Text Box",
            "image" => "Image",
            "shape" => "Shape",
            "actionButton" => "Button",
            _ => char.ToUpperInvariant(type[0]) + type.Substring(1)
        };
    }
}

public class PageVisualInfo
{
    public string PageId { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public double PageWidth { get; set; } = 1920;
    public double PageHeight { get; set; } = 1080;
    public bool IsActive { get; set; }
    public List<VisualItemInfo> Visuals { get; set; } = new();

    public int VisualCount => Visuals.Count;
}

/// <summary>Where a semantic color rule applies.</summary>
public enum SemanticColorScope
{
    /// <summary>Every page in the report.</summary>
    Report,

    /// <summary>Only the pages listed in <see cref="SemanticColorRule.PageIds"/>.</summary>
    Pages
}

/// <summary>
/// A user-defined semantic color: any legend/category/slice/series string should
/// be shown in <see cref="Hex"/> on the pages selected by <see cref="Scope"/>.
/// Values are matched as universal strings (case-insensitive, type suffix ignored),
/// independent of the fields they belong to.
/// </summary>
public sealed class SemanticColorRule
{
    /// <summary>Human-readable value to match, e.g. "Channel Partner", "Yes", "2014".</summary>
    public string Value { get; set; } = string.Empty;

    /// <summary>Target color in "#RRGGBB" form.</summary>
    public string Hex { get; set; } = string.Empty;

    /// <summary>Whether the rule targets the whole report or specific pages.</summary>
    public SemanticColorScope Scope { get; set; } = SemanticColorScope.Report;

    /// <summary>Page ids the rule targets when <see cref="Scope"/> is <see cref="SemanticColorScope.Pages"/>.</summary>
    public List<string> PageIds { get; set; } = new();
}

/// <summary>
/// A named page recipe for publishing. It records which report pages stay
/// visible when the report is published with this choice; every other page is
/// hidden in the published copy.
///
/// It also remembers the workspaces it was last published to, so publishing the
/// same group again does not mean re-ticking the destination every time. The
/// names are stored with the report (not the machine) because "where this group
/// goes" is a property of the group, while "which workspaces exist" is not.
/// </summary>
public sealed class PublishingGroup
{
    /// <summary>Friendly name the author gives the recipe, e.g. "Client A – External".</summary>
    public string Name { get; set; } = string.Empty;

    /// <summary>Ids of the pages that stay visible when this group is published.</summary>
    public List<string> VisiblePageIds { get; set; } = new();

    /// <summary>Workspaces this group was last published to; pre-ticked next time.</summary>
    public List<string> WorkspaceNames { get; set; } = new();
}

/// <summary>Result of applying semantic color rules to a report.</summary>
public sealed class SemanticColorApplyResult
{
    /// <summary>Distinct visuals whose definition file was rewritten.</summary>
    public int VisualsChanged { get; set; }

    /// <summary>Existing color selectors that were recolored.</summary>
    public int SelectorsChanged { get; set; }

    /// <summary>New color selectors that were created.</summary>
    public int SelectorsCreated { get; set; }

    /// <summary>Files written to disk.</summary>
    public int FilesWritten { get; set; }
}

/// <summary>
/// A remembered "this field was renamed to that field" decision, so a database rename
/// that recurs across refresh cycles only has to be answered once.
/// </summary>
/// <remarks>
/// Stored per report rather than per machine: whether <c>financials.Segment</c> became
/// <c>financials.SalesSegment</c> is a fact about one report's model, not about this
/// install. Replaying a mapping is still opt-in per scan, because a later rename can
/// make an old mapping wrong.
/// </remarks>
public sealed class FieldRemap
{
    /// <summary>The entity the visual used to point at, e.g. "financials".</summary>
    public string OldEntity { get; set; } = string.Empty;

    /// <summary>The field the visual used to point at, e.g. "Segment".</summary>
    public string OldProperty { get; set; } = string.Empty;

    /// <summary>The entity that replaced it, e.g. "financials".</summary>
    public string NewEntity { get; set; } = string.Empty;

    /// <summary>The replacement field, e.g. "SalesSegment".</summary>
    public string NewProperty { get; set; } = string.Empty;

    /// <summary>When this mapping was recorded, for display and for pruning stale entries.</summary>
    public DateTimeOffset RecordedUtc { get; set; }

    /// <summary>The "entity.property" form used as the stable identity of the old field.</summary>
    public string OldKey => (OldEntity + "." + OldProperty).TrimStart('.');

    /// <summary>The "entity.property" form of the replacement.</summary>
    public string NewKey => (NewEntity + "." + NewProperty).TrimStart('.');
}
