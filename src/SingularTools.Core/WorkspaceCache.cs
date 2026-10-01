using System;
using System.Collections.Generic;

namespace SingularTools.Core;

/// <summary>
/// Machine-level memory of the Power BI workspaces this app has seen, shared by
/// every publishing tool.
///
/// Detection is expensive — it drives Power BI Desktop's publish dialog through
/// UI Automation — while the list of workspaces barely changes. Caching it here
/// means tools open with the list already populated instead of forcing the author
/// to detect before they can publish.
///
/// The cache is deliberately machine-scoped, not report-scoped: it answers "which
/// workspaces can this machine see", which is the same answer for every report.
/// Which workspaces a particular publishing group targets is a different question,
/// stored per report in the group itself.
/// </summary>
public sealed class WorkspaceCache
{
    /// <summary>Workspace names as reported by Power BI Desktop, in detection order.</summary>
    public List<string> Names { get; set; } = new();

    /// <summary>When the list was last read from Power BI Desktop; null if never.</summary>
    public DateTimeOffset? LastDetectedUtc { get; set; }

    /// <summary>True when a detection has ever completed and produced this list.</summary>
    public bool HasBeenDetected => LastDetectedUtc.HasValue;

    public static WorkspaceCache Empty() => new();

    /// <summary>
    /// The remembered time in local time, for display. Null when never detected.
    /// </summary>
    public DateTimeOffset? LastDetectedLocal => LastDetectedUtc?.ToLocalTime();

    /// <summary>
    /// A ready-to-show summary of when the list was last read, e.g.
    /// "Last detected 01 Oct 2026, 09:14". Null when nothing has been detected.
    /// </summary>
    public string? DescribeLastDetected()
    {
        var local = LastDetectedLocal;
        if (local == null) return null;

        return $"Last detected {local.Value.ToString("dd MMM yyyy, HH:mm")}";
    }
}
