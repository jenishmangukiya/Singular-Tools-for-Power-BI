using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace SingularTools.Core;

/// <summary>One column of a semantic model table, as read from its TMDL.</summary>
public sealed class SemanticModelColumn
{
    public string Name { get; init; } = string.Empty;

    /// <summary>The column's <c>dataType</c> (e.g. "string", "int64"), empty when absent.</summary>
    public string DataType { get; init; } = string.Empty;

    public bool HasDataType { get; init; }

    /// <summary>An explicit <c>formatString</c> present on the column.</summary>
    public bool HasFormatString { get; init; }

    /// <summary>The current <c>sortByColumn</c> target, empty when none.</summary>
    public string SortByColumn { get; init; } = string.Empty;

    public bool HasSortByColumn { get; init; }

    /// <summary>True when the column is materialised by the partition (has a <c>sourceColumn</c>).</summary>
    public bool HasSourceColumn { get; init; }

    /// <summary>True for a DAX calculated column (<c>column Name = expression</c>).</summary>
    public bool IsCalculated { get; init; }

    public bool IsInt64 => string.Equals(DataType, "int64", StringComparison.OrdinalIgnoreCase);
}

/// <summary>A table in the semantic model, backed by a single <c>.tmdl</c> file.</summary>
public sealed class SemanticModelTable
{
    public string Name { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public IReadOnlyList<SemanticModelColumn> Columns { get; init; } = Array.Empty<SemanticModelColumn>();

    /// <summary>
    /// Auto-generated helper tables (date templates) carry <c>isPrivate</c>. Power BI
    /// hides them from model authors, so tools that present the model skip them.
    /// Tables marked only <c>isHidden</c> are still shown, matching Power BI's model
    /// view, which lists hidden tables.
    /// </summary>
    public bool IsPrivate { get; init; }
}

/// <summary>A semantic model discovered next to a report.</summary>
public sealed class SemanticModel
{
    public string FolderPath { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public IReadOnlyList<SemanticModelTable> Tables { get; init; } = Array.Empty<SemanticModelTable>();

    /// <summary>False when no TMDL table files exist (e.g. a single model.bim model).</summary>
    public bool HasTmdlDefinition { get; init; }
}

/// <summary>An order column paired with the column it sorts.</summary>
public sealed class SortByColumnPair
{
    public string TableName { get; init; } = string.Empty;
    public string TableFilePath { get; init; } = string.Empty;
    public string BaseColumn { get; init; } = string.Empty;
    public string OrderColumn { get; init; } = string.Empty;

    public bool BaseAlreadyUsesOrder { get; init; }
    public bool OrderIsInt64 { get; init; }
    public bool OrderHasFormatString { get; init; }
    public bool OrderIsCalculated { get; init; }

    /// <summary>True when the column being sorted is itself a DAX calculated column.</summary>
    public bool BaseIsCalculated { get; init; }

    /// <summary>
    /// True when either side is a DAX calculated column. Power BI should own these
    /// sort orders: writing them from TMDL can create a circular dependency.
    /// </summary>
    public bool IsCalculatedPair => BaseIsCalculated || OrderIsCalculated;

    /// <summary>True when the pair already matches what Power BI would write.</summary>
    public bool AlreadyApplied => BaseAlreadyUsesOrder && OrderIsInt64 && OrderHasFormatString;

    /// <summary>Stable identity used to remember the user's on/off choice (table + base column).</summary>
    public string Key => $"{TableName}|{BaseColumn}";
}

/// <summary>A column that ends with the order suffix but could not be paired.</summary>
public sealed class SortByColumnUnmatched
{
    public string TableName { get; init; } = string.Empty;
    public string ColumnName { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
}

/// <summary>The result of pairing order columns for a suffix. No files are touched.</summary>
public sealed class SortByColumnPlan
{
    public string Suffix { get; init; } = string.Empty;
    public IReadOnlyList<SortByColumnPair> Pairs { get; init; } = Array.Empty<SortByColumnPair>();
    public IReadOnlyList<SortByColumnUnmatched> Unmatched { get; init; } = Array.Empty<SortByColumnUnmatched>();

    public int PendingCount => Pairs.Count(p => !p.AlreadyApplied);

    public IReadOnlyList<SortByColumnPair> ForTable(string tableName) =>
        Pairs.Where(p => string.Equals(p.TableName, tableName, StringComparison.Ordinal)).ToList();
}

/// <summary>Outcome of an apply.</summary>
public sealed class SortByColumnApplyResult
{
    public int FilesWritten { get; set; }
    public int ColumnsChanged { get; set; }
    public int AlreadyApplied { get; set; }
    public List<string> ChangedFiles { get; } = new();
    public string? BackupFolder { get; set; }
}

/// <summary>
/// Applies "Sort by column" to a TMDL semantic model: every <c>column&lt;suffix&gt;</c> is
/// paired with the base column <c>column</c>, its type is set to whole number, and the
/// base column is told to sort by it. The edits mirror what Power BI Desktop itself
/// writes (including the <c>changedProperty</c> markers) and are idempotent.
///
/// Only TMDL models are supported. A <c>model.bim</c> model is reported as such and
/// left untouched.
/// </summary>
public static class SortByColumnService
{
    public const string DefaultSuffix = "_ord";

    private static readonly Regex TableHeaderRegex =
        new(@"^table\s+('(?:[^']|'')*'|""[^""]*""|[^\s]+)", RegexOptions.Compiled);

    private static readonly Regex ColumnNameRegex =
        new(@"^column\s+('(?:[^']|'')*'|""[^""]*""|[^\s=]+)", RegexOptions.Compiled);

    private static readonly Regex CalculatedColumnRegex =
        new(@"^column\s+(?:'(?:[^']|'')*'|""[^""]*""|[^\s=]+)\s*=", RegexOptions.Compiled);

    // ------------------------------------------------------------- Discovery

    /// <summary>
    /// Finds the semantic model that belongs to a report / project folder. Handles the
    /// project root (holding the <c>.pbip</c>), the <c>.Report</c> folder and a model
    /// folder passed directly.
    /// </summary>
    public static string? DiscoverModelFolder(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath)) return null;

        try
        {
            var full = Path.GetFullPath(folderPath);
            if (!Directory.Exists(full)) return null;

            // The folder itself is a model folder.
            if (Directory.Exists(Path.Combine(full, "definition", "tables")))
            {
                return full;
            }

            // A project root holding the semantic model folder.
            var sibling = Directory.GetDirectories(full, "*.SemanticModel")
                                   .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "definition", "tables")));
            if (sibling != null) return sibling;

            var name = Path.GetFileName(full);
            var parent = Directory.GetParent(full)?.FullName;

            if (parent != null)
            {
                // Named sibling of a .Report folder.
                if (name.EndsWith(".Report", StringComparison.OrdinalIgnoreCase))
                {
                    var candidate = Path.Combine(parent, name[..^".Report".Length] + ".SemanticModel");
                    if (Directory.Exists(Path.Combine(candidate, "definition", "tables"))) return candidate;
                }

                var anySibling = Directory.GetDirectories(parent, "*.SemanticModel")
                                          .FirstOrDefault(d => Directory.Exists(Path.Combine(d, "definition", "tables")));
                if (anySibling != null) return anySibling;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Reads every user-facing table TMDL of a semantic model. Never writes.
    /// Auto-generated date-template tables are marked <c>isPrivate</c>; Power BI
    /// hides them from model authors, so they are excluded here and therefore absent
    /// from every tool that offers the model's tables. Tables marked only
    /// <c>isHidden</c> are still included, matching Power BI's model view.
    /// </summary>
    public static SemanticModel LoadModel(string modelFolder)
    {
        var folder = Path.GetFullPath(modelFolder);
        var tables = new List<SemanticModelTable>();

        var tablesDir = Path.Combine(folder, "definition", "tables");
        if (Directory.Exists(tablesDir))
        {
            foreach (var file in Directory.GetFiles(tablesDir, "*.tmdl")
                                          .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var table = ParseTableFile(file);
                if (table == null || table.IsPrivate) continue;
                tables.Add(table);
            }
        }

        return new SemanticModel
        {
            FolderPath = folder,
            Name = Path.GetFileName(folder),
            Tables = tables,
            HasTmdlDefinition = tables.Count > 0
        };
    }

    // --------------------------------------------------------------- Planning

    /// <summary>
    /// Pairs every column ending with <paramref name="suffix"/> with the base column of
    /// the same table. Pure: nothing is written.
    /// </summary>
    public static SortByColumnPlan BuildPlan(SemanticModel model, string? suffix)
    {
        var normalized = (suffix ?? string.Empty).Trim();
        var pairs = new List<SortByColumnPair>();
        var unmatched = new List<SortByColumnUnmatched>();

        if (model == null || normalized.Length == 0)
        {
            return new SortByColumnPlan { Suffix = normalized, Pairs = pairs, Unmatched = unmatched };
        }

        foreach (var table in model.Tables)
        {
            var byName = new Dictionary<string, SemanticModelColumn>(StringComparer.OrdinalIgnoreCase);
            foreach (var column in table.Columns) byName[column.Name] = column;

            foreach (var order in table.Columns)
            {
                if (!order.Name.EndsWith(normalized, StringComparison.OrdinalIgnoreCase)) continue;

                if (order.Name.Length == normalized.Length)
                {
                    unmatched.Add(new SortByColumnUnmatched
                    {
                        TableName = table.Name,
                        ColumnName = order.Name,
                        Reason = "Name is only the suffix"
                    });
                    continue;
                }

                var baseName = order.Name[..^normalized.Length];
                if (!byName.TryGetValue(baseName, out var baseColumn))
                {
                    unmatched.Add(new SortByColumnUnmatched
                    {
                        TableName = table.Name,
                        ColumnName = order.Name,
                        Reason = $"No '{baseName}' column in this table"
                    });
                    continue;
                }

                if (baseColumn.Name.EndsWith(normalized, StringComparison.OrdinalIgnoreCase))
                {
                    unmatched.Add(new SortByColumnUnmatched
                    {
                        TableName = table.Name,
                        ColumnName = order.Name,
                        Reason = $"'{baseName}' is itself an order column"
                    });
                    continue;
                }

                pairs.Add(new SortByColumnPair
                {
                    TableName = table.Name,
                    TableFilePath = table.FilePath,
                    BaseColumn = baseColumn.Name,
                    OrderColumn = order.Name,
                    BaseAlreadyUsesOrder = baseColumn.HasSortByColumn &&
                                           string.Equals(baseColumn.SortByColumn, order.Name, StringComparison.OrdinalIgnoreCase),
                    OrderIsInt64 = order.IsInt64,
                    OrderHasFormatString = order.HasFormatString,
                    OrderIsCalculated = order.IsCalculated,
                    BaseIsCalculated = baseColumn.IsCalculated
                });
            }
        }

        return new SortByColumnPlan { Suffix = normalized, Pairs = pairs, Unmatched = unmatched };
    }

    // ---------------------------------------------------------------- Applying

    /// <summary>
    /// Writes the selected pairs into their TMDL files. When <paramref name="createBackup"/>
    /// is set, the touched files are copied to a timestamped backup first so the change
    /// can be reverted with <see cref="RestoreLatestBackup"/>.
    /// </summary>
    public static SortByColumnApplyResult Apply(
        SemanticModel model,
        IEnumerable<SortByColumnPair> pairs,
        bool createBackup = true,
        string? projectRoot = null)
    {
        var result = new SortByColumnApplyResult();
        var selected = (pairs ?? Enumerable.Empty<SortByColumnPair>()).ToList();

        // 1. Work out exactly which files change and what they become, without touching disk.
        var edits = new List<(string File, string Text, bool HasBom, int Changed)>();
        foreach (var group in selected.GroupBy(p => p.TableFilePath, StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(group.Key)) continue;

            result.AlreadyApplied += group.Count(p => p.AlreadyApplied);

            var (text, hasBom) = ReadText(group.Key);
            var (updated, changed) = ApplyToFile(text, group.ToList());
            if (updated != null && changed > 0 && !string.Equals(updated, text, StringComparison.Ordinal))
            {
                edits.Add((group.Key, updated, hasBom, changed));
            }
        }

        if (edits.Count == 0) return result;

        // 2. Snapshot every file we are about to overwrite, so the whole apply is reversible.
        var backupFolder = createBackup && !string.IsNullOrWhiteSpace(projectRoot)
            ? ModelBackupStore.CreateBackupFolder(projectRoot!, model.FolderPath)
            : null;

        if (backupFolder != null)
        {
            foreach (var edit in edits) ModelBackupStore.BackupFile(backupFolder, edit.File);
            result.BackupFolder = backupFolder;
        }

        // 3. Write atomically.
        foreach (var edit in edits)
        {
            WriteText(edit.File, edit.Text, edit.HasBom);
            result.FilesWritten++;
            result.ColumnsChanged += edit.Changed;
            result.ChangedFiles.Add(edit.File);
        }

        return result;
    }

    /// <summary>Splits a TMDL file, rewrites the selected column blocks and re-joins it.</summary>
    private static (string? Text, int Changed) ApplyToFile(string text, IReadOnlyList<SortByColumnPair> pairs)
    {
        var newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var hadTrailing = text.EndsWith("\n", StringComparison.Ordinal);

        var raw = text.Split('\n');
        var lines = new List<string>(raw.Length);
        foreach (var line in raw)
        {
            lines.Add(line.EndsWith("\r", StringComparison.Ordinal) ? line[..^1] : line);
        }

        if (hadTrailing && lines.Count > 0 && lines[^1].Length == 0)
        {
            lines.RemoveAt(lines.Count - 1);
        }

        var blocks = FindColumnBlocks(lines);
        var operations = new List<(int Start, int End, List<string> Replacement)>();
        var changed = 0;

        foreach (var pair in pairs)
        {
            var baseBlock = blocks.FirstOrDefault(b => string.Equals(b.Name, pair.BaseColumn, StringComparison.OrdinalIgnoreCase));
            var orderBlock = blocks.FirstOrDefault(b => string.Equals(b.Name, pair.OrderColumn, StringComparison.OrdinalIgnoreCase));
            if (baseBlock == null || orderBlock == null) continue;

            var newBase = BuildBaseBlock(lines.GetRange(baseBlock.Start, baseBlock.End - baseBlock.Start), baseBlock.PropertyIndent, pair.OrderColumn);
            var newOrder = BuildOrderBlock(lines.GetRange(orderBlock.Start, orderBlock.End - orderBlock.Start), orderBlock.PropertyIndent);

            operations.Add((baseBlock.Start, baseBlock.End, newBase));
            operations.Add((orderBlock.Start, orderBlock.End, newOrder));
            changed++;
        }

        if (changed == 0) return (null, 0);

        foreach (var op in operations.OrderByDescending(o => o.Start))
        {
            lines.RemoveRange(op.Start, op.End - op.Start);
            lines.InsertRange(op.Start, op.Replacement);
        }

        var output = string.Join(newline, lines);
        if (hadTrailing) output += newline;
        return (output, changed);
    }

    /// <summary>Rewrites the order column: whole number, formatString 0 and the PBI change marker.</summary>
    private static List<string> BuildOrderBlock(List<string> block, string propertyIndent)
    {
        var result = new List<string>(block);

        var dataTypeIndex = FindProperty(result, propertyIndent, "dataType:");
        if (dataTypeIndex >= 0)
        {
            result[dataTypeIndex] = propertyIndent + "dataType: int64";
        }
        else
        {
            dataTypeIndex = FirstPropertyIndex(result);
            result.Insert(dataTypeIndex, propertyIndent + "dataType: int64");
        }

        if (FindProperty(result, propertyIndent, "formatString:") < 0)
        {
            result.Insert(dataTypeIndex + 1, propertyIndent + "formatString: 0");
        }

        AddChangedProperty(result, propertyIndent, "DataType");
        return result;
    }

    /// <summary>Rewrites the base column: point it at the order column and add the change marker.</summary>
    private static List<string> BuildBaseBlock(List<string> block, string propertyIndent, string orderColumn)
    {
        var result = new List<string>(block);
        var line = propertyIndent + "sortByColumn: " + QuoteName(orderColumn);

        var sortIndex = FindProperty(result, propertyIndent, "sortByColumn:");
        if (sortIndex >= 0)
        {
            result[sortIndex] = line;
        }
        else
        {
            var anchor = FindProperty(result, propertyIndent, "sourceColumn:");
            if (anchor < 0) anchor = FindProperty(result, propertyIndent, "dataType:");
            var insertAt = anchor >= 0 ? anchor + 1 : FirstPropertyIndex(result);
            result.Insert(insertAt, line);
        }

        AddChangedProperty(result, propertyIndent, "SortByColumn");
        return result;
    }

    /// <summary>
    /// Adds a <c>changedProperty = X</c> marker as its own blank-line-separated paragraph,
    /// immediately before the column's annotations (or at the end when there are none).
    /// </summary>
    private static void AddChangedProperty(List<string> lines, string propertyIndent, string marker)
    {
        var target = propertyIndent + "changedProperty = " + marker;
        if (lines.Any(l => string.Equals(l.TrimEnd(), target, StringComparison.Ordinal))) return;

        var insertAt = lines.Count;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(propertyIndent + "annotation ", StringComparison.Ordinal))
            {
                insertAt = i;
                break;
            }
        }

        if (insertAt > 0 && lines[insertAt - 1].Length != 0)
        {
            lines.Insert(insertAt, string.Empty);
            insertAt++;
        }

        lines.Insert(insertAt, target);
        lines.Insert(insertAt + 1, string.Empty);
    }

    /// <summary>The index of the first property line, skipping a multi-line expression block.</summary>
    private static int FirstPropertyIndex(List<string> block)
    {
        if (block.Count > 1 && HasOddFence(block[0]))
        {
            for (var i = 1; i < block.Count; i++)
            {
                if (HasOddFence(block[i])) return i + 1;
            }
        }

        return block.Count > 0 ? 1 : 0;
    }

    private static int FindProperty(List<string> lines, string propertyIndent, string prefix)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].StartsWith(propertyIndent + prefix, StringComparison.Ordinal)) return i;
        }

        return -1;
    }

    // ------------------------------------------------------------- TMDL parse

    /// <summary>One <c>column</c> block located in a file.</summary>
    private sealed class ColumnBlock
    {
        public string Name { get; init; } = string.Empty;
        public int Start { get; init; }
        public int End { get; init; }
        public string PropertyIndent { get; init; } = "\t\t";
    }

    private static List<ColumnBlock> FindColumnBlocks(List<string> lines)
    {
        var blocks = new List<ColumnBlock>();
        var columnIndent = -1;
        var inFence = false;

        for (var i = 0; i < lines.Count; i++)
        {
            var line = lines[i];
            var trimmed = line.TrimStart();

            if (inFence)
            {
                if (HasOddFence(line)) inFence = false;
                continue;
            }

            if (trimmed.Length == 0) continue;

            var indent = LeadingTabs(line);
            if (!trimmed.StartsWith("column ", StringComparison.Ordinal)) continue;
            if (columnIndent >= 0 && indent != columnIndent) continue;

            if (columnIndent < 0) columnIndent = indent;

            var end = i + 1;
            var fence = HasOddFence(line);
            for (; end < lines.Count; end++)
            {
                var next = lines[end];
                if (fence)
                {
                    if (HasOddFence(next)) fence = false;
                    continue;
                }

                if (next.TrimStart().Length == 0) continue;
                if (LeadingTabs(next) <= indent) break;
                if (HasOddFence(next)) fence = true;
            }

            var name = ExtractColumnName(trimmed);
            if (name.Length > 0)
            {
                blocks.Add(new ColumnBlock
                {
                    Name = name,
                    Start = i,
                    End = end,
                    PropertyIndent = new string('\t', indent) + "\t"
                });
            }

            i = end - 1;
        }

        return blocks;
    }

    private static SemanticModelTable? ParseTableFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            return null;
        }

        var tableName = string.Empty;
        var isPrivate = false;
        var columns = new List<SemanticModelColumn>();
        var columnBlocks = FindColumnBlocks(lines.ToList());

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();
            if (trimmed.StartsWith("table ", StringComparison.Ordinal))
            {
                var match = TableHeaderRegex.Match(trimmed);
                if (match.Success) tableName = UnquoteName(match.Groups[1].Value);
                break;
            }
        }

        if (tableName.Length == 0) return null;

        // isPrivate is a table-level flag on its own tab-indented line.
        foreach (var line in lines)
        {
            if (string.Equals(line.Trim(), "isPrivate", StringComparison.Ordinal))
            {
                isPrivate = true;
                break;
            }
        }

        foreach (var block in columnBlocks)
        {
            columns.Add(ParseColumnBlock(lines, block));
        }

        return new SemanticModelTable
        {
            Name = tableName,
            FilePath = path,
            Columns = columns,
            IsPrivate = isPrivate
        };
    }

    private static SemanticModelColumn ParseColumnBlock(string[] lines, ColumnBlock block)
    {
        var header = lines[block.Start].TrimStart();
        var isCalculated = CalculatedColumnRegex.IsMatch(header);

        string dataType = string.Empty, sortBy = string.Empty;
        bool hasDataType = false, hasFormatString = false, hasSortBy = false, hasSource = false;

        var fence = HasOddFence(lines[block.Start]);
        for (var i = block.Start + 1; i < block.End; i++)
        {
            var line = lines[i];
            if (fence)
            {
                if (HasOddFence(line)) fence = false;
                continue;
            }

            if (HasOddFence(line))
            {
                fence = true;
                continue;
            }

            if (!line.StartsWith(block.PropertyIndent, StringComparison.Ordinal)) continue;
            var rest = line[block.PropertyIndent.Length..];

            if (rest.StartsWith("dataType:", StringComparison.Ordinal))
            {
                hasDataType = true;
                dataType = rest["dataType:".Length..].Trim();
            }
            else if (rest.StartsWith("sortByColumn:", StringComparison.Ordinal))
            {
                hasSortBy = true;
                sortBy = UnquoteName(rest["sortByColumn:".Length..].Trim());
            }
            else if (rest.StartsWith("formatString:", StringComparison.Ordinal))
            {
                hasFormatString = true;
            }
            else if (rest.StartsWith("sourceColumn:", StringComparison.Ordinal))
            {
                hasSource = true;
            }
        }

        return new SemanticModelColumn
        {
            Name = block.Name,
            DataType = dataType,
            HasDataType = hasDataType,
            HasFormatString = hasFormatString,
            SortByColumn = sortBy,
            HasSortByColumn = hasSortBy,
            HasSourceColumn = hasSource,
            IsCalculated = isCalculated
        };
    }

    private static string ExtractColumnName(string header)
    {
        var match = ColumnNameRegex.Match(header);
        return match.Success ? UnquoteName(match.Groups[1].Value) : string.Empty;
    }

    private static int LeadingTabs(string line)
    {
        var count = 0;
        while (count < line.Length && line[count] == '\t') count++;
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

    private static string UnquoteName(string raw)
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

    /// <summary>Quotes a TMDL identifier only when it is not a plain name.</summary>
    public static string QuoteName(string name) =>
        Regex.IsMatch(name, "^[A-Za-z_][A-Za-z0-9_]*$") ? name : "'" + name.Replace("'", "''") + "'";

    // ----------------------------------------------------------- File IO

    private static (string Text, bool HasBom) ReadText(string path)
    {
        var bytes = File.ReadAllBytes(path);
        var hasBom = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF;
        var offset = hasBom ? 3 : 0;
        return (Encoding.UTF8.GetString(bytes, offset, bytes.Length - offset), hasBom);
    }

    private static void WriteText(string path, string text, bool hasBom)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, text, new UTF8Encoding(hasBom));
        File.Move(temp, path, overwrite: true);
    }

    // -------------------------------------------------------------- Backups
    // The snapshot logic lives in ModelBackupStore so every model-editing tool
    // (Sort by Column, Object Security) shares the same undo target.

    /// <summary>A stable, filesystem-safe key for a project's backup folder.</summary>
    public static string ProjectKey(string projectRoot) => ModelBackupStore.ProjectKey(projectRoot);

    public static string BackupFolderFor(string projectRoot) => ModelBackupStore.BackupFolderFor(projectRoot);

    public static string? LatestBackupFolder(string projectRoot) => ModelBackupStore.LatestBackupFolder(projectRoot);

    /// <summary>Restores the files captured by the most recent apply. Returns how many were written.</summary>
    public static int RestoreLatestBackup(string projectRoot, out string? restoredFrom)
        => ModelBackupStore.RestoreLatestBackup(projectRoot, out restoredFrom);
}
