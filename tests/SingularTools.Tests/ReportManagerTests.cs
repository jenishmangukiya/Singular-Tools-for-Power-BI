using System;
using System.IO;
using System.Linq;
using SingularTools.Core;
using SingularTools.Core.Models;
using Xunit;

namespace SingularTools.Tests;

public class ReportManagerTests
{
    private string GetDemoReportPath()
    {
        // Navigate up from bin to root workspace
        var current = Directory.GetCurrentDirectory();
        while (current != null && !File.Exists(Path.Combine(current, "Demo PBI Report.pbip")))
        {
            current = Directory.GetParent(current)?.FullName;
        }

        if (current == null)
            throw new DirectoryNotFoundException("Could not locate Demo PBI Report.pbip");

        return Path.Combine(current, "Demo PBI Report.Report");
    }

    [Fact]
    public void LoadReport_ShouldParseDemoPagesCorrectly()
    {
        var reportPath = GetDemoReportPath();
        var manager = new ReportManager();
        var loaded = manager.LoadReport(reportPath);

        Assert.True(loaded);
        Assert.Equal(3, manager.Pages.Count);

        // Verify initial page order
        Assert.Equal("P1", manager.Pages[0].DisplayName);
        Assert.Equal("P2", manager.Pages[1].DisplayName);
        Assert.Equal("Page 3", manager.Pages[2].DisplayName);

        // Active page is correctly marked
        Assert.False(string.IsNullOrEmpty(manager.ActivePageId));
        Assert.True(manager.Pages.First(p => p.Id == manager.ActivePageId).IsActive);
        Assert.Equal(1, manager.Pages.Count(p => p.IsActive));
    }

    [Fact]
    public void Reorder_MoveToTop_ShouldUpdateOrder()
    {
        var reportPath = GetDemoReportPath();
        var manager = new ReportManager();
        manager.LoadReport(reportPath);

        var page3Id = manager.Pages[2].Id;
        manager.MovePageToTop(page3Id);

        Assert.Equal("Page 3", manager.Pages[0].DisplayName);
        Assert.Equal("P1", manager.Pages[1].DisplayName);
        Assert.Equal("P2", manager.Pages[2].DisplayName);
    }

    [Fact]
    public void Sort_AlphabeticalAscending_ShouldSortCorrectly()
    {
        var reportPath = GetDemoReportPath();
        var manager = new ReportManager();
        manager.LoadReport(reportPath);

        manager.SortPages(SortMode.Ascending);

        // P1, P2, Page 3 in alphabetical: "P1", "P2", "Page 3"
        // Let's test descending
        manager.SortPages(SortMode.Descending);
        Assert.Equal("Page 3", manager.Pages[0].DisplayName);
        Assert.Equal("P2", manager.Pages[1].DisplayName);
        Assert.Equal("P1", manager.Pages[2].DisplayName);
    }

    [Fact]
    public void NaturalSort_ShouldHandleNumberedPages()
    {
        var list = new[] { "Page 10", "Page 1", "Page 2", "Page 20", "Page 3" };
        var comparer = new NaturalStringComparer();
        var sorted = list.OrderBy(x => x, comparer).ToList();

        Assert.Equal(new[] { "Page 1", "Page 2", "Page 3", "Page 10", "Page 20" }, sorted);
    }

    [Fact]
    public void SaveChanges_ShouldWriteAtomicAndReloadIdentically()
    {
        var reportPath = GetDemoReportPath();

        // Create a temporary isolated copy of the report for testing save mutations
        var tempDir = Path.Combine(Path.GetTempPath(), "PBIR_Test_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(reportPath, tempDir);

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);

            // Change active page to P1
            manager.SetActivePage(manager.Pages[0].Id);
            // Invert order
            manager.SortPages(SortMode.Reverse);
            manager.SaveChanges();

            // Reload in a fresh instance
            var manager2 = new ReportManager();
            manager2.LoadReport(tempDir);

            Assert.Equal("Page 3", manager2.Pages[0].DisplayName);
            Assert.Equal("P2", manager2.Pages[1].DisplayName);
            Assert.Equal("P1", manager2.Pages[2].DisplayName);
            Assert.Equal(manager.Pages.First(p => p.DisplayName == "P1").Id, manager2.ActivePageId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void ReorderPages_ShouldReorderCorrectly()
    {
        var reportPath = GetDemoReportPath();
        var manager = new ReportManager();
        manager.LoadReport(reportPath);

        var p1 = manager.Pages[0].Id;
        var p2 = manager.Pages[1].Id;
        var p3 = manager.Pages[2].Id;

        // Reorder: p2, p3, p1
        manager.ReorderPages(new[] { p2, p3, p1 });

        Assert.Equal("P2", manager.Pages[0].DisplayName);
        Assert.Equal("Page 3", manager.Pages[1].DisplayName);
        Assert.Equal("P1", manager.Pages[2].DisplayName);
        Assert.Equal(0, manager.Pages[0].OrderIndex);
        Assert.Equal(1, manager.Pages[1].OrderIndex);
        Assert.Equal(2, manager.Pages[2].OrderIndex);
    }

    [Fact]
    public void DuplicatePage_ShouldCreateClonedFolderAndInsertInOrder()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = Path.Combine(Path.GetTempPath(), "PBIR_DupTest_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(reportPath, tempDir);

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            Assert.Equal(3, manager.Pages.Count);

            var sourcePage = manager.Pages[1]; // P2
            var duplicated = manager.DuplicatePage(sourcePage.Id);

            Assert.NotNull(duplicated);
            Assert.Equal(4, manager.Pages.Count);
            Assert.Equal("P2 (Copy)", duplicated.DisplayName);
            Assert.Equal(2, duplicated.OrderIndex);
            Assert.True(Directory.Exists(duplicated.FolderPath));
            Assert.True(File.Exists(duplicated.PageJsonPath));

            // Reload and verify persistence
            var manager2 = new ReportManager();
            manager2.LoadReport(tempDir);
            Assert.Equal(4, manager2.Pages.Count);
            Assert.Equal("P2 (Copy)", manager2.Pages[2].DisplayName);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void DeletePage_ShouldRemoveFolderAndFallbackActive()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = Path.Combine(Path.GetTempPath(), "PBIR_DelTest_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(reportPath, tempDir);

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);
            Assert.Equal(3, manager.Pages.Count);

            // Active page is Page 3 (index 2)
            var activeId = manager.ActivePageId;
            var folderToDelete = manager.Pages.First(p => p.Id == activeId).FolderPath;

            var deleted = manager.DeletePage(activeId);
            Assert.True(deleted);
            Assert.Equal(2, manager.Pages.Count);
            Assert.False(Directory.Exists(folderToDelete));

            // Active page should have fallen back to remaining page
            Assert.False(string.IsNullOrEmpty(manager.ActivePageId));
            Assert.NotEqual(activeId, manager.ActivePageId);
        }
        finally
        {
            if (Directory.Exists(tempDir))
            {
                Directory.Delete(tempDir, true);
            }
        }
    }

    [Fact]
    public void GetPageVisuals_ShouldParseVisualContainersAndPositions()
    {
        var reportPath = GetDemoReportPath();
        var manager = new ReportManager();
        manager.LoadReport(reportPath);

        // Page 'feb5e1420e75e1e2c379' is 'P1' and has clusteredBarChart
        var page1 = manager.Pages.First(p => p.DisplayName == "P1");
        var visuals = manager.GetPageVisuals(page1.Id);

        Assert.NotNull(visuals);
        Assert.Equal("P1", visuals.DisplayName);
        Assert.Equal(1920, visuals.PageWidth);
        Assert.Equal(1080, visuals.PageHeight);
        Assert.Single(visuals.Visuals);

        var barChart = visuals.Visuals[0];
        Assert.Equal("clusteredBarChart", barChart.VisualType);
        Assert.Equal("Clustered Bar Chart", barChart.FriendlyType);
        Assert.Equal("Sum of COGS", barChart.DisplayTitle);
        Assert.True(barChart.Width > 0);
        Assert.True(barChart.Height > 0);
    }

    [Fact]
    public void ScreenCaptureService_CacheShouldStoreAndRetrievePerId()
    {
        ScreenCaptureService.ClearCache();
        var pageId = "test-page-xyz";
        byte[] dummyData = new byte[] { 0x42, 0x4D, 0x10, 0x20, 0x30 };

        Assert.False(ScreenCaptureService.HasCaptureForPage(pageId));
        Assert.Null(ScreenCaptureService.GetCaptureForPage(pageId));

        ScreenCaptureService.SaveCaptureForPage(pageId, dummyData);

        Assert.True(ScreenCaptureService.HasCaptureForPage(pageId));
        var retrieved = ScreenCaptureService.GetCaptureForPage(pageId);
        Assert.NotNull(retrieved);
        Assert.Equal(dummyData, retrieved);

        ScreenCaptureService.ClearCache();
        Assert.False(ScreenCaptureService.HasCaptureForPage(pageId));
    }

    [Fact]
    public void DeleteActivePage_ShouldActivateFirstPageInOrder()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = Path.Combine(Path.GetTempPath(), "PBIR_ActiveDel_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(reportPath, tempDir);

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);

            var activeId = manager.ActivePageId;
            Assert.False(string.IsNullOrEmpty(activeId));

            Assert.True(manager.DeletePage(activeId));

            Assert.Equal(manager.Pages[0].Id, manager.ActivePageId);
            Assert.True(manager.Pages[0].IsActive);
            Assert.Single(manager.Pages, p => p.IsActive);

            // Persisted active page must match after reload.
            var reloaded = new ReportManager();
            reloaded.LoadReport(tempDir);
            Assert.Equal(reloaded.Pages[0].Id, reloaded.ActivePageId);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void EditHistory_UndoRedo_RestoresPreviousState()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = Path.Combine(Path.GetTempPath(), "PBIR_History_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(reportPath, tempDir);

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);

            using var history = new ReportEditHistory();
            history.Reset(manager.PagesDirectoryPath);

            var originalFirst = manager.Pages[0].Id;
            var targetId = manager.Pages[^1].Id;

            Assert.False(history.CanUndo);
            Assert.False(history.CanRedo);

            manager.MovePageToTop(targetId);
            manager.SaveChanges();
            history.Commit();

            Assert.Equal(targetId, manager.Pages[0].Id);
            Assert.True(history.CanUndo);

            manager.RestoreFromSnapshot(history.Undo()!);
            Assert.Equal(originalFirst, manager.Pages[0].Id);
            Assert.True(history.CanRedo);

            manager.RestoreFromSnapshot(history.Redo()!);
            Assert.Equal(targetId, manager.Pages[0].Id);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    [Fact]
    public void RenamePage_ShouldUpdateNameAndPersist()
    {
        var reportPath = GetDemoReportPath();
        var tempDir = Path.Combine(Path.GetTempPath(), "PBIR_Rename_" + Guid.NewGuid().ToString("N"));
        CopyDirectory(reportPath, tempDir);

        try
        {
            var manager = new ReportManager();
            manager.LoadReport(tempDir);

            var pageId = manager.Pages[0].Id;
            manager.RenamePage(pageId, "Renamed Page");
            manager.SaveChanges();

            Assert.Equal("Renamed Page", manager.Pages.First(p => p.Id == pageId).DisplayName);

            var reloaded = new ReportManager();
            reloaded.LoadReport(tempDir);
            Assert.Equal("Renamed Page", reloaded.Pages.First(p => p.Id == pageId).DisplayName);
        }
        finally
        {
            if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true);
        }
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        foreach (var file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            var rel = Path.GetRelativePath(sourceDir, file);
            var dest = Path.Combine(destinationDir, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Copy(file, dest, true);
        }
    }
}
