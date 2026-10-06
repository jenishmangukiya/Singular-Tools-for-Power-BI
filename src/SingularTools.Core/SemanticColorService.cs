using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SingularTools.Core.Models;

namespace SingularTools.Core;

/// <summary>
/// Applies user-defined semantic colors to a report's visuals directly in the
/// PBIR definition files. These are the colors behind the format pane's
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
/// A rule is just a display string and a color; it is matched against every
/// non-table visual without the caller knowing any field. Existing selectors with
/// that value are recolored, and selectors that do not exist yet are created on
/// every projected member field whose type fits the value (string vs numeric).
/// Tables and other non-chart visuals are ignored.
/// </summary>
public sealed class SemanticColorService
{
    /// <summary>Palette used to seed new rules when the user has not picked a color.</summary>
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

    /// <summary>Visual types that do not carry legend/series/slice data colors.</summary>
    private static readonly HashSet<string> ExcludedVisualTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tableEx", "pivotTable", "card", "cardVisual", "multiRowCard", "kpi", "gauge",
        "slicer", "advancedSlicerVisual", "textFilter", "listSlicer",
        "textbox", "image", "shape", "actionButton", "basicShape", "bookmarkNavigator",
        "pageNavigator", "qnaVisual", "map", "filledMap", "azureMap", "esriVisual"
    };

    private static readonly HashSet<string> MemberRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Category", "Series", "Legend", "Rows", "Group"
    };

    private static readonly HashSet<string> SeriesRoles = new(StringComparer.OrdinalIgnoreCase)
    {
        "Y", "Y2", "ColumnY", "LineY", "Series", "X", "Size"
    };

    private static readonly ConcurrentDictionary<string, Dictionary<string, string>> TypeCache = new();

    // --------------------------------------------------------------- Apply

    /// <summary>
    /// Applies every rule across each non-table visual: existing selectors whose
    /// value matches are recolored, and missing selectors are created on every
    /// type-compatible projected member field. Writes are atomic and only happen
    /// when something actually changed, so repeated runs are idempotent.
    /// </summary>
    public SemanticColorApplyResult ApplyRules(ReportManager manager, IReadOnlyList<SemanticColorRule> rules)
    {
        var result = new SemanticColorApplyResult();
        var pagesDir = manager.PagesDirectoryPath;
        if (rules == null || rules.Count == 0 ||
            string.IsNullOrEmpty(pagesDir) || !Directory.Exists(pagesDir))
        {
            return result;
        }

        var normalized = NormalizeRules(rules);
        if (normalized.Count == 0) return result;

        // Report-wide rules first, then page-scoped rules, so page rules win.
        var ordered = normalized
            .OrderBy(r => r.Scope == SemanticColorScope.Pages ? 1 : 0)
            .ToList();

        var columnTypes = LoadColumnTypes(manager.ReportFolderPath);

        foreach (var visualPath in VisualQueryReader.EnumerateVisualFiles(pagesDir))
        {
            var pageId = PageIdOf(pagesDir, visualPath);
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

            var visualType = root["visual"]?["visualType"]?.ToString() ?? string.Empty;
            if (!IsScannableVisual(visualType)) continue;

            var projections = VisualQueryReader.ExtractProjections(root);
            var memberFields = BuildMemberFields(projections, columnTypes);
            var measureFields = BuildMeasureFields(projections);

            if (memberFields.Count == 0 && measureFields.Count == 0 &&
                root["visual"]?["objects"]?["dataPoint"] == null)
            {
                continue;
            }

            var changed = false;

            foreach (var rule in ordered)
            {
                if (!RuleTargetsPage(rule, pageId)) continue;

                var dataPoints = root["visual"]?["objects"]?["dataPoint"]?.AsArray();

                if (dataPoints != null)
                {
                    foreach (var dataPoint in dataPoints)
                    {
                        if (dataPoint is not JsonObject entry) continue;
                        if (!SelectorTargetsValue(entry["selector"], measureFields, rule.Value)) continue;
                        if (SetFillColor(entry, rule.Hex))
                        {
                            result.SelectorsChanged++;
                            changed = true;
                        }
                    }
                }

                foreach (var field in memberFields)
                {
                    if (!ValueFitsField(field, rule.Value)) continue;

                    var literal = FormatLiteral(field, rule.Value);
                    if (MemberSelectorExists(dataPoints, field.Entity, field.Property, literal)) continue;
                    if (CreateMemberSelector(root, field, literal, rule.Hex))
                    {
                        result.SelectorsCreated++;
                        changed = true;
                    }
                }

                foreach (var field in measureFields)
                {
                    if (!MeasureMatches(field, rule.Value)) continue;

                    var current = root["visual"]?["objects"]?["dataPoint"]?.AsArray();
                    if (MetadataSelectorExists(current, field.QueryRef)) continue;
                    if (CreateMetadataSelector(root, field.QueryRef, rule.Hex))
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

    /// <summary>
    /// Returns the color currently applied to a rule's value within its scope (the
    /// whole report or the selected pages), or null when no selector references it
    /// yet. Used to keep the tool's rule list in sync after undo/redo.
    /// </summary>
    public string? GetAppliedColor(ReportManager manager, SemanticColorRule rule)
    {
        if (rule == null) return null;

        var pagesDir = manager.PagesDirectoryPath;
        if (string.IsNullOrEmpty(pagesDir) || !Directory.Exists(pagesDir)) return null;

        var normalized = NormalizeRuleValue(rule.Value);
        if (normalized.Length == 0) return null;

        var pageFilter = rule.Scope == SemanticColorScope.Pages
            ? new HashSet<string>(rule.PageIds ?? new List<string>(), StringComparer.OrdinalIgnoreCase)
            : null;

        var colors = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var visualPath in VisualQueryReader.EnumerateVisualFiles(pagesDir, pageFilter))
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

            var visualType = root["visual"]?["visualType"]?.ToString() ?? string.Empty;
            if (!IsScannableVisual(visualType)) continue;

            var dataPoints = root["visual"]?["objects"]?["dataPoint"]?.AsArray();
            if (dataPoints == null) continue;

            var measureFields = BuildMeasureFields(VisualQueryReader.ExtractProjections(root));

            foreach (var dataPoint in dataPoints)
            {
                if (dataPoint is not JsonObject entry) continue;
                if (!SelectorTargetsValue(entry["selector"], measureFields, normalized)) continue;

                var literal = entry["properties"]?["fill"]?["solid"]?["color"]?["expr"]?["Literal"]?["Value"]?.ToString();
                var hex = NormalizeHex(literal);
                if (hex.Length == 0) continue;

                colors[hex] = colors.TryGetValue(hex, out var count) ? count + 1 : 1;
            }
        }

        return colors.Count == 0
            ? null
            : colors.OrderByDescending(pair => pair.Value).First().Key;
    }

    private static bool RuleTargetsPage(SemanticColorRule rule, string pageId)
    {
        if (rule.Scope != SemanticColorScope.Pages) return true;
        return rule.PageIds != null && rule.PageIds.Contains(pageId, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>The page id (folder name under definition/pages) for a visual file path.</summary>
    private static string PageIdOf(string pagesDir, string visualPath)
    {
        var relative = Path.GetRelativePath(pagesDir, visualPath);
        var separator = relative.IndexOfAny(new[] { Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar });
        return separator > 0 ? relative.Substring(0, separator) : relative;
    }

    private static List<SemanticColorRule> NormalizeRules(IReadOnlyList<SemanticColorRule> rules)
    {
        var result = new List<SemanticColorRule>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var rule in rules)
        {
            if (rule == null) continue;

            var value = NormalizeRuleValue(rule.Value);
            if (value.Length == 0) continue;

            var hex = NormalizeHex(rule.Hex);
            if (hex.Length == 0) continue;

            var normalized = BuildNormalizedRule(rule, value, hex);
            var key = normalized.Scope == SemanticColorScope.Pages
                ? $"{value}|pages|{string.Join(",", normalized.PageIds)}"
                : $"{value}|report";

            if (seen.TryGetValue(key, out var index))
            {
                result[index] = normalized;
                continue;
            }

            seen[key] = result.Count;
            result.Add(normalized);
        }

        return result;
    }

    private static SemanticColorRule BuildNormalizedRule(SemanticColorRule source, string value, string hex)
    {
        var rule = new SemanticColorRule
        {
            Value = value,
            Hex = hex,
            Scope = source.Scope
        };

        if (source.Scope == SemanticColorScope.Pages && source.PageIds != null)
        {
            rule.PageIds = source.PageIds
                .Where(id => !string.IsNullOrWhiteSpace(id))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return rule;
    }

    private static bool SelectorTargetsValue(
        JsonNode? selector, List<MeasureField> measureFields, string value)
    {
        if (selector == null) return false;

        foreach (var literal in ExtractEqualityLiterals(selector))
        {
            if (ValueEquals(literal.Value, value)) return true;
        }

        var metadata = selector["metadata"]?.ToString();
        if (!string.IsNullOrEmpty(metadata))
        {
            if (ValueEquals(metadata, value)) return true;

            var field = measureFields.FirstOrDefault(m =>
                string.Equals(m.QueryRef, metadata, StringComparison.Ordinal));
            if (field != null && (ValueEquals(field.NativeName, value) || ValueEquals(field.Property, value)))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ValueFitsField(MemberField field, string value)
    {
        if (!field.IsNumeric) return true;
        return double.TryParse(value, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out _);
    }

    private static string FormatLiteral(MemberField field, string value)
    {
        if (field.IsNumeric)
        {
            var suffix = field.NumericSuffix;
            if (string.IsNullOrEmpty(suffix)) suffix = value.Contains('.') ? "D" : "L";
            return value + suffix;
        }

        return "'" + value.Replace("'", "''") + "'";
    }

    private static bool CreateMemberSelector(JsonNode root, MemberField field, string literal, string hex)
    {
        JsonNode? left;
        try
        {
            left = JsonNode.Parse(field.FieldJson);
        }
        catch
        {
            return false;
        }

        if (left == null) return false;

        var dataPoints = EnsureDataPointArray(root);
        if (dataPoints == null) return false;

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
                                ["Literal"] = new JsonObject { ["Value"] = literal }
                            }
                        }
                    }
                }
            }
        };

        dataPoints.Add(BuildFillEntry(selector, hex));
        return true;
    }

    private static bool CreateMetadataSelector(JsonNode root, string queryRef, string hex)
    {
        var dataPoints = EnsureDataPointArray(root);
        if (dataPoints == null) return false;

        dataPoints.Add(BuildFillEntry(new JsonObject { ["metadata"] = queryRef }, hex));
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

    private static bool MetadataSelectorExists(JsonArray? dataPoints, string queryRef)
    {
        if (dataPoints == null) return false;

        return dataPoints.Any(dp =>
            dp is JsonObject entry &&
            string.Equals(entry["selector"]?["metadata"]?.ToString(), queryRef, StringComparison.Ordinal));
    }

    private static bool MemberSelectorExists(
        JsonArray? dataPoints, string entity, string property, string literal)
    {
        if (dataPoints == null) return false;

        foreach (var dataPoint in dataPoints)
        {
            if (dataPoint is not JsonObject entry) continue;
            foreach (var existing in ExtractEqualityLiterals(entry["selector"]))
            {
                if (string.Equals(existing.Value, literal, StringComparison.Ordinal) &&
                    string.Equals(existing.Property, property, StringComparison.OrdinalIgnoreCase) &&
                    (string.IsNullOrEmpty(entity) ||
                     string.Equals(existing.Entity, entity, StringComparison.OrdinalIgnoreCase)))
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

    // ------------------------------------------------------------- Fields

    private static List<MemberField> BuildMemberFields(
        List<VisualProjection> projections, Dictionary<string, string> columnTypes)
    {
        var result = new List<MemberField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projection in projections)
        {
            if (projection.IsMeasure || string.IsNullOrEmpty(projection.Property) ||
                projection.FieldJson == null || !MemberRoles.Contains(projection.Role))
            {
                continue;
            }

            var key = projection.Entity + "|" + projection.Property;
            if (!seen.Add(key)) continue;

            var hasType = columnTypes.TryGetValue(key, out var dataType);
            var numeric = hasType && IsNumericType(dataType!);

            result.Add(new MemberField
            {
                Entity = projection.Entity,
                Property = projection.Property,
                FieldJson = projection.FieldJson!,
                IsNumeric = numeric,
                NumericSuffix = numeric ? SuffixForType(dataType!) : string.Empty
            });
        }

        return result;
    }

    private static List<MeasureField> BuildMeasureFields(List<VisualProjection> projections)
    {
        var result = new List<MeasureField>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var projection in projections)
        {
            if (!projection.IsMeasure || string.IsNullOrEmpty(projection.QueryRef) ||
                !SeriesRoles.Contains(projection.Role))
            {
                continue;
            }

            if (!seen.Add(projection.QueryRef)) continue;

            result.Add(new MeasureField
            {
                QueryRef = projection.QueryRef,
                Property = projection.Property,
                NativeName = projection.NativeQueryRef ?? string.Empty
            });
        }

        return result;
    }

    private static bool MeasureMatches(MeasureField field, string value)
    {
        return ValueEquals(field.NativeName, value) ||
               ValueEquals(field.Property, value) ||
               ValueEquals(field.QueryRef, value);
    }

    private static bool ValueEquals(string? raw, string value) =>
        !string.IsNullOrEmpty(raw) &&
        string.Equals(NormalizeLiteralDisplay(raw), value, StringComparison.OrdinalIgnoreCase);

    private readonly record struct SelectorLiteral(
        string Value, string Entity, string Property);

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
                    VisualQueryReader.TryGetFieldIdentity(left, out var entity, out var property);

                    result.Add(new SelectorLiteral(literal, entity, property));
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

    // --------------------------------------------------- Semantic model types

    private static Dictionary<string, string> LoadColumnTypes(string reportFolder)
    {
        if (string.IsNullOrEmpty(reportFolder)) return new Dictionary<string, string>();
        return TypeCache.GetOrAdd(reportFolder, LoadColumnTypesCore);
    }

    private static Dictionary<string, string> LoadColumnTypesCore(string reportFolder)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var parent = Directory.GetParent(reportFolder)?.FullName;
            if (parent == null) return result;

            string? modelDir = null;
            var baseName = Path.GetFileName(reportFolder);
            if (baseName.EndsWith(".Report", StringComparison.OrdinalIgnoreCase))
            {
                var candidate = Path.Combine(
                    parent, baseName.Substring(0, baseName.Length - ".Report".Length) + ".SemanticModel");
                if (Directory.Exists(candidate)) modelDir = candidate;
            }

            modelDir ??= Directory.GetDirectories(parent, "*.SemanticModel").FirstOrDefault();
            if (modelDir == null) return result;

            var tablesDir = Path.Combine(modelDir, "definition", "tables");
            if (!Directory.Exists(tablesDir)) return result;

            foreach (var file in Directory.GetFiles(tablesDir, "*.tmdl"))
            {
                ParseTmdl(file, result);
            }
        }
        catch
        {
            // Best effort only; the type hint is optional.
        }

        return result;
    }

    private static void ParseTmdl(string path, Dictionary<string, string> result)
    {
        var table = string.Empty;
        var column = string.Empty;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.TrimEnd();
            var trimmed = line.TrimStart();

            if (trimmed.StartsWith("table ", StringComparison.Ordinal))
            {
                table = trimmed.Substring("table ".Length).Trim().Trim('\'', '"');
                column = string.Empty;
            }
            else if (trimmed.StartsWith("column ", StringComparison.Ordinal))
            {
                column = trimmed.Substring("column ".Length).Trim().Trim('\'', '"');
            }
            else if (trimmed.StartsWith("dataType:", StringComparison.Ordinal) &&
                     table.Length > 0 && column.Length > 0)
            {
                var dataType = trimmed.Substring("dataType:".Length).Trim();
                result[table + "|" + column] = dataType;
            }
        }
    }

    private static bool IsNumericType(string dataType) =>
        dataType.Equals("int64", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("double", StringComparison.OrdinalIgnoreCase) ||
        dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase);

    private static string SuffixForType(string dataType)
    {
        if (dataType.Equals("int64", StringComparison.OrdinalIgnoreCase)) return "L";
        if (dataType.Equals("double", StringComparison.OrdinalIgnoreCase)) return "D";
        if (dataType.Equals("decimal", StringComparison.OrdinalIgnoreCase)) return "M";
        return string.Empty;
    }

    // ------------------------------------------------------- Normalization

    private static bool IsScannableVisual(string visualType)
    {
        if (string.IsNullOrWhiteSpace(visualType)) return false;
        return !ExcludedVisualTypes.Contains(visualType);
    }

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

    /// <summary>Normalizes a user-entered value: trims it and removes quotes / numeric suffixes.</summary>
    public static string NormalizeRuleValue(string? raw) =>
        NormalizeLiteralDisplay((raw ?? string.Empty).Trim());

    /// <summary>Normalizes a hex string to the "#RRGGBB" form, using single quotes for the report.</summary>
    public static string NormalizeHex(string? value)
    {
        var hex = (value ?? string.Empty).Trim().Trim('\'', '"');
        if (hex.Length == 0) return hex;
        if (hex[0] != '#') hex = "#" + hex;
        return hex.ToUpperInvariant();
    }

    /// <summary>Wraps a hex color in the single quotes PBIR literal values require.</summary>
    public static string QuoteColor(string hex) => "'" + NormalizeHex(hex) + "'";

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

    private sealed class MemberField
    {
        public string Entity { get; init; } = string.Empty;
        public string Property { get; init; } = string.Empty;
        public string FieldJson { get; init; } = string.Empty;
        public bool IsNumeric { get; init; }
        public string NumericSuffix { get; init; } = string.Empty;
    }

    private sealed class MeasureField
    {
        public string QueryRef { get; init; } = string.Empty;
        public string Property { get; init; } = string.Empty;
        public string NativeName { get; init; } = string.Empty;
    }
}
