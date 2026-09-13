using System;
using System.Runtime.InteropServices;

namespace SingularTools_App.Shell;

/// <summary>
/// Enforces a minimum window size on an unpackaged WinUI 3 window by subclassing
/// its HWND and handling WM_GETMINMAXINFO. Values are supplied in logical pixels
/// and scaled to physical pixels for the window's current DPI.
/// </summary>
internal static class NativeWindowSizing
{
    private const int WM_GETMINMAXINFO = 0x0024;
    private const uint SubclassId = 0x53494E47; // 'SING'

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate IntPtr SubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hWnd, SubclassProc pfnSubclass, UIntPtr uIdSubclass, IntPtr dwRefData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    // Held in a static field so the delegate is not collected while the window is alive.
    private static SubclassProc? _proc;
    private static int _minWidth;
    private static int _minHeight;

    public static void Apply(IntPtr hwnd, int minWidthLogical, int minHeightLogical)
    {
        if (hwnd == IntPtr.Zero) return;

        double scale = 1.0;
        try
        {
            var dpi = GetDpiForWindow(hwnd);
            if (dpi > 0) scale = dpi / 96.0;
        }
        catch { }

        _minWidth = (int)Math.Round(minWidthLogical * scale);
        _minHeight = (int)Math.Round(minHeightLogical * scale);

        _proc ??= WndProc;
        SetWindowSubclass(hwnd, _proc, SubclassId, IntPtr.Zero);
    }

    private static IntPtr WndProc(IntPtr hWnd, uint uMsg, IntPtr wParam, IntPtr lParam, UIntPtr uIdSubclass, IntPtr dwRefData)
    {
        if (uMsg == WM_GETMINMAXINFO && lParam != IntPtr.Zero)
        {
            var info = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            if (_minWidth > 0) info.ptMinTrackSize.X = _minWidth;
            if (_minHeight > 0) info.ptMinTrackSize.Y = _minHeight;
            Marshal.StructureToPtr(info, lParam, false);
            return IntPtr.Zero;
        }

        return DefSubclassProc(hWnd, uMsg, wParam, lParam);
    }
}
