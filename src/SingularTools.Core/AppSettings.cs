using System;

namespace SingularTools.Core;

/// <summary>The theme the app should render in.</summary>
public enum AppTheme
{
    /// <summary>Follow whatever Windows is set to (and never explicitly saved).</summary>
    System,

    /// <summary>Force the light theme.</summary>
    Light,

    /// <summary>Force the dark theme.</summary>
    Dark
}

/// <summary>
/// Machine-level preferences that are not tied to any one report: currently the
/// chosen app theme. Stored next to the workspace cache under
/// <c>%LOCALAPPDATA%\SingularTools</c>.
/// </summary>
/// <remarks>
/// Deliberately kept free of any WinUI type so <c>SingularTools.Core</c> stays
/// cross-platform; the app maps <see cref="Theme"/> onto an <c>ElementTheme</c>.
/// </remarks>
public sealed class AppSettings
{
    /// <summary>The persisted theme choice; <see cref="AppTheme.System"/> when never set.</summary>
    public AppTheme Theme { get; set; } = AppTheme.System;

    public static AppSettings Empty() => new();
}
