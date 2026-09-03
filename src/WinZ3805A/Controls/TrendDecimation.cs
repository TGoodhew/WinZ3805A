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
    /// Reduces a run of states to the pixel columns over which each held.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #49's lock-state shading. Separate from <see cref="ToColumns"/> because a state is not a
    /// number: two states in one column cannot be averaged, and the honest reduction is "the state
    /// that covered most of this column", not a mean of an enumeration.
    /// </para>
    /// <para>
    /// A column with no samples is absent, exactly as with the value series, so an unrecorded
    /// stretch is unshaded rather than shaded with whatever preceded it. Shading a gap would assert
    /// the receiver was locked while it was in fact unplugged.
    /// </para>
    /// </remarks>
    /// <param name="states">
    /// Samples of the state, ascending. <c>Value</c> is an arbitrary integer key — the caller
    /// decides what it means.
    /// </param>
    /// <param name="fromTicks">The left edge of the window.</param>
    /// <param name="toTicks">The right edge.</param>
    /// <param name="width">How many pixel columns the plot is wide.</param>
    public static IReadOnlyList<(int Column, int State)> ToStateColumns(
        IReadOnlyList<TrendSample> states,
        long fromTicks,
        long toTicks,
        int width)
    {
        ArgumentNullException.ThrowIfNull(states);
        ArgumentOutOfRangeException.ThrowIfLessThan(width, 1);

        if (toTicks <= fromTicks || states.Count == 0)
        {
            return [];
        }

        long span = toTicks - fromTicks;

        // One small tally per column. States are few, so a per-column dictionary would cost more
        // in allocation than the counting saves.
        Dictionary<int, int>[] tally = new Dictionary<int, int>[width];

        foreach (TrendSample state in states)
        {
            if (state.Ticks < fromTicks || state.Ticks > toTicks)
            {
                continue;
            }

            int column = (int)((state.Ticks - fromTicks) * width / span);
            if (column >= width)
            {
                column = width - 1;
            }

            Dictionary<int, int> counts = tally[column] ??= [];
            int key = (int)state.Value;
            counts[key] = counts.TryGetValue(key, out int seen) ? seen + 1 : 1;
        }

        List<(int Column, int State)> columns = new(width);
        for (int c = 0; c < width; c++)
        {
            if (tally[c] is not Dictionary<int, int> counts || counts.Count == 0)
            {
                continue;
            }

            int best = 0;
            int bestCount = -1;
            foreach ((int state, int count) in counts)
            {
                if (count > bestCount)
                {
                    best = state;
                    bestCount = count;
                }
            }

            columns.Add((c, best));
        }

        return columns;
    }

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

        List<TrendColumn> columns = new(width);
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

        (double low, double high) = TypicalRange(columns);

        // An empty window answers with an inverted infinite range, which is the honest answer to
        // "what range does no data occupy" and a terrible axis. The floor is the whole axis then.
        double extent = double.IsInfinity(low) || double.IsInfinity(high)
            ? floor
            : Math.Max(floor, Math.Max(Math.Abs(low), Math.Abs(high)));

        return (-extent, extent);
    }

    /// <summary>
    /// The y-axis bounds for a set of columns, framed on the data rather than on zero.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>For a quantity whose zero is not a reference</b> — §10.7.1's oscillator control, where
    /// 0 % is one end of an arbitrary control range rather than a meaningful value, and the
    /// diagnostic content is entirely in the deviation. Zero-anchoring such a series makes the axis
    /// extent a function of the offset from zero and nothing else: a receiver parked at −16.83 %
    /// with 0.05 % of structure across two days drew as a dead-flat line occupying a thousandth of
    /// the plot, whatever floor it was given (#183).
    /// </para>
    /// <para>
    /// <b>The minimum span is the same idea as <see cref="ZeroAnchoredBounds"/>'s floor</b>, and it
    /// is what stops the other failure. Framed tightly, a converter sitting on two adjacent codes
    /// would be magnified until its least significant bit filled the plot and read as an
    /// instrument in trouble — the shape of a healthy oscillator and a sick one would be identical.
    /// </para>
    /// <para>
    /// Bounds are snapped outward to a round step of about a quarter of the span, so the three
    /// labels §9.10.2's <c>TrendChart</c> row allows land on numbers a reader can subtract.
    /// </para>
    /// </remarks>
    /// <param name="columns">The decimated columns.</param>
    /// <param name="minimumSpan">
    /// The smallest total range to show, in the value's own units. Below it the bounds widen about
    /// the data's own midpoint rather than magnifying it.
    /// </param>
    public static (double Minimum, double Maximum) AutoBounds(
        IReadOnlyList<TrendColumn> columns,
        double minimumSpan)
    {
        ArgumentNullException.ThrowIfNull(columns);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumSpan);

        (double lowest, double highest) = TypicalRange(columns);

        // No data at all. Centred on zero is as good an answer as any, and better than an axis
        // whose labels are infinities.
        if (double.IsInfinity(lowest) || double.IsInfinity(highest))
        {
            return (-minimumSpan / 2, minimumSpan / 2);
        }

        double middle = (lowest + highest) / 2;
        double half = Math.Max(highest - lowest, minimumSpan) / 2;

        return SnapOutward(middle - half, middle + half);
    }

    /// <summary>
    /// How far beyond the bulk of the readings a value must sit before the axis ignores it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A hundred times the interquartile spread, which is deliberately enormous.</b> This is not
    /// a rule for finding unusual readings — it is a rule for finding <i>impossible</i> ones, and
    /// the two must not be confused. On this instrument the values that caused #209 were three
    /// <b>seconds</b> of time interval against a window spanning tens of nanoseconds: out by a
    /// factor of 10⁸. A genuine excursion, even a bad one, is a few tens of times the ordinary
    /// spread and stays on the axis.
    /// </para>
    /// <para>
    /// <b>An excursion is the diagnostic content on a timing instrument.</b> A framing rule that
    /// hides the largest one is worse than an unreadable axis, so the factor is set where only
    /// arithmetic nonsense falls outside it, and whatever does is counted and named beside the
    /// chart rather than quietly dropped.
    /// </para>
    /// </remarks>
    private const double OutlierFactor = 100;

    /// <summary>Below this many columns there are no quartiles worth the name.</summary>
    private const int MinimumColumnsForOutliers = 12;

    /// <summary>
    /// The range the readings occupy, ignoring any that are impossible rather than merely large
    /// (#209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Framing on the absolute extremes lets one reading set the axis for a week. Measured: the
    /// 1 PPS axis became <b>±3,000,000,000 ns</b> for data spanning −76.5 to +21 ns, the real trace
    /// occupying about two millionths of the plot height — on a chart whose zero anchoring was never
    /// in question. Both axis modes need this; neither is safe from it.
    /// </para>
    /// <para>
    /// <b>The bulk is measured with quartiles, not with a fraction of the columns.</b> The first
    /// version dropped a fixed proportion from each end, and it did nothing at all on real data: a
    /// seven-day window is mostly empty, so only 153 of 680 pixel columns held readings and 0.2 % of
    /// 153 rounds to zero. It worked in a test of 1,200 dense columns and nowhere else. Quartiles
    /// need no such constant and are unmoved by a handful of absurd values by construction.
    /// </para>
    /// <para>
    /// Only the <i>bounds</i> ignore anything. Every column is still drawn and the draw path clamps
    /// to the plot edge, so an excluded extreme appears as a trace pinned to the top or bottom
    /// rather than as nothing at all.
    /// </para>
    /// </remarks>
    public static (double Minimum, double Maximum) TypicalRange(IReadOnlyList<TrendColumn> columns)
    {
        ArgumentNullException.ThrowIfNull(columns);

        if (columns.Count == 0)
        {
            return (double.PositiveInfinity, double.NegativeInfinity);
        }

        double[] minima = new double[columns.Count];
        double[] maxima = new double[columns.Count];
        double[] pooled = new double[columns.Count * 2];

        for (int i = 0; i < columns.Count; i++)
        {
            minima[i] = columns[i].Minimum;
            maxima[i] = columns[i].Maximum;
            pooled[i * 2] = columns[i].Minimum;
            pooled[(i * 2) + 1] = columns[i].Maximum;
        }

        Array.Sort(minima);
        Array.Sort(maxima);

        double lowest = minima[0];
        double highest = maxima[^1];

        if (columns.Count < MinimumColumnsForOutliers)
        {
            return (lowest, highest);
        }

        Array.Sort(pooled);

        double lowerQuartile = pooled[pooled.Length / 4];
        double upperQuartile = pooled[pooled.Length * 3 / 4];
        double bulk = upperQuartile - lowerQuartile;

        // A window with no spread has no bulk to be outside of, and calling its largest reading
        // impossible would be arithmetic rather than judgement.
        if (bulk <= 0)
        {
            return (lowest, highest);
        }

        double ceiling = upperQuartile + (OutlierFactor * bulk);
        double basement = lowerQuartile - (OutlierFactor * bulk);

        return (LargestBelow(minima, basement, lowest), SmallestBelow(maxima, ceiling, highest));
    }

    /// <summary>The most negative value at or above <paramref name="limit"/>, or the true minimum.</summary>
    /// <remarks>
    /// Snapped to a reading rather than to the limit, so the axis still describes data the receiver
    /// produced. An axis bound at a computed threshold would be a number nothing in the window ever
    /// took.
    /// </remarks>
    private static double LargestBelow(double[] ascending, double limit, double fallback)
    {
        if (fallback >= limit)
        {
            return fallback;
        }

        foreach (double value in ascending)
        {
            if (value >= limit)
            {
                return value;
            }
        }

        return fallback;
    }

    /// <inheritdoc cref="LargestBelow" />
    private static double SmallestBelow(double[] ascending, double limit, double fallback)
    {
        if (fallback <= limit)
        {
            return fallback;
        }

        for (int i = ascending.Length - 1; i >= 0; i--)
        {
            if (ascending[i] <= limit)
            {
                return ascending[i];
            }
        }

        return fallback;
    }

    /// <summary>
    /// How many columns fall outside the axis, and the furthest value among them (#209).
    /// </summary>
    /// <remarks>
    /// The other half of <see cref="TypicalRange"/>'s bargain. Leaving a reading off the axis is
    /// only defensible if the chart says it did — a caption naming the count and the extreme keeps
    /// the excursion discoverable, where silently rescaling around it would not.
    /// </remarks>
    public static (int Count, double? Extreme) Outside(
        IReadOnlyList<TrendColumn> columns,
        double minimum,
        double maximum)
    {
        ArgumentNullException.ThrowIfNull(columns);

        int count = 0;
        double? extreme = null;

        foreach (TrendColumn column in columns)
        {
            double? worst =
                column.Maximum > maximum && (extreme is null || column.Maximum > extreme) ? column.Maximum
                : column.Minimum < minimum && (extreme is null || column.Minimum < extreme) ? column.Minimum
                : null;

            if (column.Minimum < minimum || column.Maximum > maximum)
            {
                count++;
            }

            if (worst is double value
                && (extreme is null || Math.Abs(value) > Math.Abs(extreme.Value)))
            {
                extreme = value;
            }
        }

        return (count, extreme);
    }

    /// <summary>
    /// What the chart says about readings the axis ignored, or null when it ignored none.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>"Impossible" is a claim, and the threshold is what earns it.</b> A reading is only left
    /// off the axis when it sits more than a hundred times the interquartile spread beyond the
    /// quartiles — two orders of magnitude outside everything else in the window. That is not an
    /// unusual measurement, it is not a measurement.
    /// </para>
    /// <para>
    /// <b>"Hidden from the chart" rather than "hidden".</b> The reading is still in the trend store
    /// and still in the CSV export; only the axis declines to be set by it. Saying so is the
    /// condition on framing this way at all — an excursion is the diagnostic content on a timing
    /// instrument, and a chart that quietly rescaled around one would be worse than an unreadable
    /// axis.
    /// </para>
    /// <para>
    /// <b>Scaled through <see cref="ReadoutFormatter.Seconds"/> when the quantity is time.</b>
    /// "3000000000 ns" is a number a machine writes; "3.0 s" is the same number said to a person,
    /// and it makes the point on its own — three seconds of 1 PPS error is self-evidently not a
    /// reading. §9.5.3's ns/µs/ms/s ladder already exists for exactly this and is reused rather
    /// than reinvented.
    /// </para>
    /// </remarks>
    /// <param name="count">How many readings the axis left out.</param>
    /// <param name="extreme">The furthest of them, in the chart's own units.</param>
    /// <param name="unit">The chart's unit. <c>ns</c> is scaled; anything else is shown as it is.</param>
    /// <param name="decimals">Decimals for a non-time unit, fixed per chart (§9.11 item 6).</param>
    public static string? DescribeOutside(int count, double? extreme, string unit, int decimals)
    {
        if (count <= 0 || extreme is not double furthest)
        {
            return null;
        }

        string value = string.Equals(unit, "ns", StringComparison.Ordinal)
            ? Say(ReadoutFormatter.Seconds(furthest / 1e9, 1))
            : $"{ReadoutFormatter.Format(furthest, decimals)} {unit}";

        return count == 1
            ? $"1 impossible reading hidden from the chart — {value}"
            : $"{count} impossible readings hidden from the chart — up to {value}";

        static string Say((string Value, string Unit) reading) =>
            string.IsNullOrEmpty(reading.Unit) ? reading.Value : $"{reading.Value} {reading.Unit}";
    }

    /// <summary>Widens a range to the next round step outside it, on both sides.</summary>
    /// <remarks>
    /// 1, 2, 2.5 or 5 times a power of ten, chosen so the range holds about four steps. That is the
    /// usual nice-number rule and it is here for one reason: the axis carries three labels, and
    /// <c>−16.8557</c> is not a label.
    /// </remarks>
    private static (double Minimum, double Maximum) SnapOutward(double lower, double upper)
    {
        double span = upper - lower;

        if (span <= 0 || double.IsNaN(span) || double.IsInfinity(span))
        {
            return (lower, upper);
        }

        double rough = span / 4;
        double magnitude = Math.Pow(10, Math.Floor(Math.Log10(rough)));
        double step = (rough / magnitude) switch
        {
            <= 1 => 1,
            <= 2 => 2,
            <= 2.5 => 2.5,
            <= 5 => 5,
            _ => 10,
        } * magnitude;

        return (Math.Floor(lower / step) * step, Math.Ceiling(upper / step) * step);
    }

    /// <summary>
    /// Where to put a horizontal stroke's centre so it lands on whole device pixels (#233).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A 1 px rule drawn at a fractional Y is not a 1 px rule.</b> It straddles two device pixel
    /// rows and the compositor renders each at roughly half intensity — so the line is twice as
    /// tall and half as dark as it was asked to be. Measured under High Contrast White, the trend
    /// chart's zero rule came out at <b>2.66 : 1</b> against §9.4.5's 3:1 floor for chart lines,
    /// while its brush, <c>WzStrokeDefaultBrush</c>, is <c>#3D3D3D</c> — 10.43 : 1. The token was
    /// never wrong; the rendering was.
    /// </para>
    /// <para>
    /// <b>No gate can see that</b>, which is why it survived: <c>Test-ContrastFloor.ps1</c> computes
    /// token pairs and would report 10.43, correct about the colour and silent about the line.
    /// </para>
    /// <para>
    /// The rule is the ordinary one for hairlines. A stroke covers whole pixels when its centre sits
    /// on a half-pixel boundary for an <b>odd</b> device thickness, and on a whole-pixel boundary for
    /// an <b>even</b> one. At 100 % scaling a 1 DIP stroke is one device pixel and wants y.5; at
    /// 200 % it is two and wants a whole number. Getting that backwards moves the blur rather than
    /// removing it, which is why the thickness is a parameter and not an assumption.
    /// </para>
    /// </remarks>
    /// <param name="y">The intended centre, in DIPs.</param>
    /// <param name="thickness">Stroke thickness in DIPs.</param>
    /// <param name="scale">
    /// <c>XamlRoot.RasterizationScale</c>: 1.0 at 100 % display scaling, 1.5 at 150 %. Values that
    /// are not positive and finite fall back to 1, because a snapped-to-nonsense line is worse than
    /// an unsnapped one.
    /// </param>
    public static double SnapStrokeCentre(double y, double thickness, double scale)
    {
        if (double.IsNaN(y) || double.IsInfinity(y))
        {
            return y;
        }

        double s = double.IsFinite(scale) && scale > 0 ? scale : 1.0;

        double deviceY = y * s;
        double deviceThickness = thickness * s;

        // Odd device thickness straddles a pixel centre; even lands on a boundary.
        bool odd = (int)Math.Round(deviceThickness) % 2 != 0;

        double snappedDevice = odd
            ? Math.Floor(deviceY) + 0.5
            : Math.Round(deviceY);

        return snappedDevice / s;
    }
}

/// <summary>What a <c>TrendChart</c>'s y-axis is framed on.</summary>
/// <remarks>
/// Not a styling choice. §10.7.1 separates the two charts on exactly this ground — <i>"0 ns and
/// 0 % are not the same zero"</i> — and §9.4.4's diverging fill is only meaningful when zero is on
/// the axis, so the fill follows from this rather than being set alongside it.
/// </remarks>
public enum TrendAnchoring
{
    /// <summary>
    /// Symmetric about zero, which stays at the centre of the plot. §9.10.2's rule for the 1 PPS
    /// time interval, where zero is the receiver being on time and the colour break must not drift
    /// with the data.
    /// </summary>
    Zero = 0,

    /// <summary>
    /// Framed on the window's own data, subject to a minimum span. For a quantity whose zero
    /// carries no meaning.
    /// </summary>
    Data,

}