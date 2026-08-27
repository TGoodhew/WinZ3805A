using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §10.7's σ, and the caption that says what it covers.
/// </summary>
/// <remarks>
/// The Timing page printed "σ over the last 13 s" beside a sentence blaming the absence of a trend
/// store, while reading twelve thousand rows out of that store to draw the charts immediately below
/// it. P1 persistence had arrived and the sentence had not changed. Found by looking at the page.
/// </remarks>
public class SampleDeviationTests
{
    // -------------------------------------------------------------------------------------
    // The arithmetic
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// Held against a hand-computed answer rather than against itself. For 2, 4, 4, 4, 5, 5, 7, 9
    /// the population deviation is exactly 2 and the sample deviation is √(32/7) ≈ 2.13809 — a
    /// textbook pair chosen because the two differ visibly, which is what makes this test able to
    /// tell them apart.
    /// </remarks>
    [Fact]
    public void ItIsTheSampleDeviationNotThePopulationOne()
    {
        double[] values = [2, 4, 4, 4, 5, 5, 7, 9];

        double? deviation = SampleDeviation.Of(values);

        Assert.NotNull(deviation);
        Assert.Equal(2.13809, deviation.Value, 5);
        Assert.NotEqual(2.0, deviation.Value, 5);
    }

    /// <summary>A steady receiver has no spread, and that is a number rather than a null.</summary>
    [Fact]
    public void AConstantSeriesHasNoDeviation()
    {
        double? deviation = SampleDeviation.Of([-33.1, -33.1, -33.1, -33.1]);

        Assert.NotNull(deviation);
        Assert.Equal(0, deviation.Value, 10);
    }

    /// <remarks>
    /// Two points define a line; a deviation from fewer than three is arithmetic without meaning,
    /// and returning a number for it would put one on the page.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void FewerThanThreeReadingsIsNotADeviation(int count) =>
        Assert.Null(SampleDeviation.Of([.. Enumerable.Repeat(1.0, count)]));

    [Fact]
    public void TheValuesAreRequired() =>
        Assert.Throws<ArgumentNullException>(() => SampleDeviation.Of(null!));

    // -------------------------------------------------------------------------------------
    // The caption, which is a claim about the data
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// <b>Named from the readings, not from the request.</b> The page asks the trend store for an
    /// hour; saying "σ over the last hour" would then be a claim about the receiver rather than
    /// about the data, because the application is not always running and an hour of wall clock
    /// routinely holds four minutes of readings.
    /// </remarks>
    [Fact]
    public void AnHourOfWallClockHoldingFourMinutesOfReadingsSaysFourMinutes()
    {
        string text = SampleDeviation.Describe(240, TimeSpan.FromMinutes(4));

        Assert.Contains("240 readings", text, StringComparison.Ordinal);
        Assert.Contains("4 minutes", text, StringComparison.Ordinal);
        Assert.DoesNotContain("hour", text, StringComparison.Ordinal);
    }

    /// <summary>A full hour says so, and carries the count that distinguishes it.</summary>
    [Fact]
    public void AFullHourIsSaidInHours()
    {
        string text = SampleDeviation.Describe(3_412, TimeSpan.FromMinutes(59.6));

        Assert.Contains("3,412 readings", text, StringComparison.Ordinal);
        Assert.Contains("59 minutes", text, StringComparison.Ordinal);
    }

    /// <remarks>
    /// The count is given as well as the span because they answer different questions: a deviation
    /// over 3,000 readings spread across an hour and one over 12 is not the same figure, and the
    /// span alone cannot tell them apart.
    /// </remarks>
    [Fact]
    public void TheSameSpanWithDifferentCountsReadsDifferently()
    {
        string many = SampleDeviation.Describe(3_000, TimeSpan.FromMinutes(50));
        string few = SampleDeviation.Describe(12, TimeSpan.FromMinutes(50));

        Assert.NotEqual(many, few);
        Assert.Contains("3,000", many, StringComparison.Ordinal);
        Assert.Contains("12", few, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0, "no readings yet")]
    [InlineData(1, "one reading so far")]
    [InlineData(2, "2 readings so far")]
    public void TooFewToDescribeSaysHowManyThereAre(int count, string expected) =>
        Assert.Equal(expected, SampleDeviation.Describe(count, TimeSpan.FromSeconds(3)));

    /// <summary>Rounded down, never up — the same rule the drift card's span follows (#184).</summary>
    /// <remarks>
    /// A caption sitting beside a figure must not overstate the data behind it. 119 seconds is not
    /// two minutes, and 89 minutes is not an hour and a half.
    /// </remarks>
    [Theory]
    [InlineData(119, "119 seconds")]
    [InlineData(120, "2 minutes")]
    [InlineData(179, "2 minutes")]
    public void TheSpanIsRoundedDown(double seconds, string expected) =>
        Assert.Contains(expected, SampleDeviation.Describe(50, TimeSpan.FromSeconds(seconds)), StringComparison.Ordinal);
}
