using WinZ3805A.Controls;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The two ways this application puts a span of time into words.
/// </summary>
/// <remarks>
/// They are deliberately different. <see cref="Staleness.Describe"/> is about a reading going out
/// of date and rounds hard, because the difference between 96 and 97 seconds does not change what
/// anyone does. <see cref="Staleness.DescribeDuration"/> is read against §10.8's 24-hour threshold,
/// where the difference between 23 and 25 hours decides whether a command is safe to send.
/// </remarks>
public class StalenessTests
{
    // -------------------------------------------------------------------------------------
    // §10.8's elapsed-time form
    // -------------------------------------------------------------------------------------

    /// <summary>The form §10.8's wireframe draws.</summary>
    [Fact]
    public void MatchesTheWireframesTwoUnitForm() =>
        Assert.Equal("6 d 14 h", Staleness.DescribeDuration(TimeSpan.FromDays(6) + TimeSpan.FromHours(14)));

    [Theory]
    [InlineData(0, 0, 0, 45, "45 s")]
    [InlineData(0, 0, 3, 0, "3 min")]
    [InlineData(0, 0, 59, 30, "59 min")]
    [InlineData(0, 2, 0, 0, "2 h")]
    [InlineData(0, 23, 59, 0, "23 h 59 min")]
    [InlineData(1, 0, 0, 0, "1 d")]
    [InlineData(3, 7, 42, 0, "3 d 7 h")]
    public void UsesTwoUnitsAndNeverThree(int days, int hours, int minutes, int seconds, string expected) =>
        Assert.Equal(expected, Staleness.DescribeDuration(new TimeSpan(days, hours, minutes, seconds)));

    /// <summary>
    /// A clock that stepped backwards reads as zero rather than as a negative age. The guard above
    /// this treats short as unsafe, so a negative must not sort as "large".
    /// </summary>
    [Fact]
    public void ANegativeSpanReadsAsZero() =>
        Assert.Equal("0 s", Staleness.DescribeDuration(TimeSpan.FromHours(-3)));

    /// <summary>
    /// The threshold §10.8 is read against falls inside the hours branch on one side and the days
    /// branch on the other, so it is worth pinning that both say something sensible.
    /// </summary>
    [Fact]
    public void ReadsSensiblyEitherSideOfTheLearningPeriod()
    {
        Assert.Equal("23 h 59 min", Staleness.DescribeDuration(TimeSpan.FromHours(24) - TimeSpan.FromMinutes(1)));
        Assert.Equal("1 d", Staleness.DescribeDuration(TimeSpan.FromHours(24)));
    }

    // -------------------------------------------------------------------------------------
    // The footer form, which is the one that rounds
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(null, "never updated")]
    [InlineData(0, "updated just now")]
    [InlineData(30, "updated 30 seconds ago")]
    [InlineData(90, "updated a minute ago")]
    public void TheFooterFormRoundsHard(int? seconds, string expected) =>
        Assert.Equal(expected, Staleness.Describe(seconds is int value ? TimeSpan.FromSeconds(value) : null));

    /// <summary>
    /// §10.3's thresholds: amber past 15 s, critical past 60 s. A fresh reading is neutral rather
    /// than a success — the footer's job is to report going stale, not to congratulate a poll.
    /// </summary>
    [Theory]
    [InlineData(5, Severity.Neutral)]
    [InlineData(20, Severity.Caution)]
    [InlineData(90, Severity.Critical)]
    public void SeverityFollowsTheSection103Thresholds(int seconds, Severity expected) =>
        Assert.Equal(expected, Staleness.SeverityOf(TimeSpan.FromSeconds(seconds)));
}
