using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// §10.8's manual-holdover guard, and the asymmetry that is the point of it.
/// </summary>
/// <remarks>
/// Forcing holdover inside the 24-hour SmartClock learning period corrupts the oscillator's
/// learning silently, so the guard's job is not to guess well — it is to never say "safe" without
/// grounds, and never say "too soon" without them either.
/// </remarks>
public class PowerUpGuardTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 13, 12, 0, 0, TimeSpan.Zero);

    private static (PowerUpGuard Guard, FakeTimeProvider Clock) Guard()
    {
        FakeTimeProvider clock = new(Start);
        return (new PowerUpGuard(clock), clock);
    }

    // -------------------------------------------------------------------------------------
    // Nothing observed
    // -------------------------------------------------------------------------------------

    /// <summary>A guard that has seen nothing knows nothing, and §10.8 says so out loud.</summary>
    [Fact]
    public void SaysNothingBeforeItHasSeenAnything()
    {
        (PowerUpGuard guard, _) = Guard();

        Assert.Null(guard.Elapsed);
        Assert.Equal(PowerUpSafety.Unknown, guard.Safety);
    }

    /// <summary>An unparsed mode line is not evidence that anything is running.</summary>
    [Fact]
    public void AnUnknownModeStartsNoWatch()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.Unknown);
        clock.Advance(TimeSpan.FromDays(2));

        Assert.Null(guard.Elapsed);
        Assert.Equal(PowerUpSafety.Unknown, guard.Safety);
    }

    // -------------------------------------------------------------------------------------
    // A lower bound — the app arrived after the receiver did
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// Watching a running receiver establishes a floor and nothing more: it may have been up for a
    /// year before the app started.
    /// </summary>
    [Fact]
    public void WatchingARunningReceiverGivesOnlyALowerBound()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.Locked);
        clock.Advance(TimeSpan.FromHours(3));

        Assert.True(guard.IsLowerBound);
        Assert.Equal(TimeSpan.FromHours(3), guard.Elapsed);
    }

    /// <summary>
    /// <b>The asymmetry.</b> Three hours of watching does not mean the receiver came up three hours
    /// ago, so it cannot mean "too soon" — it means the guard still does not know, and §10.8
    /// requires the extra acknowledgement for exactly this case.
    /// </summary>
    [Fact]
    public void AShortLowerBoundIsUnknownAndNeverTooSoon()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.Locked);
        clock.Advance(TimeSpan.FromHours(3));

        Assert.Equal(PowerUpSafety.Unknown, guard.Safety);
    }

    /// <summary>Past the threshold a floor is enough: at least 24 hours is 24 hours.</summary>
    [Fact]
    public void ALowerBoundPastTheThresholdIsEnoughToBeSafe()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.Locked);
        clock.Advance(TimeSpan.FromHours(25));

        Assert.Equal(PowerUpSafety.Safe, guard.Safety);
    }

    // -------------------------------------------------------------------------------------
    // An observed power-up — the app was there when it happened
    // -------------------------------------------------------------------------------------

    /// <summary>Only a watched power-up licenses the words "too soon".</summary>
    [Fact]
    public void AWatchedPowerUpGivesAnExactFigureAndCanSayTooSoon()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.PowerUp);
        clock.Advance(TimeSpan.FromHours(3));
        guard.Observe(SmartClockMode.Locked);

        Assert.False(guard.IsLowerBound);
        Assert.Equal(TimeSpan.FromHours(3), guard.Elapsed);
        Assert.Equal(PowerUpSafety.TooSoon, guard.Safety);
    }

    /// <summary>
    /// The receiver sits in power-up for minutes and is polled throughout. Re-stamping on each poll
    /// would hold the elapsed time near zero for the whole of it, and then report the end of warm-up
    /// as the moment power was applied.
    /// </summary>
    [Fact]
    public void TheFirstSightingOfPowerUpIsTheOneThatCounts()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.PowerUp);
        clock.Advance(TimeSpan.FromMinutes(10));
        guard.Observe(SmartClockMode.PowerUp);

        Assert.Equal(TimeSpan.FromMinutes(10), guard.Elapsed);
    }

    /// <summary>Once the learning period is behind it, a watched power-up reads safe.</summary>
    [Fact]
    public void AWatchedPowerUpBecomesSafeAfterTheLearningPeriod()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.PowerUp);
        clock.Advance(PowerUpGuard.LearningPeriod);

        Assert.Equal(PowerUpSafety.Safe, guard.Safety);
    }

    // -------------------------------------------------------------------------------------
    // Gaps
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A disconnect could have hidden a power cycle, so the floor it was building cannot survive it.
    /// This is the case the guard exists for: reconnecting to a receiver that rebooted while the
    /// app was away must not read as a day of continuous uptime.
    /// </summary>
    [Fact]
    public void ABrokenObservationDiscardsTheLowerBound()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.Locked);
        clock.Advance(TimeSpan.FromHours(30));
        Assert.Equal(PowerUpSafety.Safe, guard.Safety);

        guard.ObservationBroken();

        Assert.Null(guard.Elapsed);
        Assert.Equal(PowerUpSafety.Unknown, guard.Safety);
    }

    /// <summary>
    /// A watched power-up does survive a gap: a receiver that came up at a known instant did not
    /// un-come-up while the app was looking away.
    /// </summary>
    [Fact]
    public void ABrokenObservationKeepsAWatchedPowerUp()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.PowerUp);
        clock.Advance(TimeSpan.FromHours(2));
        guard.ObservationBroken();

        Assert.False(guard.IsLowerBound);
        Assert.Equal(TimeSpan.FromHours(2), guard.Elapsed);
        Assert.Equal(PowerUpSafety.TooSoon, guard.Safety);
    }

    /// <summary>After a gap the floor restarts from the next sighting, not from the old one.</summary>
    [Fact]
    public void TheLowerBoundRestartsFromTheNextSighting()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.Locked);
        clock.Advance(TimeSpan.FromHours(30));
        guard.ObservationBroken();

        guard.Observe(SmartClockMode.Locked);
        clock.Advance(TimeSpan.FromHours(1));

        Assert.Equal(TimeSpan.FromHours(1), guard.Elapsed);
        Assert.Equal(PowerUpSafety.Unknown, guard.Safety);
    }

    /// <summary>A guard pointed at a different receiver starts again from nothing.</summary>
    [Fact]
    public void ResetForgetsEverything()
    {
        (PowerUpGuard guard, FakeTimeProvider clock) = Guard();

        guard.Observe(SmartClockMode.PowerUp);
        clock.Advance(TimeSpan.FromHours(2));
        guard.Reset();

        Assert.Null(guard.Elapsed);
        Assert.Equal(PowerUpSafety.Unknown, guard.Safety);
    }

    /// <summary>§8.3's confirmation text quotes 24 hours, so the constant has to be 24 hours.</summary>
    [Fact]
    public void TheLearningPeriodIsTheTwentyFourHoursSection83Quotes() =>
        Assert.Equal(TimeSpan.FromHours(24), PowerUpGuard.LearningPeriod);
}
