using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The Time page's judgements.
/// </summary>
/// <remarks>
/// The page §10.2 requires and no §10.x section describes, so what is asserted here is what the
/// specification does define for this data: §7.4's week rollover, §11.2's time fields, and #95's
/// display zone.
/// </remarks>
public sealed class TimeViewModelTests
{
    private static readonly DateTimeOffset Captured = new(2026, 8, 13, 19, 0, 0, TimeSpan.Zero);

    private static TimeViewModel Connected(
        TimeScale scale = TimeScale.Utc,
        int rolloverEpochs = 0,
        LeapSecondPending leap = LeapSecondPending.None,
        DateTimeOffset? device = null,
        DateTimeOffset? corrected = null)
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);

        store.UpdateFull(new ReceiverStatus
        {
            TimeScale = scale,
            WeekRolloverEpochs = rolloverEpochs,
            LeapPending = leap,
            DeviceDateTime = device,
            CorrectedDateTime = corrected,
            CapturedAt = Captured,
        });

        return new TimeViewModel(store) { Connection = ConnectionStatus.Connected };
    }

    // ---- Week rollover -----------------------------------------------------------------------

    /// <remarks>
    /// §7.4: the corrected date is shown and the raw one stays available, because "a user who sees
    /// the wrong year and no explanation reasonably concludes the receiver has failed". Both are on
    /// the page at once.
    /// </remarks>
    [Fact]
    public void ACorrectedDateKeepsTheReceiversOwnAlongsideIt()
    {
        TimeViewModel model = Connected(
            rolloverEpochs: 1,
            device: new DateTimeOffset(2006, 12, 29, 3, 38, 42, TimeSpan.Zero),
            corrected: new DateTimeOffset(2026, 8, 13, 3, 38, 42, TimeSpan.Zero));

        Assert.True(model.IsDateCorrected);
        Assert.Contains("2026", model.ShownTimeText);
        Assert.Contains("2006", model.DeviceTimeText);
    }

    /// <remarks>
    /// The explanation names the arithmetic. One epoch is 1024 weeks, and the ten-bit week number
    /// is why — a reader checking a suspect date should not have to look that up.
    /// </remarks>
    [Fact]
    public void TheRolloverTextExplainsTheArithmetic()
    {
        TimeViewModel model = Connected(rolloverEpochs: 1);

        Assert.Contains("1 epoch", model.RolloverText);
        Assert.Contains("1024", model.RolloverText);
        Assert.Contains("ten bits", model.RolloverText);
    }

    [Fact]
    public void TwoEpochsArePluralAndDoubled()
    {
        TimeViewModel model = Connected(rolloverEpochs: 2);

        Assert.Contains("2 epochs", model.RolloverText);
        Assert.Contains("2048 weeks", model.RolloverText);

        // "1 epoch of 1024 weeks (1024 weeks)" is the same number twice, so the total is only
        // stated when it differs from the epoch length.
        Assert.DoesNotContain("(1024 weeks)", Connected(rolloverEpochs: 1).RolloverText);
    }

    /// <remarks>
    /// Informational rather than a caution. A corrected date is the app working as designed, and
    /// §9.4.3's severities describe the receiver's condition, not the app's own arithmetic.
    /// </remarks>
    [Fact]
    public void ACorrectionIsInformationalNotACaution()
    {
        Assert.Equal(Severity.Info, Connected(rolloverEpochs: 1).RolloverSeverity);
        Assert.Equal(Severity.Neutral, Connected(rolloverEpochs: 0).RolloverSeverity);
        Assert.Contains("unchanged", Connected(rolloverEpochs: 0).RolloverText);
    }

    // ---- Time scale --------------------------------------------------------------------------

    [Theory]
    [InlineData(TimeScale.Utc, "UTC")]
    [InlineData(TimeScale.Gps, "GPS time")]
    [InlineData(TimeScale.Local, "Local time, derived from UTC")]
    [InlineData(TimeScale.LocalGps, "Local time, derived from GPS")]
    public void TheTimeScaleIsNamed(TimeScale scale, string expected) =>
        Assert.Equal(expected, Connected(scale).TimeScaleText);

    /// <remarks>
    /// GPS time runs ahead of UTC by the accumulated leap seconds. A user comparing a timestamp off
    /// this receiver against a UTC source needs to know, and this page is the only place that says.
    /// </remarks>
    [Fact]
    public void GpsTimeCarriesTheLeapSecondCaveat()
    {
        Assert.Contains("does not include leap seconds", Connected(TimeScale.Gps).TimeScaleNote);
        Assert.Contains("does not include leap seconds", Connected(TimeScale.LocalGps).TimeScaleNote);
        Assert.Null(Connected(TimeScale.Utc).TimeScaleNote);
    }

    // ---- Leap seconds ------------------------------------------------------------------------

    /// <remarks>
    /// A pending leap second is a caution rather than neutral: it is a step the 1 PPS will take
    /// that anything downstream counting seconds has to expect.
    /// </remarks>
    [Theory]
    [InlineData(LeapSecondPending.None, Severity.Neutral)]
    [InlineData(LeapSecondPending.Plus, Severity.Caution)]
    [InlineData(LeapSecondPending.Minus, Severity.Caution)]
    public void APendingLeapSecondIsACaution(LeapSecondPending pending, Severity expected) =>
        Assert.Equal(expected, Connected(leap: pending).LeapSeverity);

    /// <remarks>
    /// Inserted and removed are opposite events and must not share a wording — one adds a second to
    /// the minute and the other takes one away.
    /// </remarks>
    [Fact]
    public void InsertedAndRemovedAreDistinguished()
    {
        Assert.Contains("inserted", Connected(leap: LeapSecondPending.Plus).LeapPendingText);
        Assert.Contains("removed", Connected(leap: LeapSecondPending.Minus).LeapPendingText);
        Assert.Equal("None announced.", Connected(leap: LeapSecondPending.None).LeapPendingText);
    }

    // ---- Display zone ------------------------------------------------------------------------

    /// <remarks>
    /// #95: a time without a zone label invites the reader to assume it is theirs, and near local
    /// midnight the date is a whole day out if it is not.
    /// </remarks>
    [Fact]
    public void TheShownTimeAlwaysCarriesItsZoneLabel()
    {
        TimeViewModel model = Connected(
            TimeScale.Utc,
            device: new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));

        model.DisplayZone = TimeZoneInfo.Utc;

        Assert.NotNull(model.ShownTime);
        Assert.False(string.IsNullOrWhiteSpace(model.ShownTime.Value.ZoneLabel));
        Assert.Contains(model.ShownTime.Value.ZoneLabel, model.ShownTimeText);
    }

    [Fact]
    public void ChangingTheZoneChangesTheShownTime()
    {
        TimeViewModel model = Connected(
            TimeScale.Utc,
            device: new DateTimeOffset(2026, 8, 13, 12, 0, 0, TimeSpan.Zero));

        model.DisplayZone = TimeZoneInfo.Utc;
        string asUtc = model.ShownTimeText;

        TimeZoneInfo elsewhere = TimeZoneInfo.GetSystemTimeZones()
            .First(zone => zone.BaseUtcOffset != TimeSpan.Zero);
        model.DisplayZone = elsewhere;

        Assert.NotEqual(asUtc, model.ShownTimeText);
    }

    [Fact]
    public void DisconnectedEmptiesEverything()
    {
        TimeViewModel model = Connected(rolloverEpochs: 1, leap: LeapSecondPending.Plus);
        model.Connection = ConnectionStatus.Disconnected;

        Assert.Equal(TimeScale.Unknown, model.TimeScale);
        Assert.Equal(ReadoutFormatter.NoValue, model.TimeScaleText);
        Assert.Equal(ReadoutFormatter.NoValue, model.DeviceTimeText);
        Assert.Equal(ReadoutFormatter.NoValue, model.ShownTimeText);
        Assert.False(model.IsDateCorrected);
        Assert.Equal(LeapSecondPending.None, model.LeapPending);
    }

    /// <summary>
    /// The rollover explanation says the 1 PPS and the time of day are unaffected.
    /// </summary>
    /// <remarks>
    /// #10 names this as an acceptance criterion, and it is the one a user actually needs.
    /// Someone whose timing reference reports 2006 is not asking about ten-bit week
    /// numbers; they are asking whether the output they discipline to is wrong. The text
    /// explained the arithmetic and never answered that until this test was written.
    /// </remarks>
    [Fact]
    public void TheRolloverTextSaysWhatIsNotWrong()
    {
        TimeViewModel model = Connected(rolloverEpochs: 1);

        Assert.True(model.IsDateCorrected);
        Assert.Contains("1 PPS", model.RolloverText, StringComparison.Ordinal);
        Assert.Contains("unaffected", model.RolloverText, StringComparison.Ordinal);
    }
}
