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

/// <summary>How a legend item is targeted in a visual's color selector.</summary>
public enum SemanticColorTargetKind
{
    /// <summary>A category/legend member, e.g. Series "2013" or slice "Paseo" (scopeId Comparison selector).</summary>
    MemberValue,

    /// <summary>A measure/field series identity, e.g. "financials.Revenue" (metadata selector).</summary>
    SeriesIdentity
}

/// <summary>
/// One distinct legend item discovered in a report's visual color selectors.
/// Member values are global to the report (the same text maps to the same color
/// everywhere); series identities are matched by their queryRef.
/// </summary>
public sealed class SemanticColorValue
{
    public SemanticColorTargetKind Kind { get; set; } = SemanticColorTargetKind.MemberValue;

    /// <summary>Stable lookup key: normalized literal for members, queryRef for series identities.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>The literal token exactly as stored, e.g. <c>'Yes'</c> / <c>2013L</c>; or the queryRef.</summary>
    public string RawValue { get; set; } = string.Empty;

    /// <summary>Human-readable value with quotes/type suffixes removed, e.g. <c>Yes</c> / <c>2013</c>.</summary>
    public string DisplayValue { get; set; } = string.Empty;

    /// <summary>Field property this item belongs to (e.g. "Year", "Revenue").</summary>
    public string FieldName { get; set; } = string.Empty;

    /// <summary>Entity (table) the field belongs to, when known.</summary>
    public string Entity { get; set; } = string.Empty;

    /// <summary>True when the user added this value by hand in the tool.</summary>
    public bool IsManual { get; set; }

    /// <summary>How many color selectors across the report reference this item.</summary>
    public int SelectorCount { get; set; }

    /// <summary>Distinct visuals (page/visual) that reference this item.</summary>
    public List<string> VisualRefs { get; set; } = new();

    /// <summary>Field names projected by the visuals where this item was found.</summary>
    public List<string> Fields { get; set; } = new();

    /// <summary>Distinct current colors; a literal hex or the word "Theme" for theme-based colors.</summary>
    public List<string> CurrentColors { get; set; } = new();

    /// <summary>The literal hex shared by every reference, or null when colors conflict or are theme-based.</summary>
    public string? CommonColor { get; set; }

    public bool HasConflict => CurrentColors.Count > 1;

    public bool HasThemeColor => CurrentColors.Any(c => string.Equals(c, "Theme", StringComparison.OrdinalIgnoreCase));

    public int VisualCount => VisualRefs.Count;

    public string KindLabel => Kind == SemanticColorTargetKind.MemberValue ? "Value" : "Series";
}

/// <summary>Result of scanning a report for bar/column/slice legend items.</summary>
public sealed class SemanticColorScan
{
    public string ReportPath { get; set; } = string.Empty;
    public List<SemanticColorValue> Values { get; set; } = new();
    public int VisualCount { get; set; }
    public int TotalSelectors { get; set; }
}

/// <summary>Result of applying a value-to-color mapping to a report.</summary>
public sealed class SemanticColorApplyResult
{
    public int VisualsChanged { get; set; }
    public int SelectorsChanged { get; set; }
    public int FilesWritten { get; set; }
}
