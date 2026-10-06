using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SingularTools.Core;
using Xunit;

namespace SingularTools.Tests;

/// <summary>
/// Covers the resolution contract the Broken Visuals tool depends on: what still resolves in
/// the demo model, and what does not. Structural assertions only — the demo report is edited
/// in place by its author, so exact column sets are asserted synthetically.
/// </summary>
public sealed class SemanticModelIndexTests : IDisposable
{
    private const string PrivateTable = "DateTableTemplate_aecf1a85-62fa-494b-a49e-4b3eefa351f0";

    /// <summary>
    /// Marked <c>isHidden</c> but not <c>isPrivate</c>, so it must stay in the index — Power BI
    /// lists hidden tables in its own model view.
    /// </summary>
    private const string HiddenTable = "LocalDateTable_22bc4709-7451-4c9d-aa8f-9e01ef89fc91";

    private readonly List<string> _tempRoots = new();

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // --------------------------------------------------------------- Fixtures

    private static string Tmdl(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>Creates a throwaway TMDL model and returns the .SemanticModel folder.</summary>
    private string CreateModel(params (string Name, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "singular-index-" + Guid.NewGuid().ToString("N"));
        var modelFolder = Path.Combine(root, "Model.SemanticModel");
        var tablesDir = Path.Combine(modelFolder, "definition", "tables");
        Directory.CreateDirectory(tablesDir);

        foreach (var file in files)
        {
            File.WriteAllText(Path.Combine(tablesDir, file.Name), file.Content);
        }

        _tempRoots.Add(root);
        return modelFolder;
    }

    /// <summary>
    /// Builds a throwaway project whose model carries every shape these tests exercise, and
    /// returns the .Report folder so sibling-model discovery is covered too.
    /// </summary>
    /// <remarks>
    /// Synthetic on purpose. The checked-in demo report is opened and edited in place from
    /// Power BI Desktop, which rewrites its tables and columns wholesale - a test asserting
    /// the demo's real column names went red the moment its author refreshed the model.
    /// </remarks>
    private string CreateSyntheticProject()
    {
        var root = Path.Combine(Path.GetTempPath(), "singular-indexproj-" + Guid.NewGuid().ToString("N"));
        var project = Path.Combine(root, "Demo");

        var reportFolder = Path.Combine(project, "Demo.Report");
        var pagesDir = Path.Combine(reportFolder, "definition", "pages");
        Directory.CreateDirectory(pagesDir);
        File.WriteAllText(Path.Combine(pagesDir, "pages.json"), "{}");

        var tablesDir = Path.Combine(project, "Demo.SemanticModel", "definition", "tables");
        Directory.CreateDirectory(tablesDir);

        File.WriteAllText(Path.Combine(tablesDir, "financials.tmdl"), Tmdl(
            "table financials",
            "\tlineageTag: 11111111-1111-1111-1111-111111111111",
            "",
            "\tcolumn Segment",
            "\t\tdataType: string",
            "",
            "\tcolumn Country",
            "\t\tdataType: string",
            "",
            "\tcolumn 'Discount Band'",
            "\t\tdataType: string",
            "",
            "\tcolumn 'Units Sold'",
            "\t\tdataType: double",
            "",
            "\tcolumn 'Gross Sales'",
            "\t\tdataType: double",
            "",
            "\tcolumn 'Discount Band_ord_2' = 'Discount Band'",
            "\t\tdataType: int64",
            "",
            "\tmeasure 'Discount Band_ord2'",
            "\t\tlineageTag: 22222222-2222-2222-2222-222222222222"));

        File.WriteAllText(Path.Combine(tablesDir, PrivateTable + ".tmdl"), Tmdl(
            "table " + PrivateTable,
            "\tisPrivate",
            "\tlineageTag: 33333333-3333-3333-3333-333333333333",
            "",
            "\tcolumn Date",
            "\t\tdataType: dateTime",
            "",
            "\tcolumn Year = YEAR([Date])",
            "\t\tdataType: int64"));

        File.WriteAllText(Path.Combine(tablesDir, HiddenTable + ".tmdl"), Tmdl(
            "table " + HiddenTable,
            "\tisHidden",
            "\tlineageTag: 44444444-4444-4444-4444-444444444444",
            "",
            "\tcolumn Date",
            "\t\tdataType: dateTime"));

        _tempRoots.Add(root);
        return reportFolder;
    }

    private SemanticModelIndex SyntheticIndex(bool includePrivate = true) =>
        SemanticModelIndex.LoadForReport(CreateSyntheticProject(), includePrivate);

    // ------------------------------------------------------------------ Loading

    [Fact]
    public void Load_IndexesTheDemoModel()
    {
        var index = SyntheticIndex();

        Assert.True(index.HasTmdlDefinition);
        Assert.Contains("financials", index.TableNames);
        Assert.Contains(PrivateTable, index.TableNames);
        Assert.Contains(HiddenTable, index.TableNames);
    }

    [Fact]
    public void LoadForReport_UsesTheSharedModelDiscovery()
    {
        var reportPath = CreateSyntheticProject();
        var viaReport = SemanticModelIndex.LoadForReport(reportPath);
        var viaFolder = SemanticModelIndex.Load(SortByColumnService.DiscoverModelFolder(reportPath)!);

        Assert.Equal(viaFolder.TableNames, viaReport.TableNames);
        Assert.True(viaReport.ColumnExists("financials", "Segment"));
    }

    [Fact]
    public void LoadForReport_OnAFolderWithoutAModel_IsEmpty()
    {
        var index = SemanticModelIndex.LoadForReport(Path.GetTempPath());

        Assert.False(index.HasTmdlDefinition);
        Assert.Empty(index.TableNames);
        Assert.False(index.TryResolve("financials", "Segment", out _));
    }

    [Fact]
    public void Load_OnAMissingFolder_IsEmptyRatherThanThrowing()
    {
        var folder = Path.Combine(Path.GetTempPath(), "singular-not-here-" + Guid.NewGuid().ToString("N"));
        var index = SemanticModelIndex.Load(folder);

        Assert.False(index.HasTmdlDefinition);
        Assert.Empty(index.TableNames);
        Assert.False(index.TryResolve("financials", "Segment", out _));

        // The requested folder is still reported so a caller can say which model it looked at.
        Assert.Equal(Path.GetFullPath(folder), index.FolderPath);
    }

    [Fact]
    public void Load_OnABlankPath_IsEmptyRatherThanThrowing()
    {
        var index = SemanticModelIndex.Load("");

        Assert.False(index.HasTmdlDefinition);
        Assert.Empty(index.TableNames);
        Assert.False(index.TableExists("financials"));
    }

    // --------------------------------------------------------------- Resolution

    [Fact]
    public void TryResolve_ResolvesAPlainColumn()
    {
        var index = SyntheticIndex();

        Assert.True(index.TryResolve("financials", "Segment", out var kind));
        Assert.Equal(SemanticFieldKind.Column, kind);
    }

    [Fact]
    public void TryResolve_ResolvesAMeasure()
    {
        // The gap SortByColumnService.LoadModel leaves: measures are not in its column list.
        var index = SyntheticIndex();

        Assert.True(index.TryResolve("financials", "Discount Band_ord2", out var kind));
        Assert.Equal(SemanticFieldKind.Measure, kind);
        Assert.True(index.MeasureExists("financials", "Discount Band_ord2"));
        Assert.False(index.ColumnExists("financials", "Discount Band_ord2"));
    }

    [Fact]
    public void TryResolve_ResolvesAQuotedNameWithSpaces()
    {
        var index = SyntheticIndex();

        Assert.True(index.TryResolve("financials", "Discount Band", out var kind));
        Assert.Equal(SemanticFieldKind.Column, kind);

        // The quoted TMDL name is stored unquoted, so the picker sees the display name.
        Assert.Contains("Discount Band", index.GetColumns("financials"));
        Assert.DoesNotContain("Discount Band", index.GetMeasures("financials"));
    }

    [Fact]
    public void TryResolve_ResolvesACalculatedColumn()
    {
        var index = SyntheticIndex();

        Assert.True(index.TryResolve("financials", "Discount Band_ord_2", out var kind));
        Assert.Equal(SemanticFieldKind.Column, kind);
    }

    [Fact]
    public void TryResolve_ResolvesACalculatedColumnAndMeasureInASyntheticModel()
    {
        var index = SemanticModelIndex.Load(CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn 'Net Sales' = SUM(t[Sales]) * 1.1",
            "\t\tdataType: double",
            "",
            "\tmeasure 'Total Net' = ```",
            "\t\t\tSUM(t[Sales])",
            "\t\t\t```",
            "\t\tformatString: 0",
            "",
            "\tmeasure \"Legacy Total\" = 1"))));

        Assert.True(index.TryResolve("t", "Net Sales", out var column));
        Assert.Equal(SemanticFieldKind.Column, column);

        Assert.True(index.TryResolve("t", "Total Net", out var fencedMeasure));
        Assert.Equal(SemanticFieldKind.Measure, fencedMeasure);

        // Double-quoted names are stripped too.
        Assert.True(index.TryResolve("t", "Legacy Total", out var quotedMeasure));
        Assert.Equal(SemanticFieldKind.Measure, quotedMeasure);
    }

    [Fact]
    public void TryResolve_IgnoresMembersInsideAFencedExpression()
    {
        var index = SemanticModelIndex.Load(CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn Total = ```",
            "\t\t\tcolumn NotAColumn",
            "\t\t\t```",
            "\t\tdataType: double",
            "",
            "\tcolumn Real",
            "\t\tdataType: string"))));

        Assert.True(index.ColumnExists("t", "Real"));
        Assert.False(index.ColumnExists("t", "NotAColumn"));
    }

    [Fact]
    public void TryResolve_ResolvesATableWhenThePropertyIsEmpty()
    {
        var index = SyntheticIndex();

        Assert.True(index.TryResolve("financials", string.Empty, out var kind));
        Assert.Equal(SemanticFieldKind.Table, kind);

        Assert.False(index.TryResolve("nope", string.Empty, out _));
    }

    [Fact]
    public void TryResolve_SeparatesAMissingTableFromAMissingField()
    {
        var index = SyntheticIndex();

        Assert.False(index.TryResolve("financials", "Gross Profit", out _));
        Assert.True(index.TableExists("financials")); // MissingField, not MissingTable

        Assert.False(index.TryResolve("no_such_table", "Segment", out _));
        Assert.False(index.TableExists("no_such_table")); // MissingTable
    }

    [Fact]
    public void LookupsAreCaseInsensitive()
    {
        var index = SyntheticIndex();

        Assert.True(index.TryResolve("FINANCIALS", "segment", out var kind));
        Assert.Equal(SemanticFieldKind.Column, kind);

        Assert.True(index.TryResolve("Financials", "DISCOUNT BAND_ORD2", out var measureKind));
        Assert.Equal(SemanticFieldKind.Measure, measureKind);

        Assert.True(index.TableExists("Financials"));
        Assert.True(index.ColumnExists("FINANCIALS", "Segment"));
        Assert.Contains("Segment", index.GetColumns("FINANCIALS"));
    }

    [Fact]
    public void LookupOnAnUnknownEntityOrNullNameDoesNotThrow()
    {
        var index = SyntheticIndex();

        Assert.False(index.TryResolve("no_such_table", "Segment", out _));
        Assert.False(index.TryResolve(null!, null!, out _));
        Assert.Empty(index.GetColumns("no_such_table"));
        Assert.Empty(index.GetMeasures("no_such_table"));

        // An entity with no measures yields an empty list, not null; the demo model does have one.
        Assert.Contains("Discount Band_ord2", index.GetMeasures("financials"));
        Assert.DoesNotContain("Discount Band_ord2", index.GetColumns("financials"));
    }

    // --------------------------------------------------------- Private auto tables

    [Fact]
    public void Load_IncludesPrivateTablesByDefault()
    {
        // A visual can legitimately reference an auto-generated date table column, so the
        // index must resolve it instead of reporting the visual as broken.
        var index = SyntheticIndex();

        Assert.True(index.TableExists(PrivateTable));
        Assert.True(index.TryResolve(PrivateTable, "Date", out var kind));
        Assert.Equal(SemanticFieldKind.Column, kind);

        // Those columns are often calculated (`column Year = YEAR([Date])`).
        Assert.True(index.TryResolve(PrivateTable, "Year", out var calculatedKind));
        Assert.Equal(SemanticFieldKind.Column, calculatedKind);
    }

    [Fact]
    public void Load_ExcludesPrivateTablesWhenAsked()
    {
        var index = SyntheticIndex(includePrivate: false);

        Assert.True(index.HasTmdlDefinition);
        Assert.False(index.TableExists(PrivateTable));
        Assert.Contains("financials", index.TableNames);

        // Hidden-but-not-private tables stay, matching Power BI's model view.
        Assert.True(index.TableExists(HiddenTable));
    }

    // ------------------------------------------------------------------ Pickers

    [Fact]
    public void GetColumns_And_GetMeasures_AreGroupedByTable()
    {
        var index = SemanticModelIndex.Load(CreateModel(
            ("a.tmdl", Tmdl(
                "table a",
                "",
                "\tcolumn Zebra",
                "\t\tdataType: string",
                "",
                "\tcolumn Alpha",
                "\t\tdataType: string",
                "",
                "\tmeasure 'Total A'",
                "\t\tformatString: 0")),
            ("b.tmdl", Tmdl(
                "table b",
                "",
                "\tcolumn Beta",
                "\t\tdataType: string"))));

        Assert.Equal(new[] { "Alpha", "Zebra" }, index.GetColumns("a"));
        Assert.Equal(new[] { "Total A" }, index.GetMeasures("a"));
        Assert.Equal(new[] { "Beta" }, index.GetColumns("b"));
        Assert.Empty(index.GetMeasures("b"));
    }

    // -------------------------------------------------------- Cosmetic-rename fix

    [Fact]
    public void NormalizeName_IgnoresCaseWhitespaceAndPunctuation()
    {
        Assert.Equal("grosssales", SemanticModelIndex.NormalizeName("Gross Sales"));
        Assert.Equal("grosssales", SemanticModelIndex.NormalizeName("gross_sales"));
        Assert.Equal("grosssales", SemanticModelIndex.NormalizeName("GROSS-SALES"));
        Assert.Equal("grosssales", SemanticModelIndex.NormalizeName("GrossSales"));
        Assert.Equal("discountband", SemanticModelIndex.NormalizeName("  Discount Band  "));
        Assert.Equal(string.Empty, SemanticModelIndex.NormalizeName("   "));
        Assert.Equal(string.Empty, SemanticModelIndex.NormalizeName(null!));
    }

    [Fact]
    public void TryResolveNormalized_MatchesACosmeticRename()
    {
        var index = SyntheticIndex();

        Assert.True(index.TryResolveNormalized("financials", "grosssales", out var entity, out var property, out var kind));
        Assert.Equal("financials", entity);
        Assert.Equal("Gross Sales", property);
        Assert.Equal(SemanticFieldKind.Column, kind);

        // The raw name works too: the argument is normalized internally.
        Assert.True(index.TryResolveNormalized("financials", "Gross_Sales", out _, out _, out _));
    }

    [Fact]
    public void TryResolveNormalized_IsCaseInsensitive()
    {
        var index = SyntheticIndex();

        Assert.True(index.TryResolveNormalized("FINANCIALS", "GROSSSALES", out var entity, out var property, out _));
        Assert.Equal("financials", entity);
        Assert.Equal("Gross Sales", property);
    }

    [Fact]
    public void TryResolveNormalized_DoesNotMatchAGenuinelyDifferentField()
    {
        var index = SyntheticIndex();

        Assert.False(index.TryResolveNormalized("financials", "grossprofit", out var entity, out var property, out _));
        Assert.Equal(string.Empty, entity);
        Assert.Equal(string.Empty, property);

        Assert.False(index.TryResolveNormalized("financials", "salesandcosts", out _, out _, out _));
        Assert.False(index.TryResolveNormalized("financials", "   ", out _, out _, out _));
    }

    [Fact]
    public void TryResolveNormalized_MatchesAMeasure()
    {
        var index = SemanticModelIndex.Load(CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn Net",
            "\t\tdataType: double",
            "",
            "\tmeasure 'Total Net' = SUM(t[Net])",
            "\t\tformatString: 0"))));

        Assert.True(index.TryResolveNormalized("t", "totalnet", out var entity, out var property, out var kind));
        Assert.Equal("t", entity);
        Assert.Equal("Total Net", property);
        Assert.Equal(SemanticFieldKind.Measure, kind);
    }

    [Fact]
    public void TryResolveNormalized_PrefersAColumnOverACollidingMeasure()
    {
        // A real collision in the demo model: the stub measure 'Discount Band_ord2' and the
        // calculated column 'Discount Band_ord_2' normalize to the same key. The documented
        // tie-break sends the suggestion to the column, because report references are mostly
        // columns — and a suggestion is only ever a suggestion.
        var index = SyntheticIndex();

        Assert.True(index.TryResolveNormalized("financials", "discountbandord2", out _, out var property, out var kind));
        Assert.Equal("Discount Band_ord_2", property);
        Assert.Equal(SemanticFieldKind.Column, kind);
    }

    [Fact]
    public void TryResolveNormalized_PrefersTheSameTable()
    {
        var index = SemanticModelIndex.Load(CreateModel(
            ("a.tmdl", Tmdl(
                "table a",
                "",
                "\tcolumn 'Net Sales'",
                "\t\tdataType: double",
                "",
                "\tmeasure 'Net Sales'",
                "\t\tformatString: 0")),
            ("b.tmdl", Tmdl(
                "table b",
                "",
                "\tcolumn 'Netsales'",
                "\t\tdataType: double"))));

        // A same-table column wins even though another table also normalizes to the same key.
        Assert.True(index.TryResolveNormalized("a", "netsales", out var entity, out var property, out var kind));
        Assert.Equal("a", entity);
        Assert.Equal("Net Sales", property);
        Assert.Equal(SemanticFieldKind.Column, kind);
    }

    [Fact]
    public void TryResolveNormalized_FallsBackToAnotherTable()
    {
        var index = SemanticModelIndex.Load(CreateModel(
            ("a.tmdl", Tmdl(
                "table a",
                "",
                "\tcolumn Cost",
                "\t\tdataType: double")),
            ("b.tmdl", Tmdl(
                "table b",
                "",
                "\tcolumn 'NetRevenue'",
                "\t\tdataType: double"))));

        // Nothing in 'a' normalizes to 'netrevenue', so the suggestion comes from 'b' — the
        // signature of a table that moved or was renamed along with its columns.
        Assert.True(index.TryResolveNormalized("a", "netrevenue", out var entity, out var property, out _));
        Assert.Equal("b", entity);
        Assert.Equal("NetRevenue", property);

        // A cross-table suggestion is opt-out for callers that would rather report nothing.
        Assert.False(index.TryResolveNormalized("a", "netrevenue", allowOtherTables: false, out _, out _, out _));

        // Same table as requested: no cross-table fallback needed.
        Assert.True(index.TryResolveNormalized("b", "netrevenue", allowOtherTables: false, out var ownEntity, out _, out _));
        Assert.Equal("b", ownEntity);
    }

    [Fact]
    public void TryResolveNormalized_FollowsARenamedTable()
    {
        var index = SemanticModelIndex.Load(CreateModel(("t.tmdl", Tmdl(
            "table 'Sales Data'",
            "",
            "\tcolumn 'Net Revenue'",
            "\t\tdataType: double"))));

        Assert.True(index.TryResolveNormalized("SalesData", "netrevenue", out var entity, out var property, out _));
        Assert.Equal("Sales Data", entity);
        Assert.Equal("Net Revenue", property);
    }

    [Fact]
    public void TryResolveNormalized_RefusesToGuessAnAmbiguousTableName()
    {
        var index = SemanticModelIndex.Load(CreateModel(
            ("a.tmdl", Tmdl(
                "table 'Sales Data'",
                "",
                "\tcolumn Net",
                "\t\tdataType: double")),
            ("b.tmdl", Tmdl(
                "table SalesData",
                "",
                "\tcolumn Net",
                "\t\tdataType: double"))));

        // Exact lookups still work for both tables...
        Assert.True(index.ColumnExists("SalesData", "Net"));
        Assert.True(index.ColumnExists("Sales Data", "Net"));

        // ...but a normalized entity name that matches two tables must not pick one of them:
        // callers that want no cross-table guess pass allowOtherTables: false.
        Assert.False(index.TryResolveNormalized("Sales_Data", "net", allowOtherTables: false, out _, out _, out _));

        // With the fallback on, the field itself is real, so a suggestion is still offered —
        // deterministically, from the first table in name order.
        Assert.True(index.TryResolveNormalized("Sales_Data", "net", out var entity, out var property, out _));
        Assert.Equal("Sales Data", entity);
        Assert.Equal("Net", property);
    }

    // --------------------------------------------------------------- Robustness

    [Fact]
    public void Load_SkipsUnparseableFilesAndKeepsGoing()
    {
        var index = SemanticModelIndex.Load(CreateModel(
            ("empty.tmdl", string.Empty),
            ("noheader.tmdl", Tmdl(
                "\tcolumn Orphan",
                "\t\tdataType: string")),
            ("nameless.tmdl", Tmdl(
                "table",
                "",
                "\tcolumn",
                "\t\tdataType: string",
                "",
                "\tmeasure",
                "\t\tformatString: 0")),
            ("good.tmdl", Tmdl(
                "table good",
                "",
                "\tcolumn Fine",
                "\t\tdataType: string"))));

        Assert.True(index.HasTmdlDefinition);
        Assert.Equal(new[] { "good" }, index.TableNames);
        Assert.True(index.ColumnExists("good", "Fine"));
        Assert.False(index.TableExists("Orphan"));
    }

    [Fact]
    public void Load_SurvivesAnUnterminatedExpressionFence()
    {
        var index = SemanticModelIndex.Load(CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn Broken = ```",
            "\t\t\tSUM(t[Sales])"))));

        Assert.True(index.HasTmdlDefinition);
        Assert.True(index.ColumnExists("t", "Broken"));
    }

    [Fact]
    public void Load_SurvivesGarbageContent()
    {
        var index = SemanticModelIndex.Load(CreateModel(
            ("a.tmdl", "\0\0 not tmdl at all \r\n\ttable ???\r\n"),
            ("b.tmdl", Tmdl(
                "table 'weird name with spaces'",
                "",
                "\tcolumn 'column'",
                "\t\tdataType: string",
                "",
                "\tcolumn 'a''b'",
                "\t\tdataType: string"))));

        Assert.True(index.HasTmdlDefinition);
        Assert.True(index.TableExists("weird name with spaces"));

        // TMDL escapes a quote by doubling it, so both round-trip to one quote.
        Assert.True(index.TryResolve("weird name with spaces", "column", out _));
        Assert.True(index.TryResolve("weird name with spaces", "a'b", out _));
    }

    [Fact]
    public void Load_MergesTwoFilesThatDeclareTheSameTable()
    {
        var index = SemanticModelIndex.Load(CreateModel(
            ("a-part1.tmdl", Tmdl(
                "table t",
                "",
                "\tcolumn First",
                "\t\tdataType: string")),
            ("a-part2.tmdl", Tmdl(
                "table t",
                "",
                "\tcolumn Second",
                "\t\tdataType: string"))));

        Assert.Single(index.TableNames);
        Assert.True(index.ColumnExists("t", "First"));
        Assert.True(index.ColumnExists("t", "Second"));
    }

    [Fact]
    public void Load_ExcludesAPrivateTableEvenWhenItIsNamedAlike()
    {
        var index = SemanticModelIndex.Load(CreateModel(("t.tmdl", Tmdl(
            "table t",
            "\tisHidden",
            "\tisPrivate",
            "",
            "\tcolumn Date",
            "\t\tdataType: dateTime"))),
            includePrivate: false);

        Assert.False(index.HasTmdlDefinition);
        Assert.Empty(index.TableNames);
    }
}
