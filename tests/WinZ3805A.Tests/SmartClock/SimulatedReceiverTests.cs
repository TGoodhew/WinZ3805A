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

        // For about a minute after the antenna goes it still says LOCK, tracking nothing; the time
        // interval answers for the first 18 seconds of that (2 Oct 2026).
        receiver.AntennaConnected = false;
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.Equal(("LOCK", 0), (receiver.SyncState, receiver.Tracked.Count));
        Assert.True(receiver.HasTimeInterval);
        clock.Advance(TimeSpan.FromSeconds(10));
        Assert.False(receiver.HasTimeInterval);
        Assert.Equal("LOCK", receiver.SyncState);

        // Then holdover, which :SYNC:STAT? calls WAIT, waiting for GPS.
        clock.Advance(receiver.Timing.CoastBeforeHoldover - TimeSpan.FromSeconds(20) + TimeSpan.FromSeconds(3));
        ScreenSnapshot holdover = receiver.Snapshot();
        Assert.Equal(("WAIT", "GPS", "GPS 1PPS invalid", 0, false), (receiver.SyncState, receiver.WaitingFor, holdover.ModeDetail, holdover.Tracked.Count, holdover.GpsOnePpsValid));
        Assert.Equal(TimeSpan.FromSeconds(3), holdover.HoldoverDuration);
        Assert.NotNull(holdover.PresentMicroseconds);
        Assert.Equal((72, 2), (receiver.OperationCondition, receiver.HoldoverCondition));

        // holdover-gps-1pps-invalid-3.txt: the antenna back and the satellites with it, the mode not
        // yet changed.
        receiver.AntennaConnected = true;
        clock.Advance(TimeSpan.FromSeconds(1));
        ScreenSnapshot signalBack = receiver.Snapshot();
        Assert.Equal("WAIT", receiver.SyncState);
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
        Assert.Equal(("HOLD", "NONE"), (receiver.SyncState, receiver.WaitingFor));
        Assert.Equal("manually initiated", receiver.Snapshot().ModeDetail);

        // GPS is still there, so the offset is still measured (2 Oct 2026).
        Assert.True(receiver.HasTimeInterval);
        Assert.Equal((88, 1), (receiver.OperationCondition, receiver.HoldoverCondition));

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
        Assert.Equal(receiver.Timing.Acquisition / 10, receiver.ReportedUtc - before);
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
        clock.Advance(receiver.Timing.CoastBeforeHoldover);
        _ = receiver.Mode;

        string log = engine.Log.ReadAll();
        Assert.Contains("Log 001:20070216.12:00:00:  GPS lock started", log, StringComparison.Ordinal);
        Assert.Contains("Log 002:20070216.12:00:54:  Holdover started, not tracking GPS", log, StringComparison.Ordinal);
    }

    [Fact]
    public void CancellingASurveyGoesBackToTheHeldPositionAndAdoptingHoldsItsEstimate()
    {
        // #633: the Position page's Cancel survey (:GPS:POS LAST) and Adopt computed position
        // (:GPS:POS SURV) did the same thing in the simulator, so section 6 could not tell them apart.
        (SimulatedReceiver cancelled, FakeTimeProvider clock) = Bench();
        double held = cancelled.HeldPosition.Height;
        clock.Advance(cancelled.Timing.Acquisition);
        Assert.True(cancelled.Surveying);
        Assert.Equal(held - 15.7, cancelled.Snapshot().Position.Height, 3);

        cancelled.CancelSurvey();
        Assert.False(cancelled.Surveying);
        Assert.Equal(held, cancelled.Snapshot().Position.Height, 3);

        (SimulatedReceiver adopted, FakeTimeProvider other) = Bench();
        other.Advance(adopted.Timing.Acquisition);
        adopted.AdoptSurvey();
        Assert.False(adopted.Surveying);
        Assert.Equal(held - 15.7, adopted.Snapshot().Position.Height, 3);
    }
}