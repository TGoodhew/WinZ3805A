namespace WinZ3805A.Services;

/// <summary>
/// Converts a §9.6 content-width requirement into the physical window size that delivers it.
/// </summary>
/// <remarks>
/// <para>
/// §9.6.1's breakpoints are widths of <i>content</i>, in effective pixels, because that is what
/// <c>NavigationView</c> measures itself against. <c>OverlappedPresenter.PreferredMinimumWidth</c>
/// is a width of <i>window</i>, in physical pixels. Two differences sit between them, and §9.6.2's
/// minimum of 1024 x 720 accounts for neither:
/// </para>
/// <list type="number">
/// <item>
/// <description>
/// <b>Non-client chrome.</b> A 1024 px window has a 1008 px client area at 100% scaling — measured,
/// not assumed. So the window opens 16 px short of the 1024 Expanded threshold and lands in
/// <c>LeftCompact</c>: an icon rail at exactly the size the layout was designed around, which is
/// the failure §9.6.2's amendment raised the minimum from 1000 to 1024 to prevent.
/// </description>
/// </item>
/// <item>
/// <description>
/// <b>Display scaling.</b> At 150% a 1024 <i>physical</i> window is 683 effective pixels of
/// content, so the window could never reach the Expanded breakpoint at all — and A11Y-7 requires
/// the app to be usable at 350%, where it is 293.
/// </description>
/// </item>
/// </list>
/// <para>
/// Pure arithmetic over four numbers, so the scaling factors this machine cannot produce — it has
/// one display at 100% — are still covered.
/// </para>
/// </remarks>
public static class WindowSizing
{
    /// <summary>
    /// The physical window size whose client area is at least the requested effective size.
    /// </summary>
    /// <param name="contentWidth">Required content width in effective pixels — a §9.6.1 threshold.</param>
    /// <param name="contentHeight">Required content height in effective pixels.</param>
    /// <param name="scale">
    /// Effective-to-physical scaling, as <c>XamlRoot.RasterizationScale</c> reports it: 1.0 at
    /// 100%, 1.5 at 150%.
    /// </param>
    /// <param name="chromeWidth">Physical pixels of window that are not client area, horizontally.</param>
    /// <param name="chromeHeight">Physical pixels of window that are not client area, vertically.</param>
    public static (int Width, int Height) PhysicalMinimum(
        int contentWidth,
        int contentHeight,
        double scale,
        int chromeWidth,
        int chromeHeight)
    {
        // A scale that is zero, negative or not a number would silently produce a window of nothing.
        // It comes from a XamlRoot that may not be ready yet, so it is guarded rather than trusted.
        double factor = scale is > 0 and <= 10 ? scale : 1.0;

        // Ceiling, not rounding: landing half a pixel below a threshold puts the whole layout in
        // the wrong breakpoint, and the cost of being one pixel over is nothing at all.
        return (
            (int)Math.Ceiling(contentWidth * factor) + Math.Max(0, chromeWidth),
            (int)Math.Ceiling(contentHeight * factor) + Math.Max(0, chromeHeight));
    }

    /// <summary>
    /// §9.6.2's compact floor, grown for the user's text scale.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>144 is a 100 %-text figure, and a fixed pixel floor cannot hold constant content across
    /// text scales.</b> That is #215: at 200 % the mode text and satellite count no longer fit in
    /// 144 effective pixels, and the count — which §9.6.2 requires — was the part pushed out.
    /// </para>
    /// <para>
    /// Only part of the floor is text. §10.3 builds the compact wireframe from a <b>32 px title
    /// bar</b> and a <b>64 px medallion</b>, both fixed by construction, leaving <b>48 px</b> for
    /// the mode line, the satellite count and their padding. So the fixed 96 stays fixed and the
    /// 48 scales, which returns exactly 144 at 100 % — the specification's own number, not an
    /// approximation of it.
    /// </para>
    /// <para>
    /// Display scaling is <i>not</i> applied here. These are effective pixels;
    /// <see cref="PhysicalMinimum"/> converts. Text scale and display scale are separate axes and
    /// multiplying by both would compound them.
    /// </para>
    /// </remarks>
    /// <param name="textScale">
    /// <c>UISettings.TextScaleFactor</c>: 1.0 at 100 %, 2.0 at 200 %. Values below 1 are clamped —
    /// Windows does not offer text smaller than 100 %, and a floor below §9.6.2's is not ours to
    /// invent.
    /// </param>
    public static int CompactMinimumHeight(double textScale)
    {
        const int Fixed = 96;      // 32 px title bar + 64 px medallion (§10.3)
        const int Scaling = 48;    // mode line, satellite count and padding (§9.6.2's remainder)

        double scale = double.IsNaN(textScale) || textScale < 1.0 ? 1.0 : textScale;

        return Fixed + (int)Math.Ceiling(Scaling * scale);
    }

    /// <summary>
    /// Caps a window size at the display it is on, so a floor can never exceed the screen.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <see cref="PhysicalMinimum"/> multiplies a content requirement by the scaling factor, and
    /// nothing in that arithmetic knows how big the display is. §9.6.2's 1024 x 720 needs 3600 x
    /// 2528 physical pixels at 350%, and A11Y-7 requires the app to be usable there. Written
    /// straight into <c>PreferredMinimumWidth</c> that floor is larger than any display it would
    /// be read on, so the window opens bigger than the screen, the title bar is off the top or the
    /// resize edges are off the sides, and there is no gesture that brings it back.
    /// </para>
    /// <para>
    /// Capping gives up the breakpoint rather than the window. Above roughly 190% scaling on a
    /// 1920-wide display the content falls below §9.6.1's 640 px Compact floor and
    /// <c>NavigationView</c> shows the Minimal pane — a layout §9.6.1 does not describe, which is
    /// the open half of #101. A pane behind a hamburger is a worse layout; a window that cannot be
    /// moved is not a layout at all.
    /// </para>
    /// </remarks>
    /// <param name="width">The wanted window width in physical pixels.</param>
    /// <param name="height">The wanted window height in physical pixels.</param>
    /// <param name="workArea">
    /// The desktop area of the display the window is on, taskbar excluded, or
    /// <see langword="null"/> when no display could be identified — in which case the wanted size
    /// is returned untouched rather than guessed at.
    /// </param>
    public static (int Width, int Height) ClampToWorkArea(int width, int height, WindowRect? workArea)
    {
        if (workArea is not { IsEmpty: false } area)
        {
            return (width, height);
        }

        return (Math.Min(width, area.Width), Math.Min(height, area.Height));
    }
}
