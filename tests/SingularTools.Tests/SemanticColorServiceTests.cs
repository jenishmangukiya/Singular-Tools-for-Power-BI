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
    private const string ColoredPage = "03d23146c353b39f1666";
    private const string SecondPage = "6ce61a33dfeca0131c91";
    private const string BarVisual = "e22efc042b68c0b157de";
    private const string TableVisual = "9ab413562c85b9b59dcb";

    private string GetDemoReportPath()
    {
        var candidates = new[]
        {
            string.Empty,
            Path.Combine("Assets", "Test_PBI_Report")
        };

        var current = Directory.GetCurrentDirectory();
        while (current != null)
        {
            foreach (var relative in candidates)
            {
                var root = Path.Combine(current, relative);
                if (File.Exists(Path.Combine(root, "Demo PBI Report.pbip")))
                {
                    return Path.Combine(root, "Demo PBI Report.Report");
                }
            }

            current = Directory.GetParent(current)?.FullName;
        }

        throw new DirectoryNotFoundException("Could not locate Demo PBI Report.pbip");
    }

    private static string VisualPath(string reportRoot, string visual)
        => VisualPath(reportRoot, ColoredPage, visual);

    private static string VisualPath(string reportRoot, string page, string visual)
        => Path.Combine(reportRoot, "definition", "pages", page, "visuals", visual, "visual.json");

    [Fact]
    public void ApplyRules_RecolorsExistingValues_AndIsIdempotent()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorRecolor_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();
            var rules = new List<SemanticColorRule> { new() { Value = "Velo", Hex = "#00AA00" } };

            var result = service.ApplyRules(manager, rules);

            Assert.True(result.SelectorsChanged >= 7, $"expected the existing Velo selectors to recolor, got {result.SelectorsChanged}");
            Assert.True(result.FilesWritten >= 7, $"expected most visuals to change, got {result.FilesWritten}");

            var barPath = VisualPath(tempDir, BarVisual);
            Assert.Contains("#00AA00", File.ReadAllText(barPath));
            Assert.Contains("#00AA00", BarChartFillFor(barPath, "Product", "'Velo'"));

            var second = service.ApplyRules(manager, rules);
            Assert.Equal(0, second.FilesWritten);
            Assert.Equal(0, second.SelectorsChanged);
            Assert.Equal(0, second.SelectorsCreated);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyRules_MatchesValuesCaseInsensitively()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorCase_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var result = service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "velo", Hex = "#ABCDEF" }
            });

            Assert.True(result.SelectorsChanged >= 7);
            Assert.Contains("#ABCDEF", File.ReadAllText(VisualPath(tempDir, BarVisual)));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyRules_CreatesSelectorOnTypeCompatibleFieldsOnly()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorCreate_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var result = service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "Channel Partner", Hex = "#123456" }
            });

            Assert.True(result.SelectorsCreated >= 1);
            Assert.True(result.FilesWritten >= 1);

            var barPath = VisualPath(tempDir, BarVisual);
            var dataPoints = ReadDataPoints(barPath);

            // Created on the string Category field (Product)...
            Assert.NotNull(FindMemberSelector(dataPoints, "Product", "'Channel Partner'"));

            // ...but never bound to the numeric Year field.
            Assert.Null(FindMemberSelector(dataPoints, "Year", "'Channel Partner'"));

            var second = service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "Channel Partner", Hex = "#123456" }
            });
            Assert.Equal(0, second.FilesWritten);
            Assert.Equal(0, second.SelectorsCreated);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyRules_NumericValue_UsesNumericSuffix()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorNumeric_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var result = service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "2015", Hex = "#654321" }
            });

            Assert.True(result.SelectorsCreated >= 1);

            var dataPoints = ReadDataPoints(VisualPath(tempDir, BarVisual));
            var numeric = FindMemberSelector(dataPoints, "Year", "2015L");
            Assert.NotNull(numeric);
            Assert.Contains("#654321", numeric!.ToJsonString());
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyRules_MatchesSeriesName_WithMetadataSelector()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorSeries_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var result = service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "Count of Date", Hex = "#0F0F0F" }
            });

            Assert.True(result.SelectorsCreated >= 1);

            var dataPoints = ReadDataPoints(VisualPath(tempDir, BarVisual));
            Assert.NotNull(FindMetadataSelector(dataPoints, "CountNonNull(financials.Date)"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyRules_IgnoresTableVisuals()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorTable_");

        try
        {
            var tablePath = VisualPath(tempDir, TableVisual);
            InjectMemberSelector(tablePath, "financials", "Product", "'Velo'", "#010203");

            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var before = File.ReadAllText(tablePath);
            service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "Velo", Hex = "#00AA00" }
            });

            Assert.Equal(before, File.ReadAllText(tablePath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAppliedColor_ReflectsReportState()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorRead_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            Assert.Null(service.GetAppliedColor(manager, new SemanticColorRule { Value = "DefinitelyNotPresent" }));

            service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "Velo", Hex = "#00AA00" }
            });

            Assert.Equal("#00AA00", service.GetAppliedColor(manager, new SemanticColorRule { Value = "Velo" }));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyRules_PageScoped_OnlyTouchesSelectedPages()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorPageScope_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var selectedBar = VisualPath(tempDir, ColoredPage, BarVisual);
            var otherBar = VisualPath(tempDir, SecondPage, BarVisual);
            var otherBefore = File.ReadAllText(otherBar);

            var result = service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new()
                {
                    Value = "Velo",
                    Hex = "#00AA00",
                    Scope = SemanticColorScope.Pages,
                    PageIds = new List<string> { ColoredPage }
                }
            });

            Assert.True(result.SelectorsChanged >= 7);
            Assert.Contains("#00AA00", File.ReadAllText(selectedBar));
            Assert.Equal(otherBefore, File.ReadAllText(otherBar));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void ApplyRules_PageLevelOverridesReportLevel()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorPrecedence_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new() { Value = "Velo", Hex = "#111111", Scope = SemanticColorScope.Report },
                new()
                {
                    Value = "Velo",
                    Hex = "#222222",
                    Scope = SemanticColorScope.Pages,
                    PageIds = new List<string> { ColoredPage }
                }
            });

            // Page rule wins on its page...
            Assert.Equal("'#222222'", BarChartFillFor(VisualPath(tempDir, ColoredPage, BarVisual), "Product", "'Velo'"));
            // ...and the report rule still applies everywhere else.
            Assert.Equal("'#111111'", BarChartFillFor(VisualPath(tempDir, SecondPage, BarVisual), "Product", "'Velo'"));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void GetAppliedColor_RespectsPageScope()
    {
        var tempDir = CopyDemoToTemp(GetDemoReportPath(), "PBIR_SemColorReadScope_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            service.ApplyRules(manager, new List<SemanticColorRule>
            {
                new()
                {
                    Value = "Velo",
                    Hex = "#00AA00",
                    Scope = SemanticColorScope.Pages,
                    PageIds = new List<string> { ColoredPage }
                }
            });

            var scoped = new SemanticColorRule
            {
                Value = "Velo",
                Scope = SemanticColorScope.Pages,
                PageIds = new List<string> { ColoredPage }
            };
            var otherPage = new SemanticColorRule
            {
                Value = "Velo",
                Scope = SemanticColorScope.Pages,
                PageIds = new List<string> { SecondPage }
            };

            Assert.Equal("#00AA00", service.GetAppliedColor(manager, scoped));
            Assert.False(string.Equals(
                service.GetAppliedColor(manager, otherPage), "#00AA00", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
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

    private static string CopyDemoToTemp(string reportPath, string prefix)
    {
        var projectRoot = Directory.GetParent(reportPath)!.FullName;
        var reportName = Path.GetFileName(reportPath);
        var tempRoot = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));

        CopyDirectory(reportPath, Path.Combine(tempRoot, reportName));

        // The service reads column types from the sibling semantic model, so copy it too.
        if (reportName.EndsWith(".Report", StringComparison.OrdinalIgnoreCase))
        {
            var modelName = reportName.Substring(0, reportName.Length - ".Report".Length) + ".SemanticModel";
            var modelSource = Path.Combine(projectRoot, modelName);
            if (Directory.Exists(modelSource))
            {
                CopyDirectory(modelSource, Path.Combine(tempRoot, modelName));
            }
        }

        return Path.Combine(tempRoot, reportName);
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destinationDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
