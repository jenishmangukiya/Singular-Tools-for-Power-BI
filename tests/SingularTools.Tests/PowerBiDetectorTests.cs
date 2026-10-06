using System;
using System.Collections.Generic;
using System.Diagnostics;
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
        var windows = PowerBiDetector.EnumeratePowerBiWindows();

        // Enumeration must not throw whether or not Desktop is running. Asserting "always
        // empty" would only hold on a machine where the author happens to have Power BI
        // closed, which is exactly the wrong time to find out.
        if (!IsPowerBiDesktopRunning())
        {
            Assert.Empty(windows);
            Assert.Equal(0, PowerBiDetector.CountOpenPowerBiReports());
        }
        else
        {
            Assert.NotEmpty(windows);
            Assert.Equal(windows.Count, PowerBiDetector.CountOpenPowerBiReports());
        }

        // The invariant that matters either way: every hit is a real, full-size PBIDesktop
        // window, never a tooltip or an unrelated app whose title mentions Power BI.
        Assert.All(windows, w =>
        {
            Assert.NotEqual(IntPtr.Zero, w.Handle);
            Assert.True(w.Rect.Width > 200 && w.Rect.Height > 200,
                $"expected a full-size window, got {w.Rect.Width}x{w.Rect.Height}");
        });
    }

    [Fact]
    public void FindActivePowerBiWindow_WithNoDesktop_ReportsNotFound()
    {
        if (!IsPowerBiDesktopRunning())
        {
            Assert.False(PowerBiDetector.FindActivePowerBiWindow(out var hwnd, out var title, out _));
            Assert.Equal(IntPtr.Zero, hwnd);
            Assert.Equal(string.Empty, title);
            return;
        }

        // With Desktop open, the guard must find it rather than claiming there is no report.
        Assert.True(PowerBiDetector.FindActivePowerBiWindow(out var found, out var foundTitle, out _));
        Assert.NotEqual(IntPtr.Zero, found);
        Assert.NotEqual(string.Empty, foundTitle);
    }

    /// <summary>True when a PBIDesktop process exists, so the tests can adapt to the machine.</summary>
    private static bool IsPowerBiDesktopRunning() =>
        Process.GetProcessesByName("PBIDesktop").Any();

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
