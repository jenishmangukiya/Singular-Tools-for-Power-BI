using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using SingularTools.Core.Models;

namespace SingularTools.Core;

public class ReportManager
{
    public string ReportFolderPath { get; private set; } = string.Empty;
    public string PagesMetadataPath { get; private set; } = string.Empty;
    public string PagesDirectoryPath { get; private set; } = string.Empty;

    public List<ReportPage> Pages { get; private set; } = new();
    public string ActivePageId { get; private set; } = string.Empty;

    public static string? DiscoverReportFolder(string? startDirectory = null)
    {
        // 1. Try finding based on active Power BI window title
        if (PowerBiDetector.FindActivePowerBiWindow(out _, out var title, out _))
        {
            var reportName = PowerBiDetector.ExtractReportName(title);
            if (!string.IsNullOrEmpty(reportName))
            {
                var candidate = FindReportByName(reportName);
                if (candidate != null) return candidate;
            }
        }

        // 2. Try start directory or current directory
        startDirectory ??= Directory.GetCurrentDirectory();
        if (IsReportFolder(startDirectory)) return startDirectory;

        // Check subdirectories
        var subdirs = Directory.GetDirectories(startDirectory, "*.Report", SearchOption.TopDirectoryOnly);
        if (subdirs.Length > 0 && IsReportFolder(subdirs[0]))
        {
            return subdirs[0];
        }

        // Look for .pbip files
        var pbipFiles = Directory.GetFiles(startDirectory, "*.pbip", SearchOption.TopDirectoryOnly);
        if (pbipFiles.Length > 0)
        {
            var baseName = Path.GetFileNameWithoutExtension(pbipFiles[0]);
            var matchingReportDir = Path.Combine(startDirectory, $"{baseName}.Report");
            if (Directory.Exists(matchingReportDir) && IsReportFolder(matchingReportDir))
            {
                return matchingReportDir;
            }
        }

        // 3. Fallback: check workspace or drive locations
        var commonDrives = new[] { @"e:\Test_Projects\Singular Tools for Power BI", Directory.GetCurrentDirectory() };
        foreach (var dir in commonDrives)
        {
            if (Directory.Exists(dir))
            {
                var match = Directory.GetDirectories(dir, "*.Report", SearchOption.AllDirectories)
                                     .FirstOrDefault(IsReportFolder);
                if (match != null) return match;
            }
        }

        return null;
    }

    public static string? FindReportByName(string reportName)
    {
        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            @"e:\Test_Projects\Singular Tools for Power BI",
            Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
            @"C:\Users\" + Environment.UserName + @"\Documents"
        };

        foreach (var root in searchRoots)
        {
            if (!Directory.Exists(root)) continue;

            // Direct match
            var direct = Path.Combine(root, $"{reportName}.Report");
            if (IsReportFolder(direct)) return direct;

            // Recursive search up to 2 levels
            try
            {
                var found = Directory.GetDirectories(root, $"{reportName}.Report", SearchOption.AllDirectories)
                                     .FirstOrDefault(IsReportFolder);
                if (found != null) return found;
            }
            catch
            {
            }
        }

        return null;
    }

    public static bool IsReportFolder(string folderPath)
    {
        if (!Directory.Exists(folderPath)) return false;
        var pagesMeta = Path.Combine(folderPath, "definition", "pages", "pages.json");
        return File.Exists(pagesMeta);
    }

    public bool LoadReport(string folderPath)
    {
        if (!IsReportFolder(folderPath))
        {
            var candidate = DiscoverReportFolder(folderPath);
            if (candidate == null) return false;
            folderPath = candidate;
        }

        ReportFolderPath = Path.GetFullPath(folderPath);
        PagesMetadataPath = Path.Combine(ReportFolderPath, "definition", "pages", "pages.json");
        PagesDirectoryPath = Path.Combine(ReportFolderPath, "definition", "pages");

        Reload();
        return true;
    }

    public void Reload()
    {
        if (!File.Exists(PagesMetadataPath))
            throw new FileNotFoundException($"Pages metadata file not found at: {PagesMetadataPath}");

        var metaJson = File.ReadAllText(PagesMetadataPath);
        var metaDoc = JsonNode.Parse(metaJson);
        if (metaDoc == null) throw new InvalidOperationException("Failed to parse pages.json");

        ActivePageId = metaDoc["activePageName"]?.ToString() ?? string.Empty;
        var pageOrderNode = metaDoc["pageOrder"]?.AsArray();
        var pageOrder = pageOrderNode?.Select(n => n?.ToString() ?? string.Empty)
            .Where(id => !string.IsNullOrEmpty(id))
            .ToList() ?? new List<string>();

        var pageMap = new Dictionary<string, ReportPage>(StringComparer.OrdinalIgnoreCase);

        var subDirs = Directory.GetDirectories(PagesDirectoryPath);
        foreach (var dir in subDirs)
        {
            var pageJsonPath = Path.Combine(dir, "page.json");
            if (!File.Exists(pageJsonPath)) continue;

            try
            {
                var content = File.ReadAllText(pageJsonPath);
                var pNode = JsonNode.Parse(content);
                if (pNode == null) continue;

                var id = pNode["name"]?.ToString() ?? Path.GetFileName(dir);
                var displayName = pNode["displayName"]?.ToString() ?? id;
                var displayOption = pNode["displayOption"]?.ToString() ?? "FitToPage";
                var width = pNode["width"]?.GetValue<double>() ?? 1920;
                var height = pNode["height"]?.GetValue<double>() ?? 1080;
                var isHidden = pNode["visibility"]?.ToString()?.Equals("Hidden", StringComparison.OrdinalIgnoreCase) ?? false;

                var page = new ReportPage
                {
                    Id = id,
                    DisplayName = displayName,
                    DisplayOption = displayOption,
                    Width = width,
                    Height = height,
                    IsHidden = isHidden,
                    FolderPath = dir,
                    PageJsonPath = pageJsonPath,
                    IsActive = string.Equals(id, ActivePageId, StringComparison.OrdinalIgnoreCase)
                };

                pageMap[id] = page;
            }
            catch
            {
            }
        }

        var orderedList = new List<ReportPage>();
        int index = 0;

        foreach (var id in pageOrder)
        {
            if (pageMap.TryGetValue(id, out var page))
            {
                page.OrderIndex = index++;
                orderedList.Add(page);
                pageMap.Remove(id);
            }
        }

        foreach (var unlisted in pageMap.Values)
        {
            unlisted.OrderIndex = index++;
            orderedList.Add(unlisted);
        }

        Pages = orderedList;
        EnsureValidActivePage();
    }

    public void MovePage(int fromIndex, int toIndex)
    {
        if (fromIndex < 0 || fromIndex >= Pages.Count) return;
        if (toIndex < 0 || toIndex >= Pages.Count) return;
        if (fromIndex == toIndex) return;

        var item = Pages[fromIndex];
        Pages.RemoveAt(fromIndex);
        Pages.Insert(toIndex, item);

        ReindexPages();
    }

    public void MovePageUp(string pageId)
    {
        var idx = Pages.FindIndex(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (idx > 0)
        {
            MovePage(idx, idx - 1);
        }
    }

    public void MovePageDown(string pageId)
    {
        var idx = Pages.FindIndex(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx < Pages.Count - 1)
        {
            MovePage(idx, idx + 1);
        }
    }

    public void MovePageToTop(string pageId)
    {
        var idx = Pages.FindIndex(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (idx > 0)
        {
            MovePage(idx, 0);
        }
    }

    public void MovePageToBottom(string pageId)
    {
        var idx = Pages.FindIndex(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0 && idx < Pages.Count - 1)
        {
            MovePage(idx, Pages.Count - 1);
        }
    }

    public void SortPages(SortMode mode)
    {
        switch (mode)
        {
            case SortMode.Ascending:
                Pages = Pages.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
                break;
            case SortMode.Descending:
                Pages = Pages.OrderByDescending(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase).ToList();
                break;
            case SortMode.Natural:
                Pages = Pages.OrderBy(p => p.DisplayName, new NaturalStringComparer()).ToList();
                break;
            case SortMode.Reverse:
                Pages.Reverse();
                break;
        }

        ReindexPages();
    }

    public void SetActivePage(string pageId)
    {
        var target = Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (target != null)
        {
            ActivePageId = target.Id;
            foreach (var p in Pages)
            {
                p.IsActive = string.Equals(p.Id, target.Id, StringComparison.OrdinalIgnoreCase);
            }
        }
    }

    public void RenamePage(string pageId, string newDisplayName)
    {
        var target = Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (target != null && !string.IsNullOrWhiteSpace(newDisplayName))
        {
            target.DisplayName = newDisplayName.Trim();
            UpdatePageDefinition(target);
        }
    }

    public void ReorderPages(IEnumerable<string> orderedIds)
    {
        var pageMap = Pages.ToDictionary(p => p.Id, StringComparer.OrdinalIgnoreCase);
        var newList = new List<ReportPage>();

        foreach (var id in orderedIds)
        {
            if (pageMap.TryGetValue(id, out var page))
            {
                newList.Add(page);
                pageMap.Remove(id);
            }
        }

        // Add any remaining unlisted pages
        foreach (var page in pageMap.Values)
        {
            newList.Add(page);
        }

        Pages = newList;
        ReindexPages();
    }

    public ReportPage? DuplicatePage(string pageId, string? newDisplayName = null)
    {
        var sourceIndex = Pages.FindIndex(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (sourceIndex < 0) return null;

        var sourcePage = Pages[sourceIndex];
        if (!Directory.Exists(sourcePage.FolderPath)) return null;

        // Generate a 20-character hex ID compliant with Power BI PBIR conventions
        string newId;
        string destFolder;
        do
        {
            byte[] bytes = new byte[10];
            System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
            newId = Convert.ToHexString(bytes).ToLowerInvariant();
            destFolder = Path.Combine(PagesDirectoryPath, newId);
        } while (Directory.Exists(destFolder) || Pages.Any(p => string.Equals(p.Id, newId, StringComparison.OrdinalIgnoreCase)));

        // Determine unique display name
        if (string.IsNullOrWhiteSpace(newDisplayName))
        {
            newDisplayName = $"{sourcePage.DisplayName} (Copy)";
            int copyNum = 2;
            while (Pages.Any(p => string.Equals(p.DisplayName, newDisplayName, StringComparison.OrdinalIgnoreCase)))
            {
                newDisplayName = $"{sourcePage.DisplayName} (Copy {copyNum++})";
            }
        }

        // Deep copy the page folder
        CopyDirectory(sourcePage.FolderPath, destFolder);

        // Update the new page.json
        var newPageJsonPath = Path.Combine(destFolder, "page.json");
        if (File.Exists(newPageJsonPath))
        {
            var json = File.ReadAllText(newPageJsonPath);
            var node = JsonNode.Parse(json);
            if (node != null)
            {
                node["name"] = newId;
                node["displayName"] = newDisplayName;
                var options = new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
                File.WriteAllText(newPageJsonPath, node.ToJsonString(options));
            }
        }

        var newPage = new ReportPage
        {
            Id = newId,
            DisplayName = newDisplayName,
            DisplayOption = sourcePage.DisplayOption,
            Width = sourcePage.Width,
            Height = sourcePage.Height,
            IsHidden = sourcePage.IsHidden,
            FolderPath = destFolder,
            PageJsonPath = newPageJsonPath,
            IsActive = false
        };

        // Insert right after source page
        Pages.Insert(sourceIndex + 1, newPage);
        ReindexPages();
        SaveChanges();

        return newPage;
    }

    public bool DeletePage(string pageId)
    {
        if (Pages.Count <= 1) return false; // Cannot delete the only remaining page

        var targetIndex = Pages.FindIndex(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (targetIndex < 0) return false;

        var targetPage = Pages[targetIndex];
        Pages.RemoveAt(targetIndex);

        // If the deleted page was active, make the first page in order active.
        if (string.Equals(ActivePageId, targetPage.Id, StringComparison.OrdinalIgnoreCase))
        {
            ActivePageId = Pages[0].Id;
            foreach (var p in Pages)
            {
                p.IsActive = string.Equals(p.Id, ActivePageId, StringComparison.OrdinalIgnoreCase);
            }
        }

        ReindexPages();

        // Persist the new page order + active page BEFORE removing the folder.
        // Otherwise pages.json briefly references a page whose folder is already
        // gone, and Power BI (which validates on external reads) raises
        // "ActivePageName not found".
        SaveChanges();

        // Now remove the page directory from disk.
        if (Directory.Exists(targetPage.FolderPath))
        {
            try
            {
                Directory.Delete(targetPage.FolderPath, recursive: true);
            }
            catch
            {
            }
        }

        return true;
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            var targetFilePath = Path.Combine(destinationDir, Path.GetFileName(file));
            File.Copy(file, targetFilePath, overwrite: true);
        }

        foreach (var subDir in Directory.GetDirectories(sourceDir))
        {
            var targetSubDir = Path.Combine(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, targetSubDir);
        }
    }

    public void TogglePageVisibility(string pageId)
    {
        var target = Pages.FirstOrDefault(p => string.Equals(p.Id, pageId, StringComparison.OrdinalIgnoreCase));
        if (target != null)
        {
            target.IsHidden = !target.IsHidden;
            UpdatePageDefinition(target);
        }
    }

    private void UpdatePageDefinition(ReportPage page)
    {
        if (string.IsNullOrEmpty(page.PageJsonPath) || !File.Exists(page.PageJsonPath)) return;

        try
        {
            var content = File.ReadAllText(page.PageJsonPath);
            var node = JsonNode.Parse(content);
            if (node == null) return;

            node["displayName"] = page.DisplayName;
            if (page.IsHidden)
            {
                node["visibility"] = "Hidden";
            }
            else if (node["visibility"] != null)
            {
                node.AsObject().Remove("visibility");
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
            };
            File.WriteAllText(page.PageJsonPath, node.ToJsonString(options));
        }
        catch
        {
        }
    }

    public void SaveChanges()
    {
        if (string.IsNullOrEmpty(PagesMetadataPath)) return;

        // Never persist an active page that does not exist (e.g. one deleted
        // externally or removed in an earlier session) — fall back to the first.
        EnsureValidActivePage();

        JsonNode? rootNode = null;
        if (File.Exists(PagesMetadataPath))
        {
            try
            {
                rootNode = JsonNode.Parse(File.ReadAllText(PagesMetadataPath));
            }
            catch { }
        }

        rootNode ??= new JsonObject();

        if (rootNode["$schema"] == null)
        {
            rootNode["$schema"] = "https://developer.microsoft.com/json-schemas/fabric/item/report/definition/pagesMetadata/1.1.0/schema.json";
        }

        var orderArray = new JsonArray();
        foreach (var p in Pages)
        {
            orderArray.Add(JsonValue.Create(p.Id));
        }

        rootNode["pageOrder"] = orderArray;
        rootNode["activePageName"] = ActivePageId;

        var options = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        var tempFile = PagesMetadataPath + ".tmp";
        File.WriteAllText(tempFile, rootNode.ToJsonString(options));
        File.Move(tempFile, PagesMetadataPath, overwrite: true);
    }

    /// <summary>
    /// Replaces the current page definitions with a previously captured snapshot
    /// (a full copy of the pages directory) and reloads the report.
    /// </summary>
    public void RestoreFromSnapshot(string snapshotDirectory)
    {
        if (string.IsNullOrEmpty(PagesDirectoryPath) || !Directory.Exists(snapshotDirectory))
        {
            return;
        }

        if (Directory.Exists(PagesDirectoryPath))
        {
            foreach (var dir in Directory.GetDirectories(PagesDirectoryPath))
            {
                try { Directory.Delete(dir, recursive: true); } catch { }
            }
            foreach (var file in Directory.GetFiles(PagesDirectoryPath))
            {
                try { File.Delete(file); } catch { }
            }
        }
        else
        {
            Directory.CreateDirectory(PagesDirectoryPath);
        }

        CopyDirectory(snapshotDirectory, PagesDirectoryPath);
        Reload();
    }

    public PageVisualInfo GetPageVisuals(string pageId)
    {
        var info = new PageVisualInfo { PageId = pageId };
        var page = Pages.FirstOrDefault(p => p.Id == pageId);
        if (page != null)
        {
            info.DisplayName = page.DisplayName;
            info.PageWidth = page.Width > 0 ? page.Width : 1920;
            info.PageHeight = page.Height > 0 ? page.Height : 1080;
            info.IsActive = page.IsActive;
        }

        var visualsDir = Path.Combine(PagesDirectoryPath, pageId, "visuals");
        if (!Directory.Exists(visualsDir))
        {
            return info;
        }

        foreach (var vDir in Directory.GetDirectories(visualsDir))
        {
            var visualJsonPath = Path.Combine(vDir, "visual.json");
            if (!File.Exists(visualJsonPath)) continue;

            try
            {
                var text = File.ReadAllText(visualJsonPath);
                var doc = JsonNode.Parse(text);
                if (doc == null) continue;

                var vItem = new VisualItemInfo
                {
                    Id = Path.GetFileName(vDir)
                };

                if (doc["position"] is JsonObject pos)
                {
                    vItem.X = pos["x"]?.GetValue<double>() ?? 0;
                    vItem.Y = pos["y"]?.GetValue<double>() ?? 0;
                    vItem.Width = pos["width"]?.GetValue<double>() ?? 200;
                    vItem.Height = pos["height"]?.GetValue<double>() ?? 150;
                    vItem.Z = pos["z"]?.GetValue<int>() ?? 0;
                }

                if (doc["visual"] is JsonObject vis)
                {
                    vItem.VisualType = vis["visualType"]?.GetValue<string>() ?? "visual";

                    // Try to get title or query projection name
                    if (vis["objects"]?["title"]?["properties"]?["text"]?["expr"]?["Literal"]?["Value"] is JsonValue titleVal)
                    {
                        vItem.DisplayTitle = titleVal.GetValue<string>().Trim('\'', '"');
                    }
                    else if (vis["query"]?["queryState"] is JsonObject qs)
                    {
                        foreach (var kv in qs)
                        {
                            if (kv.Value?["projections"] is JsonArray projArr && projArr.Count > 0)
                            {
                                var nativeRef = projArr[0]?["nativeQueryRef"]?.GetValue<string>() 
                                             ?? projArr[0]?["queryRef"]?.GetValue<string>();
                                if (!string.IsNullOrEmpty(nativeRef))
                                {
                                    vItem.DisplayTitle = nativeRef;
                                    break;
                                }
                            }
                        }
                    }

                    if (string.IsNullOrEmpty(vItem.DisplayTitle))
                    {
                        vItem.DisplayTitle = vItem.FriendlyType;
                    }
                }

                info.Visuals.Add(vItem);
            }
            catch
            {
                // Ignore parse errors on individual visuals
            }
        }

        info.Visuals = info.Visuals.OrderBy(v => v.Z).ToList();
        return info;
    }

    private void ReindexPages()
    {
        for (int i = 0; i < Pages.Count; i++)
        {
            Pages[i].OrderIndex = i;
        }
    }

    /// <summary>
    /// Guarantees <see cref="ActivePageId"/> names a page that actually exists,
    /// falling back to the first page in order. Prevents writing an
    /// activePageName that Power BI Desktop would reject as "not found".
    /// </summary>
    private void EnsureValidActivePage()
    {
        if (Pages.Count == 0)
        {
            ActivePageId = string.Empty;
            return;
        }

        if (Pages.Any(p => string.Equals(p.Id, ActivePageId, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        ActivePageId = Pages[0].Id;
        foreach (var page in Pages)
        {
            page.IsActive = string.Equals(page.Id, ActivePageId, StringComparison.OrdinalIgnoreCase);
        }
    }
}

public class NaturalStringComparer : IComparer<string>
{
    private static readonly Regex ChunkRegex = new(@"(\d+|\D+)", RegexOptions.Compiled);

    public int Compare(string? x, string? y)
    {
        if (x == null && y == null) return 0;
        if (x == null) return -1;
        if (y == null) return 1;

        var xMatches = ChunkRegex.Matches(x);
        var yMatches = ChunkRegex.Matches(y);

        int count = Math.Min(xMatches.Count, yMatches.Count);
        for (int i = 0; i < count; i++)
        {
            var xStr = xMatches[i].Value;
            var yStr = yMatches[i].Value;

            if (long.TryParse(xStr, out var xNum) && long.TryParse(yStr, out var yNum))
            {
                int numComp = xNum.CompareTo(yNum);
                if (numComp != 0) return numComp;
            }
            else
            {
                int strComp = string.Compare(xStr, yStr, StringComparison.CurrentCultureIgnoreCase);
                if (strComp != 0) return strComp;
            }
        }

        return xMatches.Count.CompareTo(yMatches.Count);
    }
}
