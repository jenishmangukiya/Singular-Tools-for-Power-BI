using System;
using System.Collections.Generic;
using System.Linq;
using SingularTools.Core;
using Xunit;

namespace SingularTools.Tests;

/// <summary>
/// The multi-report guard relies on <see cref="PowerBiDetector"/> reporting one
/// entry per open Power BI Desktop report. The window enumeration itself needs a
/// live Desktop, but the classification rules that decide what counts can be
/// exercised directly.
/// </summary>
public class PowerBiDetectorTests
{
    [Fact]
    public void EnumeratePowerBiWindows_WithNoDesktop_ReturnsEmpty()
    {
        // No Power BI Desktop in a test run: the guard must report zero, not throw.
        Assert.Empty(PowerBiDetector.EnumeratePowerBiWindows());
        Assert.Equal(0, PowerBiDetector.CountOpenPowerBiReports());
    }

    [Fact]
    public void FindActivePowerBiWindow_WithNoDesktop_ReportsNotFound()
    {
        Assert.False(PowerBiDetector.FindActivePowerBiWindow(out var hwnd, out var title, out _));
        Assert.Equal(IntPtr.Zero, hwnd);
        Assert.Equal(string.Empty, title);
    }

    [Theory]
    // "Demo PBI Report • Last saved: Today at 9:36 PM (Power BI Project)"
    [InlineData("Demo PBI Report • Last saved: Today at 9:36 PM (Power BI Project)", "Demo PBI Report")]
    [InlineData("Sales Report - Power BI Desktop", "Sales Report")]
    [InlineData("Standalone", "Standalone")]
    public void ExtractReportName_ParsesDesktopTitles(string title, string expected)
    {
        Assert.Equal(expected, PowerBiDetector.ExtractReportName(title));
    }

    [Fact]
    public void ExtractReportName_EmptyTitle_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PowerBiDetector.ExtractReportName(""));
        Assert.Equal(string.Empty, PowerBiDetector.ExtractReportName("   "));
    }

    /// <summary>
    /// Documents the rule the guard depends on: a report only counts when it is a
    /// full-size visible window, so Power BI's tooltips and popups in the same
    /// process cannot push the count over the threshold.
    /// </summary>
    [Fact]
    public void PowerBiWindow_IsSmallerThanReportThreshold_IsIgnorable()
    {
        var tooltip = new WindowRect { Left = 0, Top = 0, Right = 180, Bottom = 40 };
        var report = new WindowRect { Left = 0, Top = 0, Right = 1600, Bottom = 900 };

        Assert.True(tooltip.Width <= 200 || tooltip.Height <= 200);
        Assert.False(report.Width <= 200 || report.Height <= 200);
    }
}
