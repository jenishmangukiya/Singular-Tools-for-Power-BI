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
                Hex = rule["hex"]?.ToString() ?? string.Empty
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
            array.Add(new JsonObject
            {
                ["value"] = rule.Value,
                ["hex"] = rule.Hex
            });
        }

        section[RulesKey] = array;
        SetFeatureSection(ColorSyncFeature, section);
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
