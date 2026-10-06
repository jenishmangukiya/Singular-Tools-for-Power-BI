using System;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using SingularTools.Core;
using SingularTools.Core.Models;
using Xunit;

namespace SingularTools.Tests;

public class ReportManagerTests
{
    // ------------------------------------------------------------- Fixtures

    /// <summary>
    /// The canonical three-page report these tests exercise. Display names and
    /// order are part of the assertions below, so they are built explicitly
    /// instead of read out of the mutable demo report.
    /// </summary>
    private static TestReport ThreePageReport() => TestReports.Create(new[]
    {
        new TestPage("P1"),
        new TestPage("P2"),
        new TestPage("Page 3")
    });

    /// <summary>Three pages where P1 carries the single clustered bar chart the tests inspect.</summary>
    private static TestReport ThreePageReportWithBarChart() => TestReports.Create(new[]
    {
        new TestPage(
            "P1",
            new TestVisual(
                "clusteredBarChart",
                new TestProjection("Category", "financials", "Product"))
            {
                Title = "Sum of COGS",
                X = 32,
                Y = 48,
                Width = 640,
                Height = 360
            }),
        new TestPage("P2"),
        new TestPage("Page 3")
    });

    // ------------------------------------------------------------- Parsing

    [Fact]
    public void LoadReport_ShouldParseDemoPagesCorrectly()
    {
        using var report = ThreePageReport();
        var manager = new ReportManager();
        var loaded = manager.LoadReport(report.ReportPath);

        Assert.True(loaded);
        Assert.Equal(3, manager.Pages.Count);

        // Verify initial page order
        Assert.Equal("P1", manager.Pages[0].DisplayName);
        Assert.Equal("P2", manager.Pages[1].DisplayName);
        Assert.Equal("Page 3", manager.Pages[2].DisplayName);

        // Geometry is read back from page.json, not defaulted.
        Assert.Equal(1920, manager.Pages[0].Width);
        Assert.Equal(1080, manager.Pages[0].Height);
        Assert.Equal("FitToPage", manager.Pages[0].DisplayOption);

        // Active page is correctly marked
        Assert.False(string.IsNullOrEmpty(manager.ActivePageId));
        Assert.Equal(manager.Pages[2].Id, manager.ActivePageId);
        Assert.True(manager.Pages.First(p => p.Id == manager.ActivePageId).IsActive);
        Assert.Equal(1, manager.Pages.Count(p => p.IsActive));
    }

    [Fact]
    public void Reorder_MoveToTop_ShouldUpdateOrder()
    {
        using var report = ThreePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        var page3Id = manager.Pages[2].Id;
        manager.MovePageToTop(page3Id);

        Assert.Equal("Page 3", manager.Pages[0].DisplayName);
        Assert.Equal("P1", manager.Pages[1].DisplayName);
        Assert.Equal("P2", manager.Pages[2].DisplayName);
    }

    [Fact]
    public void Sort_AlphabeticalAscending_ShouldSortCorrectly()
    {
        using var report = ThreePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

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
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        // Change active page to P1
        manager.SetActivePage(manager.Pages[0].Id);
        // Invert order
        manager.SortPages(SortMode.Reverse);
        manager.SaveChanges();

        // The atomic write must not leave its staging file behind.
        Assert.False(File.Exists(Path.Combine(report.PagesDirectoryPath, "pages.json.tmp")));

        // Reload in a fresh instance
        var manager2 = new ReportManager();
        manager2.LoadReport(report.ReportPath);

        Assert.Equal("Page 3", manager2.Pages[0].DisplayName);
        Assert.Equal("P2", manager2.Pages[1].DisplayName);
        Assert.Equal("P1", manager2.Pages[2].DisplayName);
        Assert.Equal(manager.Pages.First(p => p.DisplayName == "P1").Id, manager2.ActivePageId);
    }

    [Fact]
    public void ReorderPages_ShouldReorderCorrectly()
    {
        using var report = ThreePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

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

    // ------------------------------------------------------ Batch moves

    /// <summary>Five pages so a multi-selection block has room to move in both directions.</summary>
    private static TestReport FivePageReport() => TestReports.Create(new[]
    {
        new TestPage("P1"),
        new TestPage("P2"),
        new TestPage("P3"),
        new TestPage("P4"),
        new TestPage("P5")
    });

    [Fact]
    public void MovePagesUp_ShouldShiftContiguousBlockTogether()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        // P3 + P4 move up one: P1, P3, P4, P2, P5
        manager.MovePagesUp(new[] { manager.Pages[2].Id, manager.Pages[3].Id });

        Assert.Equal(new[] { "P1", "P3", "P4", "P2", "P5" }, manager.Pages.Select(p => p.DisplayName));
        Assert.Equal(0, manager.Pages[0].OrderIndex);
        Assert.Equal(4, manager.Pages[4].OrderIndex);
    }

    [Fact]
    public void MovePagesUp_ShouldMoveNonContiguousSelectionIndependently()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        // P2 and P4 each step up once: P2, P1, P4, P3, P5
        manager.MovePagesUp(new[] { manager.Pages[1].Id, manager.Pages[3].Id });

        Assert.Equal(new[] { "P2", "P1", "P4", "P3", "P5" }, manager.Pages.Select(p => p.DisplayName));
    }

    [Fact]
    public void MovePagesUp_AtTop_IsANoOp()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        manager.MovePagesUp(new[] { manager.Pages[0].Id, manager.Pages[1].Id });

        Assert.Equal(new[] { "P1", "P2", "P3", "P4", "P5" }, manager.Pages.Select(p => p.DisplayName));
    }

    [Fact]
    public void MovePagesDown_ShouldShiftContiguousBlockTogether()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        // P2 + P3 move down one: P1, P4, P2, P3, P5
        manager.MovePagesDown(new[] { manager.Pages[1].Id, manager.Pages[2].Id });

        Assert.Equal(new[] { "P1", "P4", "P2", "P3", "P5" }, manager.Pages.Select(p => p.DisplayName));
    }

    [Fact]
    public void MovePagesDown_AtBottom_IsANoOp()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        manager.MovePagesDown(new[] { manager.Pages[4].Id });

        Assert.Equal(new[] { "P1", "P2", "P3", "P4", "P5" }, manager.Pages.Select(p => p.DisplayName));
    }

    [Fact]
    public void MovePagesToTop_ShouldKeepSelectionOrder()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        // P3 and P5 to the front, in report order: P3, P5, P1, P2, P4
        manager.MovePagesToTop(new[] { manager.Pages[4].Id, manager.Pages[2].Id });

        Assert.Equal(new[] { "P3", "P5", "P1", "P2", "P4" }, manager.Pages.Select(p => p.DisplayName));
    }

    [Fact]
    public void MovePagesToBottom_ShouldKeepSelectionOrder()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        // P1 and P3 to the back, in report order: P2, P4, P5, P1, P3
        manager.MovePagesToBottom(new[] { manager.Pages[0].Id, manager.Pages[2].Id });

        Assert.Equal(new[] { "P2", "P4", "P5", "P1", "P3" }, manager.Pages.Select(p => p.DisplayName));
    }

    [Fact]
    public void MovePages_ShouldPersistThroughSaveAndReload()
    {
        using var report = FivePageReport();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        manager.MovePagesToBottom(new[] { manager.Pages[0].Id, manager.Pages[2].Id });
        manager.SaveChanges();

        var manager2 = new ReportManager();
        manager2.LoadReport(report.ReportPath);

        Assert.Equal(new[] { "P2", "P4", "P5", "P1", "P3" }, manager2.Pages.Select(p => p.DisplayName));
    }

    // ------------------------------------------------------ Duplicate/delete

    [Fact]
    public void DuplicatePage_ShouldCreateClonedFolderAndInsertInOrder()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        Assert.Equal(3, manager.Pages.Count);

        var sourcePage = manager.Pages[1]; // P2
        var duplicated = manager.DuplicatePage(sourcePage.Id);

        Assert.NotNull(duplicated);
        Assert.Equal(4, manager.Pages.Count);
        Assert.Equal("P2 (Copy)", duplicated!.DisplayName);
        Assert.Equal(2, duplicated.OrderIndex);
        Assert.True(Directory.Exists(duplicated.FolderPath));
        Assert.True(File.Exists(duplicated.PageJsonPath));

        // Reload and verify persistence
        var manager2 = new ReportManager();
        manager2.LoadReport(report.ReportPath);
        Assert.Equal(4, manager2.Pages.Count);
        Assert.Equal("P2 (Copy)", manager2.Pages[2].DisplayName);
    }

    [Fact]
    public void DeletePage_ShouldRemoveFolderAndFallbackActive()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
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

    [Fact]
    public void GetPageVisuals_ShouldParseVisualContainersAndPositions()
    {
        using var report = ThreePageReportWithBarChart();
        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

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
        Assert.Equal(640, barChart.Width);
        Assert.Equal(360, barChart.Height);
    }

    [Fact]
    public void GetPageVisuals_ShouldReadTitleFromTheArrayShapePowerBiWrites()
    {
        using var report = ThreePageReportWithBarChart();

        // Power BI writes visual.objects.title as a single-element ARRAY. Indexing an array
        // with a string key throws, and GetPageVisuals swallows parse failures per visual,
        // so before this was handled a titled visual silently lost its title and fell back
        // to a projected field name. The fixture writes the object shape, so the real
        // shape is applied here.
        var visualPath = TestReports.Visual(report.ReportPath, "clusteredBarChart").VisualJsonPath;
        var root = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(visualPath))!.AsObject();

        var properties = root["visual"]!["objects"]!["title"]!["properties"];
        root["visual"]!["objects"]!["title"] = new System.Text.Json.Nodes.JsonArray(
            new System.Text.Json.Nodes.JsonObject { ["properties"] = properties!.DeepClone() });

        File.WriteAllText(visualPath, root.ToJsonString());

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        var visuals = manager.GetPageVisuals(manager.Pages.First(p => p.DisplayName == "P1").Id);

        Assert.Equal("Sum of COGS", Assert.Single(visuals.Visuals).DisplayTitle);
    }

    [Fact]
    public void Reload_WithVisualsFolderMissingVisualJson_ShouldSkipIt()
    {
        using var report = TestReports.Create(new[]
        {
            new TestPage(
                "P1",
                new TestVisual("clusteredBarChart", new TestProjection("Category", "financials", "Product")))
            {
                // Power BI leaves these behind when a visual is deleted by hand.
                DanglingVisualFolders = 2
            },
            new TestPage("P2")
        });

        // The generated ids are the ones discovery finds on disk.
        Assert.Equal(report.PageIds, TestReports.PageIds(report.ReportPath));
        Assert.Equal(report.PageIdFor("P1"), TestReports.PageWithVisuals(report.ReportPath));
        Assert.Equal(report.PageIdFor("P2"), TestReports.PageWithoutVisuals(report.ReportPath));

        // Dangling folders are skipped by discovery...
        var discovered = TestReports.PageVisuals(report.ReportPath, report.PageIdFor("P1"));
        Assert.Single(discovered);
        Assert.Equal("clusteredBarChart", TestReports.Visual(report.ReportPath, "clusteredBarChart").VisualType);

        var manager = new ReportManager();

        // ...and must not break the load.
        Assert.True(manager.LoadReport(report.ReportPath));
        Assert.Equal(2, manager.Pages.Count);

        // The dangling folder must be skipped when the page's visuals are read.
        var visuals = manager.GetPageVisuals(report.PageIdFor("P1"));
        Assert.Single(visuals.Visuals);
        Assert.Equal("clusteredBarChart", visuals.Visuals[0].VisualType);

        // ...and the report must survive a save/reload round trip.
        manager.SaveChanges();

        var reloaded = new ReportManager();
        Assert.True(reloaded.LoadReport(report.ReportPath));
        Assert.Single(reloaded.GetPageVisuals(report.PageIdFor("P1")).Visuals);
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

    // --------------------------------------------------------- Active page

    [Fact]
    public void DeleteActivePage_ShouldActivateFirstPageInOrder()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        var activeId = manager.ActivePageId;
        Assert.False(string.IsNullOrEmpty(activeId));

        Assert.True(manager.DeletePage(activeId));

        Assert.Equal(manager.Pages[0].Id, manager.ActivePageId);
        Assert.True(manager.Pages[0].IsActive);
        Assert.Single(manager.Pages, p => p.IsActive);

        // Persisted active page must match after reload.
        var reloaded = new ReportManager();
        reloaded.LoadReport(report.ReportPath);
        Assert.Equal(reloaded.Pages[0].Id, reloaded.ActivePageId);
    }

    [Fact]
    public void EditHistory_UndoRedo_RestoresPreviousState()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

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

    [Fact]
    public void EditHistory_CommittingAfterAnUndo_DiscardsTheRedoBranch()
    {
        // This is the trap that made Redo permanently disabled in every tool: undo was routed
        // through ReportWorkspace.ApplyEdit, which commits. A commit means "a new edit happened
        // from here", so it throws away the branch that was just stepped back from. Undo/redo must
        // therefore restore through ReportWorkspace.Undo/Redo, which do not commit.
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        using var history = new ReportEditHistory();
        history.Reset(manager.PagesDirectoryPath);

        manager.MovePageToTop(manager.Pages[^1].Id);
        manager.SaveChanges();
        history.Commit();

        history.Undo();
        Assert.True(history.CanRedo, "stepping back must leave the forward branch reachable");

        history.Commit();

        Assert.False(history.CanRedo, "committing after an undo is what discards the redo branch");
    }

    [Fact]
    public void EditHistory_RoundTripsThroughUndoAndRedoWithoutCommitting()
    {
        // The contract the tools rely on: stepping back and forward keeps both directions usable.
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        using var history = new ReportEditHistory();
        history.Reset(manager.PagesDirectoryPath);

        var originalFirst = manager.Pages[0].Id;
        var targetId = manager.Pages[^1].Id;

        manager.MovePageToTop(targetId);
        manager.SaveChanges();
        history.Commit();

        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        manager.RestoreFromSnapshot(history.Undo()!);
        Assert.True(history.CanRedo);
        Assert.False(history.CanUndo);

        manager.RestoreFromSnapshot(history.Redo()!);
        Assert.True(history.CanUndo);
        Assert.False(history.CanRedo);

        Assert.Equal(targetId, manager.Pages[0].Id);
        Assert.NotEqual(originalFirst, manager.Pages[0].Id);
    }

    [Fact]
    public void EditHistory_Undo_AlsoRestoresFilesOutsideThePagesDirectory()
    {
        // Report-level filters live in definition/report.json, outside the pages directory.
        // Without nominating it, an Undo would revert the visuals but leave the filter pointing
        // at the repaired field, leaving the report half-reverted.
        using var report = ThreePageReport();

        var reportJsonPath = Path.Combine(report.ReportPath, "definition", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportJsonPath)!);
        File.WriteAllText(reportJsonPath, "{ \"filterConfig\": { \"filters\": [] } }");

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        using var history = new ReportEditHistory();
        history.Reset(manager.PagesDirectoryPath, new[] { reportJsonPath });

        const string repaired = "{ \"filterConfig\": { \"filters\": [ { \"name\": \"repaired\" } ] } }";
        File.WriteAllText(reportJsonPath, repaired);
        history.Commit();

        Assert.Contains("repaired", File.ReadAllText(reportJsonPath));

        manager.RestoreFromSnapshot(history.Undo()!);

        Assert.DoesNotContain("repaired", File.ReadAllText(reportJsonPath));
        Assert.Contains("filters", File.ReadAllText(reportJsonPath));
    }

    [Fact]
    public void EditHistory_WithoutNominatedExtras_LeavesOutsideFilesAlone()
    {
        // The default must not start copying arbitrary files into snapshots.
        using var report = ThreePageReport();

        var reportJsonPath = Path.Combine(report.ReportPath, "definition", "report.json");
        Directory.CreateDirectory(Path.GetDirectoryName(reportJsonPath)!);
        File.WriteAllText(reportJsonPath, "{ \"untouched\": true }");

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        using var history = new ReportEditHistory();
        history.Reset(manager.PagesDirectoryPath);

        File.WriteAllText(reportJsonPath, "{ \"untouched\": false }");
        history.Commit();

        manager.RestoreFromSnapshot(history.Undo()!);

        Assert.Contains("false", File.ReadAllText(reportJsonPath));
    }

    [Fact]
    public void RenamePage_ShouldUpdateNameAndPersist()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        var pageId = manager.Pages[0].Id;
        manager.RenamePage(pageId, "Renamed Page");
        manager.SaveChanges();

        Assert.Equal("Renamed Page", manager.Pages.First(p => p.Id == pageId).DisplayName);

        var reloaded = new ReportManager();
        reloaded.LoadReport(report.ReportPath);
        Assert.Equal("Renamed Page", reloaded.Pages.First(p => p.Id == pageId).DisplayName);
    }

    [Fact]
    public void DeleteActivePage_PersistedMetadataShouldNeverReferenceDeletedPage()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        Assert.True(manager.Pages.Count >= 2);

        var activeId = manager.ActivePageId;
        Assert.True(manager.DeletePage(activeId));

        // pages.json must no longer list the deleted page, and its active page
        // must be one that still exists on disk (Power BI validates this).
        var pagesJsonPath = Path.Combine(report.PagesDirectoryPath, "pages.json");
        var node = JsonNode.Parse(File.ReadAllText(pagesJsonPath))!;
        var order = node["pageOrder"]!.AsArray().Select(n => n!.GetValue<string>()).ToList();

        Assert.DoesNotContain(activeId, order);

        var persistedActive = node["activePageName"]!.GetValue<string>();
        Assert.Contains(persistedActive, order);
        Assert.True(Directory.Exists(Path.Combine(report.PagesDirectoryPath, persistedActive)));
    }

    [Fact]
    public void SaveChanges_WhenActivePageMissing_ShouldFallbackToFirstPage()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);
        var firstPageId = manager.Pages[0].Id;

        // Simulate an invalid activePageName written by another tool/session.
        var pagesJsonPath = Path.Combine(report.PagesDirectoryPath, "pages.json");
        var node = JsonNode.Parse(File.ReadAllText(pagesJsonPath))!;
        node["activePageName"] = "00000000000000000000";
        File.WriteAllText(pagesJsonPath, node.ToJsonString());

        var reloaded = new ReportManager();
        reloaded.LoadReport(report.ReportPath);

        // Reload resolves the bogus active page to the first page in memory.
        Assert.Equal(firstPageId, reloaded.ActivePageId);
        Assert.True(reloaded.Pages[0].IsActive);

        reloaded.SaveChanges();

        // ...and the correction is persisted, never the invalid value.
        var saved = JsonNode.Parse(File.ReadAllText(pagesJsonPath))!;
        Assert.Equal(firstPageId, saved["activePageName"]!.GetValue<string>());
    }

    // ----------------------------------------------------------- Visibility

    [Fact]
    public void TogglePageVisibility_ShouldWriteHiddenInViewMode()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        var pageId = manager.Pages[0].Id;
        manager.TogglePageVisibility(pageId);
        manager.SaveChanges();

        var pageJsonPath = manager.Pages.First(p => p.Id == pageId).PageJsonPath;
        var node = JsonNode.Parse(File.ReadAllText(pageJsonPath))!;

        // PBIR expects "HiddenInViewMode"; the invalid "Hidden" value must never be written.
        Assert.Equal("HiddenInViewMode", node["visibility"]!.GetValue<string>());

        var reloaded = new ReportManager();
        reloaded.LoadReport(report.ReportPath);
        Assert.True(reloaded.Pages.First(p => p.Id == pageId).IsHidden);

        // Un-hiding removes the property entirely.
        reloaded.TogglePageVisibility(pageId);
        reloaded.SaveChanges();

        var cleared = JsonNode.Parse(File.ReadAllText(pageJsonPath))!;
        Assert.Null(cleared["visibility"]);
    }

    [Fact]
    public void SetPageVisibility_AndCapture_ShouldSwapAndRestore()
    {
        using var report = ThreePageReport();

        var manager = new ReportManager();
        manager.LoadReport(report.ReportPath);

        // Snapshot the report's own visibility, then hide every page.
        var original = manager.CaptureVisibility();
        Assert.Equal(manager.Pages.Count, original.Count);

        manager.SetPageVisibility(manager.Pages.ToDictionary(p => p.Id, _ => true));
        manager.SaveChanges();

        var hidden = new ReportManager();
        hidden.LoadReport(report.ReportPath);
        Assert.All(hidden.Pages, p => Assert.True(p.IsHidden));

        // Restoring the snapshot must put the report back exactly as it was.
        manager.SetPageVisibility(original);
        manager.SaveChanges();

        var restored = new ReportManager();
        restored.LoadReport(report.ReportPath);
        Assert.Equal(manager.Pages.Count, restored.Pages.Count);
        foreach (var page in restored.Pages)
        {
            Assert.Equal(original[page.Id], page.IsHidden);
        }
    }
}