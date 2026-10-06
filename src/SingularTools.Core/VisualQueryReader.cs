using System.Text.Json.Nodes;

namespace SingularTools.Core;

/// <summary>
/// One field binding found in a PBIR visual container, resolved to a table
/// (<c>entity</c>) and a column, measure or hierarchy (<c>property</c>).
/// </summary>
/// <remarks>
/// <see cref="Node"/> is the live <see cref="JsonObject"/> the reference was read
/// from, so a caller that knows how to rewrite the file can mutate
/// <c>Node["Property"]</c> and <c>Node["Expression"]["SourceRef"]["Entity"]</c>
/// in place without a second lookup.
/// </remarks>
public sealed class VisualFieldRef
{
    /// <summary>The table / entity name, from <c>Expression.SourceRef.Entity</c>.</summary>
    public string Entity { get; init; } = string.Empty;

    /// <summary>The column, measure or hierarchy name, from <c>Property</c> (or <c>Hierarchy</c>).</summary>
    public string Property { get; init; } = string.Empty;

    /// <summary>The object the reference was read from, for in-place rewriting.</summary>
    public JsonObject Node { get; init; } = new();

    /// <summary>True when the binding came from a <c>Measure</c> wrapper rather than <c>Column</c>.</summary>
    public bool IsMeasure { get; init; }

    /// <summary>True when the binding names a hierarchy rather than a column or measure.</summary>
    public bool IsHierarchy { get; init; }

    /// <summary>Stable "entity.property" identity, matching PBIR's own <c>queryRef</c> shape.</summary>
    public string Key => Entity + "." + Property;
}

/// <summary>One entry of a visual's <c>query.queryState</c>, with the role it is bound to.</summary>
public sealed class VisualProjection
{
    /// <summary>The queryState role, e.g. "Category", "Series", "Y", "Legend".</summary>
    public string Role { get; init; } = string.Empty;

    public string Entity { get; init; } = string.Empty;

    public string Property { get; init; } = string.Empty;

    public string? QueryRef { get; init; }

    public string? NativeQueryRef { get; init; }

    /// <summary>The projection's <c>field</c> node serialized, for callers that re-emit it.</summary>
    public string? FieldJson { get; init; }

    /// <summary>
    /// True when the field is wrapped in <c>Measure</c> or <c>Aggregation</c>, i.e. it
    /// produces an aggregated value rather than a raw column.
    /// </summary>
    public bool IsMeasure { get; init; }
}

/// <summary>
/// Reads field bindings out of PBIR visual containers. Shared by the tools that need
/// to know which model fields a visual depends on: Color Sync picks member fields to
/// paint, and the Field Repair tool resolves every binding against the semantic
/// model to find references a database rename left dangling.
/// </summary>
public static class VisualQueryReader
{
    /// <summary>
    /// Resolves a PBIR <c>field</c> node to its entity and property. Handles the four
    /// wrapper shapes Power BI emits — <c>Column</c>, <c>Measure</c>, <c>Aggregation</c>
    /// (whose inner expression is a column) and <c>Hierarchy</c>.
    /// </summary>
    /// <returns>False when the node is not a field binding or carries no property name.</returns>
    public static bool TryGetFieldIdentity(JsonNode? field, out string entity, out string property)
    {
        entity = string.Empty;
        property = string.Empty;
        if (field == null) return false;

        if (field["Column"] is JsonNode column)
        {
            entity = column["Expression"]?["SourceRef"]?["Entity"]?.ToString() ?? string.Empty;
            property = column["Property"]?.ToString() ?? string.Empty;
        }
        else if (field["Measure"] is JsonNode measure)
        {
            entity = measure["Expression"]?["SourceRef"]?["Entity"]?.ToString() ?? string.Empty;
            property = measure["Property"]?.ToString() ?? string.Empty;
        }
        else if (field["Aggregation"] is JsonNode aggregation)
        {
            var inner = aggregation["Expression"]?["Column"];
            entity = inner?["Expression"]?["SourceRef"]?["Entity"]?.ToString() ?? string.Empty;
            property = inner?["Property"]?.ToString() ?? string.Empty;
        }
        else if (field["Hierarchy"] is JsonNode hierarchy)
        {
            entity = hierarchy["Expression"]?["SourceRef"]?["Entity"]?.ToString() ?? string.Empty;
            property = hierarchy["Hierarchy"]?.ToString() ?? string.Empty;
        }

        return property.Length > 0;
    }

    /// <summary>
    /// Reads every <c>query.queryState</c> projection of a visual, keeping the role each
    /// is bound to. Only the well-known field roles are returned; Power BI also writes
    /// internal roles here that carry no user binding.
    /// </summary>
    public static List<VisualProjection> ExtractProjections(JsonNode root)
    {
        var result = new List<VisualProjection>();
        if (root["visual"]?["query"]?["queryState"] is not JsonObject queryState) return result;

        foreach (var role in queryState)
        {
            var projections = role.Value?["projections"]?.AsArray();
            if (projections == null) continue;

            foreach (var projection in projections)
            {
                var field = projection?["field"];
                if (field == null) continue;

                TryGetFieldIdentity(field, out var entity, out var property);

                result.Add(new VisualProjection
                {
                    Role = role.Key,
                    Entity = entity,
                    Property = property,
                    QueryRef = projection?["queryRef"]?.ToString(),
                    NativeQueryRef = projection?["nativeQueryRef"]?.ToString(),
                    FieldJson = field.ToJsonString(),
                    IsMeasure = field["Measure"] != null || field["Aggregation"] != null
                });
            }
        }

        return result;
    }

    /// <summary>
    /// Walks an entire visual container and returns <b>every</b> field reference in it,
    /// wherever it appears.
    /// </summary>
    /// <remarks>
    /// A single recursive pass is used deliberately instead of reading only
    /// <c>query.queryState</c>. Column references are also written into conditional
    /// formatting and color rules — <c>visual.objects.*[].selector.data[].scopeId
    /// .Comparison.Left</c> — and into <c>sortDefinition</c>. A scan that only read the
    /// data roles would report a visual as healthy while its color rules silently
    /// stopped matching, which is exactly the failure the Field Repair tool exists to
    /// catch.
    ///
    /// Because the walk descends through every object, the shape it keys on is the
    /// innermost one Power BI uses: an object carrying <c>Expression.SourceRef.Entity</c>
    /// plus a <c>Property</c> (or <c>Hierarchy</c>) name. <c>Column</c>, <c>Measure</c> and
    /// <c>Aggregation</c> wrappers all funnel down to it, so one matcher covers them all
    /// and nested cases (an aggregation whose expression is a column) are found for free.
    /// </remarks>
    public static List<VisualFieldRef> EnumerateFieldRefs(JsonNode? root)
    {
        var results = new List<VisualFieldRef>();
        Walk(root, parentKey: null, results);
        return results;
    }

    private static void Walk(JsonNode? node, string? parentKey, List<VisualFieldRef> results)
    {
        switch (node)
        {
            case JsonObject obj:
                CollectIfFieldRef(obj, parentKey, results);

                // Recurse into every child. A field node is still descended into because
                // an Aggregation wraps its inner column, which is itself a field ref.
                foreach (var pair in obj)
                {
                    Walk(pair.Value, pair.Key, results);
                }
                break;

            case JsonArray array:
                foreach (var item in array)
                {
                    Walk(item, parentKey, results);
                }
                break;
        }
    }

    private static void CollectIfFieldRef(JsonObject obj, string? parentKey, List<VisualFieldRef> results)
    {
        // Require a SourceRef-backed entity: this is what distinguishes a field binding
        // from the many other { Expression: ... } shaped objects in the format model.
        if (obj["Expression"]?["SourceRef"]?["Entity"] is not JsonValue entityValue ||
            entityValue.TryGetValue<string>(out var entity) is false ||
            entity.Length == 0)
        {
            return;
        }

        if (obj["Property"] is JsonValue propertyValue &&
            propertyValue.TryGetValue<string>(out var property) &&
            property.Length > 0)
        {
            results.Add(new VisualFieldRef
            {
                Entity = entity,
                Property = property,
                Node = obj,
                IsMeasure = string.Equals(parentKey, "Measure", StringComparison.Ordinal),
                IsHierarchy = false
            });
            return;
        }

        // A hierarchy binding names its level in "Hierarchy" rather than "Property".
        // Guard on JsonValue because the outer wrapper also has a "Hierarchy" key whose
        // value is an object, and ToString() on that would yield its JSON text.
        if (obj["Hierarchy"] is JsonValue hierarchyValue &&
            hierarchyValue.TryGetValue<string>(out var hierarchy) &&
            hierarchy.Length > 0)
        {
            results.Add(new VisualFieldRef
            {
                Entity = entity,
                Property = hierarchy,
                Node = obj,
                IsHierarchy = true
            });
        }
    }

    /// <summary>
    /// Enumerates every <c>visual.json</c> under a pages directory, optionally limited
    /// to a set of page ids. Individual visuals without a <c>visual.json</c> are skipped,
    /// so a half-written folder cannot fail the whole scan.
    /// </summary>
    public static IEnumerable<string> EnumerateVisualFiles(string pagesDir, IReadOnlySet<string>? pageIds = null)
    {
        foreach (var pageDir in Directory.EnumerateDirectories(pagesDir))
        {
            if (pageIds != null && !pageIds.Contains(Path.GetFileName(pageDir))) continue;

            var visualsDir = Path.Combine(pageDir, "visuals");
            if (!Directory.Exists(visualsDir)) continue;

            foreach (var visualDir in Directory.EnumerateDirectories(visualsDir))
            {
                var file = Path.Combine(visualDir, "visual.json");
                if (File.Exists(file)) yield return file;
            }
        }
    }
}