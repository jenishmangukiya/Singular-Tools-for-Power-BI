using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Graphics;
using Windows.UI;
using WinRT.Interop;
using SingularTools_App.Shell;

namespace SingularTools_App.Tools.SemanticColorManager;

/// <summary>
/// Arm-once desktop helpers for the Add/Edit dialog:
/// <list type="bullet">
///   <item><b>Tooltip text</b> — hover any tooltip on the desktop and Ctrl+Left click to grab its text.</item>
///   <item><b>Color sample</b> — move anywhere and Left click to pick the pixel color (eyedropper),
///   with a PowerToys-style magnifier that follows the cursor.</item>
/// </list>
/// Uses a low-level mouse/keyboard hook so clicks are captured reliably over any Windows
/// application and swallowed while a mode is active. Intended for short, user-driven sessions.
/// </summary>
internal sealed class ScreenPickService : IDisposable
{
    public enum PickMode
    {
        None,
        TooltipText,
        ColorSample
    }

    private const int VK_LBUTTON = 0x01;
    private const int VK_CONTROL = 0x11;
    private const int VK_LCONTROL = 0xA2;
    private const int VK_RCONTROL = 0xA3;
    private const int VK_ESCAPE = 0x1B;

    private const int WH_MOUSE_LL = 14;
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_MOUSEMOVE = 0x0200;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_KEYDOWN = 0x0100;

    private const uint SRCCOPY = 0x00CC0020;
    private const uint CLR_INVALID = 0xFFFFFFFF;

    private static readonly (int X, int Y)[] TooltipOffsets =
    {
        (0, 0), (0, 8), (8, 8), (0, 16), (-8, 8), (8, 0), (0, 24), (16, 16), (-16, 16)
    };

    private readonly DispatcherQueue? _queue;

    private IntPtr _mouseHook = IntPtr.Zero;
    private IntPtr _keyboardHook = IntPtr.Zero;
    private HookProc? _mouseProc;
    private HookProc? _keyboardProc;
    private Magnifier? _magnifier;
    private bool _previewQueued;
    private POINT? _pendingPreview;

    public ScreenPickService()
    {
        _queue = App.CurrentMainWindow?.DispatcherQueue ?? DispatcherQueue.GetForCurrentThread();
    }

    public PickMode Mode { get; private set; } = PickMode.None;

    public bool IsActive => Mode != PickMode.None;

    public event Action<string>? TextPicked;
    public event Action<Color>? ColorPreview;
    public event Action<Color>? ColorPicked;
    public event Action? Cancelled;

    public void StartTooltipTextPick() => Start(PickMode.TooltipText);

    public void StartColorSample() => Start(PickMode.ColorSample);

    public void Stop()
    {
        Mode = PickMode.None;

        if (_mouseHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_mouseHook);
            _mouseHook = IntPtr.Zero;
        }

        if (_keyboardHook != IntPtr.Zero)
        {
            UnhookWindowsHookEx(_keyboardHook);
            _keyboardHook = IntPtr.Zero;
        }

        _mouseProc = null;
        _keyboardProc = null;

        _previewQueued = false;
        _pendingPreview = null;

        _magnifier?.Dispose();
        _magnifier = null;
    }

    public void Dispose() => Stop();

    private void Start(PickMode mode)
    {
        Stop();
        Mode = mode;

        _mouseProc = MouseHookProc;
        _keyboardProc = KeyboardHookProc;

        var module = GetModuleHandle(null);
        _mouseHook = SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, module, 0);
        _keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, module, 0);

        if (_mouseHook == IntPtr.Zero)
        {
            App.Log($"ScreenPick: failed to install mouse hook (error {Marshal.GetLastWin32Error()}).");
        }

        if (mode == PickMode.ColorSample)
        {
            try
            {
                _magnifier = new Magnifier();
            }
            catch (Exception ex)
            {
                App.Log($"ScreenPick: magnifier unavailable: {ex.Message}");
                _magnifier = null;
            }
        }

        App.Log($"ScreenPick: armed {mode}.");
    }

    // ------------------------------------------------------------- Hook plumbing

    private IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Mode != PickMode.None)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);

            if (message == WM_MOUSEMOVE && Mode == PickMode.ColorSample)
            {
                // Coalesce the flood of move messages into one queued preview per
                // dispatcher tick; hook callbacks must not pile up expensive GDI work,
                // or the cursor lags and the magnifier trails behind the pointer.
                _pendingPreview = data.pt;
                if (!_previewQueued)
                {
                    _previewQueued = true;
                    Post(ProcessPreview);
                }
            }
            else if (message == WM_LBUTTONDOWN)
            {
                if (Mode == PickMode.TooltipText && IsCtrlDown())
                {
                    var point = data.pt;
                    Post(() => GrabText(point));
                    return (IntPtr)1; // swallow so the click does nothing else
                }

                if (Mode == PickMode.ColorSample)
                {
                    var point = data.pt;
                    Post(() => CommitColor(point));
                    return (IntPtr)1; // swallow the pick click
                }
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && Mode != PickMode.None && wParam.ToInt32() == WM_KEYDOWN)
        {
            var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (data.vkCode == VK_ESCAPE)
            {
                Post(() =>
                {
                    Stop();
                    Cancelled?.Invoke();
                });
                return (IntPtr)1;
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private void Post(Action action)
    {
        if (_queue == null)
        {
            action();
            return;
        }

        _queue.TryEnqueue(() => action());
    }

    // ------------------------------------------------------------- Pick actions

    private void GrabText(POINT point)
    {
        try
        {
            var text = CaptureTooltipText(point);
            if (string.IsNullOrWhiteSpace(text))
            {
                App.Log("ScreenPick: click detected but no tooltip text found.");
                ToastService.Show("No tooltip text found there. Hover until a tooltip appears, then Ctrl + Left click.", ToastSeverity.Warning);
                return;
            }

            Stop();
            TextPicked?.Invoke(text.Trim());
        }
        catch (Exception ex)
        {
            App.Log($"ScreenPick: grab failed: {ex.Message}");
        }
    }

    private void ProcessPreview()
    {
        _previewQueued = false;
        var point = _pendingPreview;
        if (point.HasValue)
        {
            PreviewColor(point.Value);
        }
    }

    private void PreviewColor(POINT point)
    {
        var color = SampleColor(point.X, point.Y);
        if (!color.HasValue) return;

        try
        {
            _magnifier?.Update(point);
        }
        catch (Exception ex)
        {
            App.Log($"ScreenPick: magnifier update failed: {ex.Message}");
        }

        ColorPreview?.Invoke(color.Value);
    }

    private void CommitColor(POINT point)
    {
        var color = SampleColor(point.X, point.Y);
        if (!color.HasValue) return;

        Stop();
        ColorPicked?.Invoke(color.Value);
    }

    // ------------------------------------------------------------ Tooltip text

    private static string? CaptureTooltipText(POINT cursor)
    {
        // 1) UI Automation at the cursor and a few small offsets (tooltips draw offset).
        foreach (var offset in TooltipOffsets)
        {
            var text = TryUiaTextAt(cursor.X + offset.X, cursor.Y + offset.Y);
            if (!string.IsNullOrWhiteSpace(text)) return text!.Trim();
        }

        // 2) UI Automation on tooltip windows near the cursor.
        var windowText = TryTooltipFromWindows(cursor);
        if (!string.IsNullOrWhiteSpace(windowText)) return windowText!.Trim();

        // 3) Classic Win32 tooltip window text.
        return TryTooltipWindowText(cursor)?.Trim();
    }

    private static string? TryUiaTextAt(int x, int y)
    {
        try
        {
            var element = AutomationElement.FromPoint(new System.Windows.Point(x, y));
            if (element == null) return null;

            if (element.Current.ControlType == ControlType.ToolTip)
            {
                var name = element.Current.Name;
                if (!string.IsNullOrWhiteSpace(name)) return name;
            }

            var help = element.Current.HelpText;
            if (!string.IsNullOrWhiteSpace(help)) return help;

            var parent = TreeWalker.ControlViewWalker.GetParent(element);
            for (var i = 0; i < 3 && parent != null; i++)
            {
                if (parent.Current.ControlType == ControlType.ToolTip)
                {
                    var parentName = parent.Current.Name;
                    if (!string.IsNullOrWhiteSpace(parentName)) return parentName;
                }

                parent = TreeWalker.ControlViewWalker.GetParent(parent);
            }
        }
        catch
        {
            // Element gone / not available; try the next point.
        }

        return null;
    }

    private static string? TryTooltipFromWindows(POINT cursor)
    {
        string? found = null;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (!GetWindowRect(hWnd, out var rect)) return true;

            // Only inspect windows that sit near the cursor.
            if (cursor.X < rect.Left - 60 || cursor.X > rect.Right + 400 ||
                cursor.Y < rect.Top - 60 || cursor.Y > rect.Bottom + 400)
            {
                return true;
            }

            try
            {
                var element = AutomationElement.FromHandle(hWnd);
                if (element == null) return true;

                if (element.Current.ControlType == ControlType.ToolTip)
                {
                    var name = element.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name)) { found = name; return false; }
                }

                var tooltips = element.FindAll(
                    TreeScope.Children,
                    new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.ToolTip));

                foreach (AutomationElement tooltip in tooltips)
                {
                    var name = tooltip.Current.Name;
                    if (!string.IsNullOrWhiteSpace(name)) { found = name; return false; }
                }
            }
            catch
            {
                // Window has no usable automation; skip.
            }

            return true;
        }, IntPtr.Zero);

        return found;
    }

    private static string? TryTooltipWindowText(POINT cursor)
    {
        string? text = null;

        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd)) return true;
            if (!ClassOf(hWnd).Equals("tooltips_class32", StringComparison.OrdinalIgnoreCase)) return true;
            if (!GetWindowRect(hWnd, out var rect)) return true;

            var value = TextOf(hWnd);
            if (string.IsNullOrWhiteSpace(value)) return true;

            var contains = cursor.X >= rect.Left && cursor.X <= rect.Right &&
                           cursor.Y >= rect.Top && cursor.Y <= rect.Bottom;

            if (contains)
            {
                text = value;
                return false;
            }

            text ??= value;
            return true;
        }, IntPtr.Zero);

        return text;
    }

    // ------------------------------------------------------------- Eye dropper

    private static Color? SampleColor(int x, int y)
    {
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return null;

        var memDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, 1, 1);
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero) return null;

            previous = SelectObject(memDc, bitmap);
            if (!BitBlt(memDc, 0, 0, 1, 1, screenDc, x, y, SRCCOPY)) return null;

            var pixel = GetPixel(memDc, 0, 0);
            if (pixel == CLR_INVALID) return null;

            return Color.FromArgb(255,
                (byte)(pixel & 0xFF),
                (byte)((pixel >> 8) & 0xFF),
                (byte)((pixel >> 16) & 0xFF));
        }
        finally
        {
            if (previous != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Color[,] SampleRegion(int centerX, int centerY, int width, int height)
    {
        var result = new Color[height, width];
        var screenDc = GetDC(IntPtr.Zero);
        if (screenDc == IntPtr.Zero) return result;

        // Center the region on the cursor, mirroring PowerToys' picker, so the
        // center cell of the magnifier is always exactly the cursor pixel. The
        // origin is *not* clamped; pixels that fall outside the virtual screen
        // are left blank. (GetSystemMetrics(0/1) is the primary strip only — the
        // window can live on a negative-offset secondary monitor.)
        var originX = centerX - width / 2;
        var originY = centerY - height / 2;

        // SM_XVIRTUALSCREEN / SM_YVIRTUALSCREEN / SM_CX / SM_CYVIRTUALSCREEN
        var vx = GetSystemMetrics(76);
        var vy = GetSystemMetrics(77);
        var vw = GetSystemMetrics(78);
        var vh = GetSystemMetrics(79);

        var memDc = IntPtr.Zero;
        var bitmap = IntPtr.Zero;
        var previous = IntPtr.Zero;

        try
        {
            memDc = CreateCompatibleDC(screenDc);
            bitmap = CreateCompatibleBitmap(screenDc, width, height);
            if (memDc == IntPtr.Zero || bitmap == IntPtr.Zero) return result;

            previous = SelectObject(memDc, bitmap);

            var srcX = Math.Max(originX, vx);
            var srcY = Math.Max(originY, vy);
            var srcRight = Math.Min(originX + width, vx + vw);
            var srcBottom = Math.Min(originY + height, vy + vh);

            if (srcRight > srcX && srcBottom > srcY)
            {
                var copyW = srcRight - srcX;
                var copyH = srcBottom - srcY;
                // Copy the on-screen part into the right offset of the full region.
                BitBlt(memDc, srcX - originX, srcY - originY, copyW, copyH,
                       screenDc, srcX, srcY, SRCCOPY);
            }

            for (var row = 0; row < height; row++)
            {
                for (var col = 0; col < width; col++)
                {
                    var screenX = originX + col;
                    var screenY = originY + row;

                    if (screenX < vx || screenY < vy || screenX >= vx + vw || screenY >= vy + vh)
                    {
                        // No screen pixel here: render a clear grey so the center marker
                        // still aligns 1:1 under the cursor even at the screen edge.
                        result[row, col] = Color.FromArgb(255, 24, 24, 24);
                        continue;
                    }

                    var pixel = GetPixel(memDc, col, row);
                    result[row, col] = pixel == CLR_INVALID
                        ? Color.FromArgb(255, 0, 0, 0)
                        : Color.FromArgb(255,
                            (byte)(pixel & 0xFF),
                            (byte)((pixel >> 8) & 0xFF),
                            (byte)((pixel >> 16) & 0xFF));
                }
            }
        }
        finally
        {
            if (previous != IntPtr.Zero && memDc != IntPtr.Zero) SelectObject(memDc, previous);
            if (bitmap != IntPtr.Zero) DeleteObject(bitmap);
            if (memDc != IntPtr.Zero) DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }

        return result;
    }

    // ------------------------------------------------------------------ Win32

    private static bool IsCtrlDown() =>
        IsDown(VK_CONTROL) || IsDown(VK_LCONTROL) || IsDown(VK_RCONTROL);

    private static bool IsDown(int virtualKey) => (GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static string ClassOf(IntPtr hWnd)
    {
        var buffer = new StringBuilder(256);
        GetClassName(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private static string TextOf(IntPtr hWnd)
    {
        var buffer = new StringBuilder(1024);
        GetWindowText(hWnd, buffer, buffer.Capacity);
        return buffer.ToString();
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc callback, IntPtr module, uint threadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(IntPtr hook);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hook, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(
        IntPtr hdcDest, int x, int y, int width, int height,
        IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern uint GetPixel(IntPtr hdc, int x, int y);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder buffer, int maxCount);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    // ---------------------------------------------------------------- Magnifier

    /// <summary>PowerToys-style magnifier: a topmost, borderless window that follows the cursor.</summary>
    private sealed class Magnifier : IDisposable
    {
        private const int GridSize = 9;
        private const int CellSize = 16;
        private const int Panel = GridSize * CellSize;
        private const int LabelHeight = 26;

        private const uint SWP_NOACTIVATE = 0x0010;
        private const uint SWP_SHOWWINDOW = 0x0040;
        private static readonly IntPtr HWND_TOPMOST = new(-1);

        private readonly Window _window;
        private readonly IntPtr _hwnd;
        private readonly Border[,] _cells = new Border[GridSize, GridSize];
        private readonly TextBlock _label;
        private readonly Color[,] _last = new Color[GridSize, GridSize];
        private Color _lastCenter;
        private bool _hasLast;

        public Magnifier()
        {
            _window = new Window();

            var grid = new Grid { Width = Panel, Height = Panel };
            for (var i = 0; i < GridSize; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(CellSize) });
                grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(CellSize) });
            }

            for (var row = 0; row < GridSize; row++)
            {
                for (var col = 0; col < GridSize; col++)
                {
                    var cell = new Border { Width = CellSize, Height = CellSize, Background = new SolidColorBrush(Microsoft.UI.Colors.Black) };
                    Grid.SetRow(cell, row);
                    Grid.SetColumn(cell, col);
                    grid.Children.Add(cell);
                    _cells[row, col] = cell;
                }
            }

            var marker = new Border
            {
                Width = CellSize,
                Height = CellSize,
                BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.White),
                BorderThickness = new Thickness(2),
                IsHitTestVisible = false
            };
            Grid.SetRow(marker, GridSize / 2);
            Grid.SetColumn(marker, GridSize / 2);
            grid.Children.Add(marker);

            _label = new TextBlock
            {
                FontSize = 12,
                Foreground = new SolidColorBrush(Microsoft.UI.Colors.White),
                Margin = new Thickness(6, 2, 6, 4)
            };

            var root = new StackPanel
            {
                Background = new SolidColorBrush(Color.FromArgb(255, 32, 32, 32)),
                Children = { grid, _label }
            };
            _window.Content = root;

            var appWindow = _window.AppWindow;
            if (appWindow.Presenter is OverlappedPresenter presenter)
            {
                presenter.SetBorderAndTitleBar(false, false);
                presenter.IsResizable = false;
                presenter.IsMaximizable = false;
                presenter.IsMinimizable = false;
                presenter.IsAlwaysOnTop = true;
            }

            appWindow.IsShownInSwitchers = false;
            appWindow.Resize(new SizeInt32(Panel, Panel + LabelHeight));
            _window.Activate();
            _hwnd = WindowNative.GetWindowHandle(_window);
        }

        public void Update(POINT cursor)
        {
            var colors = SampleRegion(cursor.X, cursor.Y, GridSize, GridSize);

            for (var row = 0; row < GridSize; row++)
            {
                for (var col = 0; col < GridSize; col++)
                {
                    if (_hasLast && _last[row, col].R == colors[row, col].R &&
                        _last[row, col].G == colors[row, col].G &&
                        _last[row, col].B == colors[row, col].B)
                    {
                        continue;
                    }

                    _last[row, col] = colors[row, col];
                    _cells[row, col].Background = new SolidColorBrush(colors[row, col]);
                }
            }

            var center = colors[GridSize / 2, GridSize / 2];
            if (!_hasLast || center.R != _lastCenter.R || center.G != _lastCenter.G || center.B != _lastCenter.B)
            {
                _lastCenter = center;
                _label.Text = $"#{center.R:X2}{center.G:X2}{center.B:X2}   R{center.R} G{center.G} B{center.B}";
            }

            _hasLast = true;
            Position(cursor);
        }

        private void Position(POINT cursor)
        {
            var width = Panel;
            var height = Panel + LabelHeight;

            var x = cursor.X + 24;
            var y = cursor.Y + 24;

            var vx = GetSystemMetrics(76); // SM_XVIRTUALSCREEN
            var vy = GetSystemMetrics(77);
            var screenWidth = vx + GetSystemMetrics(78); // right edge of virtual screen
            var screenHeight = vy + GetSystemMetrics(79);

            x = Math.Max(x, vx);
            y = Math.Max(y, vy);

            if (x + width > screenWidth) x = cursor.X - width - 24;
            if (y + height > screenHeight) y = cursor.Y - height - 24;
            if (x < vx) x = vx;
            if (y < vy) y = vy;

            SetWindowPos(_hwnd, HWND_TOPMOST, x, y, width, height, SWP_NOACTIVATE | SWP_SHOWWINDOW);
        }

        public void Dispose()
        {
            try { _window.Close(); }
            catch { }
        }
    }
}
