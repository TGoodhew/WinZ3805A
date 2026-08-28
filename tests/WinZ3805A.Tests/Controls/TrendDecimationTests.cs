using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// §9.10.2's decimation rule, which is why #38 chose a hand-rolled renderer.
/// </summary>
/// <remarks>
/// The rule is "min/max per pixel column, never by sampling", and the difference only shows on the
/// one sample that matters. A trace decimated by sampling looks perfectly good — smooth, plausible,
/// and missing the excursion the user opened the chart to find.
/// </remarks>
public sealed class TrendDecimationTests
{
    private const long Second = TimeSpan.TicksPerSecond;

    private static TrendSample[] Ramp(int count, double step = 1) =>
        [.. Enumerable.Range(0, count).Select(i => new TrendSample(i * Second, i * step))];

    [Fact]
    public void AnEmptySeriesProducesNoColumns() =>
        Assert.Empty(TrendDecimation.ToColumns([], 0, 100 * Second, 100));

    [Fact]
    public void OneSamplePerColumnIsUnchanged()
    {
        IReadOnlyList<TrendColumn> columns = TrendDecimation.ToColumns(Ramp(10), 0, 10 * Second, 10);

        Assert.Equal(10, columns.Count);
        Assert.All(columns, column => Assert.Equal(1, column.Count));
        Assert.Equal(0, columns[0].Minimum);
        Assert.Equal(9, columns[^1].Maximum);
    }

    /// <summary>
    /// The whole point. A single-sample spike among five hundred flat readings must survive into
    /// its column's maximum; sampling would keep it with probability 1/500.
    /// </summary>
    [Fact]
    public void AOneSampleExcursionSurvivesDecimation()
    {
        TrendSample[] samples = new TrendSample[500_000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new TrendSample(i * Second, 0);
        }

        samples[271_828] = new TrendSample(271_828L * Second, 250);

        IReadOnlyList<TrendColumn> columns =
            TrendDecimation.ToColumns(samples, 0, samples.Length * Second, 1200);

        Assert.Contains(columns, column => column.Maximum == 250);
    }

    /// <summary>And a negative one, which a max-only implementation would lose.</summary>
    [Fact]
    public void AOneSampleNegativeExcursionSurvivesToo()
    {
        TrendSample[] samples = new TrendSample[100_000];
        for (int i = 0; i < samples.Length; i++)
        {
            samples[i] = new TrendSample(i * Second, 5);
        }

        samples[54_321] = new TrendSample(54_321L * Second, -180);

        IReadOnlyList<TrendColumn> columns =
            TrendDecimation.ToColumns(samples, 0, samples.Length * Second, 800);

        Assert.Contains(columns, column => column.Minimum == -180);
    }

    [Fact]
    public void ColumnsAreNeverMoreNumerousThanThePlotIsWide()
    {
        IReadOnlyList<TrendColumn> columns =
            TrendDecimation.ToColumns(Ramp(604_800), 0, 604_800L * Second, 1000);

        Assert.True(columns.Count <= 1000);
        Assert.Equal(1000, columns.Count);
    }

    /// <summary>
    /// §12 budgets the buffer at 7 days of 1 s samples. The cost of drawing must depend on the
    /// plot's width, not on how much history is behind it.
    /// </summary>
    [Fact]
    public void SevenDaysOfSecondsDecimatesToThePlotWidth()
    {
        TrendSample[] week = Ramp(604_800);

        IReadOnlyList<TrendColumn> columns =
            TrendDecimation.ToColumns(week, 0, 604_800L * Second, 1200);

        Assert.Equal(1200, columns.Count);
        Assert.All(columns, column => Assert.True(column.Count >= 500));

        // Both extremes of the whole week are still on the chart.
        Assert.Equal(0, columns[0].Minimum);
        Assert.Equal(604_799, columns[^1].Maximum);
    }

    [Fact]
    public void SamplesOutsideTheWindowAreIgnored()
    {
        TrendSample[] samples =
        [
            new(-50 * Second, 999),
            new(5 * Second, 1),
            new(500 * Second, 998),
        ];

        IReadOnlyList<TrendColumn> columns = TrendDecimation.ToColumns(samples, 0, 10 * Second, 10);

        TrendColumn only = Assert.Single(columns);
        Assert.Equal(1, only.Minimum);
        Assert.Equal(1, only.Maximum);
    }

    /// <summary>
    /// A gap is a fact about the record. Zero-filling it would invent a reading of 0 ns, which is
    /// a plausible value and therefore the worst possible thing to invent.
    /// </summary>
    [Fact]
    public void AGapInTheRecordProducesNoColumnRatherThanAZero()
    {
        TrendSample[] samples =
        [
            new(0, 10),
            new(9 * Second, 20),
        ];

        IReadOnlyList<TrendColumn> columns = TrendDecimation.ToColumns(samples, 0, 10 * Second, 10);

        Assert.Equal(2, columns.Count);
        Assert.Equal(0, columns[0].Column);
        Assert.Equal(9, columns[1].Column);
        Assert.DoesNotContain(columns, column => column.Minimum == 0 && column.Count == 0);
    }

    [Fact]
    public void TheRightEdgeLandsInTheLastColumnRatherThanPastIt()
    {
        TrendSample[] samples = [new(10 * Second, 7)];

        TrendColumn only = Assert.Single(TrendDecimation.ToColumns(samples, 0, 10 * Second, 10));

        Assert.Equal(9, only.Column);
    }

    [Fact]
    public void NotANumberIsSkippedRatherThanPoisoningItsColumn()
    {
        TrendSample[] samples =
        [
            new(0, 5),
            new(1 * Second, double.NaN),
            new(2 * Second, 9),
        ];

        IReadOnlyList<TrendColumn> columns = TrendDecimation.ToColumns(samples, 0, 3 * Second, 3);

        Assert.Equal(2, columns.Count);
        Assert.All(columns, column => Assert.False(double.IsNaN(column.Minimum)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(100, 50)]
    public void AWindowThatIsNotAWindowProducesNothing(long from, long to) =>
        Assert.Empty(TrendDecimation.ToColumns(Ramp(10), from, to, 100));

    // -------------------------------------------------------------------- zero-anchored bounds

    /// <summary>
    /// §9.4.4: the diverging fill's neutral midpoint maps to exactly 0 ns. A receiver holding
    /// steadily at −40 ns must be drawn below the line, not straddling it.
    /// </summary>
    [Fact]
    public void BoundsStayCentredOnZeroWhenEveryReadingIsNegative()
    {
        IReadOnlyList<TrendColumn> columns =
            TrendDecimation.ToColumns([new(0, -40), new(Second, -38)], 0, 2 * Second, 2);

        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(columns);

        Assert.Equal(-maximum, minimum);
        Assert.True(minimum < 0 && maximum > 0);
    }

    [Fact]
    public void BoundsExpandToHoldTheLargestExcursionEitherWay()
    {
        IReadOnlyList<TrendColumn> columns =
            TrendDecimation.ToColumns([new(0, -300), new(Second, 12)], 0, 2 * Second, 2);

        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(columns);

        Assert.Equal(-300, minimum);
        Assert.Equal(300, maximum);
    }

    /// <summary>
    /// The same reasoning as the medallion's ±50 ns floor: a nanosecond-quiet loop must not be
    /// magnified until its noise fills the plot and reads as an instrument in trouble.
    /// </summary>
    [Fact]
    public void AQuietLoopIsNotAmplifiedByTheFloor()
    {
        IReadOnlyList<TrendColumn> columns =
            TrendDecimation.ToColumns([new(0, -0.4), new(Second, 0.6)], 0, 2 * Second, 2);

        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(columns);

        Assert.Equal(-50, minimum);
        Assert.Equal(50, maximum);
    }

    [Fact]
    public void AnEmptyChartStillHasAnAxis()
    {
        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds([]);

        Assert.Equal(-50, minimum);
        Assert.Equal(50, maximum);
    }

    // ------------------------------------------------------------------------ state columns

    /// <summary>
    /// A state is not a number. Two states in one column cannot be averaged, so the honest
    /// reduction is the one that covered most of the column.
    /// </summary>
    [Fact]
    public void AColumnTakesTheStateThatCoveredMostOfIt()
    {
        TrendSample[] states =
        [
            new(0, 1), new(1 * Second, 1), new(2 * Second, 1),
            new(3 * Second, 2),
        ];

        (int column, int state) = Assert.Single(
            TrendDecimation.ToStateColumns(states, 0, 4 * Second, 1));

        Assert.Equal(0, column);
        Assert.Equal(1, state);
    }

    [Fact]
    public void EachColumnGetsItsOwnState()
    {
        TrendSample[] states = [new(0, 7), new(5 * Second, 9)];

        IReadOnlyList<(int Column, int State)> columns =
            TrendDecimation.ToStateColumns(states, 0, 10 * Second, 10);

        Assert.Equal(2, columns.Count);
        Assert.Equal((0, 7), columns[0]);
        Assert.Equal((5, 9), columns[1]);
    }

    /// <summary>
    /// Shading a gap would assert the receiver was locked while it was in fact unplugged, so an
    /// unrecorded column is absent rather than inheriting whatever preceded it.
    /// </summary>
    [Fact]
    public void AnUnrecordedColumnIsNotShadedWithTheStateBeforeIt()
    {
        TrendSample[] states = [new(0, 1), new(9 * Second, 1)];

        IReadOnlyList<(int Column, int State)> columns =
            TrendDecimation.ToStateColumns(states, 0, 10 * Second, 10);

        Assert.Equal(2, columns.Count);
        Assert.DoesNotContain(columns, entry => entry.Column is > 0 and < 9);
    }

    [Fact]
    public void NoStatesProducesNoShading() =>
        Assert.Empty(TrendDecimation.ToStateColumns([], 0, 10 * Second, 10));

    [Fact]
    public void StateColumnsNeverOutnumberThePlotWidth()
    {
        TrendSample[] states =
            [.. Enumerable.Range(0, 100_000).Select(i => new TrendSample(i * Second, i % 3))];

        Assert.True(TrendDecimation.ToStateColumns(states, 0, 100_000L * Second, 500).Count <= 500);
    }
    // -------------------------------------------------------------------------------------
    // SnapStrokeCentre (#233)
    // -------------------------------------------------------------------------------------

    /// <summary>A one-pixel rule lands on one pixel, not across two.</summary>
    /// <remarks>
    /// <b>This is what #233 was.</b> Drawn at a fractional Y a 1 px stroke straddles two device
    /// rows and each renders at about half intensity, so the trend chart's zero rule measured
    /// 2.66 : 1 under High Contrast White against §9.4.5's 3 : 1 floor — while its brush is
    /// 10.43 : 1. At 100 % scaling a 1 DIP stroke is one device pixel and its centre belongs on a
    /// half-pixel boundary.
    /// </remarks>
    [Theory]
    [InlineData(100.0, 100.5)]
    [InlineData(100.4, 100.5)]
    [InlineData(100.5, 100.5)]
    [InlineData(100.9, 100.5)]
    [InlineData(101.0, 101.5)]
    public void AHairlineIsCentredOnAHalfPixelAtNormalScaling(double y, double expected) =>
        Assert.Equal(expected, TrendDecimation.SnapStrokeCentre(y, 1, 1.0), 6);

    /// <summary>An even device thickness wants a whole boundary, not a half one.</summary>
    /// <remarks>
    /// At 200 % a 1 DIP stroke is <b>two</b> device pixels, and two pixels centred on a half-pixel
    /// boundary straddle three rows. Getting this backwards moves the blur rather than removing it,
    /// which is why thickness and scale are both parameters instead of assumptions.
    /// </remarks>
    [Theory]
    [InlineData(100.1, 100.0)]   // device 200.2 -> 200, which is 100.0 DIPs
    [InlineData(100.3, 100.5)]   // device 200.6 -> 201, which is 100.5 DIPs
    public void AnEvenDeviceThicknessIsCentredOnAWholeBoundary(double y, double expected) =>
        Assert.Equal(expected, TrendDecimation.SnapStrokeCentre(y, 1, 2.0), 6);

    /// <summary>A scale that cannot be believed leaves the line where it was asked for.</summary>
    /// <remarks>
    /// <c>UIElement.RasterizationScale</c> is 0 until the control is in a visual tree, and the
    /// first draw happens then. An unsnapped line is slightly soft; a line snapped by dividing by
    /// zero is not on the screen at all.
    /// </remarks>
    [Theory]
    [InlineData(0.0)]
    [InlineData(-1.0)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnusableScaleFallsBackToUnityRatherThanNonsense(double scale) =>
        Assert.Equal(100.5, TrendDecimation.SnapStrokeCentre(100.2, 1, scale), 6);

    /// <summary>A y that is not a number is returned untouched.</summary>
    [Fact]
    public void ANonFiniteYIsLeftAlone() =>
        Assert.True(double.IsNaN(TrendDecimation.SnapStrokeCentre(double.NaN, 1, 1.0)));

}
