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
}
