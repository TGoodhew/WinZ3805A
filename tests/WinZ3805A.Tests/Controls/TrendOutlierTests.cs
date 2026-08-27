using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// What one aberrant reading may do to a week of chart (#209).
/// </summary>
/// <remarks>
/// The figures are measured, not invented. After a link misalignment on 24 Aug 2026 three impossible
/// values reached <c>trend.db</c>: time intervals of 2,000,000,000 and 3,000,000,000 ns, and an EFC
/// of +2 %. Against 12,488 samples spanning −76.5 to +21 ns, the 1 PPS axis became
/// <b>±3,000,000,000 ns</b> — the real trace occupying about two millionths of the plot height, on a
/// chart whose zero anchoring was never in question.
/// </remarks>
public class TrendOutlierTests
{
    /// <summary>A week of ordinary readings, one column per pixel, plus one impossible sample.</summary>
    private static TrendColumn[] WeekWithOneSpike(double spike)
    {
        TrendColumn[] columns = new TrendColumn[1200];

        for (int i = 0; i < columns.Length; i++)
        {
            // A calm receiver: tens of nanoseconds, wandering slowly.
            double centre = -30 + (20 * Math.Sin(i / 90.0));
            columns[i] = new TrendColumn(i, centre - 8, centre + 8, 10);
        }

        columns[640] = new TrendColumn(640, -30, spike, 10);
        return columns;
    }

    // -------------------------------------------------------------------------------------
    // The defect
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The 1 PPS chart, which is zero-anchored and correctly so. Anchoring was never the problem
    /// here — framing on the extremes was, and that is shared by both axis modes.
    /// </remarks>
    [Fact]
    public void OneImpossibleSampleNoLongerSetsTheAxisForAWeek()
    {
        TrendColumn[] week = WeekWithOneSpike(3_000_000_000);

        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(week, floor: 50);

        Assert.True(
            maximum < 1000,
            $"the axis should be framed on the readings, not on the spike; got ±{maximum}");

        // And the real data now occupies a usable share of it rather than two millionths.
        Assert.True((58.0 / (maximum - minimum)) > 0.3, "the trace should fill much of the plot");
    }

    /// <summary>The same for the data-framed axis.</summary>
    [Fact]
    public void TheDataFramedAxisIsAlsoProtected()
    {
        TrendColumn[] week = new TrendColumn[1200];

        for (int i = 0; i < week.Length; i++)
        {
            week[i] = new TrendColumn(i, -16.8557, -16.8041, 10);
        }

        week[700] = new TrendColumn(700, -16.85, 2, 10);

        (double minimum, double maximum) = TrendDecimation.AutoBounds(week, minimumSpan: 0.01);

        Assert.True(maximum < -16, $"the +2 % sample should not set the top of the axis; got {maximum}");
        Assert.True(minimum > -17, $"and the bottom should stay near the data; got {minimum}");
    }

    // -------------------------------------------------------------------------------------
    // What must still be visible
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// <b>The property that makes this safe.</b> An excursion is the diagnostic content on a timing
    /// instrument. A real one lasts minutes to hours and covers many columns; an aberrant reading
    /// covers exactly one. The rule has to tell them apart, or it is hiding the thing the chart
    /// exists to show.
    /// </remarks>
    [Fact]
    public void ARealExcursionIsNotTreatedAsAnOutlier()
    {
        TrendColumn[] week = WeekWithOneSpike(-30);

        // Forty columns of genuine excursion — an hour or two at the 7 d range.
        for (int i = 400; i < 440; i++)
        {
            week[i] = new TrendColumn(i, -900, -820, 10);
        }

        (double minimum, _) = TrendDecimation.ZeroAnchoredBounds(week, floor: 50);

        Assert.True(minimum <= -900, $"a sustained excursion must stay on the axis; got {minimum}");
    }

    /// <summary>Two aberrant columns are still excluded; the rule is not "exactly one".</summary>
    [Fact]
    public void MoreThanOneAberrantColumnIsStillExcluded()
    {
        TrendColumn[] week = WeekWithOneSpike(2_000_000_000);
        week[900] = new TrendColumn(900, -30, 3_000_000_000, 10);

        (_, double maximum) = TrendDecimation.ZeroAnchoredBounds(week, floor: 50);

        Assert.True(maximum < 1000, $"got ±{maximum}");
    }

    /// <summary>A short window excludes nothing, because a fraction of it is not a column.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(5)]
    [InlineData(11)]
    public void AShortWindowKeepsEveryReading(int count)
    {
        TrendColumn[] columns = new TrendColumn[count];

        for (int i = 0; i < count; i++)
        {
            columns[i] = new TrendColumn(i, -30, -20, 10);
        }

        columns[^1] = new TrendColumn(count - 1, -30, 5000, 10);

        (_, double maximum) = TrendDecimation.ZeroAnchoredBounds(columns, floor: 50);

        Assert.True(maximum >= 5000, $"nothing should be excluded from {count} column(s); got ±{maximum}");
    }

    /// <remarks>
    /// <b>The shape that defeated the first attempt, and the reason this test exists.</b> A seven-day
    /// window is mostly empty — the application is not running most of the week — so the trend held
    /// 12,333 readings in only <b>153 of 680 pixel columns</b>. The first rule dropped a fixed
    /// fraction of columns from each end, and 0.2 % of 153 rounds to zero: it excluded nothing at
    /// all on real data while passing against 1,200 dense synthetic ones.
    /// </remarks>
    [Fact]
    public void ASparseWeekIsFramedOnItsReadings()
    {
        // 153 populated columns, as measured from trend.db, with the sample that started #209.
        TrendColumn[] sparse = new TrendColumn[153];

        for (int i = 0; i < sparse.Length; i++)
        {
            double centre = -30 + (20 * Math.Sin(i / 11.0));
            sparse[i] = new TrendColumn(i * 4, centre - 8, centre + 8, 80);
        }

        sparse[97] = new TrendColumn(97 * 4, -30, 3_000_000_000, 80);

        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(sparse, floor: 50);

        Assert.True(maximum < 1000, $"the axis should ignore three seconds of time interval; got ±{maximum}");
        Assert.True(maximum >= 50, "and never fall below the floor");

        (int count, double? extreme) = TrendDecimation.Outside(sparse, minimum, maximum);
        Assert.Equal(1, count);
        Assert.Equal(3_000_000_000, extreme);
    }

    /// <summary>The same window without the impossible reading is framed on all of it.</summary>
    [Fact]
    public void ASparseWeekWithNothingWrongExcludesNothing()
    {
        TrendColumn[] sparse = new TrendColumn[153];

        for (int i = 0; i < sparse.Length; i++)
        {
            double centre = -30 + (20 * Math.Sin(i / 11.0));
            sparse[i] = new TrendColumn(i * 4, centre - 8, centre + 8, 80);
        }

        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(sparse, floor: 50);
        (int count, double? extreme) = TrendDecimation.Outside(sparse, minimum, maximum);

        Assert.Equal(0, count);
        Assert.Null(extreme);
    }

    // -------------------------------------------------------------------------------------
    // Saying so
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The condition on framing this way at all: leaving a reading off the axis is defensible only
    /// if the chart says it did. Silently rescaling around an excursion would be worse than the
    /// unreadable axis it replaced.
    /// </remarks>
    [Fact]
    public void WhatFallsOutsideIsCountedAndNamed()
    {
        TrendColumn[] week = WeekWithOneSpike(3_000_000_000);
        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(week, floor: 50);

        (int count, double? extreme) = TrendDecimation.Outside(week, minimum, maximum);

        Assert.Equal(1, count);
        Assert.Equal(3_000_000_000, extreme);
    }

    /// <summary>And nothing is claimed when nothing is outside.</summary>
    [Fact]
    public void AnOrdinaryWindowSaysNothing()
    {
        TrendColumn[] week = WeekWithOneSpike(-30);
        (double minimum, double maximum) = TrendDecimation.ZeroAnchoredBounds(week, floor: 50);

        (int count, double? extreme) = TrendDecimation.Outside(week, minimum, maximum);

        Assert.Equal(0, count);
        Assert.Null(extreme);
    }

    [Fact]
    public void TheColumnsAreRequired()
    {
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = TrendDecimation.TypicalRange(null!);
        });

        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = TrendDecimation.Outside(null!, 0, 1);
        });
    }
}
