using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace WinZ3805A.Services;

/// <summary>
/// Keeps a window's caption — the text Windows has, which the taskbar, Alt+Tab and Narrator name
/// the window by — when a <see cref="TitleBar"/> control draws its title (#637).
/// </summary>
/// <remarks>
/// <para>
/// <b>The <see cref="TitleBar"/> control writes its own <see cref="TitleBar.Title"/> over the
/// window's caption when it loads.</b> Measured on Windows 10 22H2 and Windows 11 26H2 with the
/// Windows App SDK 2.3.1, 2 Oct 2026: the caption was still <c>Help - WinZ3805A</c> after
/// <c>SetTitleBar</c> and after the window's first activation, and was the bare display name once
/// the title bar had loaded. So the Help and Details windows both reached Windows named exactly
/// like the main window, and a keyboard or Narrator user could not tell the three apart.
/// </para>
/// <para>
/// The drawn title is right as it is — the display name, with the window's name as the subtitle —
/// so the caption is set again after the control has had its say, rather than changing what it
/// draws. On every load, not the first only, since a load is when it writes.
/// </para>
/// </remarks>
internal static class WindowCaption
{
    /// <summary>Gives <paramref name="window"/> <paramref name="caption"/>, now and each time <paramref name="titleBar"/> loads.</summary>
    /// <remarks>
    /// The subscription is not undone: the title bar is part of the window's own content and dies
    /// with it, so the handler holds nothing that would outlive either.
    /// </remarks>
    public static void Keep(Window window, TitleBar titleBar, string caption)
    {
        window.Title = caption;
        titleBar.Loaded += (_, _) => window.Title = caption;
    }
}
