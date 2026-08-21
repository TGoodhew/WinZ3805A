using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// P1-9's restraint. Most of these assert that <b>nothing</b> is said, which is the feature.
/// </summary>
/// <remarks>
/// #57: "a notification on every state change would train the user to ignore them". The bench
/// receiver puts a number on it — 109 holdovers in 8.9 days, median outage 10.3 minutes — so the
/// policy is measured against that log rather than against an idea of how a receiver behaves.
/// </remarks>
public sealed class LockWatchTests
{
    private static (LockWatch Watch, FakeTimeProvider Clock) Watching()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 8, 20, 12, 0, 0, TimeSpan.Zero));
        return (new LockWatch(clock), clock);
    }

    // ---------------------------------------------------------------------- nothing to say

    [Fact]
    public void ALockedReceiverSaysNothing()
    {
        (LockWatch watch, _) = Watching();

        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Locked));
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Locked));
    }

    /// <summary>
    /// The case that decides whether this feature is usable: a receiver that drops out and comes
    /// straight back says nothing at all, ever.
    /// </summary>
    [Fact]
    public void AShortFlapIsNeverAnnounced()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);

        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Holdover));

        clock.Advance(TimeSpan.FromSeconds(20));
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Holdover));

        // Back before the grace period matured, so nothing was said and nothing is owed.
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Locked));
    }

    /// <summary>
    /// Nine flaps in a row, which is a quiet morning on this receiver, produce nothing.
    /// </summary>
    [Fact]
    public void AMorningOfFlappingProducesNoNotifications()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);

        for (int flap = 0; flap < 9; flap++)
        {
            Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Recovery));
            clock.Advance(TimeSpan.FromSeconds(30));
            Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Locked));
            clock.Advance(TimeSpan.FromMinutes(5));
        }
    }

    /// <summary>
    /// A missed or mangled sweep is not an outage. §11.1 says an unparseable field is null rather
    /// than a guess, and announcing a parsing hiccup as a receiver fault is the same mistake.
    /// </summary>
    [Fact]
    public void AnUnreadableSweepSaysNothingAndChangesNothing()
    {
        (LockWatch watch, _) = Watching();
        watch.Observe(SmartClockMode.Locked);

        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Unknown));
        Assert.Equal(SmartClockMode.Locked, watch.Mode);
    }

    /// <summary>And it does not stop a loss already being timed from maturing.</summary>
    [Fact]
    public void AnUnreadableSweepDoesNotRestartTheGracePeriod()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);
        watch.Observe(SmartClockMode.Holdover);

        clock.Advance(TimeSpan.FromSeconds(40));
        watch.Observe(SmartClockMode.Unknown);
        clock.Advance(TimeSpan.FromSeconds(40));

        Assert.Equal(LockAlert.Lost, watch.Observe(SmartClockMode.Holdover));
    }

    /// <summary>
    /// A receiver warming up has not lost anything — it has not acquired yet, which the user can
    /// see and did not need waking for.
    /// </summary>
    [Fact]
    public void PowerUpIsNotALoss()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();

        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.PowerUp));
        clock.Advance(TimeSpan.FromMinutes(30));
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.PowerUp));
    }

    // ------------------------------------------------------------------ when it does speak

    [Fact]
    public void ASustainedLossIsAnnouncedOnce()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);
        watch.Observe(SmartClockMode.Holdover);

        clock.Advance(LockWatch.Grace);

        Assert.Equal(LockAlert.Lost, watch.Observe(SmartClockMode.Holdover));

        // And not again, however long it lasts.
        clock.Advance(TimeSpan.FromHours(3));
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Holdover));
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Recovery));
    }

    /// <summary>
    /// The bench receiver's median outage is 10.3 minutes, which is well past the grace period —
    /// the policy stays silent for the flaps without going silent for the real thing.
    /// </summary>
    [Fact]
    public void TheMedianOutageOnThisReceiverIsAnnounced()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);
        watch.Observe(SmartClockMode.Recovery);

        clock.Advance(TimeSpan.FromMinutes(10.3));

        Assert.Equal(LockAlert.Lost, watch.Observe(SmartClockMode.Recovery));
    }

    /// <summary>Recovery is owed to a user who was told about the loss.</summary>
    [Fact]
    public void RecoveryIsAnnouncedWhenTheLossWas()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);
        watch.Observe(SmartClockMode.Holdover);
        clock.Advance(LockWatch.Grace);
        Assert.Equal(LockAlert.Lost, watch.Observe(SmartClockMode.Holdover));

        Assert.Equal(LockAlert.Regained, watch.Observe(SmartClockMode.Locked));

        // Once. Staying locked is not news.
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Locked));
    }

    /// <summary>
    /// And withheld from a user who was not. "It is back" about an event they were deliberately
    /// not told of is an alert with no referent.
    /// </summary>
    [Fact]
    public void RecoveryIsNotAnnouncedWhenTheLossWasNot()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);
        watch.Observe(SmartClockMode.Holdover);
        clock.Advance(TimeSpan.FromSeconds(10));

        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Locked));
    }

    /// <summary>
    /// One episode is at most two notifications, however much the mode flaps inside it. This is
    /// what bounds a bad night to two rather than to forty.
    /// </summary>
    [Fact]
    public void OneEpisodeIsAtMostTwoNotifications()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);

        List<LockAlert> raised = [];

        watch.Observe(SmartClockMode.Holdover);
        clock.Advance(LockWatch.Grace);

        foreach (SmartClockMode mode in new[]
        {
            SmartClockMode.Holdover, SmartClockMode.Recovery, SmartClockMode.Holdover,
            SmartClockMode.Recovery, SmartClockMode.Recovery, SmartClockMode.Locked,
        })
        {
            LockAlert alert = watch.Observe(mode);
            if (alert != LockAlert.None)
            {
                raised.Add(alert);
            }

            clock.Advance(TimeSpan.FromSeconds(10));
        }

        Assert.Equal([LockAlert.Lost, LockAlert.Regained], raised);
    }

    /// <summary>A disconnect drops a pending loss rather than letting it mature.</summary>
    /// <remarks>
    /// The receiver may be perfectly happy and the cable may be out. An alert about a receiver
    /// nobody is talking to says something the application cannot know.
    /// </remarks>
    [Fact]
    public void AResetDropsAPendingLoss()
    {
        (LockWatch watch, FakeTimeProvider clock) = Watching();
        watch.Observe(SmartClockMode.Locked);
        watch.Observe(SmartClockMode.Holdover);
        Assert.True(watch.IsWaitingOutGrace);

        watch.Reset();
        clock.Advance(TimeSpan.FromHours(1));

        Assert.False(watch.IsWaitingOutGrace);
        Assert.Equal(LockAlert.None, watch.Observe(SmartClockMode.Holdover));
    }

    // -------------------------------------------------------------------------- the wording

    /// <summary>
    /// §9.11: what happened and what to do next, second person, no apology. Holdover and recovery
    /// read differently because they are different — one is coasting, the other is working on it.
    /// </summary>
    [Fact]
    public void HoldoverAndRecoverySayDifferentThings()
    {
        (string Title, string Body)? holdover = LockWatch.Describe(LockAlert.Lost, SmartClockMode.Holdover);
        (string Title, string Body)? recovery = LockWatch.Describe(LockAlert.Lost, SmartClockMode.Recovery);

        Assert.NotNull(holdover);
        Assert.NotNull(recovery);
        Assert.NotEqual(holdover!.Value.Title, recovery!.Value.Title);
        Assert.Contains("drifting", holdover.Value.Body, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("reacquire", recovery.Value.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(LockAlert.Lost, SmartClockMode.Holdover)]
    [InlineData(LockAlert.Lost, SmartClockMode.Recovery)]
    [InlineData(LockAlert.Regained, SmartClockMode.Locked)]
    public void EveryAlertSaysWhatToDoNext(LockAlert alert, SmartClockMode mode)
    {
        (string Title, string Body)? text = LockWatch.Describe(alert, mode);

        Assert.NotNull(text);
        Assert.EndsWith(".", text!.Value.Body, StringComparison.Ordinal);
        Assert.DoesNotContain("Oops", text.Value.Body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Sorry", text.Value.Body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NothingHasNothingToSay() =>
        Assert.Null(LockWatch.Describe(LockAlert.None, SmartClockMode.Locked));
}
