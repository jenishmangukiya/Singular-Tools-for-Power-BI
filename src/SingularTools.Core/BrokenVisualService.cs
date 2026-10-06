using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SingularTools.Core.Models;

namespace SingularTools.Core;

/// <summary>Why a field reference cannot be resolved against the semantic model.</summary>
public enum BrokenFieldSeverity
{
    /// <summary>The whole table is gone from the model.</summary>
    MissingTable,

    /// <summary>The table is still there but no longer has this column or measure.</summary>
    MissingField
}

/// <summary>Which part of a visual a field reference was found in.</summary>
public enum BrokenReferenceKind
{
    /// <summary>A <c>query.queryState</c> role binding (axis, legend, values, tooltip).</summary>
    DataRole,

    /// <summary>A <c>query.sortDefinition</c> sort field.</summary>
    SortDefinition,

    /// <summary>
    /// A filter scoped to one visual (<c>visual.filterConfig</c>). Sits beside the visual's
    /// data roles rather than inside its format objects, so it is its own kind.
    /// </summary>
    VisualFilter,

    /// <summary>
    /// A conditional formatting or color rule selector. These are the references a
    /// queryState-only scan misses, and they break silently: the visual still renders,
    /// but the rule stops matching.
    /// </summary>
    FormatSelector,

    /// <summary>
    /// A <c>selector.metadata</c> series-identity key, which is a plain
    /// "<c>table.field</c>" string rather than a field node. Points a color rule at one
    /// specific series, so it also dies quietly when the field is renamed.
    /// </summary>
    SeriesIdentity,

    /// <summary>
    /// A page-level filter (<c>page.json</c> → <c>filterConfig</c>). It lives on the page
    /// rather than in a visual, so it outlives the visual and breaks on its own schedule.
    /// </summary>
    PageFilter,

    /// <summary>
    /// A report-level filter (<c>report.json</c> → <c>filterConfig</c>), applied to every
    /// page. Reported but deliberately not repaired — see <see cref="RemapResult"/>.
    /// </summary>
    ReportFilter,

    /// <summary>Any other reference found by the tree walk, e.g. a hierarchy level.</summary>
    Other
}

/// <summary>What a group of references belongs to.</summary>
public enum BrokenUsageScope
{
    /// <summary>A visual in the report.</summary>
    Visual,

    /// <summary>A filter scoped to one page (<c>page.json</c> → <c>filterConfig</c>).</summary>
    PageFilter,

    /// <summary>A filter applied to every page (<c>report.json</c> → <c>filterConfig</c>).</summary>
    ReportFilter
}

/// <summary>One place a field is referenced within a single visual, with its repeat count.</summary>
public sealed class BrokenUsageLocation
{
    public BrokenUsageLocation(string label, int count)
    {
        Label = label;
        Count = count;
    }

    /// <summary>Precise description, e.g. "Axis · Category" or "Colour rules".</summary>
    public string Label { get; }

    /// <summary>How many separate references collapsed into this label.</summary>
    public int Count { get; }

    /// <summary>"Colour rules ×8" when repeated, otherwise just the label.</summary>
    public string Display => Count > 1 ? $"{Label} ×{Count}" : Label;
}

/// <summary>
/// One place a single missing field is used — a visual, a page filter or a report filter — with
/// every repeat inside it collapsed into a counted location.
/// </summary>
/// <remarks>
/// A field used by a chart's axis and by eight colour rules is nine raw references but only two
/// places a reader cares about. Grouping keeps the detail list at one row per place instead of
/// flooding it with identical-looking lines.
///
/// A page filter and a report filter both belong to no visual, so grouping on the visual id
/// alone would merge them into one row and silently swallow one of them. The group key therefore
/// separates them.
/// </remarks>
public sealed class BrokenVisualUsageGroup
{
    public BrokenVisualUsageGroup(string groupKey, IEnumerable<BrokenFieldUsage> usages)
    {
        var list = usages.ToList();
        var first = list[0];

        GroupKey = groupKey;
        Scope = first.Kind switch
        {
            BrokenReferenceKind.ReportFilter => BrokenUsageScope.ReportFilter,
            BrokenReferenceKind.PageFilter => BrokenUsageScope.PageFilter,
            _ => BrokenUsageScope.Visual
        };

        PageId = first.PageId;
        PageDisplayName = first.PageDisplayName;
        VisualType = first.VisualType;
        VisualTitle = first.VisualTitle;
        UsageCount = list.Count;

        Locations = list
            .GroupBy(u => u.LocationLabel, StringComparer.Ordinal)
            .Select(g => new BrokenUsageLocation(g.Key, g.Count()))
            .OrderBy(l => LocationRank(l.Label))
            .ThenBy(l => l.Label, StringComparer.Ordinal)
            .ToList();
    }

    /// <summary>The grouping key: a visual id, or a page filter's page id, or the report.</summary>
    public string GroupKey { get; }

    public BrokenUsageScope Scope { get; }

    public string PageId { get; }

    public string PageDisplayName { get; }

    public string VisualType { get; }

    public string VisualTitle { get; }

    /// <summary>Total raw references this group stands for.</summary>
    public int UsageCount { get; }

    public IReadOnlyList<BrokenUsageLocation> Locations { get; }

    /// <summary>True when this visual carried its own title rather than one being inferred.</summary>
    private bool HasOwnTitle => !string.IsNullOrWhiteSpace(VisualTitle);

    /// <summary>
    /// The row heading. A filter names itself, because "Page level filter" is more useful than
    /// a page name the subtitle already carries. An untitled visual names its type, since there
    /// is nothing else to call it.
    /// </summary>
    public string Title => Scope switch
    {
        BrokenUsageScope.ReportFilter => "Report level filter",
        BrokenUsageScope.PageFilter => "Page level filter",
        _ => HasOwnTitle ? VisualTitle : $"{TypeLabel} visual"
    };

    /// <summary>
    /// The row's second line: which page, and — when the title is the visual's own — what kind
    /// of visual it is. Reporting the type twice would waste the line.
    /// </summary>
    public string Subtitle => Scope switch
    {
        BrokenUsageScope.ReportFilter => "Applies to every page",
        BrokenUsageScope.PageFilter => DescribePage(),
        _ => HasOwnTitle ? $"{DescribePage()} · {TypeLabel} visual" : DescribePage()
    };

    private string DescribePage() =>
        string.IsNullOrWhiteSpace(PageDisplayName) ? "Unknown page" : $"Page \u201C{PageDisplayName}\u201D";

    /// <summary>
    /// Whether the counted-location line adds anything. For a filter row the heading already
    /// says what it is, so repeating it underneath would be noise.
    /// </summary>
    public bool ShowLocations => Scope == BrokenUsageScope.Visual;

    public string TypeLabel => Scope == BrokenUsageScope.Visual
        ? VisualItemInfo.FormatVisualType(VisualType)
        : "Filter";

    /// <summary>"Axis · Category  ·  Colour rules ×8".</summary>
    public string LocationSummary => string.Join("  ·  ", Locations.Select(l => l.Display));

    /// <summary>
    /// Orders locations the way a reader thinks about a visual — what it plots, then how it
    /// is sorted, then the formatting that merely decorates it.
    /// </summary>
    private static int LocationRank(string label)
    {
        if (label.StartsWith("Axis", StringComparison.Ordinal)) return 0;
        if (label.StartsWith("Legend", StringComparison.Ordinal)) return 1;
        if (label.StartsWith("Values", StringComparison.Ordinal)) return 2;
        if (label.StartsWith("Row", StringComparison.Ordinal)) return 3;
        if (label.StartsWith("Tooltip", StringComparison.Ordinal)) return 4;
        if (label.StartsWith("Sort", StringComparison.Ordinal)) return 5;
        if (label.StartsWith("Visual filter", StringComparison.Ordinal)) return 6;
        if (label.StartsWith("Page level filter", StringComparison.Ordinal)) return 7;
        if (label.StartsWith("Report level filter", StringComparison.Ordinal)) return 8;
        if (label.StartsWith("Colour", StringComparison.Ordinal)) return 9;
        return 10;
    }
}

/// <summary>One place a single missing field is referenced.</summary>
public sealed class BrokenFieldUsage
{
    public string PageId { get; init; } = string.Empty;

    public string PageDisplayName { get; init; } = string.Empty;

    public string VisualId { get; init; } = string.Empty;

    /// <summary>The PBIR visual type, e.g. "clusteredBarChart".</summary>
    public string VisualType { get; init; } = string.Empty;

    /// <summary>A friendly visual type name for display.</summary>
    public string VisualTypeLabel => VisualItemInfo.FormatVisualType(VisualType);

    /// <summary>The visual's title, or its best available label.</summary>
    public string VisualTitle { get; init; } = string.Empty;

    /// <summary>The queryState role, e.g. "Category" — empty outside a data role.</summary>
    public string Role { get; init; } = string.Empty;

    public BrokenReferenceKind Kind { get; init; }

    /// <summary>True when the binding was written as a measure or an aggregation.</summary>
    public bool IsMeasure { get; init; }

    /// <summary>
    /// Exactly where in the visual this reference sits, in the author's vocabulary rather
    /// than PBIR's. The raw role is kept alongside the friendly term because "Axis" alone
    /// does not say whether the field is the category or the series.
    /// </summary>
    public string LocationLabel => Kind switch
    {
        BrokenReferenceKind.DataRole => string.IsNullOrEmpty(Role)
            ? "Field"
            : DescribeRole(Role),
        BrokenReferenceKind.SortDefinition => "Sort order",
        BrokenReferenceKind.VisualFilter => "Visual filter",
        BrokenReferenceKind.FormatSelector => "Colour rules",
        BrokenReferenceKind.SeriesIdentity => "Colour rule (series)",
        BrokenReferenceKind.PageFilter => "Page level filter",
        BrokenReferenceKind.ReportFilter => "Report level filter",
        _ => "Other part of visual"
    };

    /// <summary>
    /// Names a queryState role in the author's vocabulary. The raw role is appended only when
    /// it adds information, so <c>Values</c> does not come out as the stuttering
    /// "Values · Values" while <c>Category</c> still reads as the unambiguous "Axis · Category".
    /// </summary>
    private static string DescribeRole(string role)
    {
        var friendly = FriendlyRole(role);
        return string.Equals(friendly, role, StringComparison.OrdinalIgnoreCase)
            ? role
            : $"{friendly} · {role}";
    }

    /// <summary>Maps a PBIR queryState role to the term the format pane uses for it.</summary>
    private static string FriendlyRole(string role) => role.ToLowerInvariant() switch
    {
        "category" => "Axis",
        "x" => "Axis",
        "x2" => "Axis",
        "columny" => "Values",
        "series" => "Legend",
        "y" => "Values",
        "y2" => "Values",
        "liney" => "Values",
        "values" => "Values",
        "rows" => "Rows",
        "columns" => "Columns",
        "tooltips" => "Tooltip",
        "size" => "Size",
        "group" => "Group",
        "legend" => "Legend",
        _ => "Field"
    };

    /// <summary>One-line description for the detail list.</summary>
    public string Describe()
    {
        var label = string.IsNullOrWhiteSpace(VisualTitle) ? VisualTypeLabel : VisualTitle;
        return $"{label} · {LocationLabel}";
    }
}

/// <summary>
/// One distinct field the report still references but the model no longer provides, plus
/// everywhere it is used and the best replacement we could infer.
/// </summary>
public sealed class BrokenField
{
    public string Entity { get; init; } = string.Empty;

    public string Property { get; init; } = string.Empty;

    public BrokenFieldSeverity Severity { get; init; }

    /// <summary>Where the field was expected to live: a column or a measure.</summary>
    public SemanticFieldKind ExpectedKind { get; init; } = SemanticFieldKind.Column;

    public IReadOnlyList<BrokenFieldUsage> Usages { get; init; } = Array.Empty<BrokenFieldUsage>();

    /// <summary>
    /// The same usages collapsed to one row per place, with repeats counted. This is what the
    /// detail list renders; <see cref="Usages"/> stays flat for counting and tests.
    /// </summary>
    /// <remarks>
    /// The key separates a page filter from a report filter. Both belong to no visual, so
    /// grouping on the visual id alone merged them and hid one behind the other's heading.
    /// </remarks>
    public IReadOnlyList<BrokenVisualUsageGroup> UsagesByVisual =>
        Usages.GroupBy(UsageGroupKey, StringComparer.OrdinalIgnoreCase)
              .Select(g => new BrokenVisualUsageGroup(g.Key, g))
              .OrderBy(g => g.Scope)
              .ThenBy(g => g.PageDisplayName, StringComparer.OrdinalIgnoreCase)
              .ThenBy(g => g.Title, StringComparer.OrdinalIgnoreCase)
              .ToList();

    /// <summary>Stable per-place key: a visual id, a page filter's page (and filter), or the report.</summary>
    private static string UsageGroupKey(BrokenFieldUsage usage) => usage.Kind switch
    {
        BrokenReferenceKind.ReportFilter => "report",
        BrokenReferenceKind.PageFilter => "page:" + usage.PageId,
        _ => "visual:" + usage.VisualId
    };

    /// <summary>Distinct visuals affected — fewer than <see cref="UsageCount"/> when one visual binds twice.</summary>
    public int VisualCount =>
        Usages.Where(u => !string.IsNullOrEmpty(u.VisualId))
              .Select(u => u.VisualId)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .Count();

    /// <summary>Pages carrying a page-level filter on this field.</summary>
    public int FilteredPageCount =>
        Usages.Where(u => u.Kind == BrokenReferenceKind.PageFilter)
              .Select(u => u.PageId)
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .Count();

    /// <summary>True when a report-level filter (which applies to every page) uses this field.</summary>
    public bool HasReportFilter => Usages.Any(u => u.Kind == BrokenReferenceKind.ReportFilter);

    public int UsageCount => Usages.Count;
    /// <summary>The "entity.property" identity of the missing field.</summary>
    public string Key => (Entity + "." + Property).TrimStart('.');

    /// <summary>True when a single replacement was inferred without the author choosing.</summary>
    public bool HasSuggestion => SuggestedEntity.Length > 0 && SuggestedProperty.Length > 0;

    /// <summary>Entity of the inferred replacement, empty when none.</summary>
    public string SuggestedEntity { get; init; } = string.Empty;

    /// <summary>Field of the inferred replacement, empty when none.</summary>
    public string SuggestedProperty { get; init; } = string.Empty;

    /// <summary>
    /// True when the inferred replacement differs only by case, spacing or punctuation —
    /// the shape a careless database rename usually takes, and the one that is safe to
    /// offer as a single batch action.
    /// </summary>
    public bool IsCosmetic { get; init; }

    /// <summary>"financials.Segment → financials.SalesSegment" for display.</summary>
    public string Describe() =>
        HasSuggestion ? $"{Key} → {SuggestedEntity}.{SuggestedProperty}" : Key;

    /// <summary>True when at least one usage sits outside the visual's data roles.</summary>
    public bool HasNonRoleUsages => Usages.Any(u => u.Kind != BrokenReferenceKind.DataRole);
}

/// <summary>A visual with at least one unresolvable field reference.</summary>
public sealed class BrokenVisual
{
    public string PageId { get; init; } = string.Empty;

    public string PageDisplayName { get; init; } = string.Empty;

    public string VisualId { get; init; } = string.Empty;

    public string VisualType { get; init; } = string.Empty;

    public string VisualTypeLabel => VisualItemInfo.FormatVisualType(VisualType);

    public string VisualTitle { get; init; } = string.Empty;

    /// <summary>Absolute path of the visual.json, so a caller can open or export it.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>The distinct missing fields this one visual depends on.</summary>
    public IReadOnlyList<string> BrokenFieldKeys { get; init; } = Array.Empty<string>();

    public string Label => string.IsNullOrWhiteSpace(VisualTitle) ? VisualTypeLabel : VisualTitle;
}

/// <summary>The result of scanning a report against its semantic model.</summary>
public sealed class BrokenVisualReport
{
    /// <summary>Distinct missing fields, worst first.</summary>
    public IReadOnlyList<BrokenField> Fields { get; init; } = Array.Empty<BrokenField>();

    /// <summary>Visuals with at least one missing field.</summary>
    public IReadOnlyList<BrokenVisual> Visuals { get; init; } = Array.Empty<BrokenVisual>();

    /// <summary>False when the model is a <c>model.bim</c> and could not be read at all.</summary>
    public bool ModelReadable { get; init; } = true;

    /// <summary>Total individual references that failed to resolve.</summary>
    public int ReferenceCount { get; init; }

    /// <summary>Number of distinct fields with an inferred replacement.</summary>
    public int SuggestedCount => Fields.Count(f => f.HasSuggestion);

    /// <summary>How many of those suggestions are cosmetic-only renames.</summary>
    public int CosmeticCount => Fields.Count(f => f.IsCosmetic);

    /// <summary>True when nothing is broken.</summary>
    public bool IsClean => Fields.Count == 0;

    /// <summary>Explanation to show when the model could not be read.</summary>
    public string? UnsupportedReason { get; init; }
}

/// <summary>Outcome of applying a set of remappings.</summary>
public sealed class RemapResult
{
    /// <summary>visual.json files rewritten.</summary>
    public int FilesChanged { get; set; }

    /// <summary>Distinct visuals whose bindings changed.</summary>
    public int VisualsChanged { get; set; }

    /// <summary>Individual field references repointed.</summary>
    public int ReferencesChanged { get; set; }

    /// <summary>visual.json files that could not be read or parsed and were left alone.</summary>
    public int FilesSkipped { get; set; }

    /// <summary>
    /// Remappings that were ignored because they are no longer valid — the field they
    /// point at does not exist in the model any more.
    /// </summary>
    public List<string> Warnings { get; } = new();

    /// <summary>True when nothing at all needed changing.</summary>
    public bool NoChange => FilesChanged == 0 && ReferencesChanged == 0;
}

/// <summary>
/// Finds report visuals whose field bindings no longer resolve against the semantic
/// model, and repoints them at replacements the author chooses.
/// </summary>
/// <remarks>
/// <para>
/// This exists for the database-rename scenario. When a source column is renamed, Power BI
/// Desktop refreshes the model to the new name but leaves the report's PBIR bindings alone,
/// so every visual that used the old name renders an error. Fixing them one at a time in
/// Desktop is slow, because each fix is a separate round trip through the field well.
/// </para>
/// <para>
/// Two details make the scan trustworthy. First, every reference in a visual is found, not
/// just the data roles: conditional-formatting selectors reference columns too, and a
/// renamed column leaves those rules silently inert rather than visibly broken. Second,
/// nothing is inferred from the visual's cached display strings — the binding itself is
/// resolved against the model, so a reference is only reported when the model genuinely
/// lacks it.
/// </para>
/// <para>
/// Writes are atomic and every rewrite is driven by the caller through
/// <c>ReportWorkspace.ApplyEdit</c>, so the change participates in undo history.
/// </para>
/// </remarks>
public sealed class BrokenVisualService
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Key holding the field's own name inside a binding.</summary>
    private const string PropertyKey = "Property";

    /// <summary>Key holding the table name inside a binding's source reference.</summary>
    private const string EntityKey = "Entity";

    /// <summary>
    /// Scans every visual of the open report and resolves each field reference against the
    /// semantic model in <paramref name="modelFolder"/>.
    /// </summary>
    /// <param name="manager">The loaded report. Purely read — nothing is written.</param>
    /// <param name="modelFolder">A TMDL model folder, normally from <c>DiscoverModelFolder</c>.</param>
    public BrokenVisualReport Scan(ReportManager manager, string modelFolder) =>
        Scan(manager, SemanticModelIndex.Load(modelFolder));

    /// <summary>
    /// Scans using an index the caller already holds, so a UI that needs the model for a
    /// replacement picker does not pay to parse it twice.
    /// </summary>
    /// <param name="manager">The loaded report. Purely read — nothing is written.</param>
    /// <param name="index">The semantic model to resolve references against.</param>
    public BrokenVisualReport Scan(ReportManager manager, SemanticModelIndex index)
    {
        if (!index.HasTmdlDefinition)
        {
            return new BrokenVisualReport
            {
                ModelReadable = false,
                UnsupportedReason =
                    "This report's semantic model is a model.bim, not TMDL, so field names cannot be read. " +
                    "Convert the model to TMDL in Power BI Desktop to use this tool."
            };
        }

        // field key -> the usage, so one missing field collects every place it is used.
        var usages = new Dictionary<string, List<BrokenFieldUsage>>(StringComparer.OrdinalIgnoreCase);
        var visuals = new List<BrokenVisual>();

        // definition/report.json sits outside the pages directory that undo history and the
        // external-change signature cover, so its filters are reported and left alone.
        AddFilterUsages(
            ReadJson(Path.Combine(manager.ReportFolderPath, "definition", "report.json"))?["filterConfig"],
            pageId: string.Empty,
            displayName: "All pages",
            BrokenReferenceKind.ReportFilter,
            index,
            usages);

        foreach (var page in manager.Pages)
        {
            if (string.IsNullOrEmpty(page.FolderPath)) continue;

            // Page-level filters live in page.json rather than in any visual, so a visuals
            // walk misses them entirely. They bind the same field shape, so the same
            // resolution applies.
            AddFilterUsages(
                ReadJson(Path.Combine(page.FolderPath, "page.json"))?["filterConfig"],
                page.Id,
                page.DisplayName,
                BrokenReferenceKind.PageFilter,
                index,
                usages);

            var visualsDir = Path.Combine(page.FolderPath, "visuals");
            if (!Directory.Exists(visualsDir)) continue;

            foreach (var visualDir in Directory.EnumerateDirectories(visualsDir))
            {
                var file = Path.Combine(visualDir, "visual.json");
                if (!File.Exists(file)) continue;

                var root = ReadJson(file);
                if (root == null) continue;

                var visualType = root["visual"]?["visualType"]?.ToString() ?? string.Empty;
                var title = ReadVisualTitle(root);
                var references = VisualQueryReader.EnumerateFieldRefs(root);
                if (references.Count == 0) continue;

                var dataRoleNodes = MapDataRoleNodes(root);
                var brokenHere = new List<string>();

                foreach (var reference in references)
                {
                    if (string.IsNullOrEmpty(reference.Entity) || string.IsNullOrEmpty(reference.Property))
                    {
                        continue;
                    }

                    // A hierarchy level is a different kind of thing from a column and is
                    // not something this tool can meaningfully remap.
                    if (reference.IsHierarchy) continue;

                    if (index.TryResolve(reference.Entity, reference.Property, out _))
                    {
                        continue;
                    }

                    var kind = Classify(reference, root, dataRoleNodes);
                    var key = reference.Key;

                    if (!usages.TryGetValue(key, out var list))
                    {
                        list = new List<BrokenFieldUsage>();
                        usages[key] = list;
                    }

                    list.Add(new BrokenFieldUsage
                    {
                        PageId = page.Id,
                        PageDisplayName = page.DisplayName,
                        VisualId = Path.GetFileName(visualDir),
                        VisualType = visualType,
                        VisualTitle = title,
                        Role = dataRoleNodes.TryGetValue(reference.Node, out var role) ? role : string.Empty,
                        Kind = kind,
                        IsMeasure = reference.IsMeasure
                    });

                    if (!brokenHere.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        brokenHere.Add(key);
                    }
                }

                // A series-identity selector is just the string "table.field" rather than a
                // field node, so the tree walk above cannot see it. Without this a color rule
                // keyed to a renamed series would be the one broken thing the tool misses.
                foreach (var metadataRef in EnumerateMetadataRefs(root))
                {
                    if (index.TryResolve(metadataRef.Entity, metadataRef.Property, out _)) continue;

                    var key = metadataRef.Key;
                    if (!usages.TryGetValue(key, out var list))
                    {
                        list = new List<BrokenFieldUsage>();
                        usages[key] = list;
                    }

                    list.Add(new BrokenFieldUsage
                    {
                        PageId = page.Id,
                        PageDisplayName = page.DisplayName,
                        VisualId = Path.GetFileName(visualDir),
                        VisualType = visualType,
                        VisualTitle = title,
                        Kind = BrokenReferenceKind.SeriesIdentity,
                        IsMeasure = false
                    });

                    if (!brokenHere.Contains(key, StringComparer.OrdinalIgnoreCase))
                    {
                        brokenHere.Add(key);
                    }
                }

                if (brokenHere.Count == 0) continue;

                visuals.Add(new BrokenVisual
                {
                    PageId = page.Id,
                    PageDisplayName = page.DisplayName,
                    VisualId = Path.GetFileName(visualDir),
                    VisualType = visualType,
                    VisualTitle = title,
                    FilePath = file,
                    BrokenFieldKeys = brokenHere
                });
            }
        }

        var fields = new List<BrokenField>();
        foreach (var pair in usages)
        {
            var split = pair.Key.Split('.', 2);
            var entity = split.Length > 0 ? split[0] : string.Empty;
            var property = split.Length > 1 ? split[1] : string.Empty;

            var severity = index.TableExists(entity)
                ? BrokenFieldSeverity.MissingField
                : BrokenFieldSeverity.MissingTable;

            // Anything used as a measure was a measure, or an aggregation over a column.
            var expectedKind = pair.Value.Any(u => u.IsMeasure)
                ? SemanticFieldKind.Measure
                : SemanticFieldKind.Column;

            var cosmetic = false;
            var suggestedEntity = string.Empty;
            var suggestedProperty = string.Empty;

            // Only a missing column in a surviving table can be matched cosmetically. A
            // missing table means we have no idea where its columns went, so guessing
            // across tables would be worse than asking.
            if (severity == BrokenFieldSeverity.MissingField &&
                index.TryResolveNormalized(entity, property, out var newEntity, out var newProperty, out _))
            {
                suggestedEntity = newEntity;
                suggestedProperty = newProperty;
                cosmetic = !string.Equals(newEntity, entity, StringComparison.OrdinalIgnoreCase) ||
                           !string.Equals(newProperty, property, StringComparison.Ordinal);
            }

            fields.Add(new BrokenField
            {
                Entity = entity,
                Property = property,
                Severity = severity,
                ExpectedKind = expectedKind,
                Usages = pair.Value,
                SuggestedEntity = suggestedEntity,
                SuggestedProperty = suggestedProperty,
                IsCosmetic = cosmetic
            });
        }

        // Worst first: a vanished table is a bigger problem than a renamed column, and
        // within a severity the most-used field is the one worth fixing first.
        var ordered = fields
            .OrderBy(f => f.Severity)
            .ThenByDescending(f => f.VisualCount)
            .ThenBy(f => f.Key, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new BrokenVisualReport
        {
            Fields = ordered,
            Visuals = visuals,
            ReferenceCount = usages.Values.Sum(list => list.Count),
            ModelReadable = true
        };
    }

    /// <summary>
    /// Repoints every reference matching <paramref name="remaps"/> at its replacement and
    /// rewrites the visual's cached display strings to match.
    /// </summary>
    /// <param name="manager">The loaded report.</param>
    /// <param name="remaps">Old field to new field. Unresolvable replacements are skipped with a warning.</param>
    /// <param name="index">The model index, used to validate each replacement before writing.</param>
    /// <param name="previewOnly">
    /// When true, every file is parsed and rewritten in memory but nothing is written to
    /// disk, so the UI can show exactly what an apply would touch.
    /// </param>
    public RemapResult ApplyRemap(
        ReportManager manager,
        IEnumerable<FieldRemap> remaps,
        SemanticModelIndex index,
        bool previewOnly = false)
    {
        var result = new RemapResult();
        var wanted = new Dictionary<string, FieldRemap>(StringComparer.OrdinalIgnoreCase);

        foreach (var remap in remaps ?? Enumerable.Empty<FieldRemap>())
        {
            if (remap == null) continue;
            if (string.IsNullOrWhiteSpace(remap.OldProperty) || string.IsNullOrWhiteSpace(remap.NewProperty))
            {
                continue;
            }

            // Refuse to point a visual at something the model does not have. Writing an
            // equally broken binding would leave the author worse off than before, with no
            // indication of why.
            if (!index.TryResolve(remap.NewEntity, remap.NewProperty, out _))
            {
                result.Warnings.Add(
                    $"'{remap.NewKey}' does not exist in the model, so '{remap.OldKey}' was left unchanged.");
                continue;
            }

            wanted[remap.OldKey] = remap;
        }

        if (wanted.Count == 0) return result;

        // Every file that can hold a field binding. Report-level filters live in
        // definition/report.json, which is outside the pages directory; ReportEditHistory
        // snapshots it explicitly so a repair here is still undoable, and ReportWorkspace
        // clears it on reload.
        var candidates = new List<string>();

        var reportJson = Path.Combine(manager.ReportFolderPath, "definition", "report.json");
        if (File.Exists(reportJson)) candidates.Add(reportJson);

        foreach (var page in manager.Pages)
        {
            if (string.IsNullOrEmpty(page.FolderPath)) continue;

            // Page-level filters live in page.json, which is inside the pages directory and so
            // is covered by the same undo snapshot as the visuals.
            var pageJson = Path.Combine(page.FolderPath, "page.json");
            if (File.Exists(pageJson)) candidates.Add(pageJson);

            var visualsDir = Path.Combine(page.FolderPath, "visuals");
            if (!Directory.Exists(visualsDir)) continue;

            foreach (var visualDir in Directory.EnumerateDirectories(visualsDir))
            {
                var visualJson = Path.Combine(visualDir, "visual.json");
                if (File.Exists(visualJson)) candidates.Add(visualJson);
            }
        }

        foreach (var file in candidates)
        {
            var root = ReadJson(file);
            if (root == null)
            {
                result.FilesSkipped++;
                continue;
            }

            var changedInThisFile = 0;

            // Filter bodies run FIRST. They identify which filter they are looking at by reading
            // the filter's own canonical "field" binding, so if the generic walk below renamed
            // that binding first, no filter would match and the alias copies would be missed.
            changedInThisFile += RemapFilterBodies(root, wanted);

            foreach (var reference in VisualQueryReader.EnumerateFieldRefs(root))
            {
                if (reference.IsHierarchy) continue;
                if (!wanted.TryGetValue(reference.Key, out var remap)) continue;

                reference.Node[PropertyKey] = remap.NewProperty;

                if (reference.Node["Expression"]?["SourceRef"] is JsonObject sourceRef)
                {
                    sourceRef[EntityKey] = remap.NewEntity;
                }

                changedInThisFile++;
            }

            if (changedInThisFile == 0) continue;

            // The cached display strings are what Power BI shows in the field well and
            // what other tools (including Color Sync's measure matching) key on. Leaving
            // them stale would make a correctly rebound visual still look broken. Done
            // only after a real change, so an untouched file is never rewritten.
            foreach (var remap in wanted.Values)
            {
                RewriteCachedRefs(root, remap);
            }

            result.ReferencesChanged += changedInThisFile;
            result.VisualsChanged++;
            result.FilesChanged++;

            if (previewOnly) continue;

            WriteAtomic(file, root.ToJsonString(WriteOptions));
        }

        return result;
    }

    /// <summary>
    /// Repoints the alias-based column references inside filter bodies.
    /// </summary>
    /// <remarks>
    /// A PBIR filter stores its column twice. The <c>field</c> binding names the table directly;
    /// the <c>filter</c> expression reaches the same column through a table alias declared in
    /// <c>from</c>, so it looks like <c>SourceRef: { Source: "r" }</c> with no entity at all. A
    /// generic field walk resolves the first and skips the second, leaving the filter bound to a
    /// column that no longer exists. This walks filter objects specifically, resolves each
    /// alias back to its table through the filter's own <c>from</c> list, and rewrites the
    /// property to match.
    /// </remarks>
    private static int RemapFilterBodies(JsonNode? node, IReadOnlyDictionary<string, FieldRemap> wanted)
    {
        var changed = 0;

        switch (node)
        {
            case JsonObject obj:
                if (obj["field"] is JsonObject field &&
                    obj["filter"] is JsonObject body &&
                    VisualQueryReader.TryGetFieldIdentity(field, out var entity, out var property) &&
                    wanted.TryGetValue((entity + "." + property).TrimStart('.'), out var remap))
                {
                    // Table aliases are scoped to one filter, so the map is built per filter.
                    var aliases = ReadAliases(body);

                    changed += RewriteAliasedColumns(body, aliases, remap);

                    // A renamed table has to be repointed here too, or the alias resolves to a
                    // table that no longer exists.
                    if (body["From"] is JsonArray from)
                    {
                        foreach (var entry in from)
                        {
                            if (entry is JsonObject alias &&
                                alias[EntityKey] is JsonValue value &&
                                value.TryGetValue<string>(out var aliasEntity) &&
                                string.Equals(aliasEntity, remap.OldEntity, StringComparison.OrdinalIgnoreCase))
                            {
                                alias[EntityKey] = remap.NewEntity;
                                changed++;
                            }
                        }
                    }
                }

                foreach (var pair in obj) changed += RemapFilterBodies(pair.Value, wanted);
                break;

            case JsonArray array:
                foreach (var item in array) changed += RemapFilterBodies(item, wanted);
                break;
        }

        return changed;
    }

    /// <summary>Reads a filter body's <c>From</c> list into an alias → table map.</summary>
    private static Dictionary<string, string> ReadAliases(JsonObject body)
    {
        var aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        if (body["From"] is not JsonArray from) return aliases;

        foreach (var entry in from)
        {
            if (entry is not JsonObject alias) continue;

            var name = alias["Name"]?.ToString();
            var entity = alias[EntityKey]?.ToString();
            if (!string.IsNullOrEmpty(name) && !string.IsNullOrEmpty(entity))
            {
                aliases[name] = entity;
            }
        }

        return aliases;
    }

    /// <summary>
    /// Rewrites <c>Column</c> nodes reached through an alias when that alias resolves to the
    /// remapped table and the property matches the remapped column.
    /// </summary>
    private static int RewriteAliasedColumns(
        JsonNode? node,
        Dictionary<string, string> aliases,
        FieldRemap remap)
    {
        var changed = 0;

        switch (node)
        {
            case JsonObject obj:
                if (obj["Expression"]?["SourceRef"] is JsonObject sourceRef &&
                    sourceRef["Source"] is JsonValue sourceValue &&
                    sourceValue.TryGetValue<string>(out var source) &&
                    aliases.TryGetValue(source, out var aliasedEntity) &&
                    string.Equals(aliasedEntity, remap.OldEntity, StringComparison.OrdinalIgnoreCase) &&
                    obj[PropertyKey] is JsonValue propertyValue &&
                    propertyValue.TryGetValue<string>(out var aliasedProperty) &&
                    string.Equals(aliasedProperty, remap.OldProperty, StringComparison.Ordinal))
                {
                    obj[PropertyKey] = remap.NewProperty;
                    changed++;
                }

                foreach (var pair in obj) changed += RewriteAliasedColumns(pair.Value, aliases, remap);
                break;

            case JsonArray array:
                foreach (var item in array) changed += RewriteAliasedColumns(item, aliases, remap);
                break;
        }

        return changed;
    }

    /// <summary>
    /// Rewrites <c>queryRef</c>, <c>nativeQueryRef</c> and <c>metadata</c> strings after a
    /// rebind, substituting whole tokens only.
    /// </summary>
    /// <remarks>
    /// Substitution is token-aware rather than a plain string replace on purpose. A native
    /// reference like <c>Count of Segment</c> has to become <c>Count of SalesSegment</c>, but
    /// a column genuinely named <c>SegmentCode</c> must be left alone. Requiring
    /// non-alphanumeric characters on both sides of the match gives that for free, and
    /// touching only these three keys means a column name appearing inside a data literal
    /// can never be corrupted.
    /// </remarks>
    private static void RewriteCachedRefs(JsonNode root, FieldRemap remap)
    {
        RewriteToken(root, "queryRef", remap.OldKey, remap.NewKey);
        RewriteToken(root, "nativeQueryRef", remap.OldProperty, remap.NewProperty);
        RewriteToken(root, "metadata", remap.OldKey, remap.NewKey);
    }

    private static void RewriteToken(JsonNode? node, string key, string oldToken, string newToken)
    {
        if (node == null || string.IsNullOrEmpty(oldToken) || oldToken == newToken) return;

        switch (node)
        {
            case JsonObject obj:
                if (obj[key] is JsonValue value && value.TryGetValue<string>(out var text))
                {
                    var replaced = ReplaceToken(text, oldToken, newToken);
                    if (!string.Equals(replaced, text, StringComparison.Ordinal))
                    {
                        obj[key] = replaced;
                    }
                }

                foreach (var pair in obj) RewriteToken(pair.Value, key, oldToken, newToken);
                break;

            case JsonArray array:
                foreach (var item in array) RewriteToken(item, key, oldToken, newToken);
                break;
        }
    }

    /// <summary>
    /// Replaces whole-token occurrences of <paramref name="oldToken"/> in
    /// <paramref name="input"/>, where a token boundary is any character that is not a
    /// letter, digit or underscore.
    /// </summary>
    /// <remarks>
    /// Exposed for testing because the boundary rule is the whole correctness argument for
    /// rewriting a visual's cached display strings: too loose and a column named
    /// <c>SegmentCode</c> is corrupted, too tight and <c>Count of Segment</c> is left stale.
    /// </remarks>
    public static string ReplaceToken(string input, string oldToken, string newToken)
    {
        if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(oldToken)) return input;

        var builder = new StringBuilder(input.Length);
        int index = 0;

        while (index < input.Length)
        {
            var found = input.IndexOf(oldToken, index, StringComparison.Ordinal);
            if (found < 0)
            {
                builder.Append(input, index, input.Length - index);
                break;
            }

            builder.Append(input, index, found - index);

            var startsClean = found == 0 || !IsTokenChar(input[found - 1]);
            var after = found + oldToken.Length;
            var endsClean = after >= input.Length || !IsTokenChar(input[after]);

            if (startsClean && endsClean)
            {
                builder.Append(newToken);
            }
            else
            {
                builder.Append(oldToken);
            }

            index = after;
        }

        return builder.ToString();
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Parses a JSON file, returning null rather than throwing on anything unreadable.</summary>
    private static JsonNode? ReadJson(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            return JsonNode.Parse(File.ReadAllText(path));
        }
        catch
        {
            // A half-written or hand-edited file must not fail the whole scan.
            return null;
        }
    }

    /// <summary>
    /// Records every filter in a <c>filterConfig</c> block whose field the model no longer has.
    /// </summary>
    /// <param name="filterConfig">The block, or null when the file has no filters.</param>
    /// <param name="kind">Page-level or report-level, which decides whether it is repairable.</param>
    private static void AddFilterUsages(
        JsonNode? filterConfig,
        string pageId,
        string displayName,
        BrokenReferenceKind kind,
        SemanticModelIndex index,
        Dictionary<string, List<BrokenFieldUsage>> usages)
    {
        if (filterConfig == null) return;

        foreach (var reference in VisualQueryReader.EnumerateFieldRefs(filterConfig))
        {
            if (reference.IsHierarchy) continue;
            if (string.IsNullOrEmpty(reference.Entity) || string.IsNullOrEmpty(reference.Property)) continue;
            if (index.TryResolve(reference.Entity, reference.Property, out _)) continue;

            var key = reference.Key;
            if (!usages.TryGetValue(key, out var list))
            {
                list = new List<BrokenFieldUsage>();
                usages[key] = list;
            }

            list.Add(new BrokenFieldUsage
            {
                PageId = pageId,
                PageDisplayName = displayName,
                // No visual id: a filter belongs to the page, so the detail list shows it as
                // a page-level row rather than inventing a visual it does not belong to.
                VisualId = string.Empty,
                VisualType = string.Empty,
                VisualTitle = string.Empty,
                Kind = kind,
                IsMeasure = reference.IsMeasure
            });
        }
    }

    /// <summary>
    /// Reads every <c>selector.metadata</c> value that looks like a field reference.
    /// </summary>
    /// <remarks>
    /// These are series-identity keys, formatted as <c>table.field</c>. They are matched on
    /// shape rather than on position because <c>metadata</c> also appears in unrelated
    /// format objects, and a value with no dot cannot name a field.
    /// </remarks>
    private static IEnumerable<VisualFieldRef> EnumerateMetadataRefs(JsonNode root)
    {
        var results = new List<VisualFieldRef>();
        CollectMetadataRefs(root, results);
        return results;
    }

    private static void CollectMetadataRefs(JsonNode? node, List<VisualFieldRef> results)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj["metadata"] is JsonValue metadata &&
                    metadata.TryGetValue<string>(out var text) &&
                    TrySplitFieldRef(text, out var entity, out var property))
                {
                    results.Add(new VisualFieldRef
                    {
                        Entity = entity,
                        Property = property,
                        Node = obj
                    });
                }

                foreach (var pair in obj) CollectMetadataRefs(pair.Value, results);
                break;

            case JsonArray array:
                foreach (var item in array) CollectMetadataRefs(item, results);
                break;
        }
    }

    /// <summary>
    /// Splits a <c>table.field</c> reference. The table is everything before the first dot,
    /// matching Power BI's own <c>queryRef</c> convention.
    /// </summary>
    private static bool TrySplitFieldRef(string? value, out string entity, out string property)
    {
        entity = string.Empty;
        property = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;

        var separator = value.IndexOf('.');
        if (separator <= 0 || separator == value.Length - 1) return false;

        entity = value[..separator];
        property = value[(separator + 1)..];

        // A nested value such as "Sum of x" is not a field reference.
        if (entity.Contains(' ') || property.Contains(' ')) return false;

        return true;
    }

    /// <summary>
    /// Maps each binding node that belongs to a <c>queryState</c> role to that role name, so
    /// a usage can say <c>Category</c> rather than just "somewhere in the visual".
    /// </summary>
    private static Dictionary<JsonObject, string> MapDataRoleNodes(JsonNode root)
    {
        var map = new Dictionary<JsonObject, string>(ReferenceEqualityComparer.Instance);

        if (root["visual"]?["query"]?["queryState"] is not JsonObject queryState) return map;

        foreach (var role in queryState)
        {
            if (role.Value?["projections"] is not JsonArray projections) continue;

            foreach (var projection in projections)
            {
                if (projection?["field"] is not JsonObject field) continue;

                foreach (var reference in VisualQueryReader.EnumerateFieldRefs(field))
                {
                    map[reference.Node] = role.Key;
                }
            }
        }

        return map;
    }

    private static BrokenReferenceKind Classify(
        VisualFieldRef reference,
        JsonNode root,
        Dictionary<JsonObject, string> dataRoleNodes)
    {
        if (dataRoleNodes.ContainsKey(reference.Node)) return BrokenReferenceKind.DataRole;

        // Walk up indirectly: a sort field is not inside queryState, but it is inside a
        // sibling "sortDefinition", which is cheap to confirm by re-walking for the node.
        if (ContainsNode(root["visual"]?["query"]?["sortDefinition"], reference.Node))
        {
            return BrokenReferenceKind.SortDefinition;
        }

        // A visual-scoped filter lives at the ROOT of visual.json, as a sibling of "visual" —
// not inside it. Checking only visual.filterConfig meant every visual filter fell into
// the catch-all and read as the uninformative "Other part of visual". The nested
// location is still checked because earlier PBIR schemas wrote it there.
        if (ContainsNode(root["filterConfig"], reference.Node) ||
            ContainsNode(root["visual"]?["filterConfig"], reference.Node))
        {
            return BrokenReferenceKind.VisualFilter;
        }

        if (ContainsNode(root["visual"]?["objects"], reference.Node))
        {
            return BrokenReferenceKind.FormatSelector;
        }

        return BrokenReferenceKind.Other;
    }

    private static bool ContainsNode(JsonNode? container, JsonNode? needle)
    {
        if (container == null || needle == null) return false;
        if (ReferenceEquals(container, needle)) return true;

        switch (container)
        {
            case JsonObject obj:
                foreach (var pair in obj)
                {
                    if (ContainsNode(pair.Value, needle)) return true;
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    if (ContainsNode(item, needle)) return true;
                }
                break;
        }

        return false;
    }

    /// <summary>
    /// Reads a visual's own title, or an empty string when it has none.
    /// </summary>
    /// <remarks>
    /// Deliberately no fallback to a projected field name. That fallback made the detail row
    /// read "RAW_DATA.phone" directly above "RAW_DATA.phone" — the same text twice, once as
    /// the heading and once as the field being repaired — which looked like a duplicate bug.
    /// An untitled visual is better described by its type.
    ///
    /// <c>visual.objects.title</c> is written as a <b>single-element array</b> by Power BI,
    /// but a bare object is also seen in the wild. Indexing a <see cref="JsonArray"/> with a
    /// string key throws, so both shapes are accepted.
    /// </remarks>
    private static string ReadVisualTitle(JsonNode root)
    {
        try
        {
            if (root["visual"]?["objects"]?["title"] is JsonArray titleArray)
            {
                foreach (var entry in titleArray)
                {
                    if (TryReadTitleLiteral(entry, out var fromArray))
                    {
                        return fromArray;
                    }
                }
            }
            else if (TryReadTitleLiteral(root["visual"]?["objects"]?["title"], out var fromObject))
            {
                return fromObject;
            }
        }
        catch
        {
            // A malformed title must never cost us the rest of the visual's field data.
        }

        return string.Empty;
    }

    /// <summary>Pulls the title text out of one <c>objects.title</c> entry.</summary>
    private static bool TryReadTitleLiteral(JsonNode? titleNode, out string title)
    {
        title = string.Empty;
        if (titleNode == null) return false;

        var value = titleNode["properties"]?["text"]?["expr"]?["Literal"]?["Value"];
        if (value is not JsonValue literal || !literal.TryGetValue<string>(out var raw)) return false;

        var trimmed = raw.Trim('\'', '"');
        if (trimmed.Length == 0) return false;

        title = trimmed;
        return true;
    }

    /// <summary>
    /// Writes via a temporary file and a single overwrite move, matching
    /// <see cref="ReportManager.SaveChanges"/>, so a crash mid-write cannot leave a
    /// half-written visual that Power BI would refuse to load.
    /// </summary>
    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}