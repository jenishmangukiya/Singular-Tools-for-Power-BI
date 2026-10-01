using System;
using System.IO;
using System.Linq;
using SingularTools.Core;
using SingularTools.Core.Models;
using Xunit;

namespace SingularTools.Tests;

public class ReportConfigTests
{
    private static string NewTempRoot(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Save_CreatesConfigAtProjectRoot()
    {
        var projectRoot = NewTempRoot("PBIR_ConfigRoot_");
        var reportFolder = Path.Combine(projectRoot, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        try
        {
            var config = ReportConfig.Empty();
            config.SetColorSyncRules(new[]
            {
                new SemanticColorRule { Value = "Enterprise", Hex = "#118DFF" }
            });

            Assert.True(ReportConfigStore.Save(reportFolder, config));

            var expected = Path.Combine(projectRoot, "singular-tools.json");
            Assert.True(File.Exists(expected), "config should be written at the project root");
            Assert.Equal(expected, ReportConfigStore.ResolveConfigPath(reportFolder));
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void SaveAndLoad_RoundTripsColorSyncRules()
    {
        var projectRoot = NewTempRoot("PBIR_ConfigRound_");
        var reportFolder = Path.Combine(projectRoot, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        try
        {
            var config = ReportConfig.Empty();
            config.SetColorSyncRules(new[]
            {
                new SemanticColorRule { Value = "Enterprise", Hex = "#118DFF" },
                new SemanticColorRule { Value = "Yes", Hex = "#00AA00" }
            });

            Assert.True(ReportConfigStore.Save(reportFolder, config));

            var loaded = ReportConfigStore.Load(reportFolder);
            var rules = loaded.GetColorSyncRules();

            Assert.Equal(2, rules.Count);
            Assert.Contains(rules, r => r.Value == "Enterprise" && r.Hex == "#118DFF");
            Assert.Contains(rules, r => r.Value == "Yes" && r.Hex == "#00AA00");
            Assert.Equal(1, loaded.SchemaVersion);
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void Save_PreservesUnknownFeaturesAndKeys()
    {
        var projectRoot = NewTempRoot("PBIR_ConfigPreserve_");
        var reportFolder = Path.Combine(projectRoot, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        var configPath = Path.Combine(projectRoot, "singular-tools.json");
        var original = """
        {
          "schemaVersion": 1,
          "tool": "Singular Tools",
          "customTopLevel": { "note": "keep me" },
          "features": {
            "futureFeature": { "enabled": true, "items": [1, 2, 3] },
            "colorSync": { "rules": [ { "value": "Old", "hex": "#111111" } ] }
          }
        }
        """;
        File.WriteAllText(configPath, original);

        try
        {
            var config = ReportConfigStore.Load(reportFolder);
            config.SetColorSyncRules(new[]
            {
                new SemanticColorRule { Value = "New", Hex = "#222222" }
            });

            Assert.True(ReportConfigStore.Save(reportFolder, config));

            var written = File.ReadAllText(configPath);
            Assert.Contains("customTopLevel", written);
            Assert.Contains("keep me", written);
            Assert.Contains("futureFeature", written);
            Assert.Contains("enabled", written);
            Assert.Contains("New", written);

            var reloaded = ReportConfigStore.Load(reportFolder);
            Assert.DoesNotContain(reloaded.GetColorSyncRules(), r => r.Value == "Old");
            Assert.Contains(reloaded.GetColorSyncRules(), r => r.Value == "New");
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void ColorSyncRules_RoundTripScopeAndPages()
    {
        var projectRoot = NewTempRoot("PBIR_ConfigScope_");
        var reportFolder = Path.Combine(projectRoot, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        try
        {
            var config = ReportConfig.Empty();
            config.SetColorSyncRules(new[]
            {
                new SemanticColorRule { Value = "Yes", Hex = "#00AA00", Scope = SemanticColorScope.Report },
                new SemanticColorRule
                {
                    Value = "Enterprise",
                    Hex = "#118DFF",
                    Scope = SemanticColorScope.Pages,
                    PageIds = new System.Collections.Generic.List<string> { "aaa", "bbb" }
                }
            });

            Assert.True(ReportConfigStore.Save(reportFolder, config));

            var rules = ReportConfigStore.Load(reportFolder).GetColorSyncRules();
            Assert.Equal(2, rules.Count);

            var reportRule = rules.Single(r => r.Value == "Yes");
            Assert.Equal(SemanticColorScope.Report, reportRule.Scope);

            var pageRule = rules.Single(r => r.Value == "Enterprise");
            Assert.Equal(SemanticColorScope.Pages, pageRule.Scope);
            Assert.Equal(new[] { "aaa", "bbb" }, pageRule.PageIds);
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void ColorSyncRules_LegacyRuleWithoutScope_DefaultsToReport()
    {
        var projectRoot = NewTempRoot("PBIR_ConfigLegacy_");
        var reportFolder = Path.Combine(projectRoot, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        var configPath = Path.Combine(projectRoot, "singular-tools.json");
        File.WriteAllText(configPath, """
        {
          "schemaVersion": 1,
          "features": {
            "colorSync": { "rules": [ { "value": "Velo", "hex": "#123456" } ] }
          }
        }
        """);

        try
        {
            var rule = ReportConfigStore.Load(reportFolder).GetColorSyncRules().Single();
            Assert.Equal(SemanticColorScope.Report, rule.Scope);
            Assert.Empty(rule.PageIds);
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, true);
        }
    }

    [Fact]
    public void ResolveConfigPath_FallsBackToReportFolder_WhenNotDotReport()
    {
        var folder = NewTempRoot("PBIR_ConfigStandalone_");

        try
        {
            var expected = Path.Combine(Path.GetFullPath(folder), "singular-tools.json");
            Assert.Equal(expected, ReportConfigStore.ResolveConfigPath(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenNoConfigExists()
    {
        var projectRoot = NewTempRoot("PBIR_ConfigMissing_");
        var reportFolder = Path.Combine(projectRoot, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        try
        {
            var config = ReportConfigStore.Load(reportFolder);
            Assert.Empty(config.GetColorSyncRules());
        }
        finally
        {
            if (Directory.Exists(projectRoot)) Directory.Delete(projectRoot, true);
        }
    }
}
