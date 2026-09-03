using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// #387's throttle: how often a trend redraw can show anything.
/// </summary>
/// <remarks>
/// The numbers here are the ones §10.4 and §10.7 actually offer — 1 h, 6 h, 24 h, 7 d — against a
/// chart about 700 px wide, because a rule that is only right for invented inputs is not a rule.
/// </remarks>
public sealed class TrendRefreshPolicyTests
{
    private const double TypicalChartWidth = 700;

    /// <summary>
    /// One column of the range, which is the whole idea: a redraw that cannot move a pixel is work
    /// with no output.
    /// </summary>
    [Theory]
    [InlineData(1, 5)]        // 1 h over 700 px: a column is 5.1 s
    [InlineData(6, 30)]       // 6 h: 30.8 s
    [InlineData(24, 120)]     // 24 h: 123 s, over the two-minute ceiling
    [InlineData(168, 120)]    // 7 d: 14.4 min, likewise
    public void TheIntervalIsOneColumnOfTheRange(int rangeHours, int expectedSeconds)
    {
        TimeSpan interval = TrendRefreshPolicy.MinimumInterval(
            TimeSpan.FromHours(rangeHours), TypicalChartWidth);

        Assert.InRange(interval.TotalSeconds, expectedSeconds - 1.5, expectedSeconds + 1.5);
    }

    /// <summary>The ceiling holds however long the range is.</summary>
    /// <remarks>
    /// 7 d over a narrow chart asks for half an hour, and a plot that has not moved in half an hour
    /// reads as broken. The ceiling is the answer to that, and it is still 120 times cheaper than
    /// the redraw-per-second this replaced.
    /// </remarks>
    [Fact]
    public void TheIntervalIsNeverLongerThanTwoMinutes()
    {
        TimeSpan interval = TrendRefreshPolicy.MinimumInterval(TimeSpan.FromDays(7), 300);

        Assert.Equal(TrendRefreshPolicy.LongestInterval, interval);
    }

    /// <summary>And never shorter than a second, which is the degenerate end.</summary>
    [Theory]
    [InlineData(0.05, 4000)]   // three minutes over a very wide chart
    [InlineData(1, 0)]         // not laid out yet
    [InlineData(1, -700)]      // nor negatively
    [InlineData(0, 700)]       // no range
    public void TheIntervalIsNeverShorterThanASecond(double rangeHours, double width)
    {
        TimeSpan interval = TrendRefreshPolicy.MinimumInterval(TimeSpan.FromHours(rangeHours), width);

        Assert.Equal(TrendRefreshPolicy.ShortestInterval, interval);
    }

    /// <summary>A NaN width is a chart mid-layout, not a reason to throw.</summary>
    [Fact]
    public void ANotANumberWidthIsTheShortestInterval()
    {
        Assert.Equal(
            TrendRefreshPolicy.ShortestInterval,
            TrendRefreshPolicy.MinimumInterval(TimeSpan.FromHours(6), double.NaN));
    }

    /// <summary>
    /// The first redraw always happens: an empty chart is exactly the case a throttle must not hold
    /// back.
    /// </summary>
    [Fact]
    public void TheFirstRedrawIsNeverThrottled()
    {
        Assert.True(TrendRefreshPolicy.ShouldRedraw(0, 1, TimeSpan.FromHours(6), TypicalChartWidth));
    }

    /// <summary>
    /// The case #385 was made of: readings arriving a second apart against a 6 h window.
    /// </summary>
    /// <remarks>
    /// Thirty of these answered true before this policy existed — one full store read and
    /// decimation each — and one answers true now.
    /// </remarks>
    [Fact]
    public void AReadingASecondIsThrottledToOneRedrawAColumn()
    {
        TimeSpan range = TimeSpan.FromHours(6);
        long start = DateTimeOffset.UtcNow.UtcTicks;
        long lastRendered = start;
        int redraws = 0;

        for (int second = 1; second <= 60; second++)
        {
            long now = start + TimeSpan.FromSeconds(second).Ticks;

            if (TrendRefreshPolicy.ShouldRedraw(lastRendered, now, range, TypicalChartWidth))
            {
                redraws++;
                lastRendered = now;
            }
        }

        Assert.Equal(1, redraws);
    }

    /// <summary>A clock that has gone backwards redraws rather than freezing.</summary>
    /// <remarks>
    /// The receiver's own date can be corrected by GPS, and a test pins the clock outright, so this
    /// is reachable. Freezing until real time catches up would be the worse failure: a chart that
    /// stops for no reason a user can see.
    /// </remarks>
    [Fact]
    public void AClockThatWentBackwardsRedraws()
    {
        long later = DateTimeOffset.UtcNow.UtcTicks;
        long earlier = later - TimeSpan.FromMinutes(5).Ticks;

        Assert.True(TrendRefreshPolicy.ShouldRedraw(later, earlier, TimeSpan.FromHours(6), TypicalChartWidth));
    }
}
