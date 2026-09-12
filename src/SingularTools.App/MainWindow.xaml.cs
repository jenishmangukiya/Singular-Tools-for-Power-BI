using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using SingularTools.Core;
using Windows.Graphics;

namespace SingularTools_App;

public sealed partial class MainWindow : Window
{
    private HotkeyManager? _hotkeyManager;
    private IntPtr _hwnd = IntPtr.Zero;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private bool _hasInitializedPosition = false;

    public MainWindow()
    {
        App.Log("MainWindow initializing...");
        InitializeComponent();

        _hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

        try
        {
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);
        }
        catch (Exception ex)
        {
            App.Log($"TitleBar warning: {ex.Message}");
        }

        // Configure AppWindow geometry and behavior
        ConfigureAppWindow();

        // Apply the application icon to the window / taskbar
        ApplyAppIcon();

        // Navigate the root frame to MainPage
        RootFrame.Navigate(typeof(MainPage));

        // Initialize and start the Power BI scoped Hotkey Manager
        InitializeHotkey();

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        App.Log("MainWindow initialized.");
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        if (!_hasInitializedPosition)
        {
            _hasInitializedPosition = true;
            App.Log("MainWindow first activated - positioning...");
            try
            {
                if (PowerBiDetector.FindActivePowerBiWindow(out _, out _, out var pbiRect) && pbiRect.Width > 0)
                {
                    ShowAndCenterOverPowerBi(pbiRect);
                }
                else
                {
                    CenterOnScreen();
                }
            }
            catch (Exception ex)
            {
                App.Log($"Error during initial positioning: {ex}");
            }
        }
    }

    private void ConfigureAppWindow()
    {
        var appWindow = AppWindow;
        if (appWindow == null) return;

        // Command Palette dimensions
        appWindow.Resize(new SizeInt32(840, 560));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsMaximizable = false;
            presenter.IsAlwaysOnTop = true;
        }
    }

    private void ApplyAppIcon()
    {
        try
        {
            var iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
            if (File.Exists(iconPath))
            {
                AppWindow?.SetIcon(iconPath);
            }
        }
        catch (Exception ex)
        {
            App.Log($"SetIcon warning: {ex.Message}");
        }
    }

    private void CenterOnScreen()
    {
        if (AppWindow == null) return;

        // Default center
        AppWindow.Move(new PointInt32(200, 150));
        AppWindow.Show();
        this.Activate();

        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
    }

    private void InitializeHotkey()
    {
        try
        {
            _hotkeyManager = new HotkeyManager();
            _hotkeyManager.PowerBiHotkeyPressed += OnPowerBiHotkeyPressed;
            _hotkeyManager.Start();
        }
        catch
        {
        }
    }

    private void OnPowerBiHotkeyPressed(IntPtr pbiHwnd, WindowRect rect)
    {
        DispatcherQueue.TryEnqueue(() =>
        {
            ShowAndCenterOverPowerBi(rect);
        });
    }

    public void ShowAndCenterOverPowerBi(WindowRect pbiRect)
    {
        if (AppWindow == null) return;

        int width = 840;
        int height = 560;

        int x = pbiRect.Left + (pbiRect.Width - width) / 2;
        int y = pbiRect.Top + (pbiRect.Height - height) / 3;

        if (x < 0) x = 50;
        if (y < 0) y = 50;

        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show();
        this.Activate();

        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);

        if (RootFrame.Content is MainPage page)
        {
            page.RefreshList();
            page.FocusSearchBox();
        }
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _hotkeyManager?.Dispose();
        _hotkeyManager = null;
    }
}
