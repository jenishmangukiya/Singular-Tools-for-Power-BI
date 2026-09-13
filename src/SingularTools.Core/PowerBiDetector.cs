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
        IntPtr foundHwnd = IntPtr.Zero;
        string foundTitle = string.Empty;
        WindowRect foundRect = default;

        EnumWindows((hWnd, lParam) =>
        {
            if (IsPowerBiWindow(hWnd, out var title, out var r))
            {
                // Prefer windows that look like the main report window (has title with report name)
                if (foundHwnd == IntPtr.Zero || title.Contains("•") || title.Contains("Power BI"))
                {
                    foundHwnd = hWnd;
                    foundTitle = title;
                    foundRect = r;
                }
            }
            return true;
        }, IntPtr.Zero);

        pbiHwnd = foundHwnd;
        reportTitle = foundTitle;
        rect = foundRect;

        return foundHwnd != IntPtr.Zero;
    }

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
