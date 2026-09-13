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
    public void Scan_DemoReport_FindsColoredLegendMember()
    {
        var manager = new ReportManager();
        manager.LoadReport(GetDemoReportPath());

        var scan = new SemanticColorService().Scan(manager);

        // The demo report has two visuals with a Year legend selector for 2013.
        var year = scan.Values.Single(v => v.DisplayValue == "2013");
        Assert.Equal(SemanticColorTargetKind.MemberValue, year.Kind);
        Assert.Equal(2, year.SelectorCount);
        Assert.Equal(2, year.VisualCount);
        Assert.Contains("Year", year.Fields);
        Assert.Equal(SemanticColorService.NormalizeLiteralKey("2013L"), year.Key);

        // A member field candidate is available for adding sibling values.
        var yearField = scan.Fields.Single(f =>
            f.Kind == SemanticColorTargetKind.MemberValue && f.FieldName == "Year");
        Assert.True(yearField.IsNumeric);
        Assert.Equal("L", yearField.NumericSuffix);
        Assert.False(string.IsNullOrEmpty(yearField.TemplateJson));
    }

    [Fact]
    public void Apply_UpdatesExistingSelectors_WithQuotedLiteral_AndIsIdempotent()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = CopyDemoToTemp(reportPath, "PBIR_SemColor_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);

            var service = new SemanticColorService();
            var key = SemanticColorService.NormalizeLiteralKey("2013L");

            var result = service.Apply(manager, new Dictionary<string, string> { [key] = "#00AA00" });

            Assert.Equal(2, result.SelectorsChanged);
            Assert.Equal(2, result.VisualsChanged);
            Assert.Equal(2, result.FilesWritten);

            // PBIR literal colors require inner single quotes.
            var changedPath = Path.Combine(
                tempDir, "definition", "pages", "03d23146c353b39f1666", "visuals", "dabe8f686831522a02e0", "visual.json");
            Assert.Contains("\"'#00AA00'\"", File.ReadAllText(changedPath));

            var scan = service.Scan(manager);
            var year = scan.Values.Single(v => v.DisplayValue == "2013");
            Assert.Equal("#00AA00", year.CommonColor);
            Assert.Equal(2, year.SelectorCount);
            Assert.False(year.HasConflict);

            // Applying the same mapping again must be a no-op.
            var second = service.Apply(manager, new Dictionary<string, string> { [key] = "#00AA00" });
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
                tempDir, "definition", "pages", "03d23146c353b39f1666", "visuals", "e22efc042b68c0b157de", "visual.json");
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
    public void Apply_ManualMemberValue_CreatesSelector_AndIsIdempotent()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = CopyDemoToTemp(reportPath, "PBIR_SemColorManual_");

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var field = service.Scan(manager).Fields.Single(f =>
                f.Kind == SemanticColorTargetKind.MemberValue && f.FieldName == "Year");

            var manual = new SemanticColorManualValue
            {
                Kind = SemanticColorTargetKind.MemberValue,
                FieldName = field.FieldName,
                Entity = field.Entity,
                TemplateJson = field.TemplateJson,
                LiteralValue = "2014L"
            };

            var result = service.Apply(manager, new List<SemanticColorAssignment>
            {
                new() { Hex = "#123456", Manual = manual }
            });

            Assert.True(result.SelectorsCreated >= 1);
            Assert.True(result.FilesWritten >= 1);

            var scan = service.Scan(manager);
            var created = scan.Values.Single(v => v.Kind == SemanticColorTargetKind.MemberValue && v.DisplayValue == "2014");
            Assert.Contains("#123456", created.CurrentColors);
            Assert.Equal("2014L", created.RawValue);

            // Re-applying creates nothing new.
            var second = service.Apply(manager, new List<SemanticColorAssignment>
            {
                new() { Hex = "#123456", Manual = manual }
            });
            Assert.Equal(0, second.SelectorsCreated);
            Assert.Equal(0, second.FilesWritten);
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
            // Inject a metadata (series identity) color selector into the clustered bar chart.
            var visualPath = Path.Combine(
                tempDir, "definition", "pages", "03d23146c353b39f1666", "visuals", "e22efc042b68c0b157de", "visual.json");
            InjectMetadataSelector(visualPath, "CountNonNull(financials.Date)", "#FF0000");

            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            var service = new SemanticColorService();

            var scan = service.Scan(manager);
            var series = scan.Values.Single(v => v.Kind == SemanticColorTargetKind.SeriesIdentity);
            Assert.Equal("CountNonNull(financials.Date)", series.Key);

            var result = service.Apply(manager, new Dictionary<string, string>
            {
                [series.Key] = "#ABCDEF"
            });

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
        var visual = root["visual"]!.AsObject();

        var objects = visual["objects"] as JsonObject ?? new JsonObject();
        visual["objects"] = objects;

        var dataPoints = objects["dataPoint"] as JsonArray ?? new JsonArray();
        objects["dataPoint"] = dataPoints;

        dataPoints.Add(new JsonObject
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
            ["selector"] = new JsonObject { ["metadata"] = queryRef }
        });

        File.WriteAllText(visualPath, root.ToJsonString());
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
