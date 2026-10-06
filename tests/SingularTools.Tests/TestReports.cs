using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace SingularTools.Tests;

/// <summary>Schema URLs written into generated definition files.</summary>
internal static class TestSchemas
{
    public const string Page =
        "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/page/2.1.0/schema.json";

    public const string PagesMetadata =
        "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/pagesMetadata/1.1.0/schema.json";

    public const string VisualContainer =
        "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/visualContainer/2.12.0/schema.json";
}

/// <summary>
/// A throwaway PBIR project under the temp folder: a <c>&lt;name&gt;.Report</c>
/// folder plus an optional sibling <c>&lt;name&gt;.SemanticModel</c>. Tests own
/// exactly the pages and visuals they assert on, so they never depend on the
/// checked-in demo report (which is re-saved from Power BI and drifts).
/// Dispose to delete everything.
/// </summary>
internal sealed class TestReport : IDisposable
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _projectRoot;
    private readonly List<string> _displayNames;

    /// <summary>Writes the project to disk.</summary>
    /// <param name="reportName">Base name; the folders become <c>&lt;name&gt;.Report</c> / <c>&lt;name&gt;.SemanticModel</c>.</param>
    /// <param name="pages">Pages in page-order, written in the given order.</param>
    /// <param name="semanticModel">Optional model folder; created only when supplied.</param>
    /// <param name="activePageDisplayName">Active page; defaults to the last page.</param>
    public TestReport(
        string reportName,
        IReadOnlyList<TestPage> pages,
        TestSemanticModel? semanticModel = null,
        string? activePageDisplayName = null)
    {
        if (string.IsNullOrWhiteSpace(reportName))
            throw new ArgumentException("A report needs a name.", nameof(reportName));
        if (pages.Count == 0)
            throw new ArgumentException("A report needs at least one page.", nameof(pages));

        _projectRoot = Path.Combine(Path.GetTempPath(), "singular-pbir-" + Guid.NewGuid().ToString("N"));
        _displayNames = pages.Select(p => p.DisplayName).ToList();

        ProjectRootPath = _projectRoot;
        ReportPath = Path.Combine(_projectRoot, reportName + ".Report");
        SemanticModelPath = Path.Combine(_projectRoot, reportName + ".SemanticModel");
        PagesDirectoryPath = Path.Combine(ReportPath, "definition", "pages");

        var activeIndex = pages.Count - 1;
        if (activePageDisplayName != null)
        {
            activeIndex = IndexOfDisplayName(activePageDisplayName);
            if (activeIndex < 0)
                throw new ArgumentException(
                    $"The report has no page named '{activePageDisplayName}'.", nameof(activePageDisplayName));
        }

        var pageOrder = new JsonArray();
        var ids = new List<string>(pages.Count);

        Directory.CreateDirectory(PagesDirectoryPath);

        for (var i = 0; i < pages.Count; i++)
        {
            var pageId = PageIdAt(i);
            ids.Add(pageId);
            pageOrder.Add(pageId);
            WritePage(pageId, pages[i], i);
        }

        var metadata = new JsonObject
        {
            ["$schema"] = TestSchemas.PagesMetadata,
            ["pageOrder"] = pageOrder,
            ["activePageName"] = ids[activeIndex]
        };
        File.WriteAllText(Path.Combine(PagesDirectoryPath, "pages.json"), metadata.ToJsonString(WriteOptions));

        if (semanticModel != null)
        {
            var tablesDir = Path.Combine(SemanticModelPath, "definition", "tables");
            Directory.CreateDirectory(tablesDir);
            foreach (var table in semanticModel.Tables)
            {
                File.WriteAllText(Path.Combine(tablesDir, table.Name + ".tmdl"), table.ToTmdl());
            }
        }
    }

    public string ProjectRootPath { get; }

    public string ReportPath { get; }

    public string SemanticModelPath { get; }

    public string PagesDirectoryPath { get; }

    /// <summary>Every generated page id, in page order.</summary>
    public IReadOnlyList<string> PageIds
    {
        get
        {
            var ids = new List<string>(_displayNames.Count);
            for (var i = 0; i < _displayNames.Count; i++) ids.Add(PageIdAt(i));
            return ids;
        }
    }

    /// <summary>Generates the PBIR page id used for a page display name.</summary>
    public string PageIdFor(string displayName)
    {
        var index = IndexOfDisplayName(displayName);
        if (index < 0)
            throw new InvalidOperationException($"The report has no page named '{displayName}'.");
        return PageIdAt(index);
    }

    /// <summary>Generates the visual folder id used for the n-th visual on a page.</summary>
    public string VisualIdFor(string displayName, int index)
    {
        var pageIndex = IndexOfDisplayName(displayName);
        if (pageIndex < 0)
            throw new InvalidOperationException($"The report has no page named '{displayName}'.");
        return $"{pageIndex + 1:x2}{index + 1:x18}";
    }

    /// <summary>The visual.json path of the n-th visual (0-based) on a page.</summary>
    public string VisualPath(string displayName, int index = 0) =>
        Path.Combine(
            PagesDirectoryPath,
            PageIdFor(displayName),
            "visuals",
            VisualIdFor(displayName, index),
            "visual.json");

    private static string PageIdAt(int index) => $"{index + 1:x20}";

    private int IndexOfDisplayName(string displayName) => _displayNames.FindIndex(
        n => string.Equals(n, displayName, StringComparison.OrdinalIgnoreCase));

    private void WritePage(string pageId, TestPage page, int pageIndex)
    {
        var folder = Path.Combine(PagesDirectoryPath, pageId);
        Directory.CreateDirectory(folder);

        var pageJson = new JsonObject
        {
            ["$schema"] = TestSchemas.Page,
            ["name"] = pageId,
            ["displayName"] = page.DisplayName,
            ["displayOption"] = page.DisplayOption,
            ["height"] = page.Height,
            ["width"] = page.Width
        };

        if (!string.IsNullOrEmpty(page.Visibility))
        {
            pageJson["visibility"] = page.Visibility;
        }

        File.WriteAllText(Path.Combine(folder, "page.json"), pageJson.ToJsonString(WriteOptions));

        if (page.Visuals.Count == 0 && page.DanglingVisualFolders == 0) return;

        var visualsDir = Path.Combine(folder, "visuals");
        Directory.CreateDirectory(visualsDir);

        for (var v = 0; v < page.Visuals.Count; v++)
        {
            var visualId = $"{pageIndex + 1:x2}{v + 1:x18}";
            var visualFolder = Path.Combine(visualsDir, visualId);
            Directory.CreateDirectory(visualFolder);
            File.WriteAllText(
                Path.Combine(visualFolder, "visual.json"),
                page.Visuals[v].ToJson(visualId).ToJsonString(WriteOptions));
        }

        for (var d = 0; d < page.DanglingVisualFolders; d++)
        {
            // Power BI occasionally leaves a visual folder with no visual.json behind.
            Directory.CreateDirectory(Path.Combine(visualsDir, $"{pageIndex + 1:x2}dead{d:x14}"));
        }
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_projectRoot)) Directory.Delete(_projectRoot, recursive: true);
        }
        catch
        {
        }
    }
}

/// <summary>One page to generate. Pages default to 1920x1080, FitToPage and no visuals.</summary>
internal sealed class TestPage
{
    public TestPage(string displayName, params TestVisual[] visuals)
    {
        DisplayName = displayName;
        Visuals = visuals;
    }

    public string DisplayName { get; }

    public IReadOnlyList<TestVisual> Visuals { get; }

    public double Width { get; init; } = 1920;

    public double Height { get; init; } = 1080;

    public string DisplayOption { get; init; } = "FitToPage";

    /// <summary>Written as page.json "visibility" when set, e.g. "HiddenInViewMode".</summary>
    public string? Visibility { get; init; }

    /// <summary>
    /// How many <c>visuals\&lt;id&gt;</c> folders to create <i>without</i> a
    /// visual.json, as Power BI occasionally leaves behind.
    /// </summary>
    public int DanglingVisualFolders { get; init; }
}

/// <summary>A visual.json to generate: a visualType, a position and an optional query.</summary>
internal sealed class TestVisual
{
    public TestVisual(string visualType, params TestProjection[] projections)
    {
        VisualType = visualType;
        Projections = projections;
    }

    public string VisualType { get; }

    public IReadOnlyList<TestProjection> Projections { get; }

    /// <summary>Literal title written to visual.objects.title; omitted when null.</summary>
    public string? Title { get; init; }

    public double X { get; init; }

    public double Y { get; init; }

    public double Width { get; init; } = 600;

    public double Height { get; init; } = 400;

    public int Z { get; init; }

    internal JsonObject ToJson(string visualId)
    {
        var visual = new JsonObject { ["visualType"] = VisualType };

        if (Projections.Count > 0)
        {
            visual["query"] = BuildQuery();
        }

        if (!string.IsNullOrEmpty(Title))
        {
            visual["objects"] = new JsonObject { ["title"] = BuildTitle() };
        }

        return new JsonObject
        {
            ["$schema"] = TestSchemas.VisualContainer,
            ["name"] = visualId,
            ["position"] = new JsonObject
            {
                ["x"] = X,
                ["y"] = Y,
                ["z"] = Z,
                ["width"] = Width,
                ["height"] = Height
            },
            ["visual"] = visual
        };
    }

    private JsonObject BuildQuery()
    {
        var queryState = new JsonObject();

        foreach (var projection in Projections)
        {
            if (queryState[projection.Role] is not JsonObject role)
            {
                role = new JsonObject { ["projections"] = new JsonArray() };
                queryState[projection.Role] = role;
            }

            ((JsonArray)role["projections"]!).Add(new JsonObject
            {
                ["field"] = projection.ToFieldJson(),
                ["queryRef"] = projection.QueryRef,
                ["nativeQueryRef"] = projection.NativeQueryRef,
                ["active"] = true
            });
        }

        return new JsonObject { ["queryState"] = queryState };
    }

    private JsonObject BuildTitle() => new()
    {
        ["properties"] = new JsonObject
        {
            ["text"] = new JsonObject
            {
                ["expr"] = new JsonObject
                {
                    ["Literal"] = new JsonObject { ["Value"] = "'" + Title!.Replace("'", "''") + "'" }
                }
            }
        }
    };
}

/// <summary>
/// One <c>query.queryState</c> projection: a plain column (a member field) or,
/// when <see cref="AggregateFunction"/> is set, an aggregation (a measure).
/// </summary>
internal sealed class TestProjection
{
    private string? _queryRef;

    public TestProjection(string role, string entity, string property)
    {
        Role = role;
        Entity = entity;
        Property = property;
    }

    /// <summary>queryState role, e.g. "Category", "Series", "Y", "Rows".</summary>
    public string Role { get; }

    public string Entity { get; }

    public string Property { get; }

    /// <summary>Defaults to <c>Entity.Property</c>.</summary>
    public string QueryRef
    {
        get => _queryRef ?? $"{Entity}.{Property}";
        init => _queryRef = value;
    }

    /// <summary>The label Power BI shows, e.g. "Sum of COGS"; defaults to <see cref="Property"/>.</summary>
    public string NativeQueryRef { get; init; } = string.Empty;

    /// <summary>TMDL aggregation function (0 = Sum, 5 = CountNonNull). Makes the projection a measure.</summary>
    public int? AggregateFunction { get; init; }

    public bool IsMeasure => AggregateFunction.HasValue;

    internal JsonNode ToFieldJson()
    {
        var column = new JsonObject
        {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = Entity } },
            ["Property"] = Property
        };

        if (!IsMeasure)
        {
            return new JsonObject { ["Column"] = column };
        }

        return new JsonObject
        {
            ["Aggregation"] = new JsonObject
            {
                ["Expression"] = new JsonObject { ["Column"] = column },
                ["Function"] = AggregateFunction!.Value
            }
        };
    }
}

/// <summary>Column types for a generated <c>definition/tables</c> TMDL model.</summary>
internal sealed class TestSemanticModel
{
    private readonly List<TestSemanticTable> _tables = new();

    public IReadOnlyList<TestSemanticTable> Tables => _tables;

    public TestSemanticModel AddColumn(string table, string column, string dataType)
    {
        var existing = _tables.FirstOrDefault(t => string.Equals(t.Name, table, StringComparison.OrdinalIgnoreCase));
        if (existing == null)
        {
            existing = new TestSemanticTable(table);
            _tables.Add(existing);
        }

        existing.AddColumn(column, dataType);
        return this;
    }

    public TestSemanticModel WithStringColumn(string table, string column) =>
        AddColumn(table, column, "string");

    public TestSemanticModel WithIntColumn(string table, string column) =>
        AddColumn(table, column, "int64");

    public TestSemanticModel WithDoubleColumn(string table, string column) =>
        AddColumn(table, column, "double");
}

internal sealed class TestSemanticTable
{
    private readonly List<(string Column, string DataType)> _columns = new();

    public TestSemanticTable(string name) => Name = name;

    public string Name { get; }

    public void AddColumn(string column, string dataType) => _columns.Add((column, dataType));

    public string ToTmdl()
    {
        var lines = new List<string>
        {
            $"table {Name}",
            "\tlineageTag: 00000000-0000-0000-0000-000000000000",
            ""
        };

        foreach (var (column, dataType) in _columns)
        {
            lines.Add($"\tcolumn {column}");
            lines.Add($"\t\tdataType: {dataType}");
            lines.Add("");
        }

        return string.Join("\n", lines) + "\n";
    }
}

/// <summary>A discovered visual.json: its path, the page that owns it and its visualType.</summary>
internal sealed record VisualRef(string VisualJsonPath, string PageId, string VisualId, string VisualType);

/// <summary>Factory and discovery helpers shared by the report tests.</summary>
internal static class TestReports
{
    /// <summary>Builds a synthetic report. Dispose the result to delete the temp project.</summary>
    public static TestReport Create(
        IReadOnlyList<TestPage> pages,
        TestSemanticModel? semanticModel = null,
        string reportName = "Synthetic Report",
        string? activePageDisplayName = null) =>
        new(reportName, pages, semanticModel, activePageDisplayName);

    public static string PagesDirectory(string reportPath) =>
        Path.Combine(reportPath, "definition", "pages");

    /// <summary>Every readable visual.json in a report, ordered by page id then visual id.</summary>
    public static IReadOnlyList<VisualRef> Visuals(string reportPath)
    {
        var result = new List<VisualRef>();
        var pagesDir = PagesDirectory(reportPath);
        if (!Directory.Exists(pagesDir)) return result;

        foreach (var pageDir in Directory.EnumerateDirectories(pagesDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            result.AddRange(VisualsOnPage(pageDir, Path.GetFileName(pageDir)));
        }

        return result;
    }

    /// <summary>Every readable visual.json of one visualType, in report order.</summary>
    public static IReadOnlyList<VisualRef> Visuals(string reportPath, string visualType) =>
        Visuals(reportPath)
            .Where(v => string.Equals(v.VisualType, visualType, StringComparison.OrdinalIgnoreCase))
            .ToList();

    /// <summary>The first visual of a visualType in the report.</summary>
    public static VisualRef Visual(string reportPath, string visualType) => Require(Visuals(reportPath, visualType), visualType);

    /// <summary>The first visual of a visualType on one page.</summary>
    public static VisualRef Visual(string reportPath, string visualType, string pageId) =>
        Require(PageVisuals(reportPath, pageId), visualType);

    /// <summary>Every readable visual.json on one page (visual folders with no visual.json are skipped).</summary>
    public static IReadOnlyList<VisualRef> PageVisuals(string reportPath, string pageId) =>
        VisualsOnPage(Path.Combine(PagesDirectory(reportPath), pageId), pageId);

    /// <summary>The first page id that has at least one readable visual.json, or null.</summary>
    public static string? PageWithVisuals(string reportPath) =>
        PageIds(reportPath).FirstOrDefault(id => PageVisuals(reportPath, id).Count > 0);

    /// <summary>The first page id that has no visual.json at all, or null.</summary>
    public static string? PageWithoutVisuals(string reportPath) =>
        PageIds(reportPath).FirstOrDefault(id => PageVisuals(reportPath, id).Count == 0);

    /// <summary>Every page id in a report, in id order.</summary>
    public static IReadOnlyList<string> PageIds(string reportPath)
    {
        var pagesDir = PagesDirectory(reportPath);
        if (!Directory.Exists(pagesDir)) return Array.Empty<string>();
        return Directory.EnumerateDirectories(pagesDir)
            .OrderBy(d => d, StringComparer.Ordinal)
            .Select(Path.GetFileName)
            .Where(id => !string.IsNullOrEmpty(id))
            .Select(id => id!)
            .ToList();
    }

    private static IReadOnlyList<VisualRef> VisualsOnPage(string pageDir, string? pageId)
    {
        var result = new List<VisualRef>();
        var visualsDir = Path.Combine(pageDir, "visuals");
        if (!Directory.Exists(visualsDir)) return result;

        foreach (var visualDir in Directory.EnumerateDirectories(visualsDir).OrderBy(d => d, StringComparer.Ordinal))
        {
            var visualJsonPath = Path.Combine(visualDir, "visual.json");
            if (!File.Exists(visualJsonPath)) continue;

            result.Add(new VisualRef(
                visualJsonPath,
                pageId ?? Path.GetFileName(pageDir),
                Path.GetFileName(visualDir),
                ReadVisualType(visualJsonPath)));
        }

        return result;
    }

    private static string ReadVisualType(string visualJsonPath)
    {
        try
        {
            return JsonNode.Parse(File.ReadAllText(visualJsonPath))?["visual"]?["visualType"]?.ToString()
                   ?? string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    private static VisualRef Require(IReadOnlyList<VisualRef> candidates, string visualType) =>
        candidates.Count > 0
            ? candidates[0]
            : throw new InvalidOperationException($"The report has no '{visualType}' visual.");
}