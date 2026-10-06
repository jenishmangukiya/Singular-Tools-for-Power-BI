using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using SingularTools.Core.Models;

namespace SingularTools.Core;

/// <summary>
/// Per-report user settings, persisted as <c>singular-tools.json</c> at the PBIP
/// project root (the folder holding the <c>.pbip</c>), or at the report folder
/// root for standalone reports.
///
/// The envelope is intentionally generic: every tool owns a section under
/// <c>features</c>, so future features can be added without changing the base
/// shape. The raw document is kept in memory and mutated in place, so sections
/// and keys written by other features (or newer versions) survive a save.
/// </summary>
public sealed class ReportConfig
{
    public const string FileName = "singular-tools.json";
    public const string ColorSyncFeature = "colorSync";
    public const string RulesKey = "rules";
    public const string PublishingGroupsFeature = "publishGroups";
    public const string GroupsKey = "groups";
    public const string PublishMultiFeature = "publishMulti";
    public const string WorkspacesKey = "workspaces";
    public const string SortByColumnFeature = "sortByColumn";
    public const string SuffixKey = "suffix";
    public const string ExcludedKey = "excluded";
    public const string BrokenVisualsFeature = "brokenVisuals";

    /// <summary>
    /// The persisted section key is deliberately still <c>brokenVisuals</c> even though the tool
    /// is now called Field Repair. It is written into every author's <c>singular-tools.json</c>;
    /// renaming it would orphan their saved remaps and last-scan timestamp. Display names change,
    /// stored keys do not.
    /// </summary>
    public const string RemapsKey = "remaps";
    public const string AutoFixKey = "autoFixCosmetic";
    public const string LastScanKey = "lastScanUtc";

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly JsonObject _root = new();

    /// <summary>Schema version of the config file.</summary>
    public int SchemaVersion { get; set; } = 1;

    public static ReportConfig Empty() => new();

    public static ReportConfig FromJson(string? json)
    {
        var config = new ReportConfig();
        if (string.IsNullOrWhiteSpace(json)) return config;

        try
        {
            if (JsonNode.Parse(json) is JsonObject root)
            {
                foreach (var pair in root)
                {
                    config._root[pair.Key] = pair.Value?.DeepClone();
                }
            }
        }
        catch
        {
            // A corrupt file should not stop the tool; fall back to an empty config.
            return new ReportConfig();
        }

        config.SchemaVersion = config.ReadSchemaVersion();
        return config;
    }

    public string ToJson()
    {
        _root["schemaVersion"] = SchemaVersion;
        _root["tool"] = "Singular Tools";
        if (_root["features"] is not JsonObject) _root["features"] = new JsonObject();

        return _root.ToJsonString(WriteOptions);
    }

    /// <summary>Reads the color-sync rules from this config (empty when absent).</summary>
    public List<SemanticColorRule> GetColorSyncRules()
    {
        var rules = new List<SemanticColorRule>();
        if (FeatureSection(ColorSyncFeature) is not JsonObject section) return rules;
        if (section[RulesKey] is not JsonArray array) return rules;

        foreach (var node in array)
        {
            if (node is not JsonObject rule) continue;

            var value = rule["value"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(value)) continue;

            rules.Add(new SemanticColorRule
            {
                Value = value,
                Hex = rule["hex"]?.ToString() ?? string.Empty,
                Scope = string.Equals(rule["scope"]?.ToString(), "pages", StringComparison.OrdinalIgnoreCase)
                    ? SemanticColorScope.Pages
                    : SemanticColorScope.Report,
                PageIds = ReadPageIds(rule["pageIds"] as JsonArray)
            });
        }

        return rules;
    }

    /// <summary>Writes the color-sync rules, preserving any other keys in the section.</summary>
    public void SetColorSyncRules(IEnumerable<SemanticColorRule> rules)
    {
        var section = FeatureSection(ColorSyncFeature) as JsonObject ?? new JsonObject();
        var array = new JsonArray();

        foreach (var rule in rules)
        {
            var node = new JsonObject
            {
                ["value"] = rule.Value,
                ["hex"] = rule.Hex,
                ["scope"] = rule.Scope == SemanticColorScope.Pages ? "pages" : "report"
            };

            if (rule.Scope == SemanticColorScope.Pages)
            {
                var pageIds = new JsonArray();
                foreach (var id in rule.PageIds ?? new List<string>())
                {
                    if (!string.IsNullOrWhiteSpace(id)) pageIds.Add(id);
                }

                node["pageIds"] = pageIds;
            }

            array.Add(node);
        }

        section[RulesKey] = array;
        SetFeatureSection(ColorSyncFeature, section);
    }

    private static List<string> ReadPageIds(JsonArray? array)
    {
        var ids = new List<string>();
        if (array == null) return ids;

        foreach (var node in array)
        {
            var id = node?.ToString();
            if (!string.IsNullOrWhiteSpace(id)) ids.Add(id);
        }

        return ids;
    }

    /// <summary>Reads the publishing groups from this config (empty when absent).</summary>
    public List<PublishingGroup> GetPublishingGroups()
    {
        var groups = new List<PublishingGroup>();
        if (FeatureSection(PublishingGroupsFeature) is not JsonObject section) return groups;
        if (section[GroupsKey] is not JsonArray array) return groups;

        foreach (var node in array)
        {
            if (node is not JsonObject group) continue;

            var name = group["name"]?.ToString() ?? string.Empty;
            if (string.IsNullOrWhiteSpace(name)) continue;

            groups.Add(new PublishingGroup
            {
                Name = name,
                VisiblePageIds = ReadPageIds(group["visiblePageIds"] as JsonArray),
                WorkspaceNames = ReadPageIds(group["workspaceNames"] as JsonArray)
            });
        }

        return groups;
    }

    /// <summary>Writes the publishing groups, preserving any other keys in the section.</summary>
    public void SetPublishingGroups(IEnumerable<PublishingGroup> groups)
    {
        var section = FeatureSection(PublishingGroupsFeature) as JsonObject ?? new JsonObject();
        var array = new JsonArray();

        foreach (var group in groups)
        {
            var pageIds = new JsonArray();
            foreach (var id in group.VisiblePageIds ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(id)) pageIds.Add(id);
            }

            var workspaces = new JsonArray();
            foreach (var workspace in group.WorkspaceNames ?? new List<string>())
            {
                if (!string.IsNullOrWhiteSpace(workspace)) workspaces.Add(workspace);
            }

            array.Add(new JsonObject
            {
                ["name"] = group.Name,
                ["visiblePageIds"] = pageIds,
                ["workspaceNames"] = workspaces
            });
        }

        section[GroupsKey] = array;
        SetFeatureSection(PublishingGroupsFeature, section);
    }

    /// <summary>
    /// Reads the workspaces remembered for multi-workspace publishing (empty when
    /// absent). This is the report's pre-ticked checkbox selection, so publishing
    /// the same report again needs no re-ticking.
    /// </summary>
    public List<string> GetPublishWorkspaces()
    {
        if (FeatureSection(PublishMultiFeature) is not JsonObject section) return new List<string>();
        return ReadPageIds(section[WorkspacesKey] as JsonArray);
    }

    /// <summary>Writes the multi-workspace publish selection, preserving any other keys in the section.</summary>
    public void SetPublishWorkspaces(IEnumerable<string> workspaces)
    {
        var section = FeatureSection(PublishMultiFeature) as JsonObject ?? new JsonObject();
        var array = new JsonArray();

        foreach (var workspace in workspaces ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(workspace)) array.Add(workspace);
        }

        section[WorkspacesKey] = array;
        SetFeatureSection(PublishMultiFeature, section);
    }

    /// <summary>Reads the order-column suffix remembered for this project (empty when unset).</summary>
    public string GetSortByColumnSuffix()
    {
        if (FeatureSection(SortByColumnFeature) is not JsonObject section) return string.Empty;
        return section[SuffixKey]?.ToString()?.Trim() ?? string.Empty;
    }

    /// <summary>Remembers the order-column suffix for this project.</summary>
    public void SetSortByColumnSuffix(string? suffix)
    {
        var section = FeatureSection(SortByColumnFeature) as JsonObject ?? new JsonObject();
        section[SuffixKey] = (suffix ?? string.Empty).Trim();
        SetFeatureSection(SortByColumnFeature, section);
    }

    /// <summary>Keys (table|baseColumn) the user switched off, so they survive a restart.</summary>
    public List<string> GetSortByColumnExcluded()
    {
        if (FeatureSection(SortByColumnFeature) is not JsonObject section) return new List<string>();
        return ReadPageIds(section[ExcludedKey] as JsonArray);
    }

    /// <summary>Writes the switched-off keys, preserving every other key in the section.</summary>
    public void SetSortByColumnExcluded(IEnumerable<string> keys)
    {
        var section = FeatureSection(SortByColumnFeature) as JsonObject ?? new JsonObject();
        var array = new JsonArray();
        foreach (var key in keys ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(key)) array.Add(key);
        }

        section[ExcludedKey] = array;
        SetFeatureSection(SortByColumnFeature, section);
    }

    /// <summary>
    /// Reads the remembered broken-visual remappings (empty when absent). Later entries win
    /// over earlier ones for the same old field, so the most recent decision is the one
    /// offered first on the next scan.
    /// </summary>
    public List<FieldRemap> GetFieldRemaps()
    {
        var remaps = new List<FieldRemap>();
        if (FeatureSection(BrokenVisualsFeature) is not JsonObject section) return remaps;
        if (section[RemapsKey] is not JsonArray array) return remaps;

        foreach (var node in array)
        {
            if (node is not JsonObject entry) continue;

            var oldEntity = entry["oldEntity"]?.ToString() ?? string.Empty;
            var oldProperty = entry["oldProperty"]?.ToString() ?? string.Empty;
            var newEntity = entry["newEntity"]?.ToString() ?? string.Empty;
            var newProperty = entry["newProperty"]?.ToString() ?? string.Empty;

            // A half-written entry is useless and would silently do nothing on replay.
            if (string.IsNullOrWhiteSpace(oldProperty) || string.IsNullOrWhiteSpace(newProperty)) continue;

            var recorded = DateTimeOffset.MinValue;
            if (entry["recordedUtc"]?.ToString() is string raw &&
                DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                recorded = parsed;
            }

            remaps.Add(new FieldRemap
            {
                OldEntity = oldEntity,
                OldProperty = oldProperty,
                NewEntity = newEntity,
                NewProperty = newProperty,
                RecordedUtc = recorded
            });
        }

        return remaps;
    }

    /// <summary>
    /// Writes the remembered remappings, preserving every other key in the section and every
    /// other feature. One entry per old field: a newer mapping replaces an older one rather
    /// than accumulating duplicates that would all replay.
    /// </summary>
    public void SetFieldRemaps(IEnumerable<FieldRemap> remaps)
    {
        var section = FeatureSection(BrokenVisualsFeature) as JsonObject ?? new JsonObject();

        var latest = new Dictionary<string, FieldRemap>(StringComparer.OrdinalIgnoreCase);
        foreach (var remap in remaps ?? Enumerable.Empty<FieldRemap>())
        {
            if (remap == null || string.IsNullOrWhiteSpace(remap.OldProperty)) continue;
            if (string.IsNullOrWhiteSpace(remap.NewProperty)) continue;
            latest[remap.OldKey] = remap;
        }

        var array = new JsonArray();
        foreach (var remap in latest.Values.OrderBy(r => r.OldKey, StringComparer.OrdinalIgnoreCase))
        {
            array.Add(new JsonObject
            {
                ["oldEntity"] = remap.OldEntity,
                ["oldProperty"] = remap.OldProperty,
                ["newEntity"] = remap.NewEntity,
                ["newProperty"] = remap.NewProperty,
                ["recordedUtc"] = remap.RecordedUtc.ToString("O")
            });
        }

        section[RemapsKey] = array;
        SetFeatureSection(BrokenVisualsFeature, section);
    }

    /// <summary>
    /// Whether cosmetic-only renames may be matched automatically. Defaults to false so a
    /// repair is never applied without the author seeing it.
    /// </summary>
    public bool GetBrokenVisualsAutoFix() =>
        FeatureSection(BrokenVisualsFeature)?[AutoFixKey]?.GetValue<bool>() ?? false;

    /// <summary>Remembers the cosmetic auto-fix preference.</summary>
    public void SetBrokenVisualsAutoFix(bool enabled)
    {
        var section = FeatureSection(BrokenVisualsFeature) as JsonObject ?? new JsonObject();
        section[AutoFixKey] = enabled;
        SetFeatureSection(BrokenVisualsFeature, section);
    }

    /// <summary>
    /// When this report was last checked by Field Repair, or null if it never has been.
    /// </summary>
    /// <remarks>
    /// Kept with the report rather than per machine so the answer to "was this report
    /// checked?" is the same for everyone who opens it.
    /// </remarks>
    public DateTimeOffset? GetBrokenVisualsLastScan()
    {
        var raw = FeatureSection(BrokenVisualsFeature)?[LastScanKey]?.ToString();
        if (string.IsNullOrWhiteSpace(raw)) return null;

        return DateTimeOffset.TryParse(raw, System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.RoundtripKind, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>Records that the report was checked at the given moment (UTC).</summary>
    public void SetBrokenVisualsLastScan(DateTimeOffset scannedUtc)
    {
        var section = FeatureSection(BrokenVisualsFeature) as JsonObject ?? new JsonObject();
        section[LastScanKey] = scannedUtc.ToUniversalTime().ToString("O");
        SetFeatureSection(BrokenVisualsFeature, section);
    }

    private int ReadSchemaVersion()
    {
        try
        {
            return _root["schemaVersion"]?.GetValue<int>() ?? 1;
        }
        catch
        {
            return 1;
        }
    }

    private JsonNode? FeatureSection(string feature)
    {
        return (_root["features"] as JsonObject)?[feature];
    }

    private void SetFeatureSection(string feature, JsonNode section)
    {
        if (_root["features"] is not JsonObject features)
        {
            features = new JsonObject();
            _root["features"] = features;
        }

        features[feature] = section;
    }
}
