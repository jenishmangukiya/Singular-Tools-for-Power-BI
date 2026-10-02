using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using SingularTools.Core.Models;

namespace SingularTools.Core;

/// <summary>Visibility of a table/column within a role.</summary>
public enum OlsPermission
{
    /// <summary>No explicit rule: the object is visible (metadataPermission read).</summary>
    Default,

    /// <summary>Explicitly visible (metadataPermission read).</summary>
    Read,

    /// <summary>Object-level security: metadataPermission none.</summary>
    None
}

public enum OlsObjectKind
{
    Table,
    Column
}

public enum OlsValidationSeverity
{
    Info,
    Warning,
    Error
}

/// <summary>One <c>columnPermission</c> inside a table permission.</summary>
public sealed class OlsColumnRule
{
    public string Name { get; set; } = string.Empty;
    public OlsPermission Permission { get; set; } = OlsPermission.Default;

    /// <summary>Unmodelled nested lines (e.g. annotations), preserved verbatim.</summary>
    public List<string> ExtraLines { get; } = new();
}

/// <summary>One <c>tablePermission</c>: an RLS filter, OLS table rule, or both.</summary>
public sealed class OlsTableRule
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Inline DAX row filter on the <c>tablePermission X = ...</c> line; null when absent.</summary>
    public string? FilterInline { get; set; }

    /// <summary>
    /// Continuation lines of a multi-line / fenced DAX filter, kept verbatim
    /// (including indentation and fences) so they round-trip exactly.
    /// </summary>
    public List<string> FilterLines { get; } = new();

    public bool HasFilter => FilterInline != null || FilterLines.Count > 0;

    public OlsPermission Permission { get; set; } = OlsPermission.Default;

    public List<OlsColumnRule> Columns { get; } = new();
    public List<string> ExtraLines { get; } = new();
}

/// <summary>A security role backed by one <c>definition/roles/*.tmdl</c> file.</summary>
public sealed class OlsRole
{
    public string Name { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public bool FileExists { get; set; }
    public string ModelPermission { get; set; } = "read";

    public List<OlsTableRule> TablePermissions { get; } = new();
    public List<string> AnnotationLines { get; } = new();
    public List<string> MemberLines { get; } = new();

    /// <summary>Unmodelled role-level lines, preserved verbatim.</summary>
    public List<string> ExtraLines { get; } = new();

    /// <summary>True when any table carries a DAX filter — an RLS role.</summary>
    public bool IsRls => TablePermissions.Any(t => t.HasFilter);

    /// <summary>True when any table or column is explicitly hidden.</summary>
    public bool IsOls => TablePermissions.Any(t => t.Permission == OlsPermission.None
                                                || t.Columns.Any(c => c.Permission == OlsPermission.None));

    public int HiddenTableCount => TablePermissions.Count(t => t.Permission == OlsPermission.None);

    public int HiddenColumnCount => TablePermissions.Sum(t => t.Columns.Count(c => c.Permission == OlsPermission.None));

    /// <summary>Short label for the role list: "OLS", "RLS", "OLS + RLS" or "Empty".</summary>
    public string KindLabel
    {
        get
        {
            if (IsRls && IsOls) return "OLS + RLS";
            if (IsRls) return "RLS";
            if (IsOls) return "OLS";
            return "Empty";
        }
    }
}

/// <summary>The semantic model plus its roles, as read for the OLS tool.</summary>
public sealed class OlsModel
{
    public string FolderPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public bool HasTmdlDefinition { get; init; }
    public IReadOnlyList<SemanticModelTable> Tables { get; init; } = Array.Empty<SemanticModelTable>();
    public IReadOnlyList<OlsRole> Roles { get; init; } = Array.Empty<OlsRole>();

    public string ModelFilePath { get; init; } = string.Empty;
    public string RolesDirectory { get; init; } = string.Empty;
    public bool HasRolesFolder { get; init; }

    public OlsRole? FindRole(string name) =>
        Roles.FirstOrDefault(r => string.Equals(r.Name, name, StringComparison.OrdinalIgnoreCase));

    public SemanticModelTable? FindTable(string name) =>
        Tables.FirstOrDefault(t => string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase));
}

/// <summary>A pending change staged by the UI and applied in one batch.</summary>
public sealed class OlsChange
{
    public string RoleName { get; init; } = string.Empty;
    public OlsObjectKind Kind { get; init; }
    public string TableName { get; init; } = string.Empty;
    public string ColumnName { get; init; } = string.Empty;
    public OlsPermission Desired { get; init; }

    public string Describe()
    {
        var target = Kind == OlsObjectKind.Table ? TableName : $"{TableName}.{ColumnName}";
        var verb = Desired == OlsPermission.None ? "Hidden" : "Visible";
        return $"{RoleName} \u00B7 {target} \u2192 {verb}";
    }
}

public sealed class OlsValidationIssue
{
    public OlsValidationSeverity Severity { get; init; }
    public string RoleName { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public bool BlocksApply => Severity == OlsValidationSeverity.Error;

    public string Glyph => Severity switch
    {
        OlsValidationSeverity.Error => "\uEA39",
        OlsValidationSeverity.Warning => "\uE7BA",
        _ => "\uE946"
    };
}

public sealed class OlsApplyResult
{
    public int FilesWritten { get; set; }
    public int Changes { get; set; }
    public int RolesCreated { get; set; }
    public int RolesDeleted { get; set; }
    public int RolesRenamed { get; set; }
    public string? BackupFolder { get; set; }
    public List<string> ChangedFiles { get; } = new();
}

/// <summary>
/// Reads and writes object-level security (OLS) for a TMDL semantic model.
///
/// OLS lives in security roles: table-level OLS is
/// <c>tablePermission X</c> + <c>metadataPermission: none</c>, column-level OLS is a
/// nested <c>columnPermission</c> with the same marker. A role that carries a DAX
/// filter (<c>tablePermission X = ...</c>) is an RLS role and is left to Power BI;
/// mixing RLS and OLS in one role is not allowed.
///
/// Only changed roles are rewritten, so untouched roles keep their exact bytes.
/// Every write is atomic and snapshotted first via <see cref="ModelBackupStore"/>.
/// </summary>
public static class OlsService
{
    private static readonly Regex RoleHeaderRegex =
        new(@"^role\s+('(?:[^']|'')*'|""[^""]*""|[^\s]+)", RegexOptions.Compiled);

    /// <summary>Undo snapshots are kept separately from other tools' model edits.</summary>
    private const string BackupScope = "objectSecurity";

    private static readonly Regex TableHeaderRegex =
        new(@"^tablePermission\s+('(?:[^']|'')*'|""[^""]*""|[^\s=]+)", RegexOptions.Compiled);

    private static readonly Regex ColumnHeaderRegex =
        new(@"^columnPermission\s+('(?:[^']|'')*'|""[^""]*""|[^\s=]+)", RegexOptions.Compiled);

    /// <summary>
    /// Matches the TMDL default-property shorthand for a permission, e.g.
    /// <c>columnPermission Country = none</c> or <c>tablePermission X = none</c>.
    /// MetadataPermission is the default property of a ColumnPermission, so Power BI
    /// serializes it inline rather than as a <c>metadataPermission:</c> child.
    /// </summary>
    private static readonly Regex InlinePermissionRegex =
        new(@"^=\s*(none|read)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // ------------------------------------------------------------- Discovery

    /// <summary>Finds the semantic model that belongs to a report / project, or null.</summary>
    public static string? DiscoverModelFolder(string? folderPath) => SortByColumnService.DiscoverModelFolder(folderPath);

    /// <summary>Reads every table plus every role of a semantic model. Never writes.</summary>
    public static OlsModel LoadModel(string modelFolder)
    {
        var folder = Path.GetFullPath(modelFolder);
        var model = SortByColumnService.LoadModel(folder);

        var rolesDir = Path.Combine(folder, "definition", "roles");
        var hasRolesFolder = Directory.Exists(rolesDir);

        var roles = new List<OlsRole>();
        if (hasRolesFolder)
        {
            foreach (var file in Directory.GetFiles(rolesDir, "*.tmdl")
                                          .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var role = ParseRoleFile(file);
                if (role != null) roles.Add(role);
            }
        }

        return new OlsModel
        {
            FolderPath = folder,
            Name = Path.GetFileName(folder),
            HasTmdlDefinition = model.HasTmdlDefinition,
            // Tables are already filtered to the user-facing set by LoadModel.
            Tables = model.Tables,
            Roles = roles,
            ModelFilePath = Path.Combine(folder, "definition", "model.tmdl"),
            RolesDirectory = rolesDir,
            HasRolesFolder = hasRolesFolder
        };
    }

    // ---------------------------------------------------------------- Parsing

    public static OlsRole? ParseRoleFile(string path)
    {
        string text;
        try
        {
            text = File.ReadAllText(path);
        }
        catch
        {
            return null;
        }

        var role = ParseRoleText(text);
        if (role == null) return null;

        role.FilePath = path;
        role.FileExists = true;
        return role;
    }

    /// <summary>Parses a single role definition, or null when the text is not a role.</summary>
    public static OlsRole? ParseRoleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var lines = SplitLines(text);
        var headerIndex = lines.FindIndex(l => l.TrimStart().StartsWith("role ", StringComparison.Ordinal));
        if (headerIndex < 0) return null;

        var header = lines[headerIndex].TrimStart();
        var match = RoleHeaderRegex.Match(header);
        if (!match.Success) return null;

        var role = new OlsRole { Name = Unquote(match.Groups[1].Value) };

        var stack = new List<(int Indent, object Node)>();
        var fenceOpen = false;

        OlsTableRule? filterTable = null;
        var filterIndent = -1;
        var filterFence = false;

        for (var i = headerIndex + 1; i < lines.Count; i++)
        {
            var line = lines[i];

            // A table's DAX filter can span several lines (a fenced block). While
            // capturing it, lines belong to the filter until it dedents or a nested
            // property starts — so they are preserved verbatim, not reparsed.
            if (filterTable != null)
            {
                if (filterFence)
                {
                    filterTable.FilterLines.Add(line.TrimEnd());
                    if (HasOddFence(line)) filterFence = false;
                    continue;
                }

                if (string.IsNullOrWhiteSpace(line))
                {
                    filterTable = null;
                    continue;
                }

                var filterLineTrimmed = line.TrimStart();
                var isProperty = filterLineTrimmed.StartsWith("metadataPermission:", StringComparison.Ordinal)
                              || filterLineTrimmed.StartsWith("columnPermission ", StringComparison.Ordinal)
                              || filterLineTrimmed.StartsWith("annotation ", StringComparison.Ordinal);

                if (LeadingIndent(line) > filterIndent && !isProperty)
                {
                    filterTable.FilterLines.Add(line.TrimEnd());
                    if (HasOddFence(line)) filterFence = true;
                    continue;
                }

                filterTable = null;
            }

            if (string.IsNullOrWhiteSpace(line)) continue;

            if (fenceOpen)
            {
                AppendToCurrent(stack, role, line);
                if (HasOddFence(line)) fenceOpen = false;
                continue;
            }

            var indent = LeadingIndent(line);
            var trimmed = line.TrimStart();

            // A recognized header/property is never treated as a fence opener, even
            // when its value contains backticks (e.g. a fenced DAX filter).
            var isRecognized = trimmed.StartsWith("tablePermission ", StringComparison.Ordinal)
                            || trimmed.StartsWith("columnPermission ", StringComparison.Ordinal)
                            || trimmed.StartsWith("metadataPermission:", StringComparison.Ordinal)
                            || trimmed.StartsWith("modelPermission:", StringComparison.Ordinal)
                            || trimmed.StartsWith("member ", StringComparison.Ordinal)
                            || trimmed.StartsWith("annotation ", StringComparison.Ordinal);

            // Pop any nodes at the same or deeper indentation.
            while (stack.Count > 0 && stack[^1].Indent >= indent) stack.RemoveAt(stack.Count - 1);

            if (trimmed.StartsWith("tablePermission ", StringComparison.Ordinal))
            {
                var tableMatch = TableHeaderRegex.Match(trimmed);
                if (tableMatch.Success)
                {
                    var table = new OlsTableRule { Name = Unquote(tableMatch.Groups[1].Value) };
                    var rest = trimmed[tableMatch.Length..].Trim();
                    if (rest.StartsWith("=", StringComparison.Ordinal))
                    {
                        // A bare "= none/read" is table OLS; anything else is an RLS
                        // filter (the default property of a TablePermission is DAX).
                        var inline = InlinePermissionRegex.Match(rest);
                        if (inline.Success)
                        {
                            table.Permission = ParsePermission(inline.Groups[1].Value);
                        }
                        else
                        {
                            table.FilterInline = rest[1..].Trim();
                            filterTable = table;
                            filterIndent = indent;
                            filterFence = HasOddFence(table.FilterInline);
                        }
                    }

                    role.TablePermissions.Add(table);
                    stack.Add((indent, table));
                    continue;
                }
            }

            if (trimmed.StartsWith("columnPermission ", StringComparison.Ordinal))
            {
                var columnMatch = ColumnHeaderRegex.Match(trimmed);
                if (columnMatch.Success && CurrentTable(stack) is { } parent)
                {
                    var column = new OlsColumnRule { Name = Unquote(columnMatch.Groups[1].Value) };

                    // "columnPermission X = none" sets MetadataPermission inline.
                    var rest = trimmed[columnMatch.Length..].Trim();
                    var inline = InlinePermissionRegex.Match(rest);
                    if (inline.Success)
                    {
                        column.Permission = ParsePermission(inline.Groups[1].Value);
                    }

                    parent.Columns.Add(column);
                    stack.Add((indent, column));
                    continue;
                }
            }

            if (trimmed.StartsWith("metadataPermission:", StringComparison.Ordinal))
            {
                var value = trimmed["metadataPermission:".Length..].Trim();
                var permission = ParsePermission(value);

                switch (stack.Count > 0 ? stack[^1].Node : null)
                {
                    case OlsTableRule table:
                        table.Permission = permission;
                        break;
                    case OlsColumnRule column:
                        column.Permission = permission;
                        break;
                    default:
                        role.ExtraLines.Add(line);
                        break;
                }

                continue;
            }

            if (trimmed.StartsWith("modelPermission:", StringComparison.Ordinal))
            {
                role.ModelPermission = trimmed["modelPermission:".Length..].Trim();
                continue;
            }

            if (stack.Count == 0 && trimmed.StartsWith("member ", StringComparison.Ordinal))
            {
                role.MemberLines.Add(line.TrimEnd());
                continue;
            }

            if (trimmed.StartsWith("annotation ", StringComparison.Ordinal))
            {
                AppendAnnotation(stack, role, line);
                continue;
            }

            // Any other fenced block (not a table filter) is kept together verbatim.
            if (!isRecognized && HasOddFence(line))
            {
                AppendToCurrent(stack, role, line);
                fenceOpen = true;
                continue;
            }

            AppendToCurrent(stack, role, line);
        }

        return role;
    }

    private static void AppendAnnotation(List<(int Indent, object Node)> stack, OlsRole role, string line)
    {
        if (stack.Count == 0) role.AnnotationLines.Add(line.TrimEnd());
        else AppendExtra(stack, line);
    }

    private static void AppendToCurrent(List<(int Indent, object Node)> stack, OlsRole role, string line)
    {
        if (stack.Count == 0) role.ExtraLines.Add(line.TrimEnd());
        else AppendExtra(stack, line);
    }

    private static void AppendExtra(List<(int Indent, object Node)> stack, string line)
    {
        switch (stack[^1].Node)
        {
            case OlsTableRule table:
                table.ExtraLines.Add(line.TrimEnd());
                break;
            case OlsColumnRule column:
                column.ExtraLines.Add(line.TrimEnd());
                break;
            default:
                break;
        }
    }

    private static OlsTableRule? CurrentTable(List<(int Indent, object Node)> stack)
        => stack.Select(s => s.Node).OfType<OlsTableRule>().LastOrDefault();

    private static OlsPermission ParsePermission(string value)
        => string.Equals(value.Trim(), "none", StringComparison.OrdinalIgnoreCase)
            ? OlsPermission.None
            : OlsPermission.Read;

    // -------------------------------------------------------------- Serialize

    /// <summary>Renders a role as canonical TMDL. Only the role's own file is affected.</summary>
    public static string SerializeRole(OlsRole role)
    {
        var sb = new StringBuilder();
        sb.Append("role ").Append(SortByColumnService.QuoteName(role.Name)).Append('\n');
        sb.Append('\t').Append("modelPermission: ").Append(
            string.IsNullOrWhiteSpace(role.ModelPermission) ? "read" : role.ModelPermission).Append('\n');

        foreach (var table in role.TablePermissions)
        {
            sb.Append('\n');
            sb.Append('\t').Append("tablePermission ").Append(SortByColumnService.QuoteName(table.Name));

            if (table.FilterInline != null)
            {
                sb.Append(" = ").Append(table.FilterInline);
            }

            sb.Append('\n');

            // The DAX filter body must sit immediately under its header, before any
            // nested properties, so a fenced/multi-line expression stays intact.
            foreach (var filterLine in table.FilterLines)
            {
                sb.Append(filterLine).Append('\n');
            }

            if (table.Permission == OlsPermission.None)
            {
                sb.Append("\t\tmetadataPermission: none\n");
            }

            foreach (var column in table.Columns)
            {
                var hasContent = column.Permission != OlsPermission.Default || column.ExtraLines.Count > 0;
                if (!hasContent) continue;

                // Column-level OLS uses TMDL's default-property shorthand
                // ("columnPermission X = none"), matching what Power BI writes.
                sb.Append("\t\tcolumnPermission ").Append(SortByColumnService.QuoteName(column.Name));
                if (column.Permission != OlsPermission.Default)
                {
                    sb.Append(" = ").Append(column.Permission == OlsPermission.None ? "none" : "read");
                }

                sb.Append('\n');

                foreach (var extra in column.ExtraLines)
                {
                    sb.Append(extra).Append('\n');
                }
            }

            foreach (var extra in table.ExtraLines)
            {
                sb.Append(extra).Append('\n');
            }
        }

        if (role.AnnotationLines.Count > 0)
        {
            sb.Append('\n');
            foreach (var annotation in role.AnnotationLines)
            {
                sb.Append(annotation).Append('\n');
            }
        }

        if (role.MemberLines.Count > 0)
        {
            sb.Append('\n');
            foreach (var member in role.MemberLines)
            {
                sb.Append(member).Append('\n');
            }
        }

        foreach (var extra in role.ExtraLines)
        {
            sb.Append(extra).Append('\n');
        }

        return sb.ToString();
    }

    // ------------------------------------------------------------- Validation

    public static IReadOnlyList<OlsValidationIssue> Validate(OlsModel model, IEnumerable<OlsChange> changes)
    {
        var issues = new List<OlsValidationIssue>();
        var changeList = changes?.ToList() ?? new List<OlsChange>();
        if (model == null) return issues;

        var byRole = changeList.GroupBy(c => c.RoleName, StringComparer.OrdinalIgnoreCase);
        var relationships = LoadRelationshipGraph(model.FolderPath);

        foreach (var group in byRole)
        {
            var role = model.FindRole(group.Key);
            if (role == null) continue;

            if (role.IsRls)
            {
                issues.Add(new OlsValidationIssue
                {
                    Severity = OlsValidationSeverity.Info,
                    RoleName = role.Name,
                    Message = "This role also applies row-level filters; the OLS rules are added alongside them. Be aware a user in both an RLS and an OLS role can hit query-time errors."
                });
            }

            // Desired hidden tables (including the ones in this batch).
            var hiddenTables = new HashSet<string>(
                role.TablePermissions.Where(t => t.Permission == OlsPermission.None).Select(t => t.Name),
                StringComparer.OrdinalIgnoreCase);

            foreach (var change in group.Where(c => c.Kind == OlsObjectKind.Table))
            {
                if (change.Desired == OlsPermission.None) hiddenTables.Add(change.TableName);
                else hiddenTables.Remove(change.TableName);
            }

            foreach (var change in group)
            {
                if (model.FindTable(change.TableName) == null)
                {
                    issues.Add(new OlsValidationIssue
                    {
                        Severity = OlsValidationSeverity.Warning,
                        RoleName = role.Name,
                        Message = $"'{change.TableName}' no longer exists in the model; the rule would be stale."
                    });
                }

                if (change.Kind == OlsObjectKind.Column)
                {
                    var table = model.FindTable(change.TableName);
                    if (table != null && !table.Columns.Any(c => string.Equals(c.Name, change.ColumnName, StringComparison.OrdinalIgnoreCase)))
                    {
                        issues.Add(new OlsValidationIssue
                        {
                            Severity = OlsValidationSeverity.Warning,
                            RoleName = role.Name,
                            Message = $"{change.TableName}.{change.ColumnName} no longer exists in the model."
                        });
                    }

                    if (change.Desired == OlsPermission.None && hiddenTables.Contains(change.TableName))
                    {
                        issues.Add(new OlsValidationIssue
                        {
                            Severity = OlsValidationSeverity.Info,
                            RoleName = role.Name,
                            Message = $"{change.TableName} is already hidden, so hiding {change.TableName}.{change.ColumnName} adds nothing."
                        });
                    }
                }
            }

            foreach (var table in hiddenTables)
            {
                if (WouldBreakRelationships(relationships, model, table))
                {
                    issues.Add(new OlsValidationIssue
                    {
                        Severity = OlsValidationSeverity.Warning,
                        RoleName = role.Name,
                        Message = $"Hiding '{table}' may break a relationship chain. Tables on either side may no longer relate through it in Power BI."
                    });
                }
            }
        }

        return issues;
    }

    // ------------------------------------------------------------------ Apply

    /// <summary>
    /// Applies staged changes to their role files. Only roles with an actual
    /// difference are rewritten; the model.tmdl ref list is left alone because the
    /// set of roles does not change here.
    /// </summary>
    public static OlsApplyResult Apply(
        OlsModel model,
        IEnumerable<OlsChange> changes,
        bool createBackup = true,
        string? projectRoot = null)
    {
        var result = new OlsApplyResult();
        var changeList = changes?.ToList() ?? new List<OlsChange>();
        if (model == null || changeList.Count == 0) return result;

        var edits = new List<(string File, string Text, int Changed)>();

        foreach (var group in changeList.GroupBy(c => c.RoleName, StringComparer.OrdinalIgnoreCase))
        {
            var role = model.FindRole(group.Key);
            if (role == null) continue;

            var working = CloneRole(role);
            var changed = ApplyChangesToRole(working, group.ToList());
            if (changed == 0) continue;

            edits.Add((role.FilePath, SerializeRole(working), changed));
        }

        if (edits.Count == 0) return result;

        // Repair any missing "ref role" in model.tmdl so Power BI discovers the
        // roles, even for role files that were not created by Power BI.
        string? updatedModelText = null;
        if (File.Exists(model.ModelFilePath))
        {
            var modelText = File.ReadAllText(model.ModelFilePath);
            var working = modelText;
            foreach (var edit in edits)
            {
                var role = model.Roles.FirstOrDefault(r =>
                    string.Equals(r.FilePath, edit.File, StringComparison.OrdinalIgnoreCase));
                if (role != null) working = AddRoleRef(working, role.Name);
            }

            if (!string.Equals(working, modelText, StringComparison.Ordinal))
            {
                updatedModelText = working;
            }
        }

        var backupFolder = createBackup && !string.IsNullOrWhiteSpace(projectRoot)
            ? ModelBackupStore.CreateBackupFolder(projectRoot!, model.FolderPath, BackupScope)
            : null;

        if (backupFolder != null)
        {
            foreach (var edit in edits) ModelBackupStore.BackupFile(backupFolder, edit.File);
            if (updatedModelText != null) ModelBackupStore.BackupFile(backupFolder, model.ModelFilePath);
            result.BackupFolder = backupFolder;
        }

        foreach (var edit in edits)
        {
            WriteText(edit.File, edit.Text);
            result.FilesWritten++;
            result.Changes += edit.Changed;
            result.ChangedFiles.Add(edit.File);
        }

        if (updatedModelText != null)
        {
            WriteText(model.ModelFilePath, updatedModelText);
            result.FilesWritten++;
            result.ChangedFiles.Add(model.ModelFilePath);
        }

        return result;
    }

    /// <summary>Creates an empty OLS role file and adds its <c>ref role</c> to model.tmdl.</summary>
    public static OlsApplyResult CreateRole(
        OlsModel model,
        string roleName,
        bool createBackup = true,
        string? projectRoot = null)
    {
        var result = new OlsApplyResult();
        var name = (roleName ?? string.Empty).Trim();
        if (model == null || name.Length == 0) return result;

        if (model.FindRole(name) != null)
        {
            throw new InvalidOperationException($"A role named '{name}' already exists.");
        }

        Directory.CreateDirectory(model.RolesDirectory);
        var path = UniqueRoleFilePath(model.RolesDirectory, name);

        var role = new OlsRole { Name = name, FilePath = path, FileExists = true, ModelPermission = "read" };
        var text = SerializeRole(role);
        var modelText = File.Exists(model.ModelFilePath) ? File.ReadAllText(model.ModelFilePath) : string.Empty;
        var updatedModelText = AddRoleRef(modelText, name);

        var backupFolder = createBackup && !string.IsNullOrWhiteSpace(projectRoot)
            ? ModelBackupStore.CreateBackupFolder(projectRoot!, model.FolderPath, BackupScope)
            : null;

        if (backupFolder != null)
        {
            if (File.Exists(model.ModelFilePath)) ModelBackupStore.BackupFile(backupFolder, model.ModelFilePath);
            ModelBackupStore.RecordCreatedFile(backupFolder, path);
            result.BackupFolder = backupFolder;
        }

        WriteText(path, text);
        if (!string.IsNullOrEmpty(updatedModelText))
        {
            WriteText(model.ModelFilePath, updatedModelText);
        }

        result.RolesCreated = 1;
        result.FilesWritten = File.Exists(model.ModelFilePath) ? 2 : 1;
        result.ChangedFiles.Add(path);
        return result;
    }

    /// <summary>Renames a role: header, file name and model.tmdl ref.</summary>
    public static OlsApplyResult RenameRole(
        OlsModel model,
        string oldName,
        string newName,
        bool createBackup = true,
        string? projectRoot = null)
    {
        var result = new OlsApplyResult();
        var target = (newName ?? string.Empty).Trim();
        if (model == null || target.Length == 0) return result;

        var role = model.FindRole(oldName);
        if (role == null) return result;
        if (model.FindRole(target) != null)
        {
            throw new InvalidOperationException($"A role named '{target}' already exists.");
        }

        Directory.CreateDirectory(model.RolesDirectory);
        var newPath = UniqueRoleFilePath(model.RolesDirectory, target);
        if (string.Equals(newPath, role.FilePath, StringComparison.OrdinalIgnoreCase))
        {
            newPath = Path.Combine(model.RolesDirectory, SafeFileStem(target) + ".tmdl");
        }

        var working = CloneRole(role);
        working.Name = target;
        var text = SerializeRole(working);

        var modelText = File.Exists(model.ModelFilePath) ? File.ReadAllText(model.ModelFilePath) : string.Empty;
        var updatedModelText = AddRoleRef(RemoveRoleRef(modelText, role.Name), target);

        var backupFolder = createBackup && !string.IsNullOrWhiteSpace(projectRoot)
            ? ModelBackupStore.CreateBackupFolder(projectRoot!, model.FolderPath, BackupScope)
            : null;

        if (backupFolder != null)
        {
            if (File.Exists(role.FilePath)) ModelBackupStore.BackupFile(backupFolder, role.FilePath);
            if (File.Exists(model.ModelFilePath)) ModelBackupStore.BackupFile(backupFolder, model.ModelFilePath);
            result.BackupFolder = backupFolder;
        }

        WriteText(newPath, text);
        if (!string.Equals(newPath, role.FilePath, StringComparison.OrdinalIgnoreCase) && File.Exists(role.FilePath))
        {
            File.Delete(role.FilePath);
        }

        if (!string.IsNullOrEmpty(updatedModelText))
        {
            WriteText(model.ModelFilePath, updatedModelText);
        }

        result.RolesRenamed = 1;
        result.FilesWritten = 2;
        result.ChangedFiles.Add(newPath);
        return result;
    }

    /// <summary>Deletes a role file and its model.tmdl ref.</summary>
    public static OlsApplyResult DeleteRole(
        OlsModel model,
        string roleName,
        bool createBackup = true,
        string? projectRoot = null)
    {
        var result = new OlsApplyResult();
        if (model == null) return result;

        var role = model.FindRole(roleName);
        if (role == null) return result;

        var modelText = File.Exists(model.ModelFilePath) ? File.ReadAllText(model.ModelFilePath) : string.Empty;
        var updatedModelText = RemoveRoleRef(modelText, role.Name);

        var backupFolder = createBackup && !string.IsNullOrWhiteSpace(projectRoot)
            ? ModelBackupStore.CreateBackupFolder(projectRoot!, model.FolderPath, BackupScope)
            : null;

        if (backupFolder != null)
        {
            if (File.Exists(role.FilePath)) ModelBackupStore.BackupFile(backupFolder, role.FilePath);
            if (File.Exists(model.ModelFilePath)) ModelBackupStore.BackupFile(backupFolder, model.ModelFilePath);
            result.BackupFolder = backupFolder;
        }

        if (File.Exists(role.FilePath))
        {
            File.Delete(role.FilePath);
        }

        if (!string.IsNullOrEmpty(updatedModelText))
        {
            WriteText(model.ModelFilePath, updatedModelText);
        }

        result.RolesDeleted = 1;
        result.FilesWritten = role.FileExists ? 1 : 0;
        result.ChangedFiles.Add(role.FilePath);
        return result;
    }

    public static string? LatestBackupFolder(string projectRoot) => ModelBackupStore.LatestBackupFolder(projectRoot, BackupScope);

    public static int RestoreLatestBackup(string projectRoot, out string? restoredFrom)
        => ModelBackupStore.RestoreLatestBackup(projectRoot, out restoredFrom, BackupScope);

    /// <summary>
    /// Returns what a role would look like with the given changes applied, without
    /// touching disk. Used to preview the resulting state (e.g. the OLS/RLS badge).
    /// </summary>
    public static OlsRole PreviewRole(OlsRole role, IEnumerable<OlsChange> changes)
    {
        var clone = CloneRole(role);
        if (role != null)
        {
            ApplyChangesToRole(clone, changes?.ToList() ?? new List<OlsChange>());
        }

        return clone;
    }

    // ------------------------------------------------------------- Internals

    private static int ApplyChangesToRole(OlsRole role, IReadOnlyList<OlsChange> changes)
    {
        var changed = 0;

        foreach (var change in changes)
        {
            var table = role.TablePermissions.FirstOrDefault(
                t => string.Equals(t.Name, change.TableName, StringComparison.OrdinalIgnoreCase));

            if (change.Kind == OlsObjectKind.Table)
            {
                if (table == null)
                {
                    if (change.Desired == OlsPermission.Default) continue;
                    table = new OlsTableRule { Name = change.TableName };
                    role.TablePermissions.Add(table);
                }

                if (table.Permission != change.Desired)
                {
                    table.Permission = change.Desired;
                    changed++;
                }

                continue;
            }

            // Column change.
            if (table == null)
            {
                if (change.Desired == OlsPermission.Default) continue;
                table = new OlsTableRule { Name = change.TableName };
                role.TablePermissions.Add(table);
            }

            var column = table.Columns.FirstOrDefault(
                c => string.Equals(c.Name, change.ColumnName, StringComparison.OrdinalIgnoreCase));

            if (column == null)
            {
                if (change.Desired == OlsPermission.Default) continue;
                column = new OlsColumnRule { Name = change.ColumnName };
                table.Columns.Add(column);
            }

            if (column.Permission != change.Desired)
            {
                column.Permission = change.Desired;
                changed++;
            }
        }

        if (changed > 0) PruneEmpty(role);
        return changed;
    }

    /// <summary>Removes blocks that would serialize to nothing.</summary>
    private static void PruneEmpty(OlsRole role)
    {
        foreach (var table in role.TablePermissions.ToList())
        {
            foreach (var column in table.Columns.ToList())
            {
                if (column.Permission == OlsPermission.Default && column.ExtraLines.Count == 0)
                {
                    table.Columns.Remove(column);
                }
            }
            var isEmpty = !table.HasFilter
                          && table.Permission == OlsPermission.Default
                          && table.Columns.Count == 0
                          && table.ExtraLines.Count == 0;

            if (isEmpty) role.TablePermissions.Remove(table);
        }
    }

    private static OlsRole CloneRole(OlsRole role)
    {
        var clone = new OlsRole
        {
            Name = role.Name,
            FilePath = role.FilePath,
            FileExists = role.FileExists,
            ModelPermission = role.ModelPermission
        };

        clone.AnnotationLines.AddRange(role.AnnotationLines);
        clone.MemberLines.AddRange(role.MemberLines);
        clone.ExtraLines.AddRange(role.ExtraLines);

        foreach (var table in role.TablePermissions)
        {
            var tableClone = new OlsTableRule
            {
                Name = table.Name,
                FilterInline = table.FilterInline,
                Permission = table.Permission
            };
            tableClone.FilterLines.AddRange(table.FilterLines);
            tableClone.ExtraLines.AddRange(table.ExtraLines);

            foreach (var column in table.Columns)
            {
                var columnClone = new OlsColumnRule
                {
                    Name = column.Name,
                    Permission = column.Permission
                };
                columnClone.ExtraLines.AddRange(column.ExtraLines);
                tableClone.Columns.Add(columnClone);
            }

            clone.TablePermissions.Add(tableClone);
        }

        return clone;
    }

    // -------------------------------------------------- Relationship warnings

    private sealed class RelationshipGraph
    {
        public Dictionary<string, HashSet<string>> Adjacency { get; } = new(StringComparer.OrdinalIgnoreCase);

        public void AddEdge(string a, string b)
        {
            if (string.IsNullOrWhiteSpace(a) || string.IsNullOrWhiteSpace(b)) return;

            if (!Adjacency.TryGetValue(a, out var setA))
            {
                setA = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Adjacency[a] = setA;
            }

            if (!Adjacency.TryGetValue(b, out var setB))
            {
                setB = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                Adjacency[b] = setB;
            }

            setA.Add(b);
            setB.Add(a);
        }
    }

    private static RelationshipGraph LoadRelationshipGraph(string modelFolder)
    {
        var graph = new RelationshipGraph();
        var path = Path.Combine(modelFolder, "definition", "relationships.tmdl");
        if (!File.Exists(path)) return graph;

        string? fromTable = null;
        string? toTable = null;

        void Flush()
        {
            if (fromTable != null && toTable != null) graph.AddEdge(fromTable, toTable);
            fromTable = null;
            toTable = null;
        }

        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.TrimStart();
            if (line.StartsWith("relationship ", StringComparison.Ordinal) ||
                line.StartsWith("relationship\t", StringComparison.Ordinal))
            {
                Flush();
                continue;
            }

            if (line.StartsWith("fromTable:", StringComparison.Ordinal))
            {
                fromTable = TableOf(line["fromTable:".Length..].Trim(), explicitTable: true);
            }
            else if (line.StartsWith("toTable:", StringComparison.Ordinal))
            {
                toTable = TableOf(line["toTable:".Length..].Trim(), explicitTable: true);
            }
            else if (line.StartsWith("fromColumn:", StringComparison.Ordinal))
            {
                fromTable ??= TableOf(line["fromColumn:".Length..].Trim(), explicitTable: false);
            }
            else if (line.StartsWith("toColumn:", StringComparison.Ordinal))
            {
                toTable ??= TableOf(line["toColumn:".Length..].Trim(), explicitTable: false);
            }
        }

        Flush();
        return graph;
    }

    /// <summary>
    /// Extracts the table part of a TMDL object reference. A full reference is
    /// "Table.Column", where either part may be single-quoted.
    /// </summary>
    private static string? TableOf(string reference, bool explicitTable)
    {
        reference = reference.Trim();
        if (reference.Length == 0) return null;
        if (explicitTable) return Unquote(reference);

        if (reference[0] == '\'')
        {
            var end = reference.IndexOf('\'', 1);
            while (end >= 0 && end + 1 < reference.Length && reference[end + 1] == '\'') end = reference.IndexOf('\'', end + 2);
            if (end > 0) return reference[1..end].Replace("''", "'");
            return Unquote(reference);
        }

        var dot = reference.IndexOf('.');
        return dot > 0 ? reference[..dot] : Unquote(reference);
    }

    private static bool WouldBreakRelationships(RelationshipGraph graph, OlsModel model, string table)
    {
        if (!graph.Adjacency.TryGetValue(table, out var component) || component.Count == 0) return false;

        // Find the whole connected component that contains 'table'.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { table };
        var queue = new Queue<string>();
        queue.Enqueue(table);
        while (queue.Count > 0)
        {
            var node = queue.Dequeue();
            if (!graph.Adjacency.TryGetValue(node, out var neighbours)) continue;
            foreach (var next in neighbours)
            {
                if (seen.Add(next)) queue.Enqueue(next);
            }
        }

        if (seen.Count <= 2) return false; // Nothing to sever between.

        // Walk the component without 'table' and count how many pieces it becomes.
        var remaining = seen.Where(n => !string.Equals(n, table, StringComparison.OrdinalIgnoreCase)).ToList();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pieces = 0;

        foreach (var start in remaining)
        {
            if (visited.Contains(start)) continue;
            pieces++;

            var localQueue = new Queue<string>();
            localQueue.Enqueue(start);
            visited.Add(start);
            while (localQueue.Count > 0)
            {
                var node = localQueue.Dequeue();
                if (!graph.Adjacency.TryGetValue(node, out var neighbours)) continue;
                foreach (var next in neighbours)
                {
                    if (string.Equals(next, table, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!seen.Contains(next)) continue;
                    if (visited.Add(next)) localQueue.Enqueue(next);
                }
            }
        }

        return pieces > 1;
    }

    // ----------------------------------------------------------- model.tmdl

    public static string AddRoleRef(string modelText, string roleName)
    {
        if (HasRoleRef(modelText, roleName)) return modelText;

        var newline = modelText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var text = modelText;
        if (text.Length > 0 && !text.EndsWith("\n", StringComparison.Ordinal)) text += newline;
        if (text.Length > 0 && !text.EndsWith(newline + newline, StringComparison.Ordinal)) text += newline;

        return text + "ref role " + SortByColumnService.QuoteName(roleName) + newline;
    }

    public static string RemoveRoleRef(string modelText, string roleName)
    {
        if (string.IsNullOrEmpty(modelText)) return modelText;

        var newline = modelText.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = modelText.Replace("\r\n", "\n").Split('\n').ToList();
        lines.RemoveAll(l => IsRoleRef(l, roleName));
        while (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join(newline, lines) + newline;
    }

    public static bool HasRoleRef(string modelText, string roleName)
        => modelText.Replace("\r\n", "\n").Split('\n').Any(l => IsRoleRef(l, roleName));

    private static bool IsRoleRef(string line, string roleName)
    {
        var trimmed = line.Trim();
        if (!trimmed.StartsWith("ref role ", StringComparison.Ordinal)) return false;
        var value = trimmed["ref role ".Length..].Trim();
        return string.Equals(Unquote(value), roleName, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------- File IO

    /// <summary>Role file name for a role, sanitizing characters a file name cannot hold.</summary>
    public static string SafeFileStem(string roleName)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = roleName.Trim().Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray();
        var stem = new string(chars).Trim().TrimEnd('.');
        return stem.Length == 0 ? "role" : stem;
    }

    private static string UniqueRoleFilePath(string rolesDirectory, string roleName)
    {
        var stem = SafeFileStem(roleName);
        var path = Path.Combine(rolesDirectory, stem + ".tmdl");
        var counter = 1;
        while (File.Exists(path))
        {
            path = Path.Combine(rolesDirectory, $"{stem}_{counter++}.tmdl");
        }

        return path;
    }

    private static (string Text, bool HasBom) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        return (Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset), hasBom);
    }

    private static void WriteText(string path, string text)
    {
        var hasBom = false;
        if (File.Exists(path))
        {
            try { hasBom = ReadText(path).HasBom; } catch { }
        }

        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir)) Directory.CreateDirectory(dir);

        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(hasBom));
        File.Move(temp, path, overwrite: true);
    }

    // ------------------------------------------------------------- Helpers

    private static List<string> SplitLines(string text)
    {
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');
        var lines = normalized.Split('\n').ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    private static int LeadingIndent(string line)
    {
        var count = 0;
        while (count < line.Length && (line[count] == ' ' || line[count] == '\t')) count++;
        return count;
    }

    private static bool HasOddFence(string line)
    {
        var count = 0;
        var index = 0;
        while ((index = line.IndexOf("```", index, StringComparison.Ordinal)) >= 0)
        {
            count++;
            index += 3;
        }

        return count % 2 == 1;
    }

    private static string Unquote(string raw)
    {
        raw = raw.Trim();
        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw[1..^1].Replace("''", "'");
        }

        if (raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"')
        {
            return raw[1..^1];
        }

        return raw;
    }
}
