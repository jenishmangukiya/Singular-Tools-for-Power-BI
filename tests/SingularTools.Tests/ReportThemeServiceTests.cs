using System;
using System.IO;
using System.Linq;
using SingularTools.Core;
using Xunit;

namespace SingularTools.Tests;

public class ReportThemeServiceTests
{
    private static string NewReport(string prefix)
    {
        var root = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        var report = Path.Combine(root, "Demo.Report");
        Directory.CreateDirectory(Path.Combine(report, "definition"));
        return report;
    }

    private static void Write(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    [Fact]
    public void GetDataColors_ReadsBaseTheme_NormalizesAndDedupes()
    {
        var report = NewReport("PBIR_ThemeBase_");

        try
        {
            Write(Path.Combine(report, "definition", "report.json"), """
            { "themeCollection": { "baseTheme": { "name": "MyTheme", "type": "SharedResources" } } }
            """);
            Write(
                Path.Combine(report, "StaticResources", "SharedResources", "BaseThemes", "MyTheme.json"),
                """{ "dataColors": ["#118dff", "#222222", "#118DFF"] }""");

            var colors = ReportThemeService.GetDataColors(report);

            Assert.Equal(new[] { "#118DFF", "#222222" }, colors);
        }
        finally
        {
            if (Directory.Exists(report)) Directory.Delete(Path.GetDirectoryName(report)!, true);
        }
    }

    [Fact]
    public void GetDataColors_PrefersCustomTheme()
    {
        var report = NewReport("PBIR_ThemeCustom_");

        try
        {
            Write(Path.Combine(report, "definition", "report.json"), """
            {
              "themeCollection": {
                "customTheme": { "name": "BrandTheme", "type": "RegisteredResources" },
                "baseTheme": { "name": "MyTheme", "type": "SharedResources" }
              }
            }
            """);
            Write(
                Path.Combine(report, "StaticResources", "RegisteredResources", "BrandTheme.json"),
                """{ "dataColors": ["#ABCDEF", "#123456"] }""");
            Write(
                Path.Combine(report, "StaticResources", "SharedResources", "BaseThemes", "MyTheme.json"),
                """{ "dataColors": ["#000000"] }""");

            var colors = ReportThemeService.GetDataColors(report);

            Assert.Equal(new[] { "#ABCDEF", "#123456" }, colors);
        }
        finally
        {
            if (Directory.Exists(report)) Directory.Delete(Path.GetDirectoryName(report)!, true);
        }
    }

    [Fact]
    public void GetDataColors_ReturnsEmpty_WhenNoTheme()
    {
        var report = NewReport("PBIR_ThemeNone_");

        try
        {
            Write(Path.Combine(report, "definition", "report.json"), """{ "objects": {} }""");

            Assert.Empty(ReportThemeService.GetDataColors(report));
            Assert.Empty(ReportThemeService.GetDataColors(string.Empty));
        }
        finally
        {
            if (Directory.Exists(report)) Directory.Delete(Path.GetDirectoryName(report)!, true);
        }
    }
}
