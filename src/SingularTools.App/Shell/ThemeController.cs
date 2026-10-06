using System;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using SingularTools.Core;
using Windows.UI;

namespace SingularTools_App.Shell;

/// <summary>
/// Owns the app's light/dark theme: loads the saved choice, forces it onto the
/// window's content tree, recolors the OS-drawn caption buttons so they stay
/// legible when the app theme differs from Windows, and persists changes.
/// </summary>
/// <remarks>
/// The app follows Windows until the first toggle. On startup with no saved
/// choice the current Windows theme is resolved and remembered, so the footer
/// control can show the real state from the very first launch.
/// </remarks>
internal sealed class ThemeController
{
    private static readonly Color Transparent = Colors.Transparent;
    private static readonly Color LightButtonForeground = Colors.Black;
    private static readonly Color DarkButtonForeground = Colors.White;
    private static readonly Color LightInactiveForeground = Color.FromArgb(255, 96, 96, 96);
    private static readonly Color DarkInactiveForeground = Color.FromArgb(255, 160, 160, 160);
    private static readonly Color LightHoverBackground = Color.FromArgb(24, 0, 0, 0);
    private static readonly Color DarkHoverBackground = Color.FromArgb(32, 255, 255, 255);

    private readonly Window _window;

    public ThemeController(Window window)
    {
        _window = window ?? throw new ArgumentNullException(nameof(window));
    }

    /// <summary>The theme currently applied (always Light or Dark after <see cref="Initialize"/>).</summary>
    public AppTheme Theme { get; private set; } = AppTheme.Light;

    /// <summary>True when the dark theme is applied.</summary>
    public bool IsDark => Theme == AppTheme.Dark;

    /// <summary>Resolves the saved (or Windows) theme and applies it.</summary>
    public void Initialize()
    {
        var saved = AppSettingsStore.Load();
        Theme = saved.Theme;

        if (Theme == AppTheme.System)
        {
            // No explicit choice yet: match Windows once and remember it, so the
            // toggle reflects the actual state instead of a default.
            Theme = Application.Current.RequestedTheme == ApplicationTheme.Dark
                ? AppTheme.Dark
                : AppTheme.Light;
            Persist();
        }

        Apply();
    }

    /// <summary>Flips between light and dark, applies it and remembers the choice.</summary>
    public void Toggle()
    {
        Theme = Theme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark;
        Persist();
        Apply();
    }

    private void Apply()
    {
        if (_window.Content is FrameworkElement root)
        {
            root.RequestedTheme = Theme == AppTheme.Dark ? ElementTheme.Dark : ElementTheme.Light;
        }

        ApplyTitleBarColors();
    }

    /// <summary>
    /// Recolors the caption buttons. They are drawn by the system, not by our
    /// XAML, so they do not follow <c>RequestedTheme</c> and would otherwise keep
    /// Windows' colors — dark glyphs on a dark app title bar after a toggle.
    /// </summary>
    private void ApplyTitleBarColors()
    {
        var titleBar = _window.AppWindow?.TitleBar;
        if (titleBar == null) return;

        var dark = IsDark;

        titleBar.ButtonBackgroundColor = Transparent;
        titleBar.ButtonInactiveBackgroundColor = Transparent;
        titleBar.ButtonForegroundColor = dark ? DarkButtonForeground : LightButtonForeground;
        titleBar.ButtonInactiveForegroundColor = dark ? DarkInactiveForeground : LightInactiveForeground;
        titleBar.ButtonHoverBackgroundColor = dark ? DarkHoverBackground : LightHoverBackground;
        titleBar.ButtonHoverForegroundColor = dark ? DarkButtonForeground : LightButtonForeground;
        titleBar.ButtonPressedBackgroundColor = dark ? DarkHoverBackground : LightHoverBackground;
        titleBar.ButtonPressedForegroundColor = dark ? DarkButtonForeground : LightButtonForeground;
    }

    private void Persist()
    {
        try
        {
            AppSettingsStore.Save(new AppSettings { Theme = Theme });
        }
        catch (Exception ex)
        {
            // The theme is already applied; failing to remember it is cosmetic.
            App.Log($"Could not save theme preference: {ex.Message}");
        }
    }
}
