namespace WinZ3805A.Controls;

/// <summary>
/// How often a trend chart can usefully be redrawn (#387).
/// </summary>
/// <remarks>
/// <para>
/// A trend decimates to one column per pixel (§9.10.2), so <b>a redraw shows nothing until a whole
/// column of new data exists</b>. On a 6 h range 700 px wide that is one column every 31 seconds;
/// on 7 d it is every 14 minutes. Redrawing faster than that is work with no output — and it is not
/// cheap work, because the read and the decimation happen in front of the drawing.
/// </para>
/// <para>
/// <b>#385 is what this exists to stop.</b> <c>OverviewPage</c> re-read its whole window on every
/// property notification — at least once a second, more when readings landed — and the measured
/// consequence was 36 MB/s of allocation, a large object heap of 1.1 GB and a working set climbing
/// 8.9 MB a minute for ten hours without a ceiling. The page was showing a 6 h window where one
/// pixel is half a minute.
/// </para>
/// <para>
/// Deliberately a pure function of numbers rather than a timer: the pages already have a ticker,
/// and what they were missing was the question "would this redraw show anything". It also makes the
/// rule testable, which a <c>DispatcherTimer</c> inside a page is not — this file is linked into the
/// headless test project, so it must not name a WinUI type even in a comment.
/// </para>
/// </remarks>
public static class TrendRefreshPolicy
{
    /// <summary>
    /// Never slower than the eye expects a live chart to move, whatever the arithmetic says.
    /// </summary>
    /// <remarks>
    /// A ceiling on the interval rather than on the rate. At 7 d over a narrow chart the column
    /// arithmetic asks for something like half an hour, and a plot on a wall display that has not
    /// moved in half an hour reads as broken even when it is correct. Two minutes is the compromise:
    /// still 120× cheaper than the per-second redraw this replaces.
    /// </remarks>
    public static readonly TimeSpan LongestInterval = TimeSpan.FromMinutes(2);

    /// <summary>
    /// And never faster than once a second, whatever the arithmetic says.
    /// </summary>
    /// <remarks>
    /// The floor exists for the degenerate cases — a very wide chart on a very short range — where
    /// the column time falls below the poll interval and the policy would stop being a throttle at
    /// all. It is not reachable from any range §10.4 or §10.7 offers.
    /// </remarks>
    public static readonly TimeSpan ShortestInterval = TimeSpan.FromSeconds(1);

    /// <summary>
    /// The time one pixel column of <paramref name="range"/> covers, clamped to the bounds above.
    /// </summary>
    /// <param name="range">The window the chart is showing.</param>
    /// <param name="chartWidthPixels">
    /// The chart's width. Zero or negative means it has not been laid out yet, which is answered
    /// with <see cref="ShortestInterval"/> rather than with an exception: a chart that has no width
    /// has drawn nothing, and the caller's next redraw is the one that puts something on it.
    /// </param>
    public static TimeSpan MinimumInterval(TimeSpan range, double chartWidthPixels)
    {
        if (range <= TimeSpan.Zero || chartWidthPixels <= 0 || double.IsNaN(chartWidthPixels))
        {
            return ShortestInterval;
        }

        TimeSpan perColumn = TimeSpan.FromTicks((long)(range.Ticks / chartWidthPixels));

        if (perColumn < ShortestInterval) { return ShortestInterval; }
        if (perColumn > LongestInterval) { return LongestInterval; }

        return perColumn;
    }

    /// <summary>
    /// Whether enough time has passed since the last redraw for the next one to show anything.
    /// </summary>
    /// <param name="lastRenderedTicks">
    /// UTC ticks of the last redraw, or 0 when there has not been one — which always answers true,
    /// because the first redraw is the one that fills an empty chart.
    /// </param>
    /// <param name="nowTicks">UTC ticks now.</param>
    /// <param name="range">The window the chart is showing.</param>
    /// <param name="chartWidthPixels">The chart's width.</param>
    /// <remarks>
    /// A clock that has gone backwards — a correction, or a test with a pinned provider — answers
    /// true. The alternative is a chart that stops updating until real time catches up with a
    /// timestamp it should never have had.
    /// </remarks>
    public static bool ShouldRedraw(long lastRenderedTicks, long nowTicks, TimeSpan range, double chartWidthPixels)
    {
        if (lastRenderedTicks <= 0 || nowTicks < lastRenderedTicks)
        {
            return true;
        }

        return nowTicks - lastRenderedTicks >= MinimumInterval(range, chartWidthPixels).Ticks;
    }
}
