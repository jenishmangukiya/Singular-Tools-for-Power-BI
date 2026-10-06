using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SingularTools.Core;
using SingularTools.Core.Models;
using Xunit;

namespace SingularTools.Tests;

public class SemanticColorServiceTests
{
    // ------------------------------------------------------------- Fixtures

    /// <summary>
    /// A bar chart shaped like Power BI's own: a string category, a numeric
    /// series and a count measure. The numeric suffix and "type compatible only"
    /// behaviour both depend on the semantic model declaring Year as int64.
    /// </summary>
    private static TestProjection[] FinancialsProjections() => new TestProjection[]
    {
        new TestProjection("Category", "financials", "Product"),
        new TestProjection("Series", "financials", "Year"),
        new TestProjection("Y", "financials", "Date")
        {
            AggregateFunction = 5,
            QueryRef = "CountNonNull(financials.Date)",
            NativeQueryRef = "Count of Date"
        }
    };

    private static TestSemanticModel FinancialsModel() => new TestSemanticModel()
        .WithStringColumn("financials", "Product")
        .WithIntColumn("financials", "Year");

    /// <summary>
    /// Two pages carrying the same chart shape (so page-scoped rules can be
    /// compared against each other) plus a table visual that must be skipped.
    /// </summary>
    private static TestReport BarChartReport() => TestReports.Create(
        new[]
        {
            new TestPage(
                "Overview",
                new TestVisual("barChart", FinancialsProjections()),
                new TestVisual("tableEx", FinancialsProjections())),
            new TestPage(
                "Detail",
                new TestVisual("barChart", FinancialsProjections()))
        },
        FinancialsModel());

    /// <summary>
    /// The demo report used to ship with these selectors already in place; they
    /// are injected here instead so the counts below do not depend on whatever
    /// Power BI last saved into the checked-in fixture.
    /// </summary>
    private static void InjectVeloSelector(string visualPath) =>
        InjectMemberSelector(visualPath, "financials", "Product", "'Velo'", "#010203");

    // ------------------------------------------------------------- Discovery

    [Fact]
    public void ApplyRules_RecolorsExistingValues_AndIsIdempotent()
    {
        using var report = BarChartReport();
        var charts = TestReports.Visuals(report.ReportPath, "barChart");
        Assert.NotEmpty(charts);

        foreach (var chart in charts)
        {
            InjectVeloSelector(chart.VisualJsonPath);
        }

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();
        var rules = new List<SemanticColorRule> { new() { Value = "Velo", Hex = "#00AA00" } };

        var result = service.ApplyRules(manager, rules);

        // Every injected selector is recoloured, and nothing new is created.
        Assert.Equal(charts.Count, result.SelectorsChanged);
        Assert.Equal(charts.Count, result.FilesWritten);
        Assert.Equal(0, result.SelectorsCreated);

        foreach (var chart in charts)
        {
            Assert.Contains("#00AA00", File.ReadAllText(chart.VisualJsonPath));
            Assert.Contains("#00AA00", BarChartFillFor(chart.VisualJsonPath, "Product", "'Velo'"));
        }

        var second = service.ApplyRules(manager, rules);
        Assert.Equal(0, second.FilesWritten);
        Assert.Equal(0, second.SelectorsChanged);
        Assert.Equal(0, second.SelectorsCreated);
    }

    [Fact]
    public void ApplyRules_MatchesValuesCaseInsensitively()
    {
        using var report = BarChartReport();
        var chart = TestReports.Visual(report.ReportPath, "barChart");
        InjectVeloSelector(chart.VisualJsonPath);

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        var result = service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new() { Value = "velo", Hex = "#ABCDEF" }
        });

        Assert.Equal(1, result.SelectorsChanged);
        Assert.Contains("#ABCDEF", File.ReadAllText(chart.VisualJsonPath));
    }

    [Fact]
    public void ApplyRules_CreatesSelectorOnTypeCompatibleFieldsOnly()
    {
        using var report = BarChartReport();
        var chart = TestReports.Visual(report.ReportPath, "barChart");

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        var rule = new List<SemanticColorRule>
        {
            new() { Value = "Channel Partner", Hex = "#123456" }
        };

        var result = service.ApplyRules(manager, rule);

        Assert.True(result.SelectorsCreated >= 1);
        Assert.True(result.FilesWritten >= 1);

        var dataPoints = ReadDataPoints(chart.VisualJsonPath);

        // Created on the string Category field (Product)...
        Assert.NotNull(FindMemberSelector(dataPoints, "Product", "'Channel Partner'"));

        // ...but never bound to the numeric Year field.
        Assert.Null(FindMemberSelector(dataPoints, "Year", "'Channel Partner'"));

        var second = service.ApplyRules(manager, rule);
        Assert.Equal(0, second.FilesWritten);
        Assert.Equal(0, second.SelectorsCreated);
    }

    [Fact]
    public void ApplyRules_NumericValue_UsesNumericSuffix()
    {
        using var report = BarChartReport();
        var chart = TestReports.Visual(report.ReportPath, "barChart");

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        var result = service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new() { Value = "2015", Hex = "#654321" }
        });

        Assert.True(result.SelectorsCreated >= 1);

        var dataPoints = ReadDataPoints(chart.VisualJsonPath);
        var numeric = FindMemberSelector(dataPoints, "Year", "2015L");
        Assert.NotNull(numeric);
        Assert.Contains("#654321", numeric!.ToJsonString());
    }

    [Fact]
    public void ApplyRules_MatchesSeriesName_WithMetadataSelector()
    {
        using var report = BarChartReport();
        var chart = TestReports.Visual(report.ReportPath, "barChart");

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        var result = service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new() { Value = "Count of Date", Hex = "#0F0F0F" }
        });

        Assert.True(result.SelectorsCreated >= 1);

        var dataPoints = ReadDataPoints(chart.VisualJsonPath);
        Assert.NotNull(FindMetadataSelector(dataPoints, "CountNonNull(financials.Date)"));
    }

    [Fact]
    public void ApplyRules_IgnoresTableVisuals()
    {
        // One report carrying both a table and a chart, so "the table is skipped" and "the
        // rule was live" can be asserted against the same run.
        using var report = BarChartReport();

        var table = TestReports.Visual(report.ReportPath, "tableEx");
        var chart = TestReports.Visual(report.ReportPath, "barChart");

        InjectVeloSelector(table.VisualJsonPath);

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        var before = File.ReadAllText(table.VisualJsonPath);
        var chartBefore = File.ReadAllText(chart.VisualJsonPath);
        service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new() { Value = "Velo", Hex = "#00AA00" }
        });

        // A table has no legend, series or slice to colour, so it is left byte-for-byte alone.
        Assert.Equal(before, File.ReadAllText(table.VisualJsonPath));

        // The rule was live: a chart in the same report was still coloured.
        Assert.NotEqual(chartBefore, File.ReadAllText(chart.VisualJsonPath));
        Assert.Contains("#00AA00", BarChartFillFor(chart.VisualJsonPath, "Product", "'Velo'"));
    }

    [Fact]
    public void GetAppliedColor_ReflectsReportState()
    {
        using var report = BarChartReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        Assert.Null(service.GetAppliedColor(manager, new SemanticColorRule { Value = "DefinitelyNotPresent" }));

        service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new() { Value = "Velo", Hex = "#00AA00" }
        });

        Assert.Equal("#00AA00", service.GetAppliedColor(manager, new SemanticColorRule { Value = "Velo" }));
    }

    [Fact]
    public void ApplyRules_PageScoped_OnlyTouchesSelectedPages()
    {
        using var report = BarChartReport();
        var scopedPageId = report.PageIdFor("Overview");
        var otherPageId = report.PageIdFor("Detail");

        var selectedBar = TestReports.Visual(report.ReportPath, "barChart", scopedPageId);
        var otherBar = TestReports.Visual(report.ReportPath, "barChart", otherPageId);
        InjectVeloSelector(selectedBar.VisualJsonPath);
        InjectVeloSelector(otherBar.VisualJsonPath);

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        var otherBefore = File.ReadAllText(otherBar.VisualJsonPath);

        var result = service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new()
            {
                Value = "Velo",
                Hex = "#00AA00",
                Scope = SemanticColorScope.Pages,
                PageIds = new List<string> { scopedPageId }
            }
        });

        // Only the scoped page's chart is rewritten.
        Assert.Equal(1, result.SelectorsChanged);
        Assert.Equal(1, result.FilesWritten);
        Assert.Contains("#00AA00", File.ReadAllText(selectedBar.VisualJsonPath));
        Assert.Equal(otherBefore, File.ReadAllText(otherBar.VisualJsonPath));
    }

    [Fact]
    public void ApplyRules_PageLevelOverridesReportLevel()
    {
        using var report = BarChartReport();
        var scopedPageId = report.PageIdFor("Overview");
        var otherPageId = report.PageIdFor("Detail");

        var scopedBar = TestReports.Visual(report.ReportPath, "barChart", scopedPageId);
        var otherBar = TestReports.Visual(report.ReportPath, "barChart", otherPageId);
        InjectVeloSelector(scopedBar.VisualJsonPath);
        InjectVeloSelector(otherBar.VisualJsonPath);

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new() { Value = "Velo", Hex = "#111111", Scope = SemanticColorScope.Report },
            new()
            {
                Value = "Velo",
                Hex = "#222222",
                Scope = SemanticColorScope.Pages,
                PageIds = new List<string> { scopedPageId }
            }
        });

        // Page rule wins on its page...
        Assert.Equal("'#222222'", BarChartFillFor(scopedBar.VisualJsonPath, "Product", "'Velo'"));
        // ...and the report rule still applies everywhere else.
        Assert.Equal("'#111111'", BarChartFillFor(otherBar.VisualJsonPath, "Product", "'Velo'"));
    }

    [Fact]
    public void GetAppliedColor_RespectsPageScope()
    {
        using var report = BarChartReport();
        var scopedPageId = report.PageIdFor("Overview");
        var otherPageId = report.PageIdFor("Detail");

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var service = new SemanticColorService();

        service.ApplyRules(manager, new List<SemanticColorRule>
        {
            new()
            {
                Value = "Velo",
                Hex = "#00AA00",
                Scope = SemanticColorScope.Pages,
                PageIds = new List<string> { scopedPageId }
            }
        });

        var scoped = new SemanticColorRule
        {
            Value = "Velo",
            Scope = SemanticColorScope.Pages,
            PageIds = new List<string> { scopedPageId }
        };
        var otherPage = new SemanticColorRule
        {
            Value = "Velo",
            Scope = SemanticColorScope.Pages,
            PageIds = new List<string> { otherPageId }
        };

        Assert.Equal("#00AA00", service.GetAppliedColor(manager, scoped));
        Assert.False(string.Equals(
            service.GetAppliedColor(manager, otherPage), "#00AA00", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("'Yes'", "Yes")]
    [InlineData("'no'", "no")]
    [InlineData("2013L", "2013")]
    [InlineData("12.5D", "12.5")]
    public void NormalizeLiteralDisplay_MapsTokensToDisplay(string raw, string display)
    {
        Assert.Equal(display, SemanticColorService.NormalizeLiteralDisplay(raw));
    }

    [Theory]
    [InlineData(" Yes ", "Yes")]
    [InlineData("'Yes'", "Yes")]
    [InlineData("2013L", "2013")]
    public void NormalizeRuleValue_TrimsQuotesAndSuffixes(string raw, string expected)
    {
        Assert.Equal(expected, SemanticColorService.NormalizeRuleValue(raw));
    }

    [Fact]
    public void NormalizeHex_And_QuoteColor()
    {
        Assert.Equal("#118DFF", SemanticColorService.NormalizeHex("118dff"));
        Assert.Equal("#118DFF", SemanticColorService.NormalizeHex("'#118dff'"));
        Assert.Equal("'#118DFF'", SemanticColorService.QuoteColor("#118dff"));
        Assert.Equal("'#118DFF'", SemanticColorService.QuoteColor("118DFF"));
    }

    // ------------------------------------------------------------- Helpers

    private static JsonArray ReadDataPoints(string visualPath)
    {
        var root = JsonNode.Parse(File.ReadAllText(visualPath))!;
        return root["visual"]?["objects"]?["dataPoint"]?.AsArray() ?? new JsonArray();
    }

    private static JsonNode? FindMemberSelector(JsonArray dataPoints, string property, string literal)
    {
        foreach (var dataPoint in dataPoints)
        {
            var comparison = dataPoint?["selector"]?["data"]?[0]?["scopeId"]?["Comparison"];
            if (comparison == null) continue;

            var left = comparison["Left"]?["Column"]?["Property"]?.ToString();
            var right = comparison["Right"]?["Literal"]?["Value"]?.ToString();

            if (string.Equals(left, property, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(right, literal, StringComparison.Ordinal))
            {
                return dataPoint;
            }
        }

        return null;
    }

    private static JsonNode? FindMetadataSelector(JsonArray dataPoints, string queryRef)
    {
        return dataPoints.FirstOrDefault(dp =>
            string.Equals(dp?["selector"]?["metadata"]?.ToString(), queryRef, StringComparison.Ordinal));
    }

    private static string BarChartFillFor(string visualPath, string property, string literal)
    {
        var node = FindMemberSelector(ReadDataPoints(visualPath), property, literal);
        return node?["properties"]?["fill"]?["solid"]?["color"]?["expr"]?["Literal"]?["Value"]?.ToString() ?? string.Empty;
    }

    private static void InjectMemberSelector(
        string visualPath, string entity, string property, string literal, string hex)
    {
        var root = JsonNode.Parse(File.ReadAllText(visualPath))!;
        var objects = EnsureObjects(root);

        var dataPoints = objects["dataPoint"] as JsonArray ?? new JsonArray();
        objects["dataPoint"] = dataPoints;

        var selector = new JsonObject
        {
            ["data"] = new JsonArray
            {
                new JsonObject
                {
                    ["scopeId"] = new JsonObject
                    {
                        ["Comparison"] = new JsonObject
                        {
                            ["ComparisonKind"] = 0,
                            ["Left"] = new JsonObject
                            {
                                ["Column"] = new JsonObject
                                {
                                    ["Expression"] = new JsonObject
                                    {
                                        ["SourceRef"] = new JsonObject { ["Entity"] = entity }
                                    },
                                    ["Property"] = property
                                }
                            },
                            ["Right"] = new JsonObject
                            {
                                ["Literal"] = new JsonObject { ["Value"] = literal }
                            }
                        }
                    }
                }
            }
        };

        dataPoints.Add(BuildEntry(selector, hex));
        File.WriteAllText(visualPath, root.ToJsonString());
    }

    private static JsonObject EnsureObjects(JsonNode root)
    {
        var visual = root["visual"]!.AsObject();
        var objects = visual["objects"] as JsonObject ?? new JsonObject();
        visual["objects"] = objects;
        return objects;
    }

    private static JsonObject BuildEntry(JsonObject selector, string hex)
    {
        return new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["fill"] = new JsonObject
                {
                    ["solid"] = new JsonObject
                    {
                        ["color"] = new JsonObject
                        {
                            ["expr"] = new JsonObject
                            {
                                ["Literal"] = new JsonObject { ["Value"] = SemanticColorService.QuoteColor(hex) }
                            }
                        }
                    }
                }
            },
            ["selector"] = selector
        };
    }
}