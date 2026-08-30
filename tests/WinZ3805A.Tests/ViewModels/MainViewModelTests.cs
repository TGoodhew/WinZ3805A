using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Controls;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.3 state mapping, which P0-3 asks to be covered by view-model unit tests.
/// </summary>
public class MainViewModelTests
{

    /// <summary>The shipped driver, which owns §10.3's token table since #304.</summary>
    private static SmartClockDriver SmartClock() => new(TimeProvider.System);
    private static (MainViewModel Model, ReceiverStateStore Store, FakeTimeProvider Clock) Build()
    {
        FakeTimeProvider clock = new();
        ReceiverStateStore store = new(clock);
        MainViewModel model = new(store, clock, SmartClock()) { Connection = ConnectionStatus.Connected };
        return (model, store, clock);
    }

    // -------------------------------------------------------------------------------------
    // §10.3's seven states
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData("LOCK", ReceiverMode.Locked, "Locked to GPS")]
    [InlineData("REC", ReceiverMode.Recovering, "Recovering")]
    [InlineData("WAIT", ReceiverMode.Waiting, "Waiting to recover")]
    [InlineData("HOLD", ReceiverMode.Holdover, "Holdover")]
    [InlineData("POW", ReceiverMode.PowerUp, "Power-up")]
    [InlineData("OFF", ReceiverMode.Off, "Diagnostic / off")]
    public void EachSynchronisationStateMapsToItsModeAndText(string keyword, ReceiverMode mode, string text)
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFast(keyword, 3, 1, -5.4, -16.8, 6);

        Assert.Equal(mode, model.Mode);
        Assert.Equal(text, model.ModeText);
    }

    /// <summary>
    /// The seventh state. A mode is a claim about *now*, so it must not outlive the link that
    /// justified it — even though the readings themselves stay on screen and go stale honestly.
    /// </summary>
    [Fact]
    public void LosingTheLinkReportsDisconnectedWhileTheReadingsRemain()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();
        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 6);
        Assert.Equal(ReceiverMode.Locked, model.Mode);

        model.Connection = ConnectionStatus.Faulted;

        Assert.Equal(ReceiverMode.Disconnected, model.Mode);
        Assert.Equal("Disconnected", model.ModeText);
        Assert.Equal("Connection lost", model.ModeDetail);

        // §9.11: the readings are kept, not blanked. Their age is what tells the truth about them.
        Assert.Equal(6, model.SatelliteCount);
        Assert.Equal(-5.4, model.TimeIntervalNanoseconds);
    }

    [Theory]
    [InlineData(ConnectionStatus.Connecting, "Connecting")]
    [InlineData(ConnectionStatus.Reconnecting, "Reconnecting")]
    [InlineData(ConnectionStatus.Faulted, "Connection lost")]
    public void TheSubLineCarriesTheConnectionStateWhenThereIsNoLink(ConnectionStatus status, string expected)
    {
        (MainViewModel model, _, _) = Build();

        model.Connection = status;

        Assert.Equal(expected, model.ModeDetail);
    }

    // -------------------------------------------------------------------------------------
    // The coasting diagnostic (§10.3)
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// §10.3 calls this the single most useful diagnostic the application surfaces. It appears on
    /// real units with antenna or bias-tee faults: the receiver claims lock while verifying nothing,
    /// and every other indicator still says all is well.
    /// </summary>
    [Fact]
    public void LockedWithNoSatellitesIsReportedAsCoasting()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 0);

        Assert.True(model.IsCoasting);
        Assert.Contains("coasting", model.CoastingTooltip, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("LOCK", 1)]
    [InlineData("HOLD", 0)]
    [InlineData("REC", 0)]
    public void CoastingIsNotClaimedWhenItWouldBeWrong(string keyword, int satellites)
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFast(keyword, 3, 1, -5.4, -16.8, satellites);

        Assert.False(model.IsCoasting);
    }

    /// <summary>Holdover with no satellites is ordinary, not a fault — that is what holdover means.</summary>
    [Fact]
    public void HoldoverWithNoSatellitesIsNotCoasting()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFast("HOLD", 5, 3, -120, -16.8, 0);

        Assert.Equal(ReceiverMode.Holdover, model.Mode);
        Assert.False(model.IsCoasting);
    }

    // -------------------------------------------------------------------------------------
    // Staleness (§9.11, §10.3)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void TheFooterEscalatesThroughTheStalenessThresholds()
    {
        (MainViewModel model, ReceiverStateStore store, FakeTimeProvider clock) = Build();
        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 6);

        Assert.Equal(Severity.Neutral, model.AgeSeverity);

        clock.Advance(TimeSpan.FromSeconds(20));
        model.RaiseAll();
        Assert.Equal(Severity.Caution, model.AgeSeverity);

        clock.Advance(TimeSpan.FromSeconds(50));
        model.RaiseAll();
        Assert.Equal(Severity.Critical, model.AgeSeverity);
    }

    /// <summary>
    /// A window that has never polled is not stale — nothing has gone off. Shouting about an alarm
    /// before the first reading would be crying wolf.
    /// </summary>
    [Fact]
    public void AWindowThatHasNeverPolledIsNotReportedAsStale()
    {
        (MainViewModel model, _, _) = Build();

        Assert.Null(model.Age);
        Assert.Equal(Severity.Neutral, model.AgeSeverity);
        Assert.Equal("never updated", model.AgeDescription);
    }

    [Theory]
    [InlineData(0, "updated just now")]
    [InlineData(1, "updated just now")]
    [InlineData(20, "updated 20 seconds ago")]
    [InlineData(75, "updated a minute ago")]
    [InlineData(600, "updated 10 minutes ago")]
    [InlineData(4000, "updated an hour ago")]
    [InlineData(20000, "updated 5 hours ago")]
    [InlineData(200000, "updated more than a day ago")]
    public void AgeIsDescribedInWordsAndGetsCoarserAsItAges(int seconds, string expected)
    {
        Assert.Equal(expected, Staleness.Describe(TimeSpan.FromSeconds(seconds)));
    }

    /// <summary>A clock that stepped backwards must not produce "updated in 3 seconds".</summary>
    [Fact]
    public void ANegativeAgeIsDescribedSafely()
    {
        Assert.Equal("updated just now", Staleness.Describe(TimeSpan.FromSeconds(-5)));
    }

    // -------------------------------------------------------------------------------------
    // The rollover badge (§7.4, P0-10)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ACorrectedDateIsFlaggedAndKeepsTheRawValueForTheTooltip()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFull(new Device.Models.ReceiverStatus
        {
            DeviceDateTime = new DateTimeOffset(2006, 12, 27, 14, 45, 2, TimeSpan.Zero),
            CorrectedDateTime = new DateTimeOffset(2026, 8, 12, 14, 45, 2, TimeSpan.Zero),
            WeekRolloverEpochs = 1,
        });

        Assert.True(model.IsDateCorrected);
        Assert.Equal(2026, model.DisplayTime!.Value.Year);
        Assert.Contains("2006", model.RawDeviceDate);
    }

    /// <summary>
    /// The badge explains the offset and says what is not wrong, not only what was reported.
    /// </summary>
    /// <remarks>
    /// #10 asks for three things and the badge carried one: the raw date. It now also
    /// explains the correction and states that the time of day and the 1 PPS are
    /// unaffected - which is the question a user actually has on seeing 2006 on a timing
    /// reference, and the one the arithmetic does not answer.
    /// </remarks>
    [Fact]
    public void TheBadgeExplainsTheOffsetAndWhatIsUnaffected()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFull(new Device.Models.ReceiverStatus
        {
            DeviceDateTime = new DateTimeOffset(2006, 12, 27, 14, 45, 2, TimeSpan.Zero),
            CorrectedDateTime = new DateTimeOffset(2026, 8, 12, 14, 45, 2, TimeSpan.Zero),
            WeekRolloverEpochs = 1,
        });

        string explanation = model.RolloverExplanation!;

        Assert.Contains("2006", explanation, StringComparison.Ordinal);
        Assert.Contains("1024 weeks", explanation, StringComparison.Ordinal);
        Assert.Contains("1 PPS", explanation, StringComparison.Ordinal);
        Assert.Contains("unaffected", explanation, StringComparison.Ordinal);
    }

    /// <summary>An uncorrected date has nothing to explain, so the badge says nothing.</summary>
    [Fact]
    public void AnUncorrectedDateHasNoExplanation()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFull(new Device.Models.ReceiverStatus
        {
            DeviceDateTime = new DateTimeOffset(2026, 8, 12, 14, 45, 2, TimeSpan.Zero),
            WeekRolloverEpochs = 0,
        });

        Assert.Null(model.RolloverExplanation);
    }

    [Fact]
    public void AnUncorrectedDateCarriesNoBadge()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();

        store.UpdateFull(new Device.Models.ReceiverStatus
        {
            DeviceDateTime = new DateTimeOffset(2026, 8, 12, 14, 45, 2, TimeSpan.Zero),
            CorrectedDateTime = new DateTimeOffset(2026, 8, 12, 14, 45, 2, TimeSpan.Zero),
            WeekRolloverEpochs = 0,
        });

        Assert.False(model.IsDateCorrected);
    }

    // -------------------------------------------------------------------------------------

    [Fact]
    public void AStoreChangeReachesTheWindow()
    {
        (MainViewModel model, ReceiverStateStore store, _) = Build();
        int notifications = 0;
        model.PropertyChanged += (_, _) => notifications++;

        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 6);

        Assert.True(notifications > 0);
    }

    [Fact]
    public void ConnectIsOfferedOnlyWhenThereIsNoLink()
    {
        (MainViewModel model, _, _) = Build();

        Assert.False(model.CanConnect);

        model.Connection = ConnectionStatus.Disconnected;
        Assert.True(model.CanConnect);

        model.Connection = ConnectionStatus.Faulted;
        Assert.True(model.CanConnect);
    }
}
