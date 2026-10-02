using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SingularTools.Core;
using Xunit;

namespace SingularTools.Tests;

public sealed class OlsServiceTests : IDisposable
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

    private static string SalesTable => Tmdl(
        "table Sales",
        "\tlineageTag: 11111111-1111-1111-1111-111111111111",
        "",
        "\tcolumn Region",
        "\t\tdataType: string",
        "",
        "\tcolumn Amount",
        "\t\tdataType: double");

    private static string ProductTable => Tmdl(
        "table Product",
        "\tlineageTag: 22222222-2222-2222-2222-222222222222",
        "",
        "\tcolumn Name",
        "\t\tdataType: string");

    /// <summary>Creates a throwaway TMDL model, returning its .SemanticModel folder.</summary>
    private string CreateModel(
        IEnumerable<(string Name, string Content)>? tables = null,
        string? modelTmdl = null,
        string? relationships = null,
        IEnumerable<(string Name, string Content)>? roles = null)
    {
        var root = Path.Combine(Path.GetTempPath(), "singular-ols-" + Guid.NewGuid().ToString("N"));
        _tempRoots.Add(root);

        var modelFolder = Path.Combine(root, "Demo.SemanticModel");
        var definition = Path.Combine(modelFolder, "definition");
        var tablesDir = Path.Combine(definition, "tables");
        Directory.CreateDirectory(tablesDir);

        foreach (var table in tables ?? new[] { ("Sales.tmdl", SalesTable) })
        {
            File.WriteAllText(Path.Combine(tablesDir, table.Name), table.Content);
        }

        File.WriteAllText(Path.Combine(definition, "model.tmdl"),
            modelTmdl ?? Tmdl("model Model", "\tculture: en-US", "", "ref table Sales"));

        if (relationships != null)
        {
            File.WriteAllText(Path.Combine(definition, "relationships.tmdl"), relationships);
        }

        if (roles != null)
        {
            var rolesDir = Path.Combine(definition, "roles");
            Directory.CreateDirectory(rolesDir);
            foreach (var role in roles)
            {
                File.WriteAllText(Path.Combine(rolesDir, role.Name), role.Content);
            }
        }

        return modelFolder;
    }

    // ---------------------------------------------------------------- Parsing

    [Fact]
    public void ParseRoleText_ReadsTableAndColumnOls()
    {
        var role = OlsService.ParseRoleText(Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Product",
            "\t\tmetadataPermission: none",
            "",
            "\ttablePermission Sales",
            "\t\tcolumnPermission Amount = none"));

        Assert.NotNull(role);
        Assert.Equal("Restricted", role!.Name);
        Assert.Equal("read", role.ModelPermission);
        Assert.False(role.IsRls);
        Assert.True(role.IsOls);
        Assert.Equal(1, role.HiddenTableCount);
        Assert.Equal(1, role.HiddenColumnCount);

        var product = role.TablePermissions.Single(t => t.Name == "Product");
        Assert.Equal(OlsPermission.None, product.Permission);

        var sales = role.TablePermissions.Single(t => t.Name == "Sales");
        Assert.Equal(OlsPermission.Default, sales.Permission);
        Assert.Equal(OlsPermission.None, sales.Columns.Single().Permission);
    }

    [Fact]
    public void ParseRoleText_DetectsRlsAndMembers()
    {
        var role = OlsService.ParseRoleText(Tmdl(
            "role StoreRole",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Store = 'Store'[Store Code] IN {\"1\",\"2\"}",
            "",
            "\tmember 'user1@company.com'",
            "\tmember 'group@domain.com' = group"));

        Assert.NotNull(role);
        Assert.True(role!.IsRls);
        Assert.False(role.IsOls);
        Assert.Equal(2, role.MemberLines.Count);
    }

    [Fact]
    public void ParseRoleText_QuotedRoleAndColumnNames()
    {
        var role = OlsService.ParseRoleText(Tmdl(
            "role 'HR Reader'",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Employee",
            "\t\tcolumnPermission 'Base Rate' = none"));

        Assert.NotNull(role);
        Assert.Equal("HR Reader", role!.Name);
        Assert.Equal("Base Rate", role.TablePermissions.Single().Columns.Single().Name);
        Assert.Equal(OlsPermission.None, role.TablePermissions.Single().Columns.Single().Permission);
    }

    [Fact]
    public void SerializeRole_QuotesOnlyWhenNeeded()
    {
        var role = new OlsRole { Name = "Restricted Users", ModelPermission = "read" };
        var table = new OlsTableRule { Name = "Sales" };
        table.Columns.Add(new OlsColumnRule { Name = "Unit Price", Permission = OlsPermission.None });
        role.TablePermissions.Add(table);

        var text = OlsService.SerializeRole(role);

        Assert.Contains("role 'Restricted Users'\n", text);
        Assert.Contains("\ttablePermission Sales\n", text);
        Assert.Contains("\t\tcolumnPermission 'Unit Price' = none\n", text);
    }

    [Fact]
    public void SerializeRole_PreservesMembersAndAnnotations()
    {
        var role = OlsService.ParseRoleText(Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\tannotation PBI_ProTooling = [\"X\"]",
            "",
            "\ttablePermission Product",
            "\t\tmetadataPermission: none",
            "",
            "\tmember 'a@b.com'"))!;

        var text = OlsService.SerializeRole(role);

        Assert.Contains("annotation PBI_ProTooling", text);
        Assert.Contains("member 'a@b.com'", text);
        Assert.Contains("metadataPermission: none", text);
    }

    // ------------------------------------------------------------------ Apply

    [Fact]
    public void Apply_PersistedRulesSurviveAReload()
    {
        // Regression: after applying, reopening must show the rules as staged.
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read")) });

        var model = OlsService.LoadModel(modelFolder);
        OlsService.Apply(model, new[]
        {
            new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Column, TableName = "Sales", ColumnName = "Amount", Desired = OlsPermission.None },
            new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Column, TableName = "Sales", ColumnName = "Region", Desired = OlsPermission.None }
        }, createBackup: false);

        // New session: parse fresh from disk.
        var reloaded = OlsService.LoadModel(modelFolder).Roles.Single(r => r.Name == "Restricted");
        var sales = reloaded.TablePermissions.Single(t => t.Name == "Sales");

        Assert.Equal(OlsPermission.None, sales.Columns.Single(c => c.Name == "Amount").Permission);
        Assert.Equal(OlsPermission.None, sales.Columns.Single(c => c.Name == "Region").Permission);
        Assert.Equal(2, reloaded.HiddenColumnCount);
    }

    [Fact]
    public void Apply_RevealsAnAppliedColumn()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Sales",
            "\t\tcolumnPermission Amount = none")) });

        var model = OlsService.LoadModel(modelFolder);
        OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Column, TableName = "Sales", ColumnName = "Amount", Desired = OlsPermission.Read } },
            createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl"));

        // An explicit read is recorded rather than removing the block outright.
        Assert.Contains("columnPermission Amount = read", text);

        var reparsed = OlsService.LoadModel(modelFolder).Roles.Single();
        Assert.Equal(OlsPermission.Read,
            reparsed.TablePermissions.Single().Columns.Single().Permission);
        Assert.False(reparsed.IsOls);
    }

    [Fact]
    public void LoadModel_ExcludesOnlyPrivateTables()
    {
        var modelFolder = CreateModel(
            tables: new[]
            {
                ("financials.tmdl", SalesTable),
                ("LocalDateTable_abc.tmdl", Tmdl(
                    "table LocalDateTable_abc",
                    "\tisHidden",
                    "\tshowAsVariationsOnly",
                    "",
                    "\tcolumn Date",
                    "\t\tdataType: dateTime")),
                ("DateTableTemplate_x.tmdl", Tmdl(
                    "table DateTableTemplate_x",
                    "\tisHidden",
                    "\tisPrivate",
                    "",
                    "\tcolumn Date",
                    "\t\tdataType: dateTime"))
            },
            modelTmdl: Tmdl("model Model", "ref table financials", "ref table LocalDateTable_abc", "ref table DateTableTemplate_x"));

        // Only isPrivate is excluded; a merely-hidden table is still shown, matching
        // Power BI's model view. Filtering is central, so both tools agree.
        var shared = SortByColumnService.LoadModel(modelFolder);
        Assert.Equal(new[] { "LocalDateTable_abc", "Sales" }, shared.Tables.Select(t => t.Name).OrderBy(n => n));

        var model = OlsService.LoadModel(modelFolder);
        Assert.Equal(new[] { "LocalDateTable_abc", "Sales" }, model.Tables.Select(t => t.Name).OrderBy(n => n));
        Assert.DoesNotContain(model.Tables, t => t.IsPrivate);
    }

    [Fact]
    public void PreviewRole_ShowsOlsPlusRlsForStagedChange()
    {
        var role = new OlsRole { Name = "R1", ModelPermission = "read" };
        var sales = new OlsTableRule { Name = "financials", FilterInline = "[Country] == \"Canada\"" };
        role.TablePermissions.Add(sales);

        Assert.True(role.IsRls);
        Assert.False(role.IsOls);
        Assert.Equal("RLS", role.KindLabel);

        var preview = OlsService.PreviewRole(role, new[]
        {
            new OlsChange { RoleName = "R1", Kind = OlsObjectKind.Column, TableName = "financials", ColumnName = "Country", Desired = OlsPermission.None }
        });

        Assert.True(preview.IsRls);
        Assert.True(preview.IsOls);
        Assert.Equal("OLS + RLS", preview.KindLabel);
        Assert.Equal(1, preview.HiddenColumnCount);

        // The original role is untouched (preview is a clone).
        Assert.False(role.IsOls);
    }

    [Fact]
    public void Apply_HidesTableWithExactPowerBiShape()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Product")) });

        var model = OlsService.LoadModel(modelFolder);
        var result = OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Table, TableName = "Product", Desired = OlsPermission.None } },
            createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl"));

        Assert.Equal(1, result.Changes);
        Assert.Equal(Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Product",
            "\t\tmetadataPermission: none"), text);
    }

    [Fact]
    public void Apply_HidesColumnCreatingTableBlock()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read")) });

        var model = OlsService.LoadModel(modelFolder);
        OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Column, TableName = "Sales", ColumnName = "Amount", Desired = OlsPermission.None } },
            createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl"));

        // Column permissions use the TMDL default-property shorthand.
        Assert.Equal(Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Sales",
            "\t\tcolumnPermission Amount = none"), text);

        // The shorthand must be read back as a real OLS rule.
        var reparsed = OlsService.LoadModel(modelFolder).Roles.Single();
        Assert.Equal(OlsPermission.None,
            reparsed.TablePermissions.Single().Columns.Single().Permission);
    }

    [Fact]
    public void Apply_IsIdempotent()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Product",
            "\t\tmetadataPermission: none")) });

        var model = OlsService.LoadModel(modelFolder);
        var result = OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Table, TableName = "Product", Desired = OlsPermission.None } },
            createBackup: false);

        Assert.Equal(0, result.Changes);
        Assert.Equal(0, result.FilesWritten);
    }

    [Fact]
    public void Apply_ClearingRuleRemovesBlocks()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Sales",
            "\t\tcolumnPermission Amount = none")) });

        var model = OlsService.LoadModel(modelFolder);
        OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Column, TableName = "Sales", ColumnName = "Amount", Desired = OlsPermission.Default } },
            createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl"));

        Assert.Equal(Tmdl("role Restricted", "\tmodelPermission: read"), text);
    }

    [Fact]
    public void Apply_LeavesUntouchedRolesByteForByte()
    {
        var untouched = "role Other\n\tmodelPermission: read\n\n\ttablePermission Product\n\t\tmetadataPermission: none\n";
        var modelFolder = CreateModel(roles: new[]
        {
            ("Restricted.tmdl", Tmdl("role Restricted", "\tmodelPermission: read")),
            ("Other.tmdl", untouched)
        });

        var model = OlsService.LoadModel(modelFolder);
        OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Table, TableName = "Product", Desired = OlsPermission.None } },
            createBackup: false);

        Assert.Equal(untouched, File.ReadAllText(Path.Combine(modelFolder, "definition", "roles", "Other.tmdl")));
    }

    [Fact]
    public void Apply_AddsOlsToAnRlsRole()
    {
        // A role may carry both RLS filters and OLS rules.
        var modelFolder = CreateModel(roles: new[] { ("R.tmdl", Tmdl(
            "role R",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Sales = [Region] = \"East\"")) });

        var model = OlsService.LoadModel(modelFolder);
        var result = OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "R", Kind = OlsObjectKind.Column, TableName = "Sales", ColumnName = "Amount", Desired = OlsPermission.None } },
            createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "roles", "R.tmdl"));

        Assert.Equal(1, result.Changes);
        Assert.Equal(Tmdl(
            "role R",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Sales = [Region] = \"East\"",
            "\t\tcolumnPermission Amount = none"), text);
    }

    // -------------------------------------------------------------- Role CRUD

    [Fact]
    public void CreateRole_WritesFileAndModelRef()
    {
        var modelFolder = CreateModel();
        var model = OlsService.LoadModel(modelFolder);

        OlsService.CreateRole(model, "Restricted Users", createBackup: false);

        var rolePath = Path.Combine(modelFolder, "definition", "roles", "Restricted Users.tmdl");
        Assert.True(File.Exists(rolePath));
        Assert.Equal(Tmdl("role 'Restricted Users'", "\tmodelPermission: read"), File.ReadAllText(rolePath));
        Assert.Contains("ref role 'Restricted Users'", File.ReadAllText(Path.Combine(modelFolder, "definition", "model.tmdl")));

        var reloaded = OlsService.LoadModel(modelFolder);
        Assert.Single(reloaded.Roles);
        Assert.Equal("Restricted Users", reloaded.Roles[0].Name);
    }

    [Fact]
    public void DeleteRole_RemovesFileAndRef()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl("role Restricted", "\tmodelPermission: read")) });
        var model = OlsService.LoadModel(modelFolder);

        OlsService.DeleteRole(model, "Restricted", createBackup: false);

        Assert.False(File.Exists(Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl")));
        var modelText = File.ReadAllText(Path.Combine(modelFolder, "definition", "model.tmdl"));
        Assert.DoesNotContain("ref role Restricted", modelText);
        Assert.Contains("ref table Sales", modelText);
    }

    [Fact]
    public void RenameRole_UpdatesFileAndRef()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Product",
            "\t\tmetadataPermission: none")) });

        var model = OlsService.LoadModel(modelFolder);
        OlsService.RenameRole(model, "Restricted", "Locked", createBackup: false);

        Assert.False(File.Exists(Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl")));
        var rolePath = Path.Combine(modelFolder, "definition", "roles", "Locked.tmdl");
        Assert.True(File.Exists(rolePath));
        Assert.Contains("role Locked", File.ReadAllText(rolePath));
        Assert.Contains("metadataPermission: none", File.ReadAllText(rolePath));

        var modelText = File.ReadAllText(Path.Combine(modelFolder, "definition", "model.tmdl"));
        Assert.Contains("ref role Locked", modelText);
        Assert.DoesNotContain("ref role Restricted", modelText);
    }

    // -------------------------------------------------------------- Validation

    [Fact]
    public void Validate_NotesRlsRoleWithoutBlocking()
    {
        var modelFolder = CreateModel(roles: new[] { ("R.tmdl", Tmdl(
            "role R",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Sales = [Region] = \"East\"")) });

        var model = OlsService.LoadModel(modelFolder);
        var issues = OlsService.Validate(model,
            new[] { new OlsChange { RoleName = "R", Kind = OlsObjectKind.Column, TableName = "Sales", ColumnName = "Amount", Desired = OlsPermission.None } });

        Assert.DoesNotContain(issues, i => i.BlocksApply);
        Assert.Contains(issues, i => i.Severity == OlsValidationSeverity.Info);
    }

    [Fact]
    public void Apply_PreservesMultiLineFencedFilter()
    {
        var roleText = Tmdl(
            "role R",
            "\tmodelPermission: read",
            "",
            "\ttablePermission Sales = ```",
            "\t\tVAR c = SELECTEDVALUE('Region'[Name])",
            "\t\tRETURN [Region] = c",
            "\t\t```",
            "",
            "\tannotation PBI_Id = abc");

        var parsed = OlsService.ParseRoleText(roleText)!;
        Assert.True(parsed.IsRls);
        Assert.Equal("```", parsed.TablePermissions.Single().FilterInline);
        Assert.Equal(3, parsed.TablePermissions.Single().FilterLines.Count);

        var modelFolder = CreateModel(roles: new[] { ("R.tmdl", roleText) });
        var model = OlsService.LoadModel(modelFolder);

        OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "R", Kind = OlsObjectKind.Table, TableName = "Product", Desired = OlsPermission.None } },
            createBackup: false);

        var text = File.ReadAllText(Path.Combine(modelFolder, "definition", "roles", "R.tmdl"));

        Assert.Contains("VAR c = SELECTEDVALUE('Region'[Name])", text);
        Assert.Contains("tablePermission Sales = ```", text);
        Assert.Contains("annotation PBI_Id = abc", text);
        Assert.Contains("tablePermission Product", text);
        Assert.Contains("metadataPermission: none", text);
    }

    [Fact]
    public void Validate_WarnsWhenHidingRelationshipBridgeTable()
    {
        var relationships = Tmdl(
            "relationship r1",
            "\tfromColumn: A.Id",
            "\ttoColumn: B.AId",
            "",
            "relationship r2",
            "\tfromColumn: B.Id",
            "\ttoColumn: C.BId");

        var modelFolder = CreateModel(
            tables: new[]
            {
                ("A.tmdl", Tmdl("table A", "\tcolumn Id", "\t\tdataType: int64")),
                ("B.tmdl", Tmdl("table B", "\tcolumn Id", "\t\tdataType: int64")),
                ("C.tmdl", Tmdl("table C", "\tcolumn Id", "\t\tdataType: int64"))
            },
            modelTmdl: Tmdl("model Model", "ref table A", "ref table B", "ref table C"),
            relationships: relationships,
            roles: new[] { ("R.tmdl", Tmdl("role R", "\tmodelPermission: read")) });

        var model = OlsService.LoadModel(modelFolder);

        var bridgeIssues = OlsService.Validate(model,
            new[] { new OlsChange { RoleName = "R", Kind = OlsObjectKind.Table, TableName = "B", Desired = OlsPermission.None } });
        Assert.Contains(bridgeIssues, i => i.Severity == OlsValidationSeverity.Warning);

        var leafIssues = OlsService.Validate(model,
            new[] { new OlsChange { RoleName = "R", Kind = OlsObjectKind.Table, TableName = "A", Desired = OlsPermission.None } });
        Assert.DoesNotContain(leafIssues, i => i.Severity == OlsValidationSeverity.Warning);
    }

    // --------------------------------------------------------------- Backups

    [Fact]
    public void RestoreLatestBackup_RevertsApply()
    {
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl(
            "role Restricted",
            "\tmodelPermission: read")) });

        var projectRoot = _tempRoots.Single();
        var rolePath = Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl");
        var before = File.ReadAllText(rolePath);

        var model = OlsService.LoadModel(modelFolder);
        OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Table, TableName = "Product", Desired = OlsPermission.None } },
            createBackup: true, projectRoot: projectRoot);

        Assert.NotEqual(before, File.ReadAllText(rolePath));

        var restored = OlsService.RestoreLatestBackup(projectRoot, out var from);

        Assert.True(restored >= 1);
        Assert.NotNull(from);
        Assert.Equal(before, File.ReadAllText(rolePath));
        Assert.Null(OlsService.LatestBackupFolder(projectRoot));
    }

    [Fact]
    public void RestoreLatestBackup_UndoesRoleCreation()
    {
        var modelFolder = CreateModel();
        var projectRoot = _tempRoots.Single();
        var modelText = File.ReadAllText(Path.Combine(modelFolder, "definition", "model.tmdl"));

        var model = OlsService.LoadModel(modelFolder);
        OlsService.CreateRole(model, "Restricted", createBackup: true, projectRoot: projectRoot);

        var rolePath = Path.Combine(modelFolder, "definition", "roles", "Restricted.tmdl");
        Assert.True(File.Exists(rolePath));

        OlsService.RestoreLatestBackup(projectRoot, out _);

        Assert.False(File.Exists(rolePath));
        Assert.Equal(modelText, File.ReadAllText(Path.Combine(modelFolder, "definition", "model.tmdl")));
    }

    // ----------------------------------------------------------------- Misc

    [Theory]
    [InlineData("Restricted", "Restricted")]
    [InlineData("HR/Readers", "HR_Readers")]
    [InlineData("  spaced  ", "spaced")]
    public void SafeFileStem_Sanitizes(string input, string expected)
    {
        Assert.Equal(expected, OlsService.SafeFileStem(input));
    }

    [Fact]
    public void AddRoleRef_IsIdempotent()
    {
        var text = Tmdl("model Model", "ref table Sales");
        var once = OlsService.AddRoleRef(text, "Restricted");
        var twice = OlsService.AddRoleRef(once, "Restricted");

        Assert.Equal(once, twice);
        Assert.True(OlsService.HasRoleRef(once, "Restricted"));
    }

    [Fact]
    public void Apply_RepairsMissingModelRef()
    {
        // Role file exists but model.tmdl has no ref role line.
        var modelFolder = CreateModel(roles: new[] { ("Restricted.tmdl", Tmdl("role Restricted", "\tmodelPermission: read")) });
        var model = OlsService.LoadModel(modelFolder);

        OlsService.Apply(model,
            new[] { new OlsChange { RoleName = "Restricted", Kind = OlsObjectKind.Table, TableName = "Product", Desired = OlsPermission.None } },
            createBackup: false);

        Assert.Contains("ref role Restricted", File.ReadAllText(Path.Combine(modelFolder, "definition", "model.tmdl")));
    }

    [Fact]
    public void RemoveRoleRef_PreservesCrlf()
    {
        var text = "model Model\r\nref table Sales\r\nref role Restricted\r\nref cultureInfo en-US\r\n";
        var result = OlsService.RemoveRoleRef(text, "Restricted");

        Assert.DoesNotContain("ref role Restricted", result);
        Assert.Contains("ref cultureInfo en-US\r\n", result);
        Assert.DoesNotContain("\r\r", result);
    }
}
