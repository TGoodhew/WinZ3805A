namespace WinZ3805A.Controls;

/// <summary>
/// The scaling behind the medallion ring (§9.10.2), as pure arithmetic.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the control, and free of every WinUI type, so the one part of the ring that can
/// be quietly wrong is testable. A ring that draws is easy to see; a ring that draws at the wrong
/// scale looks entirely plausible and misleads exactly the user who trusts it most.
/// </para>
/// <para>
/// The ring is <b>qualitative by design</b>. It answers "is the loop calm or is it hunting" at a
/// glance and must never be read for values — the figure itself is set beside it in a
/// <c>WzReadout*</c> style wherever the readout row is on screen (Large on the main window, Medium
/// on Overview), and in the collapsed layouts the count takes the centre (#279). That is why the
/// scale adapts: absolute nanoseconds would make a well-behaved receiver draw a flat line forever
/// and a poor one clip.
/// </para>
/// </remarks>
public static class MedallionRingMath
{
    /// <summary>
    /// The smallest half-range the ring will ever use, in nanoseconds (§9.10.2).
    /// </summary>
    /// <remarks>
    /// This floor is the whole reason the ring is trustworthy. A receiver holding to a few
    /// nanoseconds has a standard deviation of almost nothing, and a purely relative scale would
    /// amplify that into a ring full of teeth — showing alarm where there is none. Below this
    /// threshold the ring simply goes quiet, which is the honest rendering of a calm loop.
    /// </remarks>
    public const double MinimumHalfRangeNanoseconds = 50d;

    /// <summary>How many standard deviations the ring spans before the floor takes over.</summary>
    public const double SigmaSpan = 3d;

    /// <summary>
    /// The half-range the ring should use for a window of samples: three sigma, or the floor,
    /// whichever is larger.
    /// </summary>
    /// <param name="samples">
    /// The window, oldest first. Nulls are gaps — polls that did not land — and are excluded from
    /// the statistics rather than counted as zero, which would drag sigma down and make a hunting
    /// loop look calmer than it is.
    /// </param>
    public static double HalfRange(IReadOnlyList<double?>? samples)
    {
        if (samples is null)
        {
            return MinimumHalfRangeNanoseconds;
        }

        double sum = 0;
        int count = 0;
        foreach (double? sample in samples)
        {
            if (sample is double value && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                sum += value;
                count++;
            }
        }

        // One sample has no spread, so there is nothing to scale to yet.
        if (count < 2)
        {
            return MinimumHalfRangeNanoseconds;
        }

        double mean = sum / count;
        double squares = 0;
        foreach (double? sample in samples)
        {
            if (sample is double value && !double.IsNaN(value) && !double.IsInfinity(value))
            {
                double delta = value - mean;
                squares += delta * delta;
            }
        }

        double sigma = Math.Sqrt(squares / count);
        return Math.Max(SigmaSpan * sigma, MinimumHalfRangeNanoseconds);
    }

    /// <summary>
    /// Maps one sample onto the ring, as a signed fraction of the ring's amplitude.
    /// </summary>
    /// <param name="sample">The sample, or <see langword="null"/> for a gap.</param>
    /// <param name="halfRange">The half-range from <see cref="HalfRange"/>.</param>
    /// <returns>
    /// −1 to +1, or <see langword="null"/> for a gap so the caller can leave a space rather than
    /// draw a zero. A gap and a reading of zero mean opposite things — "we did not hear" against
    /// "we heard, and it was perfect" — and drawing them alike would be a lie about the second.
    /// </returns>
    /// <remarks>
    /// Zero-anchored, not mean-anchored. The time interval is an error signal against GPS, so zero
    /// is the meaningful centre; centring on the window's own mean would hide a receiver sitting
    /// steadily 40 ns off, which is precisely the fault worth seeing.
    /// </remarks>
    public static double? Fraction(double? sample, double halfRange)
    {
        if (sample is not double value || double.IsNaN(value) || double.IsInfinity(value))
        {
            return null;
        }

        if (halfRange <= 0)
        {
            return 0;
        }

        return Math.Clamp(value / halfRange, -1d, 1d);
    }

    /// <summary>
    /// The radial extent of one sparkline mark: from the baseline outward for a positive reading,
    /// inward for a negative one, by the reading's share of the band (§9.10.2).
    /// </summary>
    /// <param name="fraction">The reading as <see cref="Fraction"/> maps it, −1 to +1.</param>
    /// <param name="midRadius">The radius of the ring's baseline.</param>
    /// <param name="band">The full radial depth the ring may occupy; a reading of ±1 reaches half of it.</param>
    /// <returns>The inner and outer radii of the mark. The outer is the smaller for a negative reading.</returns>
    /// <remarks>
    /// A reading of exactly zero still gets a one-pixel mark, outward, or a perfect loop would look
    /// like a dead one: the minimum tick is what distinguishes "on target" from "no data". A reading
    /// too small to reach a pixel is lifted to one on its own side for the same reason.
    /// </remarks>
    public static (double Inner, double Outer) SparklineMark(double fraction, double midRadius, double band)
    {
        double outer = midRadius + (fraction * band / 2);

        if (Math.Abs(outer - midRadius) < 1)
        {
            outer = midRadius + Math.Sign(fraction == 0 ? 1 : fraction);
        }

        return (midRadius, outer);
    }

    /// <summary>
    /// The radial extent of one mark on a uniform ring, which is what the compact medallion draws
    /// (§9.10.2, #307).
    /// </summary>
    /// <remarks>
    /// <para>
    /// At 64 px the sparkline's sixty marks of differing length make the circle read as lumpy rather
    /// than as a circle with a trace on it — and the circle being the one shape the eye finds without
    /// focusing is the whole reason §9.3 reserves it for the medallion. So compact gives the trace
    /// up: every mark is the same length, centred on the baseline, and the ring says "this is the
    /// medallion, in this state's colour" and nothing more. The reading is not lost; the figure
    /// itself is a Details page away, and §9.1 says the ring must never be read for values anyway.
    /// </para>
    /// <para>
    /// Half the band, symmetric: long enough to read as a dotted ring rather than a hairline, short
    /// enough that the marks stay inside the depth the sparkline would have used, so the two rings
    /// share an outline and switching between sizes does not change the medallion's silhouette.
    /// </para>
    /// </remarks>
    public static (double Inner, double Outer) UniformMark(double midRadius, double band) =>
        (midRadius - (band / 4), midRadius + (band / 4));

    /// <summary>The medallion glyph's font size for a given diameter (§10.3, #48).</summary>
    /// <remarks>
    /// <para>
    /// <b>§10.3's wireframe draws "glyph 56 px" inside a 160 px medallion.</b> The glyph carried no
    /// <c>FontSize</c> at all, so it inherited the body size and rendered about 12 px — a detail on
    /// the medallion rather than the medallion's own statement.
    /// </para>
    /// <para>
    /// In Light that was easy to miss, because the dotted ring carries the state and the glyph only
    /// confirms it. Under high contrast §9.10.2 collapses the ring to a plain stroke carrying no
    /// state, which leaves the glyph as the <b>only</b> non-textual carrier of severity — measured
    /// at 31 ink pixels inside a 186 px circle, 2.3 % of what the same medallion draws in Light
    /// (#48).
    /// </para>
    /// <para>
    /// A ratio rather than the literal 56, so the 64 and 96 px sizes §9.10.2 lists scale with it and
    /// a size added later cannot arrive without a glyph size.
    /// </para>
    /// </remarks>
    public static double GlyphSize(double diameter) => diameter * (56.0 / 160.0);

    /// <summary>
    /// The centre numeral's size for a medallion of this diameter (#279).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Larger than the glyph, on purpose.</b> A digit is a simpler shape than a symbol and can
    /// be read smaller, but this goes the other way because of what it is for: G1 asks for the
    /// count legible at <b>two metres</b>, and compact is the mode that promise is measured in. At
    /// the 64 px compact diameter this gives 32 px against the glyph's 22.
    /// </para>
    /// <para>
    /// <b>Half the diameter, not more, because two digits have to fit.</b> A count can reach twelve
    /// or more, and two lining figures at half the diameter occupy roughly two thirds of the
    /// circle's width - inside the ring with room to spare. A larger ratio reads better for one
    /// digit and clips for two.
    /// </para>
    /// </remarks>
    public static double CountSize(double diameter) => diameter * 0.5;

}
