using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using SingularTools.Core;
using SingularTools_App.Shell;
using Windows.Graphics;

namespace SingularTools_App;

public sealed partial class MainWindow : Window
{
    private IntPtr _hwnd = IntPtr.Zero;

    /// <summary>How often the "more than one Power BI report" guard re-checks.</summary>
    private static readonly TimeSpan ReportGuardInterval = TimeSpan.FromMilliseconds(1500);

    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _reportGuardTimer;
    private bool _multiReportBlocked;

    /// <summary>Minimum spacing between focus-triggered Power BI saves.</summary>
    private static readonly TimeSpan PowerBiFocusSaveCooldown = TimeSpan.FromSeconds(2);

    private bool _wasActive;
    private DateTime _lastPowerBiSaveUtc = DateTime.MinValue;

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    private const int SW_RESTORE = 9;
    private const int SW_SHOW = 5;

    private bool _hasInitializedPosition = false;
    private readonly Dictionary<ToastCard, Microsoft.UI.Dispatching.DispatcherQueueTimer> _toastTimers = new();

    public MainWindow()
    {
        App.Log("MainWindow initializing...");
        InitializeComponent();

        ToastService.Requested += OnToastRequested;
        App.Workspace.ExternalChangeDetected += OnExternalChangeDetected;

        _ = BrandAssets.ApplyAsync(AppTitleBarLogo);

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

        ConfigureAppWindow();

        try
        {
            NativeWindowSizing.Apply(_hwnd, 760, 520);
        }
        catch (Exception ex)
        {
            App.Log($"Min-size subclass warning: {ex.Message}");
        }

        ApplyAppIcon();

        BuildToolNavigation();

        Activated += MainWindow_Activated;
        Closed += MainWindow_Closed;
        StartReportGuard();
        App.Log("MainWindow initialized.");
    }

    private IToolPage? ActiveTool => ToolFrame.Content as IToolPage;

    /// <summary>Selects and navigates to a tool by its id (used by the Home launcher).</summary>
    public void NavigateToTool(string toolId)
    {
        foreach (var item in ToolNav.MenuItems.OfType<NavigationViewItem>())
        {
            if (item.Tag is string id && string.Equals(id, toolId, StringComparison.OrdinalIgnoreCase))
            {
                ToolNav.SelectedItem = item;
                return;
            }
        }
    }

    private void BuildToolNavigation()
    {
        ToolNav.MenuItems.Clear();

        foreach (var tool in ToolRegistry.Tools)
        {
            var item = new NavigationViewItem
            {
                Content = tool.Title,
                Tag = tool.Id,
                Icon = new FontIcon { Glyph = tool.Glyph }
            };
            ToolTipService.SetToolTip(item, tool.Description);
            ToolNav.MenuItems.Add(item);
        }

        if (ToolNav.MenuItems.FirstOrDefault() is NavigationViewItem first)
        {
            ToolNav.SelectedItem = first;
        }
    }

    private void ToolNav_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItem is not NavigationViewItem item || item.Tag is not string id)
        {
            return;
        }

        var tool = ToolRegistry.Tools.FirstOrDefault(t => t.Id == id);
        if (tool == null)
        {
            return;
        }

        if (ToolFrame.CurrentSourcePageType != tool.PageType)
        {
            ToolFrame.Navigate(tool.PageType);
        }

        TitleBarToolText.Text = $"\u00B7 {tool.Title}";
        ToolTipService.SetToolTip(TitleBarToolText, tool.Description);

        ActiveTool?.OnActivated();
    }

    private void MainWindow_Activated(object sender, WindowActivatedEventArgs args)
    {
        // Coming back to the app is exactly when a second report may have appeared.
        UpdateReportGuard();

        if (args.WindowActivationState == WindowActivationState.Deactivated)
        {
            _wasActive = false;
            return;
        }

        // The author may have edited the open report in Power BI Desktop without
        // saving. Persist those edits on focus gain so every tool reads current
        // data; the file watcher reloads us once Desktop writes. Off-thread and
        // coalesced with our own apply-external-changes passes.
        if (!_wasActive)
        {
            _wasActive = true;
            MaybeRequestPowerBiSave();
        }

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

            // The taskbar button only exists once the window has been shown, so
            // re-apply the icon here to make sure it appears in the taskbar too.
            ApplyAppIcon();
        }
    }

    /// <summary>
    /// Saves Power BI Desktop's unsaved report edits when the app regains focus,
    /// throttled so ordinary focus toggles do not hammer Desktop. Skipped while the
    /// multi-report guard is up, since we cannot tell which report is meant.
    /// </summary>
    private void MaybeRequestPowerBiSave()
    {
        if (!App.Workspace.HasReport || _multiReportBlocked)
        {
            return;
        }

        var now = DateTime.UtcNow;
        if (now - _lastPowerBiSaveUtc < PowerBiFocusSaveCooldown)
        {
            return;
        }

        _lastPowerBiSaveUtc = now;
        App.Workspace.RequestPowerBiSave();
    }

    private void ConfigureAppWindow()
    {
        var appWindow = AppWindow;
        if (appWindow == null) return;

        appWindow.Resize(new SizeInt32(900, 600));

        if (appWindow.Presenter is OverlappedPresenter presenter)
        {
            presenter.IsResizable = true;
            presenter.IsMaximizable = false;
            presenter.IsMinimizable = true;
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
                App.Log($"Window icon applied: {iconPath}");
            }
            else
            {
                App.Log($"Window icon missing at: {iconPath}");
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

        AppWindow.Move(new PointInt32(200, 150));
        AppWindow.Show();
        this.Activate();

        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);
    }

    public void ShowAndCenterOverPowerBi(WindowRect pbiRect)
    {
        if (AppWindow == null) return;

        var size = AppWindow.Size;
        int width = size.Width > 0 ? size.Width : 900;
        int height = size.Height > 0 ? size.Height : 600;

        int x = pbiRect.Left + (pbiRect.Width - width) / 2;
        int y = pbiRect.Top + (pbiRect.Height - height) / 3;

        if (x < 0) x = 50;
        if (y < 0) y = 50;

        AppWindow.Move(new PointInt32(x, y));
        AppWindow.Show();
        this.Activate();

        ShowWindow(_hwnd, SW_RESTORE);
        SetForegroundWindow(_hwnd);

        ActiveTool?.OnActivated();
    }

    private void MainWindow_Closed(object sender, WindowEventArgs args)
    {
        _reportGuardTimer?.Stop();
        _reportGuardTimer = null;
    }

    // ---- Multiple Power BI reports guard ----------------------------------

    /// <summary>
    /// Polls how many Power BI Desktop reports are open. Singular Tools cannot tell
    /// which report the author means when a second one is open alongside the one the
    /// tool was launched from, so the whole UI is blurred out and blocked until it
    /// goes back to a single report.
    /// </summary>
    private void StartReportGuard()
    {
        try
        {
            UpdateReportGuard();

            _reportGuardTimer = DispatcherQueue.CreateTimer();
            _reportGuardTimer.Interval = ReportGuardInterval;
            _reportGuardTimer.IsRepeating = true;
            _reportGuardTimer.Tick += (_, _) => UpdateReportGuard();
            _reportGuardTimer.Start();
        }
        catch (Exception ex)
        {
            App.Log($"Report guard failed to start: {ex.Message}");
        }
    }

    private void UpdateReportGuard()
    {
        int count;
        try
        {
            count = PowerBiDetector.CountOpenPowerBiReports();
        }
        catch (Exception ex)
        {
            App.Log($"Report guard check failed: {ex.Message}");
            return;
        }

        var blocked = count > 1;
        if (blocked == _multiReportBlocked)
        {
            return;
        }

        _multiReportBlocked = blocked;
        ApplyReportGuard(blocked, count);
    }

    private void ApplyReportGuard(bool blocked, int count)
    {
        try
        {
            if (!blocked)
            {
                MultiReportOverlay.Visibility = Visibility.Collapsed;
                App.Log("Report guard cleared: back to a single Power BI report.");
                return;
            }

            MultiReportDetailText.Text = $"{count} Power BI Desktop reports detected.";
            MultiReportOverlay.Visibility = Visibility.Visible;
            App.Log($"Report guard engaged: {count} Power BI reports open.");

            // The overlay covers the app, but focus can still sit on the content
            // behind it, so move it somewhere harmless.
            MultiReportOverlay.Focus(FocusState.Programmatic);
        }
        catch (Exception ex)
        {
            App.Log($"Report guard overlay failed: {ex.Message}");
        }
    }

    private void OnExternalChangeDetected(object? sender, EventArgs e)
    {
        ToastService.Show("Report updated in Power BI Desktop — refreshed.", ToastSeverity.Informational);
    }

    private void OnToastRequested(ToastRequest request)
    {
        if (!DispatcherQueue.HasThreadAccess)
        {
            DispatcherQueue.TryEnqueue(() => AddToast(request));
            return;
        }

        AddToast(request);
    }

    private void AddToast(ToastRequest request)
    {
        if (ToastHost == null) return;

        // Keep only a handful of toasts on screen at once.
        while (ToastHost.Children.Count >= 4)
        {
            if (ToastHost.Children[0] is ToastCard oldest && _toastTimers.TryGetValue(oldest, out var oldTimer))
            {
                oldTimer.Stop();
                _toastTimers.Remove(oldest);
            }
            ToastHost.Children.RemoveAt(0);
        }

        var card = new ToastCard();
        card.Apply(request.Severity, request.Message);
        ToastHost.Children.Add(card);
        card.PlayIn();

        var timer = DispatcherQueue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(request.DurationMs);
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            _toastTimers.Remove(card);
            card.PlayOut(() =>
            {
                if (ToastHost.Children.Contains(card))
                {
                    ToastHost.Children.Remove(card);
                }
            });
        };
        _toastTimers[card] = timer;
        timer.Start();
    }
}
