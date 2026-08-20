using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §10.14's leap-second read: which queries may be asked, and when.
/// </summary>
/// <remarks>
/// The rule this pins is not about formatting. Two of the four `:PTIM:LEAP:` queries are rejected
/// with <c>E-230</c> when nothing is announced, so asking them unconditionally would put two errors
/// in the receiver's error queue every time the page opened — and they would then surface on the
/// Diagnostics page as if something had gone wrong. Since the failure is on the *receiver* rather
/// than in the application, no unit test would catch it after the fact; the decision is therefore
/// held in a type that can be asserted without one.
/// </remarks>
public sealed class LeapSecondQueryTests
{
    // ------------------------------------------------------------------- what may be asked, when

    [Fact]
    public void NothingAnnouncedMeansTheDateAndDirectionAreNotAsked() =>
        Assert.False(LeapSecondQueries.NeedsAnnouncementDetail(LeapSecondPending.None));

    [Theory]
    [InlineData(LeapSecondPending.Plus)]
    [InlineData(LeapSecondPending.Minus)]
    public void AnAnnouncementMeansTheyAre(LeapSecondPending pending) =>
        Assert.True(LeapSecondQueries.NeedsAnnouncementDetail(pending));

    /// <summary>
    /// The bench receiver on 20 Aug 2026: <c>STAT?</c> answered <c>0</c>, and on that answer the
    /// other two must not be asked.
    /// </summary>
    [Fact]
    public void TheBenchReceiversAnswerDoesNotProvokeTheRejectedQueries()
    {
        LeapSecondPending pending = LeapSecondQueries.Decode(status: 0, direction: null);

        Assert.Equal(LeapSecondPending.None, pending);
        Assert.False(LeapSecondQueries.NeedsAnnouncementDetail(pending));
    }

    /// <summary>An unreadable status is treated as "none", which is the answer that asks nothing.</summary>
    [Fact]
    public void AnUnreadableStatusAsksNothingFurther()
    {
        LeapSecondPending pending = LeapSecondQueries.Decode(status: null, direction: null);

        Assert.Equal(LeapSecondPending.None, pending);
        Assert.False(LeapSecondQueries.NeedsAnnouncementDetail(pending));
    }

    /// <summary>All four are in the catalog, so none of them routes around §8.1.</summary>
    [Theory]
    [InlineData(":PTIM:LEAP:ACC?")]
    [InlineData(":PTIM:LEAP:STAT?")]
    [InlineData(":PTIM:LEAP:DATE?")]
    [InlineData(":PTIM:LEAP:DUR?")]
    public void EveryQueryIsCatalogued(string mnemonic)
    {
        ScpiCommand command = Assert.IsType<ScpiCommand>(CommandCatalog.Find(mnemonic));

        Assert.Equal(SafetyTier.Safe, command.Tier);
        Assert.True(command.IsQuery);
    }

    /// <summary>
    /// And the two conditional ones say so in the catalog. A caller reading only the description
    /// would otherwise ask unconditionally, which is exactly the mistake this page avoids.
    /// </summary>
    [Theory]
    [InlineData(":PTIM:LEAP:DATE?")]
    [InlineData(":PTIM:LEAP:DUR?")]
    public void TheConditionalQueriesSaySoInTheCatalog(string mnemonic) =>
        Assert.Contains(
            "only while one is announced",
            CommandCatalog.Find(mnemonic)!.Description,
            StringComparison.Ordinal);

    /// <summary>And the two that always answer do not carry that caveat.</summary>
    [Theory]
    [InlineData(":PTIM:LEAP:ACC?")]
    [InlineData(":PTIM:LEAP:STAT?")]
    public void TheUnconditionalQueriesDoNot(string mnemonic) =>
        Assert.DoesNotContain(
            "only while one is announced",
            CommandCatalog.Find(mnemonic)!.Description,
            StringComparison.Ordinal);

    // -------------------------------------------------------------------------- the direction

    [Fact]
    public void APositiveDirectionInsertsASecond() =>
        Assert.Equal(LeapSecondPending.Plus, LeapSecondQueries.Decode(status: 1, direction: 1));

    [Fact]
    public void ANegativeDirectionRemovesOne() =>
        Assert.Equal(LeapSecondPending.Minus, LeapSecondQueries.Decode(status: 1, direction: -1));

    /// <summary>
    /// An announcement whose direction could not be read is still an announcement. "A leap second is
    /// coming and I could not read which way" is a great deal more useful than silence, and the
    /// insert is the commoner case by a wide margin.
    /// </summary>
    [Fact]
    public void AnAnnouncementWithNoReadableDirectionIsStillReported() =>
        Assert.Equal(LeapSecondPending.Plus, LeapSecondQueries.Decode(status: 1, direction: null));

    // ------------------------------------------------------------------------------- the date

    [Fact]
    public void ADateIsReadAsYearMonthDay() =>
        Assert.Equal(new DateOnly(2026, 12, 31), LeapSecondQueries.ParseDate(["+2026,+12,+31"]));

    [Fact]
    public void ALeadingSpaceDoesNotStopIt() =>
        Assert.Equal(new DateOnly(2026, 6, 30), LeapSecondQueries.ParseDate([" 2026, 6, 30"]));

    [Theory]
    [InlineData("")]
    [InlineData("2026")]
    [InlineData("2026,12")]
    [InlineData("not,a,date")]
    public void SomethingThatIsNotADateBecomesNullRatherThanThrowing(string line) =>
        Assert.Null(LeapSecondQueries.ParseDate([line]));

    [Fact]
    public void NoLinesAtAllIsNullRatherThanAnException()
    {
        Assert.Null(LeapSecondQueries.ParseDate(null));
        Assert.Null(LeapSecondQueries.ParseDate([]));
    }

    /// <summary>
    /// §11.1: an impossible date is refused, not nudged. 31 June is as unreadable as month 13, and
    /// substituting a nearby day would invent a date the receiver never sent.
    /// </summary>
    [Theory]
    [InlineData("2026,6,31")]
    [InlineData("2026,13,1")]
    [InlineData("2026,0,1")]
    [InlineData("2026,2,30")]
    [InlineData("1970,12,31")]
    [InlineData("2300,12,31")]
    public void AnImpossibleDateIsRefusedRatherThanNudged(string line) =>
        Assert.Null(LeapSecondQueries.ParseDate([line]));

    /// <summary>A leap day is a real date and must survive the plausibility check.</summary>
    [Fact]
    public void TheTwentyNinthOfFebruaryInALeapYearSurvives() =>
        Assert.Equal(new DateOnly(2028, 2, 29), LeapSecondQueries.ParseDate(["2028,2,29"]));

    // ---------------------------------------------------------------------------- the reading

    [Fact]
    public void AnUnknownReadingCarriesNothing()
    {
        Assert.Null(LeapSecondReading.Unknown.AccumulatedSeconds);
        Assert.Null(LeapSecondReading.Unknown.AnnouncedDate);
        Assert.Null(LeapSecondReading.Unknown.Error);
        Assert.Equal(LeapSecondPending.None, LeapSecondReading.Unknown.Pending);
    }

    /// <summary>The bench receiver's whole answer, as a reading.</summary>
    [Fact]
    public void TheBenchReceiverReadsAsEighteenSecondsAndNoAnnouncement()
    {
        LeapSecondReading reading = new(
            AccumulatedSeconds: 18,
            Pending: LeapSecondQueries.Decode(status: 0, direction: null),
            AnnouncedDate: null,
            Error: null);

        Assert.Equal(18, reading.AccumulatedSeconds);
        Assert.Equal(LeapSecondPending.None, reading.Pending);
        Assert.Null(reading.Error);
    }
}
