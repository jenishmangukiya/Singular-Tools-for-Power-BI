using System.Text;
using System.Text.RegularExpressions;

namespace SingularTools.Core;

/// <summary>
/// What a report field reference resolved to. A reference with no property part
/// (<c>financials</c>) is a table; <c>financials[Sales]</c> is a column;
/// <c>financials[Total Sales]</c> is a measure. The distinction matters to the caller:
/// "the table is gone" and "the table is there but this column is not" are different
/// problems with different fixes.
/// </summary>
public enum SemanticFieldKind
{
    /// <summary>A table (entity) itself, i.e. a reference with no property part.</summary>
    Table,

    /// <summary>A column of a table. DAX calculated columns are columns too.</summary>
    Column,

    /// <summary>A measure.</summary>
    Measure
}

/// <summary>
/// A read-only index of a TMDL semantic model that answers one question: does this field
/// reference still resolve? It exists for the Field Repair tool, which walks the report's
/// PBIR field references and reports the ones pointing at columns or measures the database
/// administrator renamed away.
///
/// It deliberately does not reuse <see cref="SortByColumnService.LoadModel"/> even though
/// that is the codebase's single table reader. The two answer different questions:
///
/// <list type="bullet">
/// <item><c>LoadModel</c> drives a <i>picker</i>: it presents the model's tables to a human
/// and so hides auto-generated <c>isPrivate</c> date tables, exactly as Power BI's model
/// view does.</item>
/// <item>This index answers <i>does it exist</i>. A visual legitimately references a
/// <c>DateTableTemplate_*</c> / <c>LocalDateTable_*</c> column, and Power BI writes those
/// references into the report even though the author never picked them from a list. Skipping
/// private tables here would report every such visual as broken — a false positive that
/// would send users "fixing" reports that are perfectly fine. Hence
/// <see cref="Load(string, bool)"/>'s <c>includePrivate</c> defaults to <c>true</c>.</item>
/// </list>
///
/// The index is also read-only and non-throwing: a report whose model is half written, or
/// whose TMDL is malformed, must still produce a usable (if incomplete) answer instead of an
/// exception, because a broken index would mark every visual in the report as broken.
/// </summary>
public sealed class SemanticModelIndex
{
    private static readonly Regex TableHeaderRegex =
        new(@"^table\s+('(?:[^']|'')*'|""[^""]*""|[^\s]+)", RegexOptions.Compiled);

    // The bare-name alternative deliberately excludes '=' so that a DAX calculated column's
    // declaration line — `column 'Discount Band_ord_2' = ``` ... ``` ` — yields the name and
    // not the expression. That is why no separate CalculatedColumnRegex is needed here the way
    // SortByColumnService needs one to *classify* a column as calculated.
    private static readonly Regex ColumnNameRegex =
        new(@"^column\s+('(?:[^']|'')*'|""[^""]*""|[^\s=]+)", RegexOptions.Compiled);

    private static readonly Regex MeasureNameRegex =
        new(@"^measure\s+('(?:[^']|'')*'|""[^""]*""|[^\s=]+)", RegexOptions.Compiled);

    private readonly Dictionary<string, IndexedTable> _tables;
    private readonly Dictionary<string, List<IndexedTable>> _tablesByNormalizedName;
    private readonly Dictionary<string, List<IndexedField>> _fieldsByNormalizedName;
    private readonly IReadOnlyList<string> _tableNames;

    private SemanticModelIndex(
        string folderPath,
        Dictionary<string, IndexedTable> tables,
        Dictionary<string, List<IndexedTable>> tablesByNormalizedName,
        Dictionary<string, List<IndexedField>> fieldsByNormalizedName)
    {
        FolderPath = folderPath;
        _tables = tables;
        _tablesByNormalizedName = tablesByNormalizedName;
        _fieldsByNormalizedName = fieldsByNormalizedName;

        var ordered = tables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase).ToList();
        _tableNames = ordered.Select(t => t.Name).ToList();

        foreach (var table in ordered)
        {
            table.Freeze();
        }
    }

    /// <summary>The model folder that was indexed, empty when none could be resolved.</summary>
    public string FolderPath { get; }

    /// <summary>
    /// False when nothing could be indexed — no <c>definition/tables</c> folder, or every
    /// table file in it was unreadable. Callers must treat this as "the model could not be
    /// read" and stay silent rather than reporting every visual as broken.
    /// </summary>
    public bool HasTmdlDefinition => _tableNames.Count > 0;

    /// <summary>Every indexed table name, sorted case-insensitively.</summary>
    public IReadOnlyList<string> TableNames => _tableNames;

    // ----------------------------------------------------------------- Loading

    /// <summary>
    /// Indexes <c>definition/tables/*.tmdl</c> under <paramref name="modelFolder"/>.
    /// Never throws and never writes.
    ///
    /// <paramref name="includePrivate"/> defaults to <c>true</c> because this index answers
    /// "does this reference resolve", not "what may I show the user". Power BI writes
    /// <c>DateTableTemplate_*</c> and <c>LocalDateTable_*</c> references into report PBIR on
    /// the author's behalf, so excluding those tables would mark working visuals as broken.
    /// Pass <c>false</c> only when the index feeds a picker that must mirror Power BI's model
    /// view.
    ///
    /// Files are read in ordinal-ignore-case filename order, matching
    /// <see cref="SortByColumnService.LoadModel"/>, so the reported candidates are stable
    /// between runs.
    /// </summary>
    public static SemanticModelIndex Load(string modelFolder, bool includePrivate = true)
    {
        var tables = new Dictionary<string, IndexedTable>(StringComparer.OrdinalIgnoreCase);

        var folder = string.Empty;
        try
        {
            if (!string.IsNullOrWhiteSpace(modelFolder)) folder = Path.GetFullPath(modelFolder);
        }
        catch
        {
            // An unusable path is just an empty index, not a crash.
            folder = string.Empty;
        }

        if (folder.Length > 0)
        {
            foreach (var file in EnumerateTableFiles(folder))
            {
                IndexedTable? parsed;
                try
                {
                    parsed = ParseTableFile(file);
                }
                catch
                {
                    parsed = null;
                }

                if (parsed == null) continue;
                if (!includePrivate && parsed.IsPrivate) continue;

                if (tables.TryGetValue(parsed.Name, out var existing))
                {
                    // Two files declaring the same table is malformed TMDL. Merging keeps the
                    // fields resolvable instead of letting file order silently drop half a
                    // table.
                    existing.IsPrivate |= parsed.IsPrivate;
                    foreach (var pair in parsed.Fields) existing.Add(pair.Key, pair.Value);
                    continue;
                }

                tables[parsed.Name] = parsed;
            }
        }

        var tablesByNormalized = new Dictionary<string, List<IndexedTable>>(StringComparer.OrdinalIgnoreCase);
        var fieldsByNormalized = new Dictionary<string, List<IndexedField>>(StringComparer.OrdinalIgnoreCase);

        foreach (var table in tables.Values.OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var tableKey = NormalizeName(table.Name);
            if (tableKey.Length > 0)
            {
                if (!tablesByNormalized.TryGetValue(tableKey, out var tableMatches))
                {
                    tableMatches = new List<IndexedTable>();
                    tablesByNormalized[tableKey] = tableMatches;
                }

                tableMatches.Add(table);
            }

            foreach (var field in table.Fields)
            {
                var fieldKey = NormalizeName(field.Key);
                if (fieldKey.Length == 0) continue;

                if (!fieldsByNormalized.TryGetValue(fieldKey, out var fieldMatches))
                {
                    fieldMatches = new List<IndexedField>();
                    fieldsByNormalized[fieldKey] = fieldMatches;
                }

                fieldMatches.Add(new IndexedField(table.Name, field.Key, field.Value));
            }
        }

        return new SemanticModelIndex(folder, tables, tablesByNormalized, fieldsByNormalized);
    }

    /// <summary>
    /// Indexes the model that belongs to a report or project folder, reusing
    /// <see cref="SortByColumnService.DiscoverModelFolder"/> so there is one definition of
    /// "the model next to this report". Returns an empty index when no TMDL model is found
    /// (for example a report paired with a <c>model.bim</c>).
    /// </summary>
    public static SemanticModelIndex LoadForReport(string? folderPath, bool includePrivate = true)
    {
        var modelFolder = SortByColumnService.DiscoverModelFolder(folderPath);
        return string.IsNullOrWhiteSpace(modelFolder)
            ? new SemanticModelIndex(
                string.Empty,
                new Dictionary<string, IndexedTable>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, List<IndexedTable>>(StringComparer.OrdinalIgnoreCase),
                new Dictionary<string, List<IndexedField>>(StringComparer.OrdinalIgnoreCase))
            : Load(modelFolder!, includePrivate);
    }

    // --------------------------------------------------------------- Resolution

    /// <summary>
    /// Resolves an <c>entity</c> + <c>property</c> pair, case-insensitively. An empty
    /// <paramref name="property"/> resolves the entity itself and reports
    /// <see cref="SemanticFieldKind.Table"/>.
    ///
    /// Surrounding whitespace is ignored. On failure <paramref name="kind"/> is undefined and
    /// must not be read; use <see cref="TableExists"/> to tell a missing table from a missing
    /// field.
    /// </summary>
    public bool TryResolve(string entity, string property, out SemanticFieldKind kind)
    {
        kind = SemanticFieldKind.Table;

        var table = FindTable(entity);
        if (table == null) return false;

        var name = property?.Trim();
        if (string.IsNullOrEmpty(name)) return true;

        return table.Fields.TryGetValue(name!, out kind);
    }

    /// <summary>True when the entity is a known table. This is the MissingTable check.</summary>
    public bool TableExists(string entity) => FindTable(entity) != null;

    /// <summary>True when the entity has a column (calculated columns included) of that name.</summary>
    public bool ColumnExists(string entity, string property) =>
        TryResolve(entity, property, out var kind) && kind == SemanticFieldKind.Column;

    /// <summary>True when the entity has a measure of that name.</summary>
    public bool MeasureExists(string entity, string property) =>
        TryResolve(entity, property, out var kind) && kind == SemanticFieldKind.Measure;

    // ------------------------------------------------------------------ Pickers

    /// <summary>
    /// Column names of an entity, sorted case-insensitively, for a replacement picker. Empty
    /// for an unknown entity — never null, never throwing.
    /// </summary>
    public IReadOnlyList<string> GetColumns(string entity) => FindTable(entity)?.Columns ?? Array.Empty<string>();

    /// <summary>Measure names of an entity, sorted case-insensitively. Empty when it has none.</summary>
    public IReadOnlyList<string> GetMeasures(string entity) => FindTable(entity)?.Measures ?? Array.Empty<string>();

    // ------------------------------------------------------- Cosmetic-rename fix

    /// <summary>
    /// Collapses a field or table name to its comparison key: lower-cased, with every
    /// whitespace, underscore and punctuation character dropped. <c>"Gross Sales"</c>,
    /// <c>"gross_sales"</c> and <c>"GROSSSALES"</c> all become <c>"grosssales"</c>.
    ///
    /// This is what a DBA rename usually looks like from the report's point of view — the
    /// source column was re-spelled or re-cased, not replaced — so it separates "the field is
    /// gone" from "the field is here under a different spelling". It is deliberately lossy and
    /// is only ever used to <i>suggest</i> a replacement; the suggestion still goes through
    /// the user, because a lossy comparison can collide across columns.
    /// </summary>
    public static string NormalizeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var buffer = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            if (char.IsLetterOrDigit(ch)) buffer.Append(char.ToLowerInvariant(ch));
        }

        return buffer.ToString();
    }

    /// <summary>
    /// Resolves a field whose *normalized* name matches, so a cosmetic DBA rename
    /// (<c>Gross Sales</c> → <c>GrossSales</c>) can be spotted and auto-fixed.
    /// Candidates in the named entity win over candidates in any other table; pass
    /// <see cref="TryResolveNormalized(string, string, bool, out string, out string, out SemanticFieldKind)"/>
    /// to forbid the cross-table fallback.
    /// </summary>
    public bool TryResolveNormalized(
        string entity,
        string normalizedProperty,
        out string actualEntity,
        out string actualProperty,
        out SemanticFieldKind kind) =>
        TryResolveNormalized(entity, normalizedProperty, true, out actualEntity, out actualProperty, out kind);

    /// <summary>
    /// Resolves a field by <see cref="NormalizeName"/> equality. <paramref name="normalizedProperty"/>
    /// is normalized again internally, so passing a raw name is fine.
    ///
    /// Search order: the named entity first, then — only when <paramref name="allowOtherTables"/>
    /// is set and nothing matched in that entity — any other table. Same-entity-first matters
    /// because a rename keeps a field in its table, whereas a cross-table hit can be a
    /// coincidence of the lossy comparison. Ties between a column and a measure of the same
    /// normalized name go to the column: report references are overwhelmingly columns and
    /// measure names are far more likely to collide.
    ///
    /// The entity is matched case-insensitively and, failing that, by its own normalized name
    /// — but only when that name is unambiguous, because guessing between two tables would
    /// point a rename at the wrong table.
    ///
    /// On failure all outs are <see cref="string.Empty"/> / the default kind.
    /// </summary>
    public bool TryResolveNormalized(
        string entity,
        string normalizedProperty,
        bool allowOtherTables,
        out string actualEntity,
        out string actualProperty,
        out SemanticFieldKind kind)
    {
        actualEntity = string.Empty;
        actualProperty = string.Empty;
        kind = SemanticFieldKind.Table;

        var key = NormalizeName(normalizedProperty);
        if (key.Length == 0) return false;
        if (!_fieldsByNormalizedName.TryGetValue(key, out var candidates) || candidates.Count == 0) return false;

        var table = FindTableForNormalizedMatch(entity);

        var match = default(IndexedField);
        var found = false;
        if (table != null)
        {
            foreach (var candidate in candidates)
            {
                if (!string.Equals(candidate.Entity, table.Name, StringComparison.OrdinalIgnoreCase)) continue;
                if (!found || candidate.Kind == SemanticFieldKind.Column)
                {
                    match = candidate;
                    found = true;
                }

                if (candidate.Kind == SemanticFieldKind.Column) break;
            }
        }

        if (!found && allowOtherTables)
        {
            foreach (var candidate in candidates)
            {
                if (!found || candidate.Kind == SemanticFieldKind.Column)
                {
                    match = candidate;
                    found = true;
                }

                if (candidate.Kind == SemanticFieldKind.Column) break;
            }
        }

        if (!found) return false;

        actualEntity = match.Entity;
        actualProperty = match.Property;
        kind = match.Kind;
        return true;
    }

    // -------------------------------------------------------------------- TMDL

    /// <summary>The <c>*.tmdl</c> files of a model folder, in a stable order. Never throws.</summary>
    private static IEnumerable<string> EnumerateTableFiles(string modelFolder)
    {
        var tablesDir = Path.Combine(modelFolder, "definition", "tables");

        string[] files;
        try
        {
            if (!Directory.Exists(tablesDir)) return Array.Empty<string>();
            files = Directory.GetFiles(tablesDir, "*.tmdl");
        }
        catch
        {
            return Array.Empty<string>();
        }

        return files.OrderBy(f => f, StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>
    /// Reads one table file. Returns null when the file is unreadable or holds no table
    /// header; the caller simply moves on to the next file.
    /// </summary>
    private static IndexedTable? ParseTableFile(string path)
    {
        string[] lines;
        try
        {
            lines = File.ReadAllLines(path);
        }
        catch
        {
            // Locked, deleted mid-scan, or not text at all: one bad file must not cost us
            // the whole index.
            return null;
        }

        var name = string.Empty;

        // The header is the first unindented `table` line. Anything else unindented (a
        // top-level property in a malformed file) is ignored rather than guessed at.
        foreach (var line in lines)
        {
            if (LeadingTabs(line) != 0) continue;

            var trimmed = line.TrimStart();
            if (!trimmed.StartsWith("table ", StringComparison.Ordinal)) continue;

            var match = TableHeaderRegex.Match(trimmed);
            if (match.Success) name = UnquoteName(match.Groups[1].Value);
            break;
        }

        if (name.Length == 0) return null;

        // isPrivate is a bare, tab-indented table-level flag, exactly as
        // SortByColumnService.ParseTableFile detects it, so both readers agree on which tables
        // Power BI auto-generated.
        var isPrivate = false;
        foreach (var line in lines)
        {
            if (string.Equals(line.Trim(), "isPrivate", StringComparison.Ordinal))
            {
                isPrivate = true;
                break;
            }
        }

        var table = new IndexedTable { Name = name, IsPrivate = isPrivate };
        var inFence = false;

        foreach (var line in lines)
        {
            var trimmed = line.TrimStart();

            if (inFence)
            {
                if (HasOddFence(line)) inFence = false;
                continue;
            }

            if (trimmed.Length == 0) continue;

            // TMDL indents table members (column, measure, partition, ...) with exactly one
            // tab; anything deeper is a property of the member above it. Requiring that
            // indent is what keeps a `column` word inside a DAX or M expression from being
            // mistaken for a declaration.
            if (LeadingTabs(line) == 1)
            {
                var column = ColumnNameRegex.Match(trimmed);
                if (column.Success)
                {
                    table.Add(UnquoteName(column.Groups[1].Value), SemanticFieldKind.Column);
                }
                else
                {
                    var measure = MeasureNameRegex.Match(trimmed);
                    if (measure.Success)
                    {
                        table.Add(UnquoteName(measure.Groups[1].Value), SemanticFieldKind.Measure);
                    }
                }
            }

            // A calculated column or measure opens its DAX on the declaration line with an odd
            // number of ``` fences, so the toggle has to come after the declaration above —
            // otherwise its body would be scanned for members and the closing fence never
            // found.
            if (HasOddFence(line)) inFence = true;
        }

        return table;
    }

    private IndexedTable? FindTable(string entity)
    {
        var name = entity?.Trim();
        if (string.IsNullOrEmpty(name)) return null;

        return _tables.TryGetValue(name!, out var table) ? table : null;
    }

    /// <summary>
    /// Exact match, else a unique normalized match so a renamed table
    /// (<c>"Sales Data"</c> → <c>"SalesData"</c>) still matches. Ambiguity resolves to null
    /// on purpose: a wrong table guess would propose a replacement in the wrong place.
    /// </summary>
    private IndexedTable? FindTableForNormalizedMatch(string entity)
    {
        var exact = FindTable(entity);
        if (exact != null) return exact;

        var key = NormalizeName(entity);
        if (key.Length == 0) return null;

        if (!_tablesByNormalizedName.TryGetValue(key, out var matches) || matches.Count != 1) return null;
        return matches[0];
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

    // ------------------------------------------------------------------- Internals

    private readonly struct IndexedField
    {
        public IndexedField(string entity, string property, SemanticFieldKind kind)
        {
            Entity = entity;
            Property = property;
            Kind = kind;
        }

        public string Entity { get; }
        public string Property { get; }
        public SemanticFieldKind Kind { get; }
    }

    /// <summary>One table's fields, accumulated while its file is read.</summary>
    private sealed class IndexedTable
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>Auto-generated (<c>DateTableTemplate_*</c>) tables Power BI hides from authors.</summary>
        public bool IsPrivate { get; set; }

        /// <summary>Every column and measure of the table, keyed case-insensitively.</summary>
        public Dictionary<string, SemanticFieldKind> Fields { get; } = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>Sorted column names, populated by <see cref="Freeze"/>.</summary>
        public IReadOnlyList<string> Columns { get; private set; } = Array.Empty<string>();

        /// <summary>Sorted measure names, populated by <see cref="Freeze"/>.</summary>
        public IReadOnlyList<string> Measures { get; private set; } = Array.Empty<string>();

        public void Add(string name, SemanticFieldKind kind)
        {
            if (string.IsNullOrEmpty(name)) return;
            if (Fields.ContainsKey(name)) return; // first declaration wins

            Fields[name] = kind;
        }

        /// <summary>Sorts the picker lists once the table is fully read.</summary>
        public void Freeze()
        {
            Columns = Fields
                .Where(f => f.Value == SemanticFieldKind.Column)
                .Select(f => f.Key)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();

            Measures = Fields
                .Where(f => f.Value == SemanticFieldKind.Measure)
                .Select(f => f.Key)
                .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
