using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Simulation.SmartClock;

namespace WinZ3805A.Tests.SmartClock;

/// <summary>
/// The simulated receiver's timeline: the states the bench unit was captured in, in the order it
/// passed through them (#639).
/// </summary>
public sealed class SimulatedReceiverTests
{
    private static (SimulatedReceiver Receiver, FakeTimeProvider Clock) Bench()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        return (new SimulatedReceiver(clock), clock);
    }

    [Fact]
    public void APowerUpPassesThroughEveryCapturedStateToLock()
    {
        (SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        ReceiverTiming timing = receiver.Timing;

        ScreenSnapshot acquiring = receiver.Snapshot();
        Assert.Equal(("POW", "GPS acquisition", 0, 9, 3), (receiver.SyncState, acquiring.ModeDetail, acquiring.Tracked.Count, acquiring.Tfom, acquiring.Ffom));
        Assert.True(acquiring.TimeProvisional);
        Assert.False(receiver.HasTimeInterval);

        clock.Advance(timing.Acquisition);
        ScreenSnapshot fine = receiver.Snapshot();
        Assert.Equal(("POW", "fine freq adj"), (receiver.SyncState, fine.ModeDetail));
        Assert.NotNull(fine.ModeTimeIntervalNanoseconds);
        Assert.NotEmpty(fine.Tracked);

        clock.Advance(timing.FineFrequency);
        ScreenSnapshot stabilizing = receiver.Snapshot();
        Assert.Equal(("LOCK", "stabilizing frequency", 1, OutputsSummary.ValidReducedAccuracy), (receiver.SyncState, stabilizing.ModeDetail, stabilizing.Ffom, stabilizing.Outputs));
        Assert.True(receiver.HasTimeInterval);

        clock.Advance(timing.Stabilizing);
        ScreenSnapshot locked = receiver.Snapshot();
        Assert.Equal(("LOCK", (string?)null, 0, OutputsSummary.Valid), (receiver.SyncState, locked.ModeDetail, locked.Ffom, locked.Outputs));
    }

    [Fact]
    public void PullingTheAntennaCausesHoldoverAndReconnectingItRecovers()
    {
        (SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        receiver.StartLocked();

        receiver.AntennaConnected = false;
        clock.Advance(TimeSpan.FromSeconds(3));
        ScreenSnapshot holdover = receiver.Snapshot();
        Assert.Equal(("HOLD", "GPS 1PPS invalid", 0, false), (receiver.SyncState, holdover.ModeDetail, holdover.Tracked.Count, holdover.GpsOnePpsValid));
        Assert.Equal(TimeSpan.FromSeconds(3), holdover.HoldoverDuration);
        Assert.NotNull(holdover.PresentMicroseconds);

        // holdover-gps-1pps-invalid-3.txt: the antenna back and the satellites with it, the mode not
        // yet changed.
        receiver.AntennaConnected = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        ScreenSnapshot signalBack = receiver.Snapshot();
        Assert.Equal("HOLD", receiver.SyncState);
        Assert.True(signalBack.GpsOnePpsValid);

        clock.Advance(receiver.Timing.HoldoverRelease);
        ScreenSnapshot recovery = receiver.Snapshot();
        Assert.Equal(("REC", "fine freq adj"), (receiver.SyncState, recovery.ModeDetail));
        Assert.NotNull(recovery.HoldoverDuration);   // the duration keeps counting into recovery

        clock.Advance(receiver.Timing.Recovery);
        Assert.Equal("LOCK", receiver.SyncState);
        Assert.Null(receiver.HoldoverDuration);
        Assert.True(receiver.LastHoldover > TimeSpan.FromSeconds(90));
    }

    [Fact]
    public void AForcedHoldoverWaitsForAnExplicitRecovery()
    {
        (SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        receiver.StartLocked();

        receiver.ForceHoldover();
        clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("HOLD", receiver.SyncState);
        Assert.Equal("manually initiated", receiver.Snapshot().ModeDetail);

        receiver.Recover();
        Assert.Equal("REC", receiver.SyncState);
    }

    [Fact]
    public void SpeedRunsTheTimelineFasterButNotTheReportedClock()
    {
        (SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        receiver.Speed = 10;
        DateTime before = receiver.ReportedUtc;

        clock.Advance(receiver.Timing.Acquisition / 10);

        Assert.Equal("fine freq adj", receiver.Snapshot().ModeDetail);
        Assert.Equal(TimeSpan.FromSeconds(3), receiver.ReportedUtc - before);
    }

    [Fact]
    public void TheReportedDateIsOneRolloverBehind()
    {
        // The bench unit's dates are 1024 weeks behind (§7.4).
        (SimulatedReceiver receiver, _) = Bench();

        Assert.Equal(new DateTime(2007, 2, 16, 12, 0, 0), receiver.ReportedUtc);
    }

    [Fact]
    public void TheLogRecordsLockAndHoldover()
    {
        (SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        ScpiEngine engine = new(receiver, clock);

        receiver.StartLocked();
        receiver.AntennaConnected = false;
        clock.Advance(TimeSpan.FromSeconds(2));
        _ = receiver.Mode;

        string log = engine.Log.ReadAll();
        Assert.Contains("Log 001:20070216.12:00:00:  GPS lock started", log, StringComparison.Ordinal);
        Assert.Contains("Log 002:20070216.12:00:00:  Holdover started, not tracking GPS", log, StringComparison.Ordinal);
    }
}
