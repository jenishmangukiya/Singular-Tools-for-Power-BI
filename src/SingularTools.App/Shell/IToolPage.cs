using System;

namespace SingularTools_App.Shell;

/// <summary>
/// Contract implemented by every tool hosted in the Singular Tools shell.
/// Adding a new tool only requires implementing this interface and registering
/// it in <see cref="ToolRegistry"/>.
/// </summary>
public interface IToolPage
{
    /// <summary>Stable identifier used for navigation and persistence.</summary>
    string ToolId { get; }

    /// <summary>Short label shown in the navigation rail.</summary>
    string Title { get; }

    /// <summary>One-line description shown as the navigation tooltip.</summary>
    string Description { get; }

    /// <summary>Segoe Fluent Icons glyph for the navigation rail.</summary>
    string Glyph { get; }

    /// <summary>
    /// Called when the tool becomes the active tool. Tools should refresh their
    /// data and focus their primary input here.
    /// </summary>
    void OnActivated();
}

/// <summary>Describes a tool so the shell can build navigation without referencing the page type directly.</summary>
public sealed class ToolDescriptor
{
    public ToolDescriptor(string id, string title, string description, string glyph, Type pageType)
    {
        Id = id;
        Title = title;
        Description = description;
        Glyph = glyph;
        PageType = pageType;
    }

    public string Id { get; }
    public string Title { get; }
    public string Description { get; }
    public string Glyph { get; }
    public Type PageType { get; }
}
