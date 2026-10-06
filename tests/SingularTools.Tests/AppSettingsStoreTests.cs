using System;
using System.IO;
using SingularTools.Core;
using Xunit;

namespace SingularTools.Tests;

public class AppSettingsStoreTests
{
    private static string NewTempRoot(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SaveAndLoad_RoundTripsTheme()
    {
        var folder = NewTempRoot("ST_SettingsRound_");
        var path = Path.Combine(folder, AppSettingsStore.FileName);

        try
        {
            Assert.True(AppSettingsStore.Save(new AppSettings { Theme = AppTheme.Dark }, path));

            var loaded = AppSettingsStore.Load(path);
            Assert.Equal(AppTheme.Dark, loaded.Theme);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_ReturnsSystemDefault_WhenFileMissing()
    {
        var folder = NewTempRoot("ST_SettingsMissing_");
        try
        {
            var loaded = AppSettingsStore.Load(Path.Combine(folder, AppSettingsStore.FileName));
            Assert.Equal(AppTheme.System, loaded.Theme);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_ReturnsSystemDefault_WhenFileCorrupt()
    {
        var folder = NewTempRoot("ST_SettingsCorrupt_");
        var path = Path.Combine(folder, AppSettingsStore.FileName);

        try
        {
            File.WriteAllText(path, "{ this is not json");
            Assert.Equal(AppTheme.System, AppSettingsStore.Load(path).Theme);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Theory]
    [InlineData("light", AppTheme.Light)]
    [InlineData("dark", AppTheme.Dark)]
    [InlineData("Dark", AppTheme.Dark)]
    [InlineData("system", AppTheme.System)]
    [InlineData("nonsense", AppTheme.System)]
    public void ParseTheme_MapsStoredStrings(string raw, AppTheme expected)
    {
        Assert.Equal(expected, AppSettingsStore.ParseTheme(raw));
    }

    [Fact]
    public void ThemeToString_RoundTripsThroughParseTheme()
    {
        foreach (var theme in new[] { AppTheme.System, AppTheme.Light, AppTheme.Dark })
        {
            Assert.Equal(theme, AppSettingsStore.ParseTheme(AppSettingsStore.ThemeToString(theme)));
        }
    }
}
