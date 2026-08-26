using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// §10.7.1's oscillator-control axis, which is framed on its data rather than on zero (#183).
/// </summary>
/// <remarks>
/// The numbers below are not invented. They are the 22–24 Aug 2026 capture measured out of
/// <c>trend.db</c> and written into #183 — 71,067 rows, EFC
/// spanning −16.8557 … −16.8041 %, 280 distinct values. A bounds function held against a series
/// whose answer is known is the only kind worth having, and this one had shipped for weeks drawing
/// that series as a flat line.
/// </remarks>
public class TrendAutoBoundsTests
{
    /// <summary>The bench receiver's EFC extremes, as two columns of a decimated window.</summary>
    private static TrendColumn[] BenchWindow =>
    [
        new(0, -16.8557, -16.8301, 400),
        new(1, -16.8402, -16.8041, 400),
    ];

    // -------------------------------------------------------------------------------------
    // The defect
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// What #183 is. Zero-anchored with the shipped <c>Floor="25"</c>, the bench window gets a
    /// 50-percentage-point axis for 0.05 percentage points of data — the trace occupies about a
    /// thousandth of the plot height and reads as dead flat. Asserted here so the two are visible
    /// side by side, and so nobody re-anchors this axis on zero without seeing the number.
    /// </remarks>
    [Fact]
    public void ZeroAnchoringGivesTheBenchWindowAFiftyPointAxis()
    {
        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(BenchWindow, floor: 25);

        Assert.Equal(-25, minimum);
        Assert.Equal(25, maximum);

        double dataSpan = -16.8041 - -16.8557;
        Assert.True((dataSpan / (maximum - minimum)) < 0.002, "the data should occupy under 0.2% of the axis");
    }

    /// <summary>The same window, framed on itself.</summary>
    /// <remarks>
    /// Snapped outward to a step of 0.02, so the three labels §9.1 allows read −16.86, −16.83 and
    /// −16.80 rather than −16.8557 and −16.8041. The data now fills most of the plot, which is the
    /// whole point.
    /// </remarks>
    [Fact]
    public void TheBenchReceiversFortySevenHourWindowFillsThePlot()
    {
        (double minimum, double maximum) = TrendDecimation.AutoBounds(BenchWindow, minimumSpan: 0.01);

        Assert.Equal(-16.86, minimum, 10);
        Assert.Equal(-16.80, maximum, 10);

        double dataSpan = -16.8041 - -16.8557;
        Assert.True((dataSpan / (maximum - minimum)) > 0.8, "the data should fill most of the axis");
    }

    /// <summary>And the midpoint label is a round number, because the bounds were snapped.</summary>
    [Fact]
    public void TheMidpointOfTheBenchWindowIsARoundNumber()
    {
        (double minimum, double maximum) = TrendDecimation.AutoBounds(BenchWindow, minimumSpan: 0.01);

        Assert.Equal(-16.83, (minimum + maximum) / 2, 10);
    }

    // -------------------------------------------------------------------------------------
    // The other failure, which is why there is a minimum span at all
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// A converter dithering between two adjacent codes. Framed tightly this would be magnified
    /// until its least significant bit filled the plot, and a rock-steady oscillator would draw
    /// exactly like a drifting one. The minimum span is the same guard the medallion ring's
    /// ±50 ns floor provides (§9.10.2).
    /// </remarks>
    [Fact]
    public void ASeriesQuieterThanTheMinimumSpanIsNotMagnified()
    {
        TrendColumn[] dithering = [new(0, -16.8302, -16.8300, 900)];

        (double minimum, double maximum) = TrendDecimation.AutoBounds(dithering, minimumSpan: 0.01);

        Assert.True(maximum - minimum >= 0.01, "the axis must not be narrower than the minimum span");
    }

    /// <summary>A dead-flat series is a range of nothing, and still gets an axis.</summary>
    [Fact]
    public void AFlatSeriesStillGetsAnAxis()
    {
        TrendColumn[] flat = [new(0, -16.83, -16.83, 900), new(1, -16.83, -16.83, 900)];

        (double minimum, double maximum) = TrendDecimation.AutoBounds(flat, minimumSpan: 0.01);

        Assert.True(maximum - minimum >= 0.01);
        Assert.True(minimum < -16.83 && maximum > -16.83, "the value should sit inside the axis");
    }

    /// <summary>No data is not a crash, and not an axis labelled with infinities.</summary>
    [Fact]
    public void AnEmptyWindowIsCentredOnZero()
    {
        (double minimum, double maximum) = TrendDecimation.AutoBounds([], minimumSpan: 0.01);

        Assert.Equal(-0.005, minimum, 10);
        Assert.Equal(0.005, maximum, 10);
    }

    // -------------------------------------------------------------------------------------
    // Framing
    // -------------------------------------------------------------------------------------

    /// <summary>The bounds always contain the data, whatever the snapping does.</summary>
    [Theory]
    [InlineData(-16.8557, -16.8041)]
    [InlineData(0.0, 1.0)]
    [InlineData(-100.0, 100.0)]
    [InlineData(3.7, 3.9)]
    [InlineData(-0.0004, 0.0004)]
    [InlineData(12345.6, 12345.9)]
    public void TheBoundsAlwaysContainTheData(double low, double high)
    {
        (double minimum, double maximum) = TrendDecimation.AutoBounds(
            [new(0, low, high, 10)], minimumSpan: 0.01);

        Assert.True(minimum <= low, $"{minimum} should be at or below {low}");
        Assert.True(maximum >= high, $"{maximum} should be at or above {high}");
    }

    /// <remarks>
    /// A series that straddles zero is framed on itself like any other — this axis carries no
    /// opinion about zero, which is the difference between it and <c>ZeroAnchoredBounds</c>. It is
    /// not used for the 1 PPS chart, where §9.4.4 requires the opposite.
    /// </remarks>
    [Fact]
    public void ZeroIsNotSpecialToThisAxis()
    {
        (double minimum, double maximum) = TrendDecimation.AutoBounds(
            [new(0, 40.0, 60.0, 10)], minimumSpan: 0.01);

        Assert.True(minimum > 0, "the axis need not reach zero");
        Assert.True(minimum <= 40 && maximum >= 60);
    }

    /// <summary>Bounds land on multiples of the step they were snapped to.</summary>
    /// <remarks>
    /// The point of snapping: three labels a reader can subtract. −16.8557 is not a label.
    /// </remarks>
    [Fact]
    public void BoundsLandOnRoundNumbers()
    {
        (double minimum, double maximum) = TrendDecimation.AutoBounds(BenchWindow, minimumSpan: 0.01);

        Assert.Equal(0, Math.Round(minimum / 0.02) - (minimum / 0.02), 6);
        Assert.Equal(0, Math.Round(maximum / 0.02) - (maximum / 0.02), 6);
    }

    // -------------------------------------------------------------------------------------
    // Guards
    // -------------------------------------------------------------------------------------

    [Fact]
    public void TheColumnsAreRequired() =>
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = TrendDecimation.AutoBounds(null!, 1);
        });

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void AMinimumSpanOfNothingIsRejected(double span) =>
        Assert.Throws<ArgumentOutOfRangeException>(() =>
        {
            _ = TrendDecimation.AutoBounds([], span);
        });
}
