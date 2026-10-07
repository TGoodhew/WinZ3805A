namespace WinZ3805A.Controls;

/// <summary>
/// Whether a scrolling dialog has to say that it scrolls, and what it says (#767).
/// </summary>
/// <remarks>
/// <para>
/// Fluent hides a scroll bar until the pointer comes near it, so in a scrolling dialog the only sign
/// that there is more is whatever the bottom edge of the view happens to cut through. In the Manage
/// satellites dialog that was usually half a row of PRN buttons — until a size where the view ended
/// exactly on a row boundary, and twenty satellites were out of view with nothing on screen to say
/// so: 200 % text in the Minimal window, 7 Oct 2026. A cue that depends on where the edge falls is a
/// coincidence, not a cue.
/// </para>
/// <para>
/// So the dialog says it in words, below the scrolling area where scrolling cannot move it, whenever
/// and only while its content is taller than its view. The words are text rather than a scroll bar
/// forced open, because WinUI has no per-control switch for that — the indicator's visibility is
/// driven by the stock template's own visual states — and because text is the one form a keyboard
/// user who is not watching the edge, and a screen reader, both receive.
/// </para>
/// <para>
/// <b>The sentence keeps one shape whatever the count.</b> Its height is part of the layout it
/// describes: a cue that changed its wording when every satellite came into view could change its
/// line count, and so the view's height, and so the count — and flip between the two at a boundary.
/// One shape, differing only in its digits, has nothing to flip between.
/// </para>
/// <para>
/// <b>Except when every satellite is in view and the dialog still scrolls</b>, because what is out
/// of view is then the buttons below the grid. "Showing 32 of 32 satellites. Scroll for more." says
/// there is more of what is all on screen (Win11 VM, 150 % and 200 % text in the Medium window, 7 Oct
/// 2026), so the line says only "Scroll for more." That cannot flip either: it is the sentence's own
/// last words, which wrap onto no more lines than the whole, so switching to it can only give the
/// view more room — and more room cannot take a satellite out of view.
/// </para>
/// <para>
/// This is the arithmetic only; the dialog measures, and this decides. It reads no UI type, so it is
/// tested headlessly.
/// </para>
/// </remarks>
public static class OverflowCue
{
    /// <summary>
    /// How far a measurement may be off and still count, in effective pixels.
    /// </summary>
    /// <remarks>
    /// Layout rounds to physical pixels, so at 150 % scaling an edge that is exactly on the boundary
    /// can measure a third of a pixel either side of it. Half a pixel absorbs that without admitting
    /// anything a person could see as cut.
    /// </remarks>
    public const double Tolerance = 0.5;

    /// <summary>Whether content <paramref name="extent"/> tall overflows a view <paramref name="viewport"/> tall.</summary>
    public static bool Overflows(double extent, double viewport) => extent - viewport > Tolerance;

    /// <summary>
    /// Whether an item whose top is <paramref name="top"/> below the view's top, and which is
    /// <paramref name="height"/> tall, has at least half its height inside a view
    /// <paramref name="viewport"/> tall.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Half, not wholly. The first rule was wholly in view, and in a 640 × 432 window at 200 % text the
    /// view was shorter than one row: six buttons showed, each cut about half way, and the line read
    /// "Showing 0 of 32" over them (Win11 VM, 7 Oct 2026). A count that contradicts what is on screen
    /// is worse than none.
    /// </para>
    /// <para>
    /// Half, not any part: a row with a sliver showing is a row the user cannot read, and at 150 % text
    /// in the Compact window a row about 40 % visible is still rightly left out. An item with no height
    /// has not been laid out and is not in view.
    /// </para>
    /// </remarks>
    public static bool IsInView(double top, double height, double viewport)
    {
        if (height <= 0)
        {
            return false;
        }

        double visible = Math.Min(top + height, viewport) - Math.Max(top, 0);
        return visible >= (height / 2) - Tolerance;
    }

    /// <summary>How many of <paramref name="items"/> are at least half in a view <paramref name="viewport"/> tall.</summary>
    /// <param name="items">Each item's top relative to the view's top, and its height.</param>
    /// <param name="viewport">The view's height.</param>
    public static int CountInView(IEnumerable<(double Top, double Height)> items, double viewport)
    {
        ArgumentNullException.ThrowIfNull(items);

        return items.Count(item => IsInView(item.Top, item.Height, viewport));
    }

    /// <summary>
    /// The sentence to show, or null when the content fits and nothing needs saying.
    /// </summary>
    /// <param name="overflows">Whether the content is taller than its view.</param>
    /// <param name="inView">How many satellites are at least half in view.</param>
    /// <param name="total">How many there are; zero before the receiver has been read.</param>
    /// <returns>
    /// Null when the content fits; "Scroll for more." alone when there is nothing to count or every
    /// satellite is already in view; otherwise how many are in view, and that there is more.
    /// </returns>
    public static string? Describe(bool overflows, int inView, int total)
    {
        if (!overflows)
        {
            return null;
        }

        return total <= 0 || inView >= total
            ? "Scroll for more."
            : $"Showing {Math.Max(inView, 0)} of {total} satellites. Scroll for more.";
    }
}
