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
///   <c>Right.Literal.Value</c> (e.g. Series "2013", slice "Velo");</item>
///   <item>a <b>series identity</b> — <c>selector.metadata</c> keyed by the field's
///   queryRef (e.g. "financials.Revenue").</item>
/// </list>
///
/// Member values are aggregated globally: the same text is one item across the
/// whole report, and applying its color recolors every bar/column/slice that uses
/// it. Only values that already carry a selector exist in the definition files and
/// can therefore be discovered; tables and other non-chart visuals are ignored.
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

    /// <summary>Visual types that do not carry legend/series/slice data colors.</summary>
    private static readonly HashSet<string> ExcludedVisualTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "tableEx", "pivotTable", "card", "cardVisual", "multiRowCard", "kpi", "gauge",
        "slicer", "advancedSlicerVisual", "textFilter", "listSlicer",
        "textbox", "image", "shape", "actionButton", "basicShape", "bookmarkNavigator",
        "pageNavigator", "qnaVisual", "map", "filledMap", "azureMap", "esriVisual"
    };

    // ---------------------------------------------------------------- Scan

    /// <summary>
    /// Scans every color-capable chart in the report and returns the distinct legend
    /// values used by their color selectors, aggregated globally across visuals.
    /// </summary>
    public SemanticColorScan Scan(ReportManager manager)
    {
        var scan = new SemanticColorScan { ReportPath = manager.ReportFolderPath };
        var pagesDir = manager.PagesDirectoryPath;
        if (string.IsNullOrEmpty(pagesDir) || !Directory.Exists(pagesDir))
        {
            return scan;
        }

        var values = new Dictionary<string, SemanticColorValue>(StringComparer.Ordinal);

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

            var visualType = root["visual"]?["visualType"]?.ToString() ?? string.Empty;
            if (!IsScannableVisual(visualType)) continue;

            scan.VisualCount++;

            var visualRef = MakeVisualRef(pagesDir, visualPath);
            var fieldNames = ExtractProjectionNames(root);

            var dataPoints = root["visual"]?["objects"]?["dataPoint"]?.AsArray();
            if (dataPoints == null) continue;

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

        return scan;
    }

    // --------------------------------------------------------------- Apply

    /// <summary>
    /// Recolors every existing selector that matches one of the supplied item keys,
    /// across all color-capable visuals. Writes are atomic and skipped when nothing
    /// changes.
    /// </summary>
    public SemanticColorApplyResult Apply(ReportManager manager, IReadOnlyDictionary<string, string> valueColors)
    {
        var result = new SemanticColorApplyResult();
        var pagesDir = manager.PagesDirectoryPath;
        if (valueColors == null || valueColors.Count == 0 ||
            string.IsNullOrEmpty(pagesDir) || !Directory.Exists(pagesDir))
        {
            return result;
        }

        var lookup = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var pair in valueColors)
        {
            if (string.IsNullOrWhiteSpace(pair.Key) || string.IsNullOrWhiteSpace(pair.Value)) continue;
            lookup[pair.Key] = NormalizeHex(pair.Value);
        }

        if (lookup.Count == 0) return result;

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

            var visualType = root["visual"]?["visualType"]?.ToString() ?? string.Empty;
            if (!IsScannableVisual(visualType)) continue;

            var dataPoints = root["visual"]?["objects"]?["dataPoint"]?.AsArray();
            if (dataPoints == null) continue;

            var changed = false;

            foreach (var dataPoint in dataPoints)
            {
                if (dataPoint is not JsonObject entry) continue;
                if (!TryMatchKey(entry, lookup, out var hex) || hex == null) continue;
                if (SetFillColor(entry, hex))
                {
                    result.SelectorsChanged++;
                    changed = true;
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

    private static bool TryMatchKey(JsonObject dataPoint, Dictionary<string, string> lookup, out string? hex)
    {
        hex = string.Empty;

        var metadata = dataPoint["selector"]?["metadata"]?.ToString();
        if (!string.IsNullOrEmpty(metadata))
        {
            return lookup.TryGetValue(metadata, out hex);
        }

        foreach (var literal in ExtractEqualityLiterals(dataPoint["selector"]))
        {
            if (lookup.TryGetValue(NormalizeLiteralKey(literal.Value), out hex))
            {
                return true;
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
            Entity = literal.Entity
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

    private static List<string> ExtractProjectionNames(JsonNode root)
    {
        var names = new List<string>();
        if (root["visual"]?["query"]?["queryState"] is not JsonObject queryState) return names;

        foreach (var role in queryState)
        {
            var projections = role.Value?["projections"]?.AsArray();
            if (projections == null) continue;

            foreach (var projection in projections)
            {
                var field = projection?["field"];
                var property = field?["Column"]?["Property"]?.ToString()
                               ?? field?["Measure"]?["Property"]?.ToString()
                               ?? field?["Aggregation"]?["Expression"]?["Column"]?["Property"]?.ToString()
                               ?? field?["Hierarchy"]?["Hierarchy"]?.ToString();

                if (!string.IsNullOrEmpty(property) &&
                    !names.Contains(property, StringComparer.OrdinalIgnoreCase))
                {
                    names.Add(property);
                }
            }
        }

        return names;
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
                    var entity = string.Empty;
                    var property = string.Empty;
                    TryGetFieldIdentity(comparison["Left"], out entity, out property);

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
}
