using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SingularTools.Core.Models;

namespace SingularTools.Core;

/// <summary>
/// Reads and writes the legend/series/slice colors of a report's visuals directly
/// in the PBIR definition files. These are the colors behind the format pane's
/// <b>Columns / Bars / Slices → Apply settings to → Color</b> control and live in
/// <c>visual.objects.dataPoint</c> as one of two selector shapes:
///
/// <list type="bullet">
///   <item>a <b>member value</b> — <c>selector.data[].scopeId.Comparison</c> with a
///   <c>Right.Literal.Value</c> (e.g. Series "2013", slice "Paseo");</item>
///   <item>a <b>series identity</b> — <c>selector.metadata</c> keyed by the field's
///   queryRef (e.g. "financials.Revenue").</item>
/// </list>
///
/// Member values are matched globally (the same text receives the same color
/// everywhere). Only data points that already carry a selector can be discovered
/// from the definition files; the tool additionally allows creating new member
/// selectors for a field once a sibling value is known.
/// </summary>
public sealed class SemanticColorService
{
    /// <summary>Palette used to seed the picker when a value has no literal color yet.</summary>
    public static readonly string[] DefaultPalette =
    {
        "#118DFF", "#12239E", "#E66C37", "#6B007B",
        "#E044A7", "#744EC2", "#D9B300", "#D64550",
        "#1AAB40", "#197278", "#4092FF", "#FF8080"
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly HashSet<string> MemberRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Category", "Series", "Legend", "Rows", "Group"
    };

    private static readonly HashSet<string> SeriesRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Y", "Y2", "ColumnY", "LineY", "Series", "X", "Size"
    };

    // ---------------------------------------------------------------- Scan

    /// <summary>Scans every visual in the report and aggregates its legend/series/slice color items.</summary>
    public SemanticColorScan Scan(ReportManager manager)
    {
        var scan = new SemanticColorScan { ReportPath = manager.ReportFolderPath };
        var pagesDir = manager.PagesDirectoryPath;
        if (string.IsNullOrEmpty(pagesDir) || !Directory.Exists(pagesDir))
        {
            return scan;
        }

        var values = new Dictionary<string, SemanticColorValue>(StringComparer.Ordinal);
        var memberFields = new Dictionary<string, SemanticColorFieldInfo>(StringComparer.OrdinalIgnoreCase);
        var seriesFields = new Dictionary<string, SemanticColorFieldInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var visualPath in EnumerateVisualFiles(pagesDir))
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(visualPath));
            }
            catch
            {
                continue;
            }

            if (root == null) continue;
            scan.VisualCount++;

            var visualRef = MakeVisualRef(pagesDir, visualPath);
            var projections = ExtractProjections(root);
            var fieldNames = projections
                .Select(p => p.Property)
                .Where(p => !string.IsNullOrEmpty(p))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            var dataPoints = root["visual"]?["objects"]?["dataPoint"]?.AsArray();
            if (dataPoints != null)
            {
                foreach (var dataPoint in dataPoints)
                {
                    if (dataPoint is not JsonObject entry) continue;

                    var selector = entry["selector"];
                    var color = ParseColorDisplay(entry["properties"]?["fill"]?["solid"]?["color"]?["expr"]);
                    var metadata = selector?["metadata"]?.ToString();

                    if (!string.IsNullOrEmpty(metadata))
                    {
                        var series = GetOrAddSeries(values, metadata);
                        Accumulate(series, visualRef, fieldNames, color);
                        scan.TotalSelectors++;
                        continue;
                    }

                    foreach (var literal in ExtractEqualityLiterals(selector))
                    {
                        var key = NormalizeLiteralKey(literal.Value);
                        if (key.Length == 0) continue;

                        var value = GetOrAddMember(values, key, literal);
                        Accumulate(value, visualRef, fieldNames, color);
                        scan.TotalSelectors++;

                        if (!string.IsNullOrEmpty(literal.LeftJson) && !string.IsNullOrEmpty(value.FieldName))
                        {
                            var fieldKey = value.Entity + "|" + value.FieldName;
                            if (!memberFields.ContainsKey(fieldKey))
                            {
                                memberFields[fieldKey] = new SemanticColorFieldInfo
                                {
                                    Kind = SemanticColorTargetKind.MemberValue,
                                    FieldName = value.FieldName,
                                    Entity = value.Entity,
                                    TemplateJson = literal.LeftJson!
                                };
                            }
                        }
                    }
                }
            }

            // Field candidates for the "add value" flow: legend/category fields become
            // member fields, measure projections become series identities.
            foreach (var projection in projections)
            {
                if (projection.IsMeasure && projection.QueryRef != null && SeriesRoles.Contains(projection.Role))
                {
                    var seriesKey = projection.QueryRef;
                    if (!seriesFields.ContainsKey(seriesKey))
                    {
                        seriesFields[seriesKey] = new SemanticColorFieldInfo
                        {
                            Kind = SemanticColorTargetKind.SeriesIdentity,
                            FieldName = projection.Property,
                            Entity = projection.Entity,
                            QueryRef = projection.QueryRef
                        };
                    }
                }
                else if (!projection.IsMeasure && !string.IsNullOrEmpty(projection.Property) &&
                         MemberRoles.Contains(projection.Role) && projection.FieldJson != null)
                {
                    var fieldKey = projection.Entity + "|" + projection.Property;
                    if (!memberFields.ContainsKey(fieldKey))
                    {
                        memberFields[fieldKey] = new SemanticColorFieldInfo
                        {
                            Kind = SemanticColorTargetKind.MemberValue,
                            FieldName = projection.Property,
                            Entity = projection.Entity,
                            TemplateJson = projection.FieldJson
                        };
                    }
                }
            }

            // A measure series that was explicitly colored should be offered even if
            // its role was not in the series list above.
            foreach (var value in values.Values.Where(v => v.Kind == SemanticColorTargetKind.SeriesIdentity && !v.IsManual))
            {
                if (!seriesFields.ContainsKey(value.Key))
                {
                    seriesFields[value.Key] = new SemanticColorFieldInfo
                    {
                        Kind = SemanticColorTargetKind.SeriesIdentity,
                        FieldName = value.FieldName,
                        Entity = value.Entity,
                        QueryRef = value.Key
                    };
                }
            }
        }

        scan.Values = values.Values
            .OrderBy(v => v.Kind)
            .ThenByDescending(v => v.SelectorCount)
            .ThenBy(v => v.DisplayValue, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        foreach (var value in scan.Values)
        {
            value.CommonColor = value.CurrentColors.Count == 1 && IsHexColor(value.CurrentColors[0])
                ? NormalizeHex(value.CurrentColors[0])
                : null;
        }

        // Infer numeric typing from sibling member values so created literals match.
        foreach (var field in memberFields.Values)
        {
            var sample = scan.Values.FirstOrDefault(v =>
                v.Kind == SemanticColorTargetKind.MemberValue &&
                string.Equals(v.Entity, field.Entity, StringComparison.OrdinalIgnoreCase) &&
                string.Equals(v.FieldName, field.FieldName, StringComparison.OrdinalIgnoreCase) &&
                IsNumericLiteral(v.RawValue));

            if (sample != null)
            {
                field.IsNumeric = true;
                field.NumericSuffix = NumericSuffixOf(sample.RawValue);
            }
        }

        scan.Fields = memberFields.Values
            .Concat(seriesFields.Values)
            .OrderBy(f => f.Kind)
            .ThenBy(f => f.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        return scan;
    }

    // --------------------------------------------------------------- Apply

    /// <summary>Recolors every existing selector matching one of the supplied item keys.</summary>
    public SemanticColorApplyResult Apply(ReportManager manager, IReadOnlyDictionary<string, string> valueColors)
    {
        var assignments = valueColors
            .Select(pair => new SemanticColorAssignment { Key = pair.Key, Hex = pair.Value })
            .ToList();
        return Apply(manager, assignments);
    }

    /// <summary>
    /// Applies color decisions to the report: existing selectors are recolored and
    /// manual values/series are created on visuals that use the relevant field.
    /// Writes are atomic and skipped when nothing changes.
    /// </summary>
    public SemanticColorApplyResult Apply(ReportManager manager, IReadOnlyList<SemanticColorAssignment> assignments)
    {
        var result = new SemanticColorApplyResult();
        var pagesDir = manager.PagesDirectoryPath;
        if (assignments == null || assignments.Count == 0 ||
            string.IsNullOrEmpty(pagesDir) || !Directory.Exists(pagesDir))
        {
            return result;
        }

        var normalized = new List<SemanticColorAssignment>();
        foreach (var assignment in assignments)
        {
            if (string.IsNullOrWhiteSpace(assignment.Hex)) continue;
            var hex = NormalizeHex(assignment.Hex);
            if (hex.Length == 0) continue;
            normalized.Add(new SemanticColorAssignment
            {
                Key = assignment.Key,
                Hex = hex,
                Manual = assignment.Manual
            });
        }

        if (normalized.Count == 0) return result;

        foreach (var visualPath in EnumerateVisualFiles(pagesDir))
        {
            JsonNode? root;
            try
            {
                root = JsonNode.Parse(File.ReadAllText(visualPath));
            }
            catch
            {
                continue;
            }

            if (root == null) continue;

            var changed = false;
            var dataPoints = root["visual"]?["objects"]?["dataPoint"]?.AsArray();

            foreach (var assignment in normalized)
            {
                // 1. Recolor existing selectors that match the item key.
                if (!string.IsNullOrEmpty(assignment.Key) && dataPoints != null)
                {
                    foreach (var dataPoint in dataPoints)
                    {
                        if (dataPoint is not JsonObject entry) continue;
                        if (!SelectorMatches(entry, assignment.Key!)) continue;
                        if (SetFillColor(entry, assignment.Hex))
                        {
                            result.SelectorsChanged++;
                            changed = true;
                        }
                    }
                }

                // 2. Create a selector for a manual value/series.
                if (assignment.Manual != null)
                {
                    if (CreateSelector(root, assignment.Manual, assignment.Hex))
                    {
                        result.SelectorsCreated++;
                        changed = true;
                    }
                }
            }

            if (!changed) continue;

            var temp = visualPath + ".tmp";
            File.WriteAllText(temp, root.ToJsonString(WriteOptions));
            File.Move(temp, visualPath, overwrite: true);

            result.FilesWritten++;
            result.VisualsChanged++;
        }

        return result;
    }

    private static bool CreateSelector(JsonNode root, SemanticColorManualValue manual, string hex)
    {
        var projections = ExtractProjections(root);

        if (manual.Kind == SemanticColorTargetKind.SeriesIdentity)
        {
            if (string.IsNullOrEmpty(manual.QueryRef)) return false;
            if (!projections.Any(p => string.Equals(p.QueryRef, manual.QueryRef, StringComparison.Ordinal)))
            {
                return false;
            }

            var dataPoints = EnsureDataPointArray(root);
            if (dataPoints == null) return false;
            if (MetadataSelectorExists(dataPoints, manual.QueryRef)) return false;

            dataPoints.Add(BuildFillEntry(
                new JsonObject { ["metadata"] = manual.QueryRef },
                hex));
            return true;
        }

        if (string.IsNullOrEmpty(manual.TemplateJson)) return false;
        if (!VisualProjects(root, projections, manual.Entity, manual.FieldName)) return false;

        JsonNode? left;
        try
        {
            left = JsonNode.Parse(manual.TemplateJson);
        }
        catch
        {
            return false;
        }

        if (left == null) return false;

        var memberDataPoints = EnsureDataPointArray(root);
        if (memberDataPoints == null) return false;
        if (MemberSelectorExists(memberDataPoints, manual.Entity, manual.FieldName, manual.LiteralValue))
        {
            return false;
        }

        var selector = new JsonObject
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
                            ["Left"] = left,
                            ["Right"] = new JsonObject
                            {
                                ["Literal"] = new JsonObject { ["Value"] = manual.LiteralValue }
                            }
                        }
                    }
                }
            }
        };

        memberDataPoints.Add(BuildFillEntry(selector, hex));
        return true;
    }

    private static JsonObject BuildFillEntry(JsonObject selector, string hex)
    {
        return new JsonObject
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
                                ["Literal"] = new JsonObject { ["Value"] = QuoteColor(hex) }
                            }
                        }
                    }
                }
            },
            ["selector"] = selector
        };
    }

    private static bool VisualProjects(JsonNode root, List<ProjectionInfo> projections, string entity, string property)
    {
        if (string.IsNullOrEmpty(property)) return false;

        return projections.Any(p =>
            string.Equals(p.Property, property, StringComparison.OrdinalIgnoreCase) &&
            (string.IsNullOrEmpty(entity) || string.Equals(p.Entity, entity, StringComparison.OrdinalIgnoreCase)));
    }

    private static bool SelectorMatches(JsonObject dataPoint, string key)
    {
        var metadata = dataPoint["selector"]?["metadata"]?.ToString();
        if (!string.IsNullOrEmpty(metadata))
        {
            return string.Equals(metadata, key, StringComparison.Ordinal);
        }

        foreach (var literal in ExtractEqualityLiterals(dataPoint["selector"]))
        {
            if (string.Equals(NormalizeLiteralKey(literal.Value), key, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MetadataSelectorExists(JsonArray dataPoints, string queryRef)
    {
        return dataPoints.Any(dp =>
            dp is JsonObject entry &&
            string.Equals(entry["selector"]?["metadata"]?.ToString(), queryRef, StringComparison.Ordinal));
    }

    private static bool MemberSelectorExists(JsonArray dataPoints, string entity, string property, string literal)
    {
        foreach (var dataPoint in dataPoints)
        {
            if (dataPoint is not JsonObject entry) continue;
            foreach (var existing in ExtractEqualityLiterals(entry["selector"]))
            {
                if (string.Equals(existing.Value, literal, StringComparison.Ordinal) &&
                    string.Equals(existing.Property, property, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(entity) || string.Equals(existing.Entity, entity, StringComparison.OrdinalIgnoreCase)))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SetFillColor(JsonObject dataPoint, string hex)
    {
        var properties = EnsureObject(dataPoint, "properties");
        var fill = EnsureObject(properties, "fill");
        var solid = EnsureObject(fill, "solid");
        var color = EnsureObject(solid, "color");

        var existing = color["expr"]?["Literal"]?["Value"]?.ToString();
        if (!string.IsNullOrEmpty(existing) &&
            string.Equals(NormalizeHex(existing), hex, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        color["expr"] = new JsonObject
        {
            ["Literal"] = new JsonObject { ["Value"] = QuoteColor(hex) }
        };
        return true;
    }

    private static JsonArray? EnsureDataPointArray(JsonNode root)
    {
        if (root["visual"] is not JsonObject visual) return null;

        if (visual["objects"] is not JsonObject objects)
        {
            objects = new JsonObject();
            visual["objects"] = objects;
        }

        if (objects["dataPoint"] is not JsonArray dataPoint)
        {
            dataPoint = new JsonArray();
            objects["dataPoint"] = dataPoint;
        }

        return dataPoint;
    }

    private static JsonObject EnsureObject(JsonObject parent, string key)
    {
        if (parent[key] is JsonObject existing) return existing;

        var created = new JsonObject();
        parent[key] = created;
        return created;
    }

    // ------------------------------------------------------------- Parsing

    private static SemanticColorValue GetOrAddMember(
        Dictionary<string, SemanticColorValue> map, string key, SelectorLiteral literal)
    {
        if (map.TryGetValue(key, out var existing)) return existing;

        var value = new SemanticColorValue
        {
            Kind = SemanticColorTargetKind.MemberValue,
            Key = key,
            RawValue = literal.Value,
            DisplayValue = NormalizeLiteralDisplay(literal.Value),
            FieldName = literal.Property,
            Entity = literal.Entity,
            FieldTemplateJson = literal.LeftJson
        };
        map[key] = value;
        return value;
    }

    private static SemanticColorValue GetOrAddSeries(Dictionary<string, SemanticColorValue> map, string queryRef)
    {
        if (map.TryGetValue(queryRef, out var existing)) return existing;

        var (entity, property) = SplitQueryRef(queryRef);
        var value = new SemanticColorValue
        {
            Kind = SemanticColorTargetKind.SeriesIdentity,
            Key = queryRef,
            RawValue = queryRef,
            DisplayValue = property.Length > 0 ? property : queryRef,
            FieldName = property,
            Entity = entity
        };
        map[queryRef] = value;
        return value;
    }

    private static void Accumulate(
        SemanticColorValue value, string visualRef, List<string> fieldNames, string? color)
    {
        value.SelectorCount++;

        if (!value.VisualRefs.Contains(visualRef, StringComparer.OrdinalIgnoreCase))
        {
            value.VisualRefs.Add(visualRef);
        }

        foreach (var field in fieldNames)
        {
            if (!value.Fields.Contains(field, StringComparer.OrdinalIgnoreCase))
            {
                value.Fields.Add(field);
            }
        }

        if (!string.IsNullOrEmpty(color) &&
            !value.CurrentColors.Contains(color, StringComparer.OrdinalIgnoreCase))
        {
            value.CurrentColors.Add(color);
        }
    }

    private static List<ProjectionInfo> ExtractProjections(JsonNode root)
    {
        var result = new List<ProjectionInfo>();
        if (root["visual"]?["query"]?["queryState"] is not JsonObject queryState) return result;

        foreach (var role in queryState)
        {
            var projections = role.Value?["projections"]?.AsArray();
            if (projections == null) continue;

            foreach (var projection in projections)
            {
                var field = projection?["field"];
                if (field == null) continue;

                var property = "";
                var entity = "";
                TryGetFieldIdentity(field, out entity, out property);

                result.Add(new ProjectionInfo
                {
                    Role = role.Key,
                    Entity = entity,
                    Property = property,
                    QueryRef = projection?["queryRef"]?.ToString(),
                    FieldJson = field.ToJsonString(),
                    IsMeasure = field["Measure"] != null || field["Aggregation"] != null
                });
            }
        }

        return result;
    }

    private static bool TryGetFieldIdentity(JsonNode? field, out string entity, out string property)
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

    private static (string Entity, string Property) SplitQueryRef(string queryRef)
    {
        var working = queryRef;
        var open = working.IndexOf('(');
        if (open >= 0 && working.EndsWith(")", StringComparison.Ordinal))
        {
            working = working.Substring(open + 1, working.Length - open - 2);
        }

        var dot = working.LastIndexOf('.');
        if (dot >= 0 && dot < working.Length - 1)
        {
            return (working.Substring(0, dot), working.Substring(dot + 1));
        }

        return (string.Empty, working);
    }

    private readonly record struct SelectorLiteral(
        string Value, string? LeftJson, string Entity, string Property);

    private static List<SelectorLiteral> ExtractEqualityLiterals(JsonNode? selector)
    {
        var result = new List<SelectorLiteral>();
        if (selector == null) return result;

        var data = selector["data"]?.AsArray();
        if (data == null) return result;

        foreach (var item in data)
        {
            CollectFromScope(item?["scopeId"], result);
        }

        return result;
    }

    private static void CollectFromScope(JsonNode? scope, List<SelectorLiteral> result)
    {
        if (scope == null) return;

        var comparison = scope["Comparison"];
        if (comparison != null)
        {
            int kind = 0;
            try { kind = comparison["ComparisonKind"]?.GetValue<int>() ?? 0; }
            catch { kind = 0; }

            if (kind == 0)
            {
                var literal = comparison["Right"]?["Literal"]?["Value"]?.ToString();
                if (!string.IsNullOrEmpty(literal))
                {
                    var left = comparison["Left"];
                    var entity = string.Empty;
                    var property = string.Empty;
                    TryGetFieldIdentity(left, out entity, out property);

                    result.Add(new SelectorLiteral(literal, left?.ToJsonString(), entity, property));
                }
            }
        }

        var nested = scope["Data"]?.AsArray();
        if (nested != null)
        {
            foreach (var item in nested)
            {
                CollectFromScope(item?["scopeId"] ?? item, result);
            }
        }

        var inner = scope["scopeId"];
        if (inner != null) CollectFromScope(inner, result);
    }

    private static string? ParseColorDisplay(JsonNode? expr)
    {
        if (expr == null) return null;

        var literal = expr["Literal"]?["Value"]?.ToString();
        if (!string.IsNullOrEmpty(literal))
        {
            var cleaned = NormalizeHex(literal);
            return cleaned.Length > 0 ? cleaned : literal;
        }

        if (expr["ThemeDataColor"] != null) return "Theme";

        return null;
    }

    // ------------------------------------------------------- Normalization

    /// <summary>Normalizes a stored literal token to its human-readable form ("'Yes'" → "Yes", "2013L" → "2013").</summary>
    public static string NormalizeLiteralDisplay(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;

        if (raw.Length >= 2 && raw[0] == '\'' && raw[^1] == '\'')
        {
            return raw.Substring(1, raw.Length - 2).Replace("''", "'");
        }

        if (raw.Length > 1 && NumericSuffixOf(raw).Length > 0)
        {
            return raw.Substring(0, raw.Length - 1);
        }

        return raw;
    }

    /// <summary>Builds a stable, global lookup key for a stored literal token.</summary>
    public static string NormalizeLiteralKey(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return string.Empty;
        var isString = raw.Length >= 2 && raw[0] == '\'';
        var display = NormalizeLiteralDisplay(raw);
        return (isString ? "s:" : "v:") + display.ToLowerInvariant();
    }

    /// <summary>Normalizes a hex string to the "#RRGGBB" form, using single quotes for the report.</summary>
    public static string NormalizeHex(string value)
    {
        var hex = value.Trim().Trim('\'', '"');
        if (hex.Length == 0) return hex;
        if (hex[0] != '#') hex = "#" + hex;
        return hex.ToUpperInvariant();
    }

    /// <summary>Wraps a hex color in the single quotes PBIR literal values require.</summary>
    public static string QuoteColor(string hex) => "'" + NormalizeHex(hex) + "'";

    private static bool IsHexColor(string value) => value.StartsWith("#", StringComparison.Ordinal);

    private static bool IsNumericLiteral(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return false;
        if (raw[0] == '\'') return false;

        var stripped = NumericSuffixOf(raw).Length > 0 ? raw.Substring(0, raw.Length - 1) : raw;
        return double.TryParse(stripped, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static string NumericSuffixOf(string raw)
    {
        if (raw.Length <= 1 || raw[0] == '\'') return string.Empty;

        var suffix = char.ToUpperInvariant(raw[^1]);
        var before = raw[^2];
        if ((suffix == 'L' || suffix == 'D' || suffix == 'M' || suffix == 'F') &&
            (char.IsDigit(before) || before == '.'))
        {
            return raw[^1].ToString();
        }

        return string.Empty;
    }

    // -------------------------------------------------------------- Files

    private static string MakeVisualRef(string pagesDir, string visualPath)
    {
        var directory = Path.GetDirectoryName(visualPath) ?? visualPath;
        var relative = Path.GetRelativePath(pagesDir, directory);
        return relative.Replace('\\', '/');
    }

    private static IEnumerable<string> EnumerateVisualFiles(string pagesDir)
    {
        foreach (var pageDir in Directory.EnumerateDirectories(pagesDir))
        {
            var visualsDir = Path.Combine(pageDir, "visuals");
            if (!Directory.Exists(visualsDir)) continue;

            foreach (var visualDir in Directory.EnumerateDirectories(visualsDir))
            {
                var file = Path.Combine(visualDir, "visual.json");
                if (File.Exists(file)) yield return file;
            }
        }
    }

    private sealed class ProjectionInfo
    {
        public string Role { get; init; } = string.Empty;
        public string Entity { get; init; } = string.Empty;
        public string Property { get; init; } = string.Empty;
        public string? QueryRef { get; init; }
        public string? FieldJson { get; init; }
        public bool IsMeasure { get; init; }
    }
}
