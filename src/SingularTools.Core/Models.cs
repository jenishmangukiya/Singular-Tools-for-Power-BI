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
