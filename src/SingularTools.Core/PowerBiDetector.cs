using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace SingularTools.Core;

public struct WindowRect
{
    public int Left;
    public int Top;
    public int Right;
    public int Bottom;

    public int Width => Right - Left;
    public int Height => Bottom - Top;
}

public static class PowerBiDetector
{
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto)]
    public static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetWindowRect(IntPtr hWnd, out WindowRect lpRect);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    public static bool IsPowerBiWindow(IntPtr hWnd, out string title, out WindowRect rect)
    {
        title = string.Empty;
        rect = default;

        if (hWnd == IntPtr.Zero || !IsWindowVisible(hWnd)) return false;

        GetWindowThreadProcessId(hWnd, out uint pid);
        if (pid == 0) return false;

        try
        {
            var proc = Process.GetProcessById((int)pid);
            var procName = proc.ProcessName;

            var sb = new StringBuilder(512);
            GetWindowText(hWnd, sb, 512);
            title = sb.ToString();

            // Identify Power BI Desktop by its process. The window title alone is not
            // reliable: other windows (e.g. File Explorer showing a folder whose name
            // contains "Power BI") would match, while Power BI's own title is
            // typically just the report name and may not contain "Power BI" at all.
            bool isPbiProcess = string.Equals(procName, "PBIDesktop", StringComparison.OrdinalIgnoreCase);

            if (isPbiProcess)
            {
                GetWindowRect(hWnd, out rect);
                // Filter out zero-sized invisible tooltips/popups
                if (rect.Width > 200 && rect.Height > 200)
                {
                    return true;
                }
            }
        }
        catch
        {
        }

        return false;
    }

    public static bool FindActivePowerBiWindow(out IntPtr pbiHwnd, out string reportTitle, out WindowRect rect)
    {
        var windows = EnumeratePowerBiWindows();

        pbiHwnd = IntPtr.Zero;
        reportTitle = string.Empty;
        rect = default;

        foreach (var window in windows)
        {
            // Prefer windows that look like the main report window (title with report name).
            if (pbiHwnd == IntPtr.Zero ||
                window.Title.Contains("•") ||
                window.Title.Contains("Power BI"))
            {
                pbiHwnd = window.Handle;
                reportTitle = window.Title;
                rect = window.Rect;
            }
        }

        return pbiHwnd != IntPtr.Zero;
    }

    /// <summary>A visible top-level Power BI Desktop window.</summary>
    public readonly struct PowerBiWindow
    {
        public PowerBiWindow(IntPtr handle, string title, WindowRect rect)
        {
            Handle = handle;
            Title = title;
            Rect = rect;
        }

        public IntPtr Handle { get; }
        public string Title { get; }
        public WindowRect Rect { get; }
    }

    /// <summary>
    /// Every visible, full-size Power BI Desktop window. Desktop is single-instance,
    /// so opening a second report adds a second top-level window to the same
    /// <c>PBIDesktop</c> process rather than a second process.
    ///
    /// The size filter in <see cref="IsPowerBiWindow"/> already excludes the
    /// zero-sized tooltips and popups that share the process.
    /// </summary>
    public static List<PowerBiWindow> EnumeratePowerBiWindows()
    {
        var windows = new List<PowerBiWindow>();

        EnumWindows((hWnd, lParam) =>
        {
            if (IsPowerBiWindow(hWnd, out var title, out var r))
            {
                windows.Add(new PowerBiWindow(hWnd, title, r));
            }
            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// How many Power BI Desktop reports are currently open. More than one means
    /// Singular Tools cannot tell which report the author means, so the shell
    /// blocks the UI.
    /// </summary>
    public static int CountOpenPowerBiReports() => EnumeratePowerBiWindows().Count;

    public static string ExtractReportName(string windowTitle)
    {
        if (string.IsNullOrWhiteSpace(windowTitle)) return string.Empty;

        // Examples:
        // "Demo PBI Report • Last saved: Today at 9:36 PM (Power BI Project)"
        // "Sales Report - Power BI Desktop"
        string name = windowTitle;

        if (name.Contains("•"))
        {
            name = name.Split('•')[0].Trim();
        }
        else if (name.Contains(" - Power BI"))
        {
            name = name.Split(new[] { " - Power BI" }, StringSplitOptions.None)[0].Trim();
        }

        return name;
    }
}
