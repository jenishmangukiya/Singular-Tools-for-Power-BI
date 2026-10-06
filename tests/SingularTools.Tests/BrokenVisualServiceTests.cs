using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SingularTools.Core;
using SingularTools.Core.Models;
using Xunit;

namespace SingularTools.Tests;

public class BrokenVisualServiceTests
{
    private const string Table = "financials";

    private static ReportManager LoadManager(TestReport report)
    {
        var manager = new ReportManager();
        Assert.True(manager.LoadReport(report.ReportPath));
        return manager;
    }

    private static SemanticModelIndex Index(TestReport report) =>
        SemanticModelIndex.Load(report.SemanticModelPath);

    /// <summary>
    /// A report whose visuals bind columns that the model does provide, so it scans clean
    /// until something is renamed out from under it.
    /// </summary>
    private static TestReport HealthyReport(string reportName = "Healthy Report") =>
        TestReports.Create(
            new[]
            {
                new TestPage("Overview",
                    new TestVisual("clusteredBarChart",
                        new TestProjection("Category", Table, "Segment") { NativeQueryRef = "Segment" },
                        new TestProjection("Y", Table, "GrossSales") { NativeQueryRef = "Sum of GrossSales" })
                    {
                        Title = "Sales by segment"
                    },
                    new TestVisual("tableEx",
                        new TestProjection("Values", Table, "Product")))
            },
            new TestSemanticModel()
                .WithStringColumn(Table, "Segment")
                .WithDoubleColumn(Table, "GrossSales")
                .WithStringColumn(Table, "Product")
                // A decoy whose name normalizes to the same value as "Segment", so a
                // normalized match that ignores the entity would pick the wrong column.
                .WithStringColumn(Table, "Segment-Code"),
            reportName: reportName);

    // ------------------------------------------------------- TMDL mutation

    /// <summary>
    /// Renames a column declaration in the generated model. Line-ending agnostic because the
    /// fixture writer's convention is not something these tests should depend on.
    /// </summary>
    private static void RenameColumn(TestReport report, string table, string oldName, string newName)
    {
        var path = TablePath(report, table);
        var text = File.ReadAllText(path);

        var updated = Regex.Replace(
            text,
            $@"^([ \t]*)column[ \t]+{Regex.Escape(oldName)}[ \t\r]*$",
            $"$1column {newName}",
            RegexOptions.Multiline);

        Assert.NotEqual(text, updated);
        File.WriteAllText(path, updated);
    }

    private static void RenameTable(TestReport report, string oldName, string newName)
    {
        var path = TablePath(report, Table);
        var text = File.ReadAllText(path);

        var updated = Regex.Replace(
            text,
            $@"^table[ \t]+{Regex.Escape(oldName)}[ \t\r]*$",
            $"table {newName}",
            RegexOptions.Multiline);

        Assert.NotEqual(text, updated);
        File.WriteAllText(path, updated);
    }

    private static string TablePath(TestReport report, string table) =>
        Path.Combine(report.SemanticModelPath, "definition", "tables", table + ".tmdl");

    /// <summary>
    /// Injects a conditional-formatting member selector, the shape a color rule uses. Column
    /// references live here as well as in the data roles, and those are the ones a
    /// queryState-only scan silently misses.
    /// </summary>
    private static void InjectMemberSelector(string visualJsonPath, string entity, string property, string literal)
    {
        var root = JsonNode.Parse(File.ReadAllText(visualJsonPath))!.AsObject();
        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var column = new JsonObject
        {
            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = entity } },
            ["Property"] = property
        };

        var entry = new JsonObject
        {
            ["properties"] = new JsonObject
            {
                ["fill"] = new JsonObject
                {
                    ["solid"] = new JsonObject
                    {
                        ["color"] = new JsonObject
                        {
                            ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "'#118DFF'" } }
                        }
                    }
                }
            },
            ["selector"] = new JsonObject
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
                                ["Left"] = new JsonObject { ["Column"] = column },
                                ["Right"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "'" + literal + "'" } }
                            }
                        }
                    }
                }
            }
        };

        if (root["visual"]?["objects"] is not JsonObject objects)
        {
            objects = new JsonObject();
            root["visual"]!["objects"] = objects;
        }

        if (objects["dataPoint"] is not JsonArray dataPoints)
        {
            dataPoints = new JsonArray();
            objects["dataPoint"] = dataPoints;
        }

        dataPoints.Add(entry);
        File.WriteAllText(visualJsonPath, root.ToJsonString(options));
    }

    // ------------------------------------------------------------- baseline

    [Fact]
    public void Scan_ReportsClean_WhenEveryFieldResolves()
    {
        using var report = HealthyReport();
        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.True(result.ModelReadable);
        Assert.True(result.IsClean,
            $"expected a clean scan, found: {string.Join(", ", result.Fields.Select(f => f.Describe()))}");
        Assert.Empty(result.Visuals);
    }

    [Fact]
    public void Scan_ReportsModelUnreadable_WhenThereIsNoTmdl()
    {
        // No semantic model at all: the scan must say so rather than claim every visual broke.
        using var report = TestReports.Create(
            new[] { new TestPage("Only", new TestVisual("card", new TestProjection("Values", Table, "Segment"))) });

        var empty = Path.Combine(report.ProjectRootPath, "NotAModel");
        Directory.CreateDirectory(empty);

        var result = new BrokenVisualService().Scan(LoadManager(report), SemanticModelIndex.Load(empty));

        Assert.False(result.ModelReadable);
        Assert.NotNull(result.UnsupportedReason);
    }

    [Fact]
    public void Scan_ToleratesPageWithNoVisuals()
    {
        using var report = TestReports.Create(
            new[]
            {
                new TestPage("Empty"),
                new TestPage("Full", new TestVisual("pieChart", new TestProjection("Values", Table, "Segment")))
            },
            new TestSemanticModel().WithStringColumn(Table, "Segment"));

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.True(result.IsClean);
    }

    // ------------------------------------------------------------ detection

    [Fact]
    public void Scan_DetectsRenamedColumn_AndGroupsEveryUsage()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.False(result.IsClean);

        var field = Assert.Single(result.Fields);
        Assert.Equal("financials.Segment", field.Key);
        Assert.Equal(BrokenFieldSeverity.MissingField, field.Severity);
        Assert.Equal(SemanticFieldKind.Column, field.ExpectedKind);
        Assert.Equal(1, field.VisualCount);
        Assert.Single(result.Visuals);

        var usage = Assert.Single(field.Usages);
        Assert.Equal(BrokenReferenceKind.DataRole, usage.Kind);
        Assert.Equal("Category", usage.Role);
        Assert.Equal("Overview", usage.PageDisplayName);
    }

    [Fact]
    public void Scan_CountsEveryVisualThatBindsTheField()
    {
        using var report = TestReports.Create(
            new[]
            {
                new TestPage("One", new TestVisual("card", new TestProjection("Values", Table, "Segment"))),
                new TestPage("Two", new TestVisual("card", new TestProjection("Values", Table, "Segment"))),
                new TestPage("Three", new TestVisual("card", new TestProjection("Values", Table, "Product")))
            },
            new TestSemanticModel()
                .WithStringColumn(Table, "Segment")
                .WithStringColumn(Table, "Product"));

        RenameColumn(report, Table, "Segment", "SalesSegment");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        var field = Assert.Single(result.Fields);
        Assert.Equal(2, field.VisualCount);
        Assert.Equal(2, field.UsageCount);
        Assert.Equal(2, result.Visuals.Count);
        Assert.Equal(2, result.ReferenceCount);
    }

    [Fact]
    public void Scan_DetectsRenamedTable_AsMissingTableSeverity()
    {
        using var report = HealthyReport();
        RenameTable(report, Table, "financials_v2");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.False(result.IsClean);
        Assert.NotEmpty(result.Fields);
        Assert.All(result.Fields, f => Assert.Equal(BrokenFieldSeverity.MissingTable, f.Severity));
        Assert.All(result.Fields, f => Assert.Equal("financials", f.Entity));
    }

    [Fact]
    public void Scan_DoesNotGuessAReplacement_WhenTheWholeTableIsGone()
    {
        using var report = HealthyReport();

        // "Segment-Code" normalizes to the same value as "Segment", so a normalized lookup
        // that ignored the entity would happily suggest it. With the table renamed there is
        // no entity to match within, and guessing across tables would silently rebind the
        // report to different data.
        RenameTable(report, Table, "financials_v2");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.DoesNotContain(result.Fields, f => f.HasSuggestion);
        Assert.Equal(0, result.SuggestedCount);
    }

    [Fact]
    public void Scan_FindsReferencesInsideFormatSelectors()
    {
        using var report = HealthyReport();

        var visual = TestReports.Visual(report.ReportPath, "tableEx");
        InjectMemberSelector(visual.VisualJsonPath, Table, "Product", "Velo");

        RenameColumn(report, Table, "Product", "ProductRenamed");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        var field = Assert.Single(result.Fields);
        Assert.Equal("financials.Product", field.Key);

        // The whole point of walking the tree rather than just queryState: a color rule's
        // reference breaks just as surely as an axis, but the visual still renders.
        Assert.Contains(field.Usages, u => u.Kind == BrokenReferenceKind.FormatSelector);
        Assert.True(field.HasNonRoleUsages);
    }

    [Fact]
    public void Scan_SuggestsCosmeticReplacement_ForCasingOnlyRename()
    {
        // "GrossSales" -> "Gross_Sales" differs only by punctuation, which Power BI treats as a
        // genuinely different field, so the visual does break — but the replacement is
        // obvious and needs no judgement from the author.
        using var report = HealthyReport();
        RenameColumn(report, Table, "GrossSales", "Gross_Sales");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        var field = Assert.Single(result.Fields);
        Assert.Equal("financials.GrossSales", field.Key);
        Assert.True(field.HasSuggestion);
        Assert.True(field.IsCosmetic);
        Assert.Equal("financials", field.SuggestedEntity);
        Assert.Equal("Gross_Sales", field.SuggestedProperty);
        Assert.Equal(1, result.CosmeticCount);
        Assert.Equal(1, result.SuggestedCount);
    }

    [Fact]
    public void Scan_TreatsCaseOnlyRenameAsNotBroken()
    {
        // Field lookup is case-insensitive, so "Segment" -> "segment" is not a break at all.
        // Reporting it would send the author chasing a problem that does not exist.
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "segment");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.True(result.IsClean);
        Assert.Equal(0, result.SuggestedCount);
    }

    [Fact]
    public void Scan_DoesNotSuggestWhenTheOldNameIsGoneEntirely()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "CompletelyDifferent");

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        var field = Assert.Single(result.Fields);
        Assert.False(field.HasSuggestion);
        Assert.False(field.IsCosmetic);
    }

    [Fact]
    public void Scan_FindsSeriesIdentitySelectorsInMetadataStrings()
    {
        // A series-identity selector is the plain string "table.field", not a field node, so
        // it is invisible to a tree walk. It still dies quietly when the field is renamed.
        using var report = HealthyReport();

        var visual = TestReports.Visual(report.ReportPath, "clusteredBarChart");
        var root = JsonNode.Parse(File.ReadAllText(visual.VisualJsonPath))!.AsObject();

        var dataPoints = new JsonArray
        {
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["fill"] = new JsonObject
                    {
                        ["solid"] = new JsonObject
                        {
                            ["color"] = new JsonObject
                            {
                                ["expr"] = new JsonObject { ["Literal"] = new JsonObject { ["Value"] = "'#118DFF'" } }
                            }
                        }
                    }
                },
                ["selector"] = new JsonObject { ["metadata"] = "financials.GrossSales" }
            }
        };
        root["visual"]!["objects"]!["dataPoint"] = dataPoints;
        File.WriteAllText(visual.VisualJsonPath, root.ToJsonString());

        RenameColumn(report, Table, "GrossSales", "Revenue");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();

        var field = Assert.Single(service.Scan(manager, index).Fields);
        Assert.Equal("financials.GrossSales", field.Key);
        Assert.Contains(field.Usages, u => u.Kind == BrokenReferenceKind.SeriesIdentity);

        service.ApplyRemap(manager, new[]
        {
            new FieldRemap { OldEntity = Table, OldProperty = "GrossSales", NewEntity = Table, NewProperty = "Revenue" }
        }, index);

        var after = JsonNode.Parse(File.ReadAllText(visual.VisualJsonPath))!;
        Assert.Equal("financials.Revenue",
            after["visual"]!["objects"]!["dataPoint"]![0]!["selector"]!["metadata"]!.GetValue<string>());
    }

    [Fact]
    public void Scan_IgnoresMetadataValuesThatAreNotFieldReferences()
    {
        using var report = HealthyReport();

        var visual = TestReports.Visual(report.ReportPath, "clusteredBarChart");
        var root = JsonNode.Parse(File.ReadAllText(visual.VisualJsonPath))!.AsObject();
        root["visual"]!["objects"]!["general"] = new JsonObject { ["metadata"] = "not a field reference" };
        File.WriteAllText(visual.VisualJsonPath, root.ToJsonString());

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.True(result.IsClean);
    }

    [Fact]
    public void Scan_DetectsAggregationOverARenamedColumn()
    {
        // An aggregation wraps a column, so the thing that breaks is the column underneath.
        // ExpectedKind must therefore say Column, or the replacement picker would offer the
        // author a measure and produce a measure-over-a-column that never existed.
        using var report = TestReports.Create(
            new[]
            {
                new TestPage("Overview",
                    new TestVisual("columnChart",
                        new TestProjection("Y", Table, "GrossSales")
                        {
                            AggregateFunction = 0,
                            QueryRef = "Sum(financials.GrossSales)",
                            NativeQueryRef = "Sum of GrossSales"
                        }))
            },
            new TestSemanticModel().WithDoubleColumn(Table, "GrossSales"));

        RenameColumn(report, Table, "GrossSales", "Revenue");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();

        var field = Assert.Single(service.Scan(manager, index).Fields);
        Assert.Equal("financials.GrossSales", field.Key);
        Assert.Equal(SemanticFieldKind.Column, field.ExpectedKind);

        // And the repair has to rebuild the aggregation's display string, not just the
        // inner binding.
        service.ApplyRemap(manager, new[]
        {
            new FieldRemap { OldEntity = Table, OldProperty = "GrossSales", NewEntity = Table, NewProperty = "Revenue" }
        }, index);

        var root = JsonNode.Parse(File.ReadAllText(TestReports.Visuals(report.ReportPath)[0].VisualJsonPath))!;
        var projection = root["visual"]!["query"]!["queryState"]!["Y"]!["projections"]![0]!;

        Assert.Equal("Revenue", projection["field"]!["Aggregation"]!["Expression"]!["Column"]!["Property"]!.GetValue<string>());
        Assert.Equal("Sum(financials.Revenue)", projection["queryRef"]!.GetValue<string>());
        Assert.Equal("Sum of Revenue", projection["nativeQueryRef"]!.GetValue<string>());

        Assert.True(service.Scan(manager, index).IsClean);
    }

    [Fact]
    public void Scan_IgnoresHierarchyBindings()
    {
        // A hierarchy level is not something this tool can meaningfully remap, so it is left
        // out of the report rather than offered as a fixable break.
        using var report = HealthyReport();

        var visual = TestReports.Visual(report.ReportPath, "clusteredBarChart");
        var root = JsonNode.Parse(File.ReadAllText(visual.VisualJsonPath))!.AsObject();
        root["visual"]!["query"]!["queryState"]!["Category"] = new JsonObject
        {
            ["projections"] = new JsonArray
            {
                new JsonObject
                {
                    ["field"] = new JsonObject
                    {
                        ["Hierarchy"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = Table } },
                            ["Hierarchy"] = "Geography"
                        }
                    },
                    ["queryRef"] = "financials.Geography"
                }
            }
        };
        File.WriteAllText(visual.VisualJsonPath, root.ToJsonString());

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));

        Assert.True(result.IsClean);
    }

    // ---------------------------------------------------------------- remap

    private static FieldRemap SegmentTo(string newName) => new()
    {
        OldEntity = Table,
        OldProperty = "Segment",
        NewEntity = Table,
        NewProperty = newName
    };

    [Fact]
    public void ApplyRemap_RepointsFieldAndCaches_SoRescanIsClean()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();

        Assert.False(service.Scan(manager, index).IsClean);

        var result = service.ApplyRemap(manager, new[] { SegmentTo("SegmentRenamed") }, index);

        Assert.Empty(result.Warnings);
        Assert.Equal(1, result.ReferencesChanged);
        Assert.Equal(1, result.FilesChanged);
        Assert.Equal(1, result.VisualsChanged);

        var after = service.Scan(manager, index);
        Assert.True(after.IsClean,
            $"expected a clean rescan, found: {string.Join(", ", after.Fields.Select(f => f.Describe()))}");
    }

    [Fact]
    public void ApplyRemap_RewritesCachedDisplayStrings()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        new BrokenVisualService().ApplyRemap(manager, new[] { SegmentTo("SegmentRenamed") }, Index(report));

        var visual = TestReports.Visual(report.ReportPath, "clusteredBarChart");
        var root = JsonNode.Parse(File.ReadAllText(visual.VisualJsonPath))!;

        // queryRef and nativeQueryRef drive Power BI's field well, and other tools key off
        // them. A rebind that left them stale would still read as broken.
        var projection = root["visual"]!["query"]!["queryState"]!["Category"]!["projections"]![0]!;
        Assert.Equal("financials.SegmentRenamed", projection["queryRef"]!.GetValue<string>());
        Assert.Equal("SegmentRenamed", projection["nativeQueryRef"]!.GetValue<string>());

        // The binding itself must point at the new column too.
        Assert.Equal("SegmentRenamed",
            projection["field"]!["Column"]!["Property"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyRemap_LeavesSimilarColumnNamesAlone()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        new BrokenVisualService().ApplyRemap(manager, new[] { SegmentTo("SegmentRenamed") }, Index(report));

        // "Segment-Code" is a different column that merely shares a prefix; the token-aware
        // rewrite must not touch it.
        var text = File.ReadAllText(TestReports.Visual(report.ReportPath, "tableEx").VisualJsonPath);
        Assert.Contains("financials.Product", text);
        Assert.DoesNotContain("Segment-Code\"", text);
    }

    [Fact]
    public void ApplyRemap_PreviewOnly_TouchesNothingOnDisk()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        var visualPath = TestReports.Visual(report.ReportPath, "clusteredBarChart").VisualJsonPath;
        var before = File.ReadAllText(visualPath);

        var result = new BrokenVisualService()
            .ApplyRemap(manager, new[] { SegmentTo("SegmentRenamed") }, Index(report), previewOnly: true);

        // The preview still counts what it would do, otherwise the UI has nothing to show.
        Assert.Equal(1, result.ReferencesChanged);
        Assert.Equal(1, result.FilesChanged);
        Assert.Equal(before, File.ReadAllText(visualPath));
    }

    [Fact]
    public void ApplyRemap_RefusesReplacementThatIsNotInTheModel()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        var result = new BrokenVisualService()
            .ApplyRemap(manager, new[] { SegmentTo("DoesNotExist") }, Index(report));

        // Writing an equally broken binding would leave the author worse off with no clue why.
        Assert.NotEmpty(result.Warnings);
        Assert.Contains("DoesNotExist", result.Warnings[0]);
        Assert.True(result.NoChange);
        Assert.Equal(0, result.FilesChanged);
    }

    [Fact]
    public void ApplyRemap_IsIdempotent()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();
        var remap = new[] { SegmentTo("SegmentRenamed") };

        service.ApplyRemap(manager, remap, index);
        var second = service.ApplyRemap(manager, remap, index);

        Assert.True(second.NoChange);
        Assert.Equal(0, second.ReferencesChanged);
        Assert.Equal(0, second.FilesChanged);
    }

    [Fact]
    public void ApplyRemap_FixesEveryVisualThatBindsTheField()
    {
        using var report = TestReports.Create(
            new[]
            {
                new TestPage("One", new TestVisual("card", new TestProjection("Values", Table, "Segment"))),
                new TestPage("Two", new TestVisual("card", new TestProjection("Values", Table, "Segment")))
            },
            new TestSemanticModel()
                .WithStringColumn(Table, "Segment")
                .WithStringColumn(Table, "Product"));

        RenameColumn(report, Table, "Segment", "SalesSegment");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();

        var result = service.ApplyRemap(manager, new[] { SegmentTo("SalesSegment") }, index);

        Assert.Equal(2, result.ReferencesChanged);
        Assert.Equal(2, result.FilesChanged);
        Assert.True(service.Scan(manager, index).IsClean);
    }

    [Fact]
    public void ApplyRemap_RepointsSelectorReferencesToo()
    {
        using var report = HealthyReport();

        var visual = TestReports.Visual(report.ReportPath, "tableEx");
        InjectMemberSelector(visual.VisualJsonPath, Table, "Product", "Velo");

        RenameColumn(report, Table, "Product", "ProductRenamed");

        var manager = LoadManager(report);
        var result = new BrokenVisualService().ApplyRemap(manager, new[]
        {
            new FieldRemap { OldEntity = Table, OldProperty = "Product", NewEntity = Table, NewProperty = "ProductRenamed" }
        }, Index(report));

        Assert.Equal(2, result.ReferencesChanged);

        var root = JsonNode.Parse(File.ReadAllText(visual.VisualJsonPath))!;
        var comparison = root["visual"]!["objects"]!["dataPoint"]![0]!["selector"]!["data"]![0]!["scopeId"]!["Comparison"]!;

        // Leaving the color rule pointed at the old column would keep it silently inert.
        Assert.Equal("ProductRenamed", comparison["Left"]!["Column"]!["Property"]!.GetValue<string>());
    }

    [Fact]
    public void ApplyRemap_SkipsUnparseableVisualWithoutFailing()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        // A truncated file, as a crash mid-write would leave behind.
        File.WriteAllText(TestReports.Visual(report.ReportPath, "tableEx").VisualJsonPath, "{ \"visual\": ");

        var manager = LoadManager(report);
        var result = new BrokenVisualService()
            .ApplyRemap(manager, new[] { SegmentTo("SegmentRenamed") }, Index(report));

        Assert.Equal(1, result.FilesSkipped);
        Assert.Equal(1, result.FilesChanged);
    }

    // -------------------------------------------------------- usage grouping

    [Fact]
    public void UsagesByVisual_CollapsesRepeatedReferencesIntoOneRow()
    {
        using var report = HealthyReport();

        var visual = TestReports.Visual(report.ReportPath, "clusteredBarChart");
        // Eight colour rules on the same field, as Power BI writes when a value list is
        // coloured member by member.
        for (var i = 0; i < 8; i++)
        {
            InjectMemberSelector(visual.VisualJsonPath, Table, "Segment", $"Value{i}");
        }

        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var field = Assert.Single(new BrokenVisualService().Scan(LoadManager(report), Index(report)).Fields);

        // Nine raw references: one axis plus eight rules.
        Assert.Equal(9, field.UsageCount);

        // ...but one place a reader cares about, and the repeat is a count not a wall of rows.
        var group = Assert.Single(field.UsagesByVisual);
        Assert.Equal(9, group.UsageCount);
        Assert.Equal(2, group.Locations.Count);
        Assert.Equal("Axis · Category", group.Locations[0].Label);
        Assert.Equal("Colour rules", group.Locations[1].Label);
        Assert.Equal(8, group.Locations[1].Count);
        Assert.Equal("Axis · Category  ·  Colour rules ×8", group.LocationSummary);

        // Raw usages stay flat for counting, which is what the grouping is built on.
        Assert.Equal(9, field.Usages.Count);
    }

    [Fact]
    public void UsagesByVisual_SeparatesVisualsOnDifferentPages()
    {
        using var report = TestReports.Create(
            new[]
            {
                new TestPage("Alpha", new TestVisual("card", new TestProjection("Values", Table, "Segment"))),
                new TestPage("Beta", new TestVisual("tableEx", new TestProjection("Values", Table, "Segment")))
            },
            new TestSemanticModel().WithStringColumn(Table, "Segment"));

        RenameColumn(report, Table, "Segment", "SalesSegment");

        var field = Assert.Single(new BrokenVisualService().Scan(LoadManager(report), Index(report)).Fields);

        Assert.Equal(2, field.VisualCount);
        Assert.Equal(2, field.UsagesByVisual.Count);
        Assert.Equal(new[] { "Alpha", "Beta" }, field.UsagesByVisual.Select(g => g.PageDisplayName));
    }

    [Theory]
    [InlineData("Category", "Axis · Category")]
    [InlineData("Series", "Legend · Series")]
    [InlineData("Y", "Values · Y")]
    // The raw role is dropped when it adds nothing, so "Values" does not read "Values · Values".
    [InlineData("Values", "Values")]
    [InlineData("Rows", "Rows")]
    [InlineData("Tooltips", "Tooltip · Tooltips")]
    public void LocationLabel_NamesTheRoleInTheAuthorsVocabulary(string role, string expected)
    {
        var usage = new BrokenFieldUsage { Role = role, Kind = BrokenReferenceKind.DataRole };
        Assert.Equal(expected, usage.LocationLabel);
    }

    [Fact]
    public void UsageGroup_NamesAnUntitledVisualByItsTypeAndThePageByName()
    {
        // The table visual carries no title, so naming it after a projected field would print
        // the field being repaired twice — once as the heading, once as the row above it.
        using var report = HealthyReport();
        RenameColumn(report, Table, "Product", "ProductRenamed");

        var group = Assert.Single(Assert.Single(new BrokenVisualService()
            .Scan(LoadManager(report), Index(report)).Fields).UsagesByVisual);

        Assert.Equal("Table visual", group.Title);
        Assert.Contains("Overview", group.Subtitle);
        Assert.Contains("page", group.Subtitle, StringComparison.OrdinalIgnoreCase);
        Assert.True(group.ShowLocations);
    }

    [Fact]
    public void UsageGroup_ShowsPageAndTypeWhenTheVisualHasItsOwnTitle()
    {
        using var report = HealthyReport();
        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var group = Assert.Single(Assert.Single(new BrokenVisualService()
            .Scan(LoadManager(report), Index(report)).Fields).UsagesByVisual);

        Assert.Equal("Sales by segment", group.Title);

        // Both facts the reader needs, and the type appears only once.
        Assert.Contains("Overview", group.Subtitle);
        Assert.Contains("Clustered Bar Chart", group.Subtitle);
    }

    [Fact]
    public void UsageGroup_LabelsAPageFilterAndDropsTheRedundantLocationLine()
    {
        using var report = HealthyReport();
        InjectPageFilter(report, "Overview", Table, "Product");
        RenameColumn(report, Table, "Product", "ProductRenamed");

        var field = Assert.Single(new BrokenVisualService().Scan(LoadManager(report), Index(report)).Fields);
        var group = Assert.Single(field.UsagesByVisual, g => g.Scope == BrokenUsageScope.PageFilter);

        Assert.Equal("Page level filter", group.Title);
        Assert.Contains("Overview", group.Subtitle);

        // The heading already says what it is, so repeating it in the location line is noise.
        Assert.False(group.ShowLocations);
    }

    [Fact]
    public void Scan_ClassifiesAVisualScopedFilter()
    {
        // A visual filter sits beside "query", not inside "objects", so without its own check
        // it fell into the catch-all and read as the uninformative "Other".
        using var report = HealthyReport();

        var visual = TestReports.Visual(report.ReportPath, "clusteredBarChart");
        InjectVisualFilter(visual.VisualJsonPath, Table, "Product");

        RenameColumn(report, Table, "Product", "ProductRenamed");

        var field = Assert.Single(new BrokenVisualService().Scan(LoadManager(report), Index(report)).Fields);

        Assert.Contains(field.Usages, u => u.Kind == BrokenReferenceKind.VisualFilter);
        Assert.Contains("Visual filter",
            field.UsagesByVisual.SelectMany(g => g.Locations).Select(l => l.Label));
    }

    // ------------------------------------------------------------- filters

    /// <summary>
    /// Injects a visual-scoped filter. PBIR writes this at the ROOT of visual.json, as a sibling
    /// of <c>visual</c> — not inside it, which is the mistake that made every visual filter read
    /// as "Other part of visual".
    /// </summary>
    private static void InjectVisualFilter(string visualJsonPath, string entity, string property)
    {
        var root = JsonNode.Parse(File.ReadAllText(visualJsonPath))!.AsObject();

        root["filterConfig"] = new JsonObject
        {
            ["filters"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "visualfilter1",
                    ["type"] = "Categorical",
                    ["field"] = new JsonObject
                    {
                        ["Column"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = entity } },
                            ["Property"] = property
                        }
                    }
                }
            }
        };

        File.WriteAllText(visualJsonPath, root.ToJsonString());
    }

    /// <summary>Adds a page-level filter on the given column to a page's page.json.</summary>
    private static void InjectPageFilter(TestReport report, string pageDisplayName, string entity, string property)
    {
        var pageId = report.PageIdFor(pageDisplayName);
        var path = Path.Combine(report.PagesDirectoryPath, pageId, "page.json");

        var root = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        root["filterConfig"] = new JsonObject
        {
            ["filters"] = new JsonArray
            {
                new JsonObject
                {
                    ["name"] = "filter1",
                    ["type"] = "Categorical",
                    ["field"] = new JsonObject
                    {
                        ["Column"] = new JsonObject
                        {
                            ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = entity } },
                            ["Property"] = property
                        }
                    }
                }
            }
        };

        File.WriteAllText(path, root.ToJsonString());
    }

    [Fact]
    public void Scan_DetectsAndRepairsAPageLevelFilter()
    {
        using var report = HealthyReport();
        InjectPageFilter(report, "Overview", Table, "Product");

        RenameColumn(report, Table, "Product", "ProductRenamed");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();

        var field = Assert.Single(service.Scan(manager, index).Fields);
        Assert.Equal("financials.Product", field.Key);

        // The filter counts separately from the visuals, so the impact text can say so.
        Assert.Equal(1, field.FilteredPageCount);
        Assert.Equal(1, field.Usages.Count(u => u.Kind == BrokenReferenceKind.PageFilter));

        // A page-level usage belongs to no visual, so it gets its own row alongside
        // the table visual that also binds this column.
        var filterGroup = Assert.Single(field.UsagesByVisual, g => g.Scope == BrokenUsageScope.PageFilter);
        Assert.Equal("Page level filter", Assert.Single(filterGroup.Locations).Label);
        Assert.Equal(2, field.UsagesByVisual.Count);

        service.ApplyRemap(manager, new[]
        {
            new FieldRemap { OldEntity = Table, OldProperty = "Product", NewEntity = Table, NewProperty = "ProductRenamed" }
        }, index);

        var pageJson = JsonNode.Parse(File.ReadAllText(
            Path.Combine(report.PagesDirectoryPath, report.PageIdFor("Overview"), "page.json")))!;

        Assert.Equal("ProductRenamed",
            pageJson["filterConfig"]!["filters"]![0]!["field"]!["Column"]!["Property"]!.GetValue<string>());

        Assert.True(service.Scan(manager, index).IsClean);
    }

    [Fact]
    public void Scan_ReportsReportLevelFilters_ButMarksThemNotAutoFixable()
    {
        using var report = HealthyReport();

        var reportJsonPath = Path.Combine(report.ReportPath, "definition", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportJsonPath)!);
        File.WriteAllText(reportJsonPath, new JsonObject
        {
            ["filterConfig"] = new JsonObject
            {
                ["filters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["field"] = new JsonObject
                        {
                            ["Column"] = new JsonObject
                            {
                                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = Table } },
                                ["Property"] = "Product"
                            }
                        }
                    }
                }
            }
        }.ToJsonString());

        RenameColumn(report, Table, "Product", "ProductRenamed");

        var field = Assert.Single(new BrokenVisualService().Scan(LoadManager(report), Index(report)).Fields);

        // It also lives in a visual, so the field as a whole is still repairable...
        Assert.Contains(field.Usages, u => u.Kind == BrokenReferenceKind.PageFilter || u.Kind == BrokenReferenceKind.DataRole);

        // ...and the report filter is reported as its own place, not folded into the visual.
        Assert.Contains(field.UsagesByVisual, g => g.Scope == BrokenUsageScope.ReportFilter);
    }

    [Fact]
    public void Scan_ReportsAFieldUsedOnlyByAReportFilter()
    {
        using var report = TestReports.Create(
            new[] { new TestPage("Only", new TestVisual("card", new TestProjection("Values", Table, "Segment"))) },
            new TestSemanticModel().WithStringColumn(Table, "Segment"));

        var reportJsonPath = Path.Combine(report.ReportPath, "definition", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportJsonPath)!);
        File.WriteAllText(reportJsonPath, new JsonObject
        {
            ["filterConfig"] = new JsonObject
            {
                ["filters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["field"] = new JsonObject
                        {
                            ["Column"] = new JsonObject
                            {
                                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = Table } },
                                ["Property"] = "OnlyInFilter"
                            }
                        }
                    }
                }
            }
        }.ToJsonString());

        var result = new BrokenVisualService().Scan(LoadManager(report), Index(report));
        var filterOnly = Assert.Single(result.Fields, f => f.Property == "OnlyInFilter");

        Assert.True(filterOnly.HasReportFilter);
        Assert.Equal(0, filterOnly.VisualCount);
        Assert.Equal(0, filterOnly.FilteredPageCount);

        // It still has to be described as in use, not "not referenced", or the impact text lies.
        var group = Assert.Single(filterOnly.UsagesByVisual);
        Assert.Equal(BrokenUsageScope.ReportFilter, group.Scope);
        Assert.Equal("Report level filter", group.Title);
        Assert.Equal("Applies to every page", group.Subtitle);
    }

    [Fact]
    public void ApplyRemap_RepairsAReportLevelFilterIncludingItsAliasedExpression()
    {
        // A report filter names its column twice: in the canonical "field" binding, and again in
        // "filter.Where" through a table alias whose SourceRef has no Entity. Rewriting only the
        // first leaves the filter filtering on the old column while its label shows the new one.
        using var report = TestReports.Create(
            new[] { new TestPage("Overview", new TestVisual("card", new TestProjection("Values", Table, "Segment"))) },
            new TestSemanticModel().WithStringColumn(Table, "Segment").WithStringColumn(Table, "SegmentRenamed"));

        var reportJsonPath = Path.Combine(report.ReportPath, "definition", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportJsonPath)!);
        File.WriteAllText(reportJsonPath, new JsonObject
        {
            ["filterConfig"] = new JsonObject
            {
                ["filters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "filter1",
                        ["field"] = new JsonObject
                        {
                            ["Column"] = new JsonObject
                            {
                                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = Table } },
                                ["Property"] = "Segment"
                            }
                        },
                        ["filter"] = new JsonObject
                        {
                            ["Version"] = 2,
                            ["From"] = new JsonArray
                            {
                                // The alias resolves to the table being renamed.
                                new JsonObject { ["Name"] = "r", ["Entity"] = Table, ["Type"] = 0 }
                            },
                            ["Where"] = new JsonArray
                            {
                                new JsonObject
                                {
                                    ["Condition"] = new JsonObject
                                    {
                                        ["In"] = new JsonObject
                                        {
                                            ["Expressions"] = new JsonArray
                                            {
                                                new JsonObject
                                                {
                                                    ["Column"] = new JsonObject
                                                    {
                                                        ["Expression"] = new JsonObject
                                                        {
                                                            ["SourceRef"] = new JsonObject { ["Source"] = "r" }
                                                        },
                                                        ["Property"] = "Segment"
                                                    }
                                                }
                                            }
                                        }
                                    }
                                }
                            }
                        }
                    }
                }
            }
        }.ToJsonString());

        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();

        var field = Assert.Single(service.Scan(manager, index).Fields);
        Assert.True(field.HasReportFilter);

        var result = service.ApplyRemap(manager, new[]
        {
            new FieldRemap { OldEntity = Table, OldProperty = "Segment", NewEntity = Table, NewProperty = "SegmentRenamed" }
        }, index);

        // report.json is now written, where before it was deliberately skipped.
        Assert.Empty(result.Warnings);
        Assert.True(result.FilesChanged >= 2, $"expected the visual and report.json, got {result.FilesChanged}");

        var saved = JsonNode.Parse(File.ReadAllText(reportJsonPath))!;
        var filter = saved["filterConfig"]!["filters"]![0]!;

        // The canonical binding.
        Assert.Equal("SegmentRenamed", filter["field"]!["Column"]!["Property"]!.GetValue<string>());

        // And the aliased copy, which a plain field walk cannot resolve.
        var aliased = filter["filter"]!["Where"]![0]!["Condition"]!["In"]!["Expressions"]![0]!["Column"]!;
        Assert.Equal("SegmentRenamed", aliased["Property"]!.GetValue<string>());

        Assert.True(service.Scan(manager, index).IsClean);
    }

    [Fact]
    public void ApplyRemap_IsUndoableAcrossVisualsPageFiltersAndTheReportFilter()
    {
        // The whole point of routing repairs through the workspace: one Undo puts every place the
        // field was referenced back the way it was, including definition/report.json, which sits
        // outside the pages directory and would otherwise survive the undo.
        using var report = TestReports.Create(
            new[] { new TestPage("Overview", new TestVisual("card", new TestProjection("Values", Table, "Segment"))) },
            new TestSemanticModel().WithStringColumn(Table, "Segment").WithStringColumn(Table, "SegmentRenamed"));

        InjectPageFilter(report, "Overview", Table, "Segment");

        var reportJsonPath = Path.Combine(report.ReportPath, "definition", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportJsonPath)!);
        File.WriteAllText(reportJsonPath, new JsonObject
        {
            ["filterConfig"] = new JsonObject
            {
                ["filters"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["name"] = "reportfilter1",
                        ["field"] = new JsonObject
                        {
                            ["Column"] = new JsonObject
                            {
                                ["Expression"] = new JsonObject { ["SourceRef"] = new JsonObject { ["Entity"] = Table } },
                                ["Property"] = "Segment"
                            }
                        }
                    }
                }
            }
        }.ToJsonString());

        RenameColumn(report, Table, "Segment", "SegmentRenamed");

        var manager = LoadManager(report);
        var index = Index(report);
        var service = new BrokenVisualService();

        using var history = new ReportEditHistory();
        history.Reset(manager.PagesDirectoryPath, new[] { reportJsonPath });

        Assert.False(service.Scan(manager, index).IsClean);

        service.ApplyRemap(manager, new[]
        {
            new FieldRemap { OldEntity = Table, OldProperty = "Segment", NewEntity = Table, NewProperty = "SegmentRenamed" }
        }, index);
        history.Commit();

        var pageJsonPath = Path.Combine(report.PagesDirectoryPath, report.PageIdFor("Overview"), "page.json");
        var visualPath = TestReports.Visual(report.ReportPath, "card").VisualJsonPath;

        Assert.Contains("SegmentRenamed", File.ReadAllText(reportJsonPath));
        Assert.Contains("SegmentRenamed", File.ReadAllText(pageJsonPath));
        Assert.Contains("SegmentRenamed", File.ReadAllText(visualPath));
        Assert.True(service.Scan(manager, index).IsClean);

        // Undo restores all three places.
        manager.RestoreFromSnapshot(history.Undo()!);

        Assert.DoesNotContain("SegmentRenamed", File.ReadAllText(reportJsonPath));
        Assert.DoesNotContain("SegmentRenamed", File.ReadAllText(pageJsonPath));
        Assert.DoesNotContain("SegmentRenamed", File.ReadAllText(visualPath));
        Assert.False(service.Scan(manager, index).IsClean);

        // And redo puts the repair back.
        manager.RestoreFromSnapshot(history.Redo()!);

        Assert.Contains("SegmentRenamed", File.ReadAllText(reportJsonPath));
        Assert.True(service.Scan(manager, index).IsClean);
    }

    // -------------------------------------------------------- token rewriting
    [Theory]
    // Whole-token matches are replaced.
    [InlineData("Segment", "Segment", "SalesSegment", "SalesSegment")]
    [InlineData("Count of Segment", "Segment", "SalesSegment", "Count of SalesSegment")]
    [InlineData("financials.Segment", "financials.Segment", "financials.SalesSegment", "financials.SalesSegment")]
    [InlineData("Sum(financials.Segment)", "financials.Segment", "financials.SalesSegment", "Sum(financials.SalesSegment)")]
    [InlineData("a.Segment,b.Segment", "a.Segment", "a.SalesSegment", "a.SalesSegment,b.Segment")]
    // Partial matches are left alone: a column genuinely named SegmentCode is not Segment.
    [InlineData("SegmentCode", "Segment", "SalesSegment", "SegmentCode")]
    [InlineData("MySegment", "Segment", "SalesSegment", "MySegment")]
    [InlineData("Segment_Code", "Segment", "SalesSegment", "Segment_Code")]
    public void ReplaceToken_OnlyReplacesWholeTokens(string input, string oldToken, string newToken, string expected)
    {
        Assert.Equal(expected, BrokenVisualService.ReplaceToken(input, oldToken, newToken));
    }
}