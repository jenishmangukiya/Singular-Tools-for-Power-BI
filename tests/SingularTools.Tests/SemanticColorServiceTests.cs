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

    private string GetDemoReportPath()
    {
        var current = Directory.GetCurrentDirectory();
        while (current != null && !File.Exists(Path.Combine(current, "Demo PBI Report.pbip")))
        {
            current = Directory.GetParent(current)?.FullName;
        }

        if (current == null)
            throw new DirectoryNotFoundException("Could not locate Demo PBI Report.pbip");

        return Path.Combine(current, "Demo PBI Report.Report");
    }

    private static string CopyDemoToTemp(string reportPath, string prefix)
    {
        var tempDir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        CopyDirectory(reportPath, tempDir);
        return tempDir;
    }

    [Fact]
    public void Scan_DedupesValuesGloballyAcrossVisuals()
    {
        var manager = new ReportManager();
        manager.LoadReport(GetDemoReportPath());

        var scan = new SemanticColorService().Scan(manager);

        // No value appears twice: each normalized key is a single global item.
        var duplicateKeys = scan.Values
            .GroupBy(v => v.Key, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();
        Assert.Empty(duplicateKeys);

        // 'Velo' is colored in several visuals but listed once, spanning them.
        var velo = scan.Values.Single(v => v.DisplayValue == "Velo");
        Assert.Equal(SemanticColorTargetKind.MemberValue, velo.Kind);
        Assert.True(velo.VisualCount >= 2, $"expected Velo across multiple visuals, got {velo.VisualCount}");
        Assert.Equal(velo.SelectorCount, velo.VisualCount);
    }

    [Fact]
    public void Scan_IgnoresTableVisuals()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = CopyDemoToTemp(reportPath, "PBIR_SemColorTable_");

        try
        {
            // The demo 'Discount Band' visual is a table; color a value on it.
            var tablePath = Path.Combine(
                tempDir, "definition", "pages", ColoredPage, "visuals", "9ab413562c85b9b59dcb", "visual.json");
            InjectMemberSelector(tablePath, "financials", "Discount Band", "'ShouldNotAppear'", "#010203");

            var manager = new ReportManager();
            manager.LoadReport(tempDir);

            var scan = new SemanticColorService().Scan(manager);

            Assert.DoesNotContain(scan.Values, v => v.DisplayValue == "ShouldNotAppear");
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Apply_RecolorsEveryMatchingSelector_WithQuotedLiteral_AndIsIdempotent()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = CopyDemoToTemp(reportPath, "PBIR_SemColor_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var velo = service.Scan(manager).Values.Single(v => v.DisplayValue == "Velo");

            var result = service.Apply(manager, new Dictionary<string, string> { [velo.Key] = "#00AA00" });

            Assert.True(result.FilesWritten >= 2);
            Assert.Equal(result.SelectorsChanged, result.VisualsChanged);
            Assert.Equal(result.FilesWritten, result.VisualsChanged);

            // PBIR literal colors require inner single quotes.
            var changedPath = Path.Combine(
                tempDir, "definition", "pages", ColoredPage, "visuals", "e22efc042b68c0b157de", "visual.json");
            Assert.Contains("\"'#00AA00'\"", File.ReadAllText(changedPath));

            var rescanned = service.Scan(manager).Values.Single(v => v.DisplayValue == "Velo");
            Assert.Equal("#00AA00", rescanned.CommonColor);
            Assert.False(rescanned.HasConflict);

            // Applying the same mapping again must be a no-op.
            var second = service.Apply(manager, new Dictionary<string, string> { [velo.Key] = "#00AA00" });
            Assert.Equal(0, second.FilesWritten);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Apply_DoesNotTouchVisualsWithoutMatchingSelector()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = CopyDemoToTemp(reportPath, "PBIR_SemColorNoop_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);

            var untouchedPath = Path.Combine(
                tempDir, "definition", "pages", ColoredPage, "visuals", "9ab413562c85b9b59dcb", "visual.json");
            var before = File.ReadAllText(untouchedPath);

            var service = new SemanticColorService();
            var key = SemanticColorService.NormalizeLiteralKey("'NoSuchValue'");
            var result = service.Apply(manager, new Dictionary<string, string> { [key] = "#123456" });

            Assert.Equal(0, result.FilesWritten);
            Assert.Equal(before, File.ReadAllText(untouchedPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void Scan_And_Apply_HandlesSeriesMetadataSelector()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = CopyDemoToTemp(reportPath, "PBIR_SemColorSeries_");

        try
        {
            var visualPath = Path.Combine(
                tempDir, "definition", "pages", ColoredPage, "visuals", "e22efc042b68c0b157de", "visual.json");
            InjectMetadataSelector(visualPath, "CountNonNull(financials.Date)", "#FF0000");

            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var series = service.Scan(manager).Values.Single(v => v.Kind == SemanticColorTargetKind.SeriesIdentity);
            Assert.Equal("CountNonNull(financials.Date)", series.Key);

            var result = service.Apply(manager, new Dictionary<string, string> { [series.Key] = "#ABCDEF" });

            Assert.Equal(1, result.SelectorsChanged);
            Assert.Contains("\"'#ABCDEF'\"", File.ReadAllText(visualPath));
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Theory]
    [InlineData("'Yes'", "Yes", "s:yes")]
    [InlineData("'no'", "no", "s:no")]
    [InlineData("2013L", "2013", "v:2013")]
    [InlineData("12.5D", "12.5", "v:12.5")]
    public void NormalizeLiteral_MapsTokensToDisplayAndKey(string raw, string display, string key)
    {
        Assert.Equal(display, SemanticColorService.NormalizeLiteralDisplay(raw));
        Assert.Equal(key, SemanticColorService.NormalizeLiteralKey(raw));
    }

    [Fact]
    public void QuoteColor_WrapsHexInSingleQuotes()
    {
        Assert.Equal("'#118DFF'", SemanticColorService.QuoteColor("#118dff"));
        Assert.Equal("'#118DFF'", SemanticColorService.QuoteColor("118DFF"));
    }

    private static void InjectMetadataSelector(string visualPath, string queryRef, string hex)
    {
        var root = JsonNode.Parse(File.ReadAllText(visualPath))!;
        var objects = EnsureObjects(root);

        var dataPoints = objects["dataPoint"] as JsonArray ?? new JsonArray();
        objects["dataPoint"] = dataPoints;

        dataPoints.Add(BuildEntry(
            new JsonObject { ["metadata"] = queryRef },
            hex));

        File.WriteAllText(visualPath, root.ToJsonString());
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
