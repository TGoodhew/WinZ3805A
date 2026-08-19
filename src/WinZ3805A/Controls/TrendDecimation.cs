namespace WinZ3805A.Controls;

/// <summary>One reading on a trend: when it was taken, and what it was.</summary>
/// <param name="Ticks">
/// UTC ticks. A <see cref="long"/> rather than a <see cref="DateTimeOffset"/> because §12 sizes the
/// ring buffer at roughly sixteen bytes a sample for 604 800 of them, and a DateTimeOffset is
/// sixteen on its own.
/// </param>
/// <param name="Value">The reading. Nanoseconds for 1 PPS time interval.</param>
public readonly record struct TrendSample(long Ticks, double Value);

/// <summary>
/// What one pixel column of a trend chart has to draw.
/// </summary>
/// <param name="Column">Its x position, zero-based from the left edge of the plot.</param>
/// <param name="Minimum">The lowest value falling in this column.</param>
/// <param name="Maximum">The highest.</param>
/// <param name="Count">How many samples it covers, which the tooltip needs.</param>
public readonly record struct TrendColumn(int Column, double Minimum, double Maximum, int Count);

/// <summary>
/// Reduces a trend to one column per pixel, keeping both extremes of each (§9.10.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>Min and max per column, never a sample.</b> §9.10.2 is explicit and the reason is a number:
/// at the 7-day range there are 604 800 samples across perhaps 1 200 pixels, so each column covers
/// about eight minutes. Taking one sample per column — the obvious implementation — throws away
/// 499 of every 500 readings, and a one-second excursion has a 1-in-500 chance of being the one
/// kept. The glitch a user opened the chart to find is exactly what sampling deletes.
/// </para>
/// <para>
/// Keeping both extremes costs one extra double per column and makes a single-sample spike a
/// full-height stroke rather than a coin toss. This is the whole reason #38 chose a hand-rolled
/// renderer: LiveCharts had no downsampling and materialised 1.65 GB when given the raw series.
/// </para>
/// <para>
/// A column with no samples in it is <b>omitted</b>, not zero-filled. The receiver is not always
/// connected and a gap in the record is a fact about the record; drawing it as 0 ns would invent a
/// reading, and §11.1's rule that unparseable becomes null rather than a guess applies just as much
/// to a gap in time. The medallion ring already works this way.
/// </para>
/// </remarks>
public static class TrendDecimation
{
    /// <summary>
    /// Buckets samples into pixel columns across a time window.
    /// </summary>
    /// <param name="samples">
    /// Readings in ascending time order. Anything outside the window is skipped, so a caller may
    /// pass the whole buffer and let the window select.
    /// </param>
    /// <param name="fromTicks">The left edge of the window, in UTC ticks.</param>
    /// <param name="toTicks">The right edge. Must be after <paramref name="fromTicks"/>.</param>
    /// <param name="width">How many pixel columns the plot is wide.</param>
    /// <returns>
    /// One entry per column that has data, ascending. Never longer than
    /// <paramref name="width"/>, which is what bounds the drawing cost regardless of how many
    /// samples went in.
    /// </returns>
    public static IReadOnlyList<TrendColumn> ToColumns(
        IReadOnlyList<TrendSample> samples,
        long fromTicks,
        long toTicks,
        int width)
    {
        ArgumentNullException.ThrowIfNull(samples);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        if (toTicks <= fromTicks || samples.Count == 0)
        {
            return [];
        }

        long span = toTicks - fromTicks;

        // Accumulated in parallel arrays rather than a dictionary of structs: this runs over the
        // whole buffer on every resize, and 604 800 dictionary lookups is a different order of cost
        // from 604 800 array writes.
        double[] minimum = new double[width];
        double[] maximum = new double[width];
        int[] count = new int[width];

        for (int i = 0; i < samples.Count; i++)
        {
            TrendSample sample = samples[i];

            if (sample.Ticks < fromTicks || sample.Ticks > toTicks || double.IsNaN(sample.Value))
            {
                continue;
            }

            // The right edge belongs to the last column rather than to one past it.
            int column = (int)((sample.Ticks - fromTicks) * width / span);
            if (column >= width)
            {
                column = width - 1;
            }

            if (count[column] == 0)
            {
                minimum[column] = sample.Value;
                maximum[column] = sample.Value;
            }
            else
            {
                if (sample.Value < minimum[column])
                {
                    minimum[column] = sample.Value;
                }

                if (sample.Value > maximum[column])
                {
                    maximum[column] = sample.Value;
                }
            }

            count[column]++;
        }

        List<TrendColumn> columns = [];
        for (int c = 0; c < width; c++)
        {
            if (count[c] > 0)
            {
                columns.Add(new TrendColumn(c, minimum[c], maximum[c], count[c]));
            }
        }

        return columns;
    }

    /// <summary>
    /// The y-axis bounds for a set of columns, anchored so that zero is always on the axis.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §9.10.2 requires the time-interval axis to be zero-anchored and §9.4.4 requires the diverging
    /// fill's neutral midpoint to map to <b>exactly</b> 0 ns rather than to the middle of the data.
    /// Both are the same rule seen from two sides: an axis that framed the data would put the colour
    /// break wherever the readings happened to sit, so a receiver holding steadily at −40 ns would
    /// be drawn as if it were straddling zero.
    /// </para>
    /// <para>
    /// The floor exists for the same reason the medallion ring has one. A loop sitting quietly
    /// within a nanosecond would otherwise be magnified until its noise filled the plot and looked
    /// like an instrument in trouble.
    /// </para>
    /// </remarks>
    /// <param name="columns">The decimated columns.</param>
    /// <param name="floor">
    /// The smallest half-range to show, in the value's own units. Defaults to the ±50 ns the
    /// medallion uses, so the two surfaces do not disagree about what "quiet" looks like.
    /// </param>
    public static (double Minimum, double Maximum) ZeroAnchoredBounds(
        IReadOnlyList<TrendColumn> columns,
        double floor = 50)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(floor);

        double extent = floor;

        foreach (TrendColumn column in columns)
        {
            extent = Math.Max(extent, Math.Max(Math.Abs(column.Minimum), Math.Abs(column.Maximum)));
        }

        return (-extent, extent);
    }
}
