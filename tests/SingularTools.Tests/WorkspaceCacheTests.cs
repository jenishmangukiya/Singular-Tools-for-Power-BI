using System;
using System.IO;
using System.Linq;
using SingularTools.Core;
using SingularTools.Core.Models;
using Xunit;

namespace SingularTools.Tests;

public class WorkspaceCacheTests
{
    private static string NewTempRoot(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void SaveAndLoad_RoundTripsNamesAndTimestamp()
    {
        var folder = NewTempRoot("WS_CacheRound_");
        var path = Path.Combine(folder, WorkspaceCacheStore.FileName);

        try
        {
            var stamp = new DateTimeOffset(2026, 10, 1, 9, 14, 0, TimeSpan.Zero);
            Assert.True(WorkspaceCacheStore.SaveDetected(new[] { "My workspace", "Client A" }, stamp, path));

            var loaded = WorkspaceCacheStore.Load(path);
            Assert.Equal(new[] { "My workspace", "Client A" }, loaded.Names);
            Assert.True(loaded.HasBeenDetected);
            Assert.Equal(stamp, loaded.LastDetectedUtc);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenFileMissing()
    {
        var folder = NewTempRoot("WS_CacheMissing_");
        try
        {
            var loaded = WorkspaceCacheStore.Load(Path.Combine(folder, "nope.json"));
            Assert.Empty(loaded.Names);
            Assert.False(loaded.HasBeenDetected);
            Assert.Null(loaded.DescribeLastDetected());
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Load_ReturnsEmpty_WhenFileCorrupt()
    {
        var folder = NewTempRoot("WS_CacheCorrupt_");
        var path = Path.Combine(folder, WorkspaceCacheStore.FileName);
        File.WriteAllText(path, "{ this is not json ");

        try
        {
            var loaded = WorkspaceCacheStore.Load(path);
            Assert.Empty(loaded.Names);
            Assert.False(loaded.HasBeenDetected);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Names_AreDeduplicatedCaseInsensitively_AndBlanksDropped()
    {
        var folder = NewTempRoot("WS_CacheDedupe_");
        var path = Path.Combine(folder, WorkspaceCacheStore.FileName);

        try
        {
            WorkspaceCacheStore.SaveDetected(new[] { "Alpha", "alpha", "  ", "", "Beta", " Alpha " }, null, path);

            var loaded = WorkspaceCacheStore.Load(path);
            Assert.Equal(new[] { "Alpha", "Beta" }, loaded.Names);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void DescribeLastDetected_FormatsLocalTime()
    {
        var cache = new WorkspaceCache
        {
            Names = { "My workspace" },
            // 13:14 UTC shown in the machine's local zone.
            LastDetectedUtc = new DateTimeOffset(2026, 10, 1, 13, 14, 0, TimeSpan.Zero)
        };

        var expected = cache.LastDetectedLocal!.Value.ToString("dd MMM yyyy, HH:mm");
        Assert.Equal($"Last detected {expected}", cache.DescribeLastDetected());
    }

    [Fact]
    public void Migration_SeedsFromLegacySelectionFiles()
    {
        var folder = NewTempRoot("WS_CacheMigrate_");
        File.WriteAllText(Path.Combine(folder, "publishing-manager.json"), "[\"Shared WS\"]");
        File.WriteAllText(Path.Combine(folder, "publishing-groups-workspaces.json"), "[\"Group WS\", \"shared ws\"]");

        try
        {
            var migrated = WorkspaceCacheStore.TryMigrateLegacySelection(folder);

            Assert.NotNull(migrated);
            Assert.Equal(new[] { "Shared WS", "Group WS" }, migrated!.Names);
            // The legacy files only recorded names, so no timestamp is claimed.
            Assert.False(migrated.HasBeenDetected);

            // The legacy files must survive so a rollback still finds them.
            Assert.True(File.Exists(Path.Combine(folder, "publishing-manager.json")));
            Assert.True(File.Exists(Path.Combine(folder, "publishing-groups-workspaces.json")));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Migration_IsSkipped_WhenCacheAlreadyExists()
    {
        var folder = NewTempRoot("WS_CacheMigrateSkip_");
        File.WriteAllText(Path.Combine(folder, "publishing-manager.json"), "[\"Old WS\"]");
        WorkspaceCacheStore.SaveDetected(new[] { "New WS" }, null, Path.Combine(folder, WorkspaceCacheStore.FileName));

        try
        {
            Assert.Null(WorkspaceCacheStore.TryMigrateLegacySelection(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Migration_ReturnsNull_WhenNothingToMigrate()
    {
        var folder = NewTempRoot("WS_CacheMigrateNone_");
        try
        {
            Assert.Null(WorkspaceCacheStore.TryMigrateLegacySelection(folder));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void Save_SkipsWrite_WhenContentUnchanged()
    {
        var folder = NewTempRoot("WS_CacheNoop_");
        var path = Path.Combine(folder, WorkspaceCacheStore.FileName);
        var cache = new WorkspaceCache
        {
            Names = { "My workspace" },
            LastDetectedUtc = new DateTimeOffset(2026, 10, 1, 9, 0, 0, TimeSpan.Zero)
        };

        try
        {
            Assert.True(WorkspaceCacheStore.Save(cache, path));
            var firstWrite = File.GetLastWriteTimeUtc(path);

            System.Threading.Thread.Sleep(50);
            Assert.True(WorkspaceCacheStore.Save(cache, path));

            Assert.Equal(firstWrite, File.GetLastWriteTimeUtc(path));
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void PublishingGroups_RoundTripWorkspaceNames()
    {
        var folder = NewTempRoot("WS_GroupDest_");
        var reportFolder = Path.Combine(folder, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        try
        {
            var config = ReportConfig.Empty();
            config.SetPublishingGroups(new[]
            {
                new PublishingGroup
                {
                    Name = "Client A",
                    VisiblePageIds = new System.Collections.Generic.List<string> { "p1" },
                    WorkspaceNames = new System.Collections.Generic.List<string> { "My workspace", "Client A WS" }
                }
            });

            Assert.True(ReportConfigStore.Save(reportFolder, config));

            var groups = ReportConfigStore.Load(reportFolder).GetPublishingGroups();
            var group = groups.Single();

            Assert.Equal("Client A", group.Name);
            Assert.Equal(new[] { "p1" }, group.VisiblePageIds);
            Assert.Equal(new[] { "My workspace", "Client A WS" }, group.WorkspaceNames);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }

    [Fact]
    public void PublishingGroups_LegacyGroupWithoutWorkspaces_LoadsEmptyList()
    {
        var folder = NewTempRoot("WS_GroupLegacy_");
        var reportFolder = Path.Combine(folder, "Demo.Report");
        Directory.CreateDirectory(reportFolder);

        File.WriteAllText(Path.Combine(folder, "singular-tools.json"), """
        {
          "schemaVersion": 1,
          "features": {
            "publishGroups": {
              "groups": [ { "name": "Old", "visiblePageIds": [ "p1" ] } ]
            }
          }
        }
        """);

        try
        {
            var group = ReportConfigStore.Load(reportFolder).GetPublishingGroups().Single();
            Assert.Empty(group.WorkspaceNames);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
    }
}
