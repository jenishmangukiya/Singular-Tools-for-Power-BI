using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace SingularTools_App;

/// <summary>
/// Synthetic mouse input for the few Power BI Desktop controls that render inside
/// WebView2 without exposing a usable UI Automation pattern. Used as a fallback
/// after <c>InvokePattern</c> fails, so the primary path stays automation-based.
/// </summary>
internal static class NativeInput
{
    private const uint MouseEventLeftDown = 0x0002;
    private const uint MouseEventLeftUp = 0x0004;

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    /// <summary>Moves the pointer to the point and issues a left click.</summary>
    public static void Click(int x, int y)
    {
        SetCursorPos(x, y);
        Thread.Sleep(80);
        mouse_event(MouseEventLeftDown, 0, 0, 0, UIntPtr.Zero);
        Thread.Sleep(40);
        mouse_event(MouseEventLeftUp, 0, 0, 0, UIntPtr.Zero);
    }
}
