using System;
using System.Collections.Generic;
using Microsoft.UI.Xaml.Controls;
using SingularTools.Core;

namespace SingularTools_App.Shell;

/// <summary>
/// Shared presentation of the workspace cache, so both publishing tools label
/// detection and report its age in exactly the same words.
///
/// Detection drives Power BI Desktop's publish dialog, so it is slow and rarely
/// changes anything. Once a list has been cached the button becomes an explicit
/// "Redetect" and the last successful time is shown beside it.
/// </summary>
internal static class WorkspaceDetectPresenter
{
    public const string DetectLabel = "Detect workspaces";
    public const string RedetectLabel = "Redetect workspaces";

    /// <summary>Button label for the current cache state.</summary>
    public static string LabelFor(WorkspaceCache? cache)
        => cache != null && cache.Names.Count > 0 ? RedetectLabel : DetectLabel;

    /// <summary>
    /// Applies the cache state to a button and an optional "last detected" caption.
    /// The caption is hidden when nothing has been detected yet.
    /// </summary>
    public static void Apply(Button? button, TextBlock? caption, WorkspaceCache? cache)
    {
        SetButtonLabel(button, LabelFor(cache));

        if (caption == null) return;

        var description = cache?.DescribeLastDetected();
        caption.Text = description ?? string.Empty;
        caption.Visibility = string.IsNullOrEmpty(description)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
    }

    /// <summary>
    /// Sets a button's label when it is built from a text child (the convention
    /// used across these tool pages).
    /// </summary>
    public static void SetButtonLabel(Button? button, string label)
    {
        if (button == null) return;

        if (button.Content is TextBlock text)
        {
            text.Text = label;
            return;
        }

        // Some buttons wrap their label in a panel alongside an icon.
        if (button.Content is Panel panel)
        {
            foreach (var child in panel.Children)
            {
                if (child is TextBlock childText)
                {
                    childText.Text = label;
                }
            }
        }
    }

    /// <summary>
    /// Merges freshly detected names into the cache, preserving the detection order
    /// Power BI returned and stamping the time.
    /// </summary>
    public static WorkspaceCache Merge(IReadOnlyList<string> detected, WorkspaceCache? previous)
    {
        var cache = new WorkspaceCache
        {
            Names = new List<string>(detected),
            LastDetectedUtc = DateTimeOffset.UtcNow
        };

        if (cache.Names.Count == 0 && previous != null)
        {
            // A detection that returned nothing is far more likely to mean the
            // dialog was not ready than that every workspace vanished, so keep
            // what we had rather than wiping the author's list.
            cache.Names = new List<string>(previous.Names);
        }

        return cache;
    }
}
