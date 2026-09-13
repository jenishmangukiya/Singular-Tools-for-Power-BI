using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SingularTools.Core.Models;

namespace SingularTools_App.Tools.SemanticColorManager;

/// <summary>
/// Persists the user's semantic color rules per report under
/// <c>%LOCALAPPDATA%\SingularPowerTools\semantic-colors\&lt;hash&gt;.json</c>.
/// Rules are user input, independent of the report definition, so they survive
/// closing the app and are reloaded when the same report is opened again.
/// </summary>
internal static class SemanticColorRuleStore
{
    private sealed class Payload
    {
        public string ReportPath { get; set; } = string.Empty;
        public List<SemanticColorRule> Rules { get; set; } = new();
    }

    private static string RootDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "SingularPowerTools",
        "semantic-colors");

    public static List<SemanticColorRule> Load(string reportPath)
    {
        var rules = new List<SemanticColorRule>();
        if (string.IsNullOrWhiteSpace(reportPath)) return rules;

        try
        {
            var file = FileFor(reportPath);
            if (!File.Exists(file)) return rules;

            var payload = JsonSerializer.Deserialize<Payload>(File.ReadAllText(file));
            if (payload?.Rules != null)
            {
                rules.AddRange(payload.Rules);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Semantic color rule load failed: {ex.Message}");
        }

        return rules;
    }

    public static void Save(string reportPath, IEnumerable<SemanticColorRule> rules)
    {
        if (string.IsNullOrWhiteSpace(reportPath)) return;

        try
        {
            Directory.CreateDirectory(RootDirectory);

            var payload = new Payload
            {
                ReportPath = reportPath,
                Rules = new List<SemanticColorRule>(rules)
            };

            var options = new JsonSerializerOptions { WriteIndented = true };
            File.WriteAllText(FileFor(reportPath), JsonSerializer.Serialize(payload, options));
        }
        catch (Exception ex)
        {
            App.Log($"Semantic color rule save failed: {ex.Message}");
        }
    }

    private static string FileFor(string reportPath)
    {
        var normalized = Path.GetFullPath(reportPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .ToLowerInvariant();

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)));
        return Path.Combine(RootDirectory, hash + ".json");
    }
}
