using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SingularTools.Core;
using Xunit;

namespace SingularTools.Tests;

public sealed class SortByColumnServiceTests : IDisposable
{
    private readonly List<string> _tempRoots = new();

    public void Dispose()
    {
        foreach (var root in _tempRoots)
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    // ------------------------------------------------------------- Fixtures

    private static string Tmdl(params string[] lines) => string.Join("\n", lines) + "\n";

    /// <summary>Creates a throwaway TMDL model and returns the .SemanticModel folder.</summary>
    private string CreateModel(params (string Name, string Content)[] files)
    {
        var root = Path.Combine(Path.GetTempPath(), "singular-sortby-" + Guid.NewGuid().ToString("N"));
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

    private static string GetDemoReportPath()
    {
        var candidates = new[] { string.Empty, Path.Combine("Assets", "Test_PBI_Report") };
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

    /// <summary>A table whose two order columns are still plain strings and unsorted.</summary>
    private static string UnsortedTable => Tmdl(
        "table financials",
        "\tlineageTag: 4158b95d-186b-4a29-b15b-1623931baf56",
        "",
        "\tcolumn 'Discount Band'",
        "\t\tdataType: string",
        "\t\tlineageTag: ec65e3db-498c-4454-9027-834af4383c6f",
        "\t\tsummarizeBy: none",
        "\t\tsourceColumn: Discount Band",
        "",
        "\t\tannotation SummarizationSetBy = Automatic",
        "",
        "\tcolumn 'Discount Band_ord'",
        "\t\tdataType: string",
        "\t\tlineageTag: a926fe13-96a8-4719-8297-ff1f391a178e",
        "\t\tsummarizeBy: none",
        "\t\tsourceColumn: Discount Band_ord",
        "",
        "\t\tannotation SummarizationSetBy = Automatic",
        "",
        "\tpartition financials = m",
        "\t\tmode: import",
        "",
        "\tannotation PBI_ResultType = Table");

    private static string SortedTable => Tmdl(
        "table financials",
        "\tlineageTag: 4158b95d-186b-4a29-b15b-1623931baf56",
        "",
        "\tcolumn 'Discount Band'",
        "\t\tdataType: string",
        "\t\tlineageTag: ec65e3db-498c-4454-9027-834af4383c6f",
        "\t\tsummarizeBy: none",
        "\t\tsourceColumn: Discount Band",
        "\t\tsortByColumn: 'Discount Band_ord'",
        "",
        "\t\tchangedProperty = SortByColumn",
        "",
        "\t\tannotation SummarizationSetBy = Automatic",
        "",
        "\tcolumn 'Discount Band_ord'",
        "\t\tdataType: int64",
        "\t\tformatString: 0",
        "\t\tlineageTag: a926fe13-96a8-4719-8297-ff1f391a178e",
        "\t\tsummarizeBy: none",
        "\t\tsourceColumn: Discount Band_ord",
        "",
        "\t\tchangedProperty = DataType",
        "",
        "\t\tannotation SummarizationSetBy = Automatic",
        "",
        "\tpartition financials = m",
        "\t\tmode: import",
        "",
        "\tannotation PBI_ResultType = Table");

    // --------------------------------------------------------------- Discovery

    [Fact]
    public void DiscoverModelFolder_FindsSiblingOfReport()
    {
        var folder = SortByColumnService.DiscoverModelFolder(GetDemoReportPath());

        Assert.NotNull(folder);
        Assert.EndsWith("Demo PBI Report.SemanticModel", folder);
        Assert.True(Directory.Exists(Path.Combine(folder!, "definition", "tables")));
    }

    [Fact]
    public void LoadModel_ReadsDemoTablesAndColumns()
    {
        var folder = SortByColumnService.DiscoverModelFolder(GetDemoReportPath())!;
        var model = SortByColumnService.LoadModel(folder);

        Assert.True(model.HasTmdlDefinition);

        // Structural only: the demo model is edited in place by its author, so its
        // sort-by/type state is not asserted here (that is covered synthetically).
        var financials = model.Tables.Single(t => t.Name == "financials");
        Assert.Contains(financials.Columns, c => c.Name == "Discount Band");
        Assert.Contains(financials.Columns, c => c.Name == "Discount Band_ord");
        Assert.Equal("string", financials.Columns.Single(c => c.Name == "Discount Band").DataType);
    }

    // ---------------------------------------------------------------- Planning

    [Fact]
    public void BuildPlan_OnDemoReport_PairsDiscountBand()
    {
        var model = SortByColumnService.LoadModel(SortByColumnService.DiscoverModelFolder(GetDemoReportPath())!);
        var plan = SortByColumnService.BuildPlan(model, "_ord");

        var pair = Assert.Single(plan.Pairs, p => p.BaseColumn == "Discount Band");
        Assert.Equal("Discount Band_ord", pair.OrderColumn);
    }

    [Fact]
    public void BuildPlan_ReportsUnmatchedOrderColumns()
    {
        var model = SortByColumnService.LoadModel(CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn Name",
            "\t\tdataType: string",
            "",
            "\tcolumn Orphan_ord",
            "\t\tdataType: string"))));

        var plan = SortByColumnService.BuildPlan(model, "_ord");

        Assert.Empty(plan.Pairs);
        var unmatched = Assert.Single(plan.Unmatched);
        Assert.Equal("Orphan_ord", unmatched.ColumnName);
    }

    [Fact]
    public void BuildPlan_FlagsCalculatedColumns()
    {
        var model = SortByColumnService.LoadModel(CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn Data",
            "\t\tdataType: string",
            "",
            "\tcolumn Data_ord",
            "\t\tdataType: string",
            "",
            "\tcolumn CalcBase = \"x\"",
            "",
            "\tcolumn CalcBase_ord",
            "\t\tdataType: string",
            "",
            "\tcolumn Calc = \"y\"",
            "",
            "\tcolumn Calc_ord = 1"))));

        var plan = SortByColumnService.BuildPlan(model, "_ord");

        Assert.False(plan.Pairs.Single(p => p.BaseColumn == "Data").IsCalculatedPair);
        Assert.False(plan.Pairs.Single(p => p.BaseColumn == "Data").BaseIsCalculated);

        Assert.True(plan.Pairs.Single(p => p.BaseColumn == "CalcBase").BaseIsCalculated);

        var calculated = plan.Pairs.Single(p => p.BaseColumn == "Calc");
        Assert.True(calculated.OrderIsCalculated);
        Assert.True(calculated.IsCalculatedPair);
    }

    [Fact]
    public void BuildPlan_EmptySuffix_ReturnsNothing()
    {
        var model = SortByColumnService.LoadModel(CreateModel(("t.tmdl", UnsortedTable)));
        var plan = SortByColumnService.BuildPlan(model, "   ");

        Assert.Empty(plan.Pairs);
        Assert.Empty(plan.Unmatched);
    }

    // ----------------------------------------------------------------- Applying

    [Fact]
    public void Apply_WritesExactlyWhatPowerBiWrites()
    {
        var modelFolder = CreateModel(("financials.tmdl", UnsortedTable));
        var file = Path.Combine(modelFolder, "definition", "tables", "financials.tmdl");
        var model = SortByColumnService.LoadModel(modelFolder);
        var plan = SortByColumnService.BuildPlan(model, "_ord");

        var result = SortByColumnService.Apply(model, plan.Pairs, createBackup: false);

        Assert.Equal(1, result.FilesWritten);
        Assert.Equal(1, result.ColumnsChanged);
        Assert.Equal(SortedTable, File.ReadAllText(file));
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var modelFolder = CreateModel(("financials.tmdl", UnsortedTable));
        var file = Path.Combine(modelFolder, "definition", "tables", "financials.tmdl");

        var first = SortByColumnService.LoadModel(modelFolder);
        SortByColumnService.Apply(first, SortByColumnService.BuildPlan(first, "_ord").Pairs, createBackup: false);

        var afterFirst = File.ReadAllText(file);

        var second = SortByColumnService.LoadModel(modelFolder);
        var plan = SortByColumnService.BuildPlan(second, "_ord");
        var result = SortByColumnService.Apply(second, plan.Pairs, createBackup: false);

        Assert.True(plan.Pairs.Single().AlreadyApplied);
        Assert.Equal(0, result.FilesWritten);
        Assert.Equal(afterFirst, File.ReadAllText(file));
    }

    [Fact]
    public void Apply_SetsDataTypeOnCalculatedOrderColumn()
    {
        var modelFolder = CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn Name",
            "\t\tdataType: string",
            "\t\tsourceColumn: Name",
            "",
            "\tcolumn Name_ord = 1")));

        var model = SortByColumnService.LoadModel(modelFolder);
        var plan = SortByColumnService.BuildPlan(model, "_ord");
        SortByColumnService.Apply(model, plan.Pairs, createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "tables", "t.tmdl"));

        Assert.Contains("\tcolumn Name_ord = 1\n", text);
        Assert.Contains("\t\tdataType: int64\n", text);
        Assert.Contains("\t\tformatString: 0\n", text);
        Assert.Contains("\t\tchangedProperty = DataType\n", text);
        Assert.Contains("\t\tsortByColumn: Name_ord\n", text);
        Assert.Contains("\t\tchangedProperty = SortByColumn\n", text);
    }

    [Fact]
    public void Apply_OverwritesAnExistingSortByColumn()
    {
        var modelFolder = CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn Name",
            "\t\tdataType: string",
            "\t\tsourceColumn: Name",
            "\t\tsortByColumn: 'Legacy'",
            "",
            "\tcolumn Name_ord",
            "\t\tdataType: string")));

        var model = SortByColumnService.LoadModel(modelFolder);
        var plan = SortByColumnService.BuildPlan(model, "_ord");
        SortByColumnService.Apply(model, plan.Pairs, createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "tables", "t.tmdl"));

        Assert.Contains("\t\tsortByColumn: Name_ord\n", text);
        Assert.DoesNotContain("Legacy", text);
    }

    [Fact]
    public void Apply_OnlyTouchesSelectedPairs()
    {
        var modelFolder = CreateModel(("t.tmdl", Tmdl(
            "table t",
            "",
            "\tcolumn A",
            "\t\tdataType: string",
            "",
            "\tcolumn A_ord",
            "\t\tdataType: string",
            "",
            "\tcolumn B",
            "\t\tdataType: string",
            "",
            "\tcolumn B_ord",
            "\t\tdataType: string")));

        var model = SortByColumnService.LoadModel(modelFolder);
        var plan = SortByColumnService.BuildPlan(model, "_ord");

        // Apply only the A pair.
        SortByColumnService.Apply(model, plan.Pairs.Where(p => p.BaseColumn == "A"), createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "tables", "t.tmdl"));

        Assert.Contains("\t\tsortByColumn: A_ord\n", text);
        Assert.DoesNotContain("sortByColumn: B_ord", text);
    }

    // ----------------------------------------------------------------- Backups

    [Fact]
    public void RestoreLatestBackup_RevertsTheApply()
    {
        var modelFolder = CreateModel(("financials.tmdl", UnsortedTable));
        var projectRoot = _tempRoots.Single();
        var file = Path.Combine(modelFolder, "definition", "tables", "financials.tmdl");

        var model = SortByColumnService.LoadModel(modelFolder);
        SortByColumnService.Apply(model, SortByColumnService.BuildPlan(model, "_ord").Pairs,
                                  createBackup: true, projectRoot: projectRoot);

        Assert.NotEqual(UnsortedTable, File.ReadAllText(file));

        var restored = SortByColumnService.RestoreLatestBackup(projectRoot, out var from);

        Assert.Equal(1, restored);
        Assert.NotNull(from);
        Assert.Equal(UnsortedTable, File.ReadAllText(file));
        Assert.Null(SortByColumnService.LatestBackupFolder(projectRoot));
    }

    // ------------------------------------------------------------------ Quoting

    [Theory]
    [InlineData("Country", "Country")]
    [InlineData("Discount Band", "'Discount Band'")]
    [InlineData("Weird'Name", "'Weird''Name'")]
    [InlineData("1Number", "'1Number'")]
    public void QuoteName_QuotesOnlyWhenNeeded(string input, string expected)
    {
        Assert.Equal(expected, SortByColumnService.QuoteName(input));
    }
}
