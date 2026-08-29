using System.Text;
using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The two §7.3 cadences and what they write into the store (§12, §15 step 4).
/// </summary>
public class PollingServiceTests
{
    private const string Identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Answers every fast-tier query the way the reference unit does, leading space and all.</summary>
    private static ControllableTransport Receiver(Func<string, string?>? overrides = null) =>
        new(command =>
        {
            string? overridden = overrides?.Invoke(command);
            if (overridden is not null)
            {
                return overridden;
            }

            return command switch
            {
                _ when command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) => Identity,
                ":SYNC:STAT?" => " LOCK",
                ":SYNC:TFOM?" => " +3",
                ":SYNC:FFOM?" => " +1",
                ":SYNC:TINT?" => " -5.4E-009",
                ":DIAG:ROSC:EFC:REL?" => " -1.68528E+001",
                ":GPS:SAT:TRAC:COUN?" => " +1",
                ":SYST:STAT?" => StatusScreen(),
                _ => " 0",
            };
        })
        { Banner = Identity };

    /// <summary>The captured screen, so the poller is exercised against real device output.</summary>
    private static string StatusScreen()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "locked-stabilizing.txt");
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return text.TrimEnd('\r', '\n');
    }

    private static async Task<(DeviceSessionService Session, ReceiverStateStore Store)> ConnectedAsync(
        ControllableTransport transport,
        TimeProvider clock)
    {
        DeviceSessionService session = new((_, _) => transport, clock);
        await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout);
        return (session, new ReceiverStateStore(clock));
    }

    /// <summary>Winds the clock until a condition holds, so a cadence is assertable without waiting.</summary>
    /// <summary>
    /// Advances the fake clock a second at a time until <paramref name="condition"/> holds.
    /// </summary>
    /// <param name="clock">The pinned clock the poller is running on.</param>
    /// <param name="condition">What the caller is waiting to become true.</param>
    /// <param name="progress">
    /// Total sweeps so far, when the caller has a poller to ask. Supplying it makes the wait
    /// settle before each advance instead of racing the loop.
    /// </param>
    /// <remarks>
    /// <para>
    /// <b>Advancing the clock while a sweep is still running loses ticks.</b>
    /// <see cref="PollingService"/> drives one <c>PeriodicTimer</c>, and that timer deliberately
    /// does not queue a tick that fires while nobody is awaiting it — which is what makes the
    /// no-overlap rule structural rather than a flag. So a wait that advances again before the
    /// loop has parked on <c>WaitForNextTickAsync</c> silently drops the sweep it just asked for.
    /// </para>
    /// <para>
    /// The loss is not even, which is what makes it worth fixing rather than tolerating: a full
    /// screen occupies the loop far longer than a fast scalar sweep, so the fast tier loses
    /// proportionally more of its ticks. That is exactly the shape of the failure this replaced —
    /// 14 fast sweeps to 3 full on a loaded CI runner, against a required ratio of five, while
    /// passing six runs out of six on an idle development machine.
    /// </para>
    /// </remarks>
    private static async Task WaitFor(
        FakeTimeProvider clock,
        Func<bool> condition,
        Func<int>? progress = null)
    {
        using CancellationTokenSource giveUp = new(TestTimeout);
        while (!condition() && !giveUp.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await SettleAsync(progress, giveUp.Token);
        }

        Assert.True(condition(), "The condition never held.");
    }

    /// <summary>
    /// Gives the poll loop real time to finish whatever the last tick started.
    /// </summary>
    /// <remarks>
    /// Quiescence rather than a fixed delay: the sweep count is sampled until two consecutive
    /// samples agree, so a runner that needs 40 ms gets 40 ms and an idle one is not slowed to
    /// the worst case. Without a <c>progress</c> probe there is nothing to sample and this falls
    /// back to the fixed wait it replaced, which is adequate for the callers that only need one
    /// sweep to have happened.
    /// </remarks>
    private static async Task SettleAsync(Func<int>? progress, CancellationToken cancellationToken)
    {
        if (progress is null)
        {
            await Task.Delay(5, cancellationToken);
            return;
        }

        int previous = -1;

        // Bounded so a genuinely stuck loop still fails on TestTimeout rather than here.
        for (int attempt = 0; attempt < 100; attempt++)
        {
            await Task.Delay(2, cancellationToken);

            int current = progress();
            if (current == previous)
            {
                return;
            }

            previous = current;
        }
    }

    // -------------------------------------------------------------------------------------

    /// <summary>
    /// One sweep of the fast tier lands every §7.3 scalar in the store, converted — the time
    /// interval in nanoseconds rather than the seconds the wire carries.
    /// </summary>
    [Fact]
    public async Task AFastSweepFillsTheStoreFromTheScalarQueries()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        await WaitFor(clock, () => poller.FastSweeps >= 1);
        await poller.StopAsync();

        Assert.Equal("LOCK", store.SyncState);
        Assert.Equal(3, store.Tfom);
        Assert.Equal(1, store.Ffom);
        Assert.NotNull(store.OnePpsTiNanoseconds);
        Assert.Equal(-5.4, store.OnePpsTiNanoseconds.Value, 6);
        Assert.NotNull(store.OscillatorControl);
        Assert.Equal(-16.8528, store.OscillatorControl.Value, 4);
        Assert.Equal(1, store.TrackedCount);
        Assert.NotNull(store.LastFastPoll);
    }

    /// <summary>Removes a temporary database and the two files SQLite keeps beside it.</summary>
    /// <remarks>
    /// Best effort. A file still held after disposal is a tidiness problem in the temp folder, not
    /// a failed assertion, and reporting it as one would make an unrelated test look broken.
    /// </remarks>
    private static void Discard(string database)
    {
        foreach (string path in new[] { database, database + "-wal", database + "-shm" })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
                // See the remarks.
            }
        }
    }

    /// <summary>
    /// A sweep whose sync state is not a state the receiver reports is not written to the trend
    /// (#209).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The link misaligned on 24 Aug 2026 and three impossible values reached <c>trend.db</c>: time
    /// intervals of two and three <b>seconds</b>, and an EFC of +2 %. §11.1 nulls an
    /// <i>unparseable</i> field and none of those were unparseable — they were somebody else's
    /// reply, parsed correctly.
    /// </para>
    /// <para>
    /// The +2 % is why a range check is not the answer: it is inside the oscillator's control range
    /// and indistinguishable from a real reading by magnitude. What identifies the sweep is the
    /// company it keeps, so the whole sweep goes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ASweepWithAnImpossibleSyncStateIsNotStored()
    {
        FakeTimeProvider clock = new();
        string database = Path.Combine(Path.GetTempPath(), $"trend-{Guid.NewGuid():N}.db");

        try
        {
            IReadOnlyList<TrendRecord> stored;
            using (TrendStore trends = new(database))
            {

            // What was actually recorded that day, tail and all, with the values that came with it.
            ControllableTransport transport = Receiver(command => command switch
            {
                ":SYNC:STAT?" => " OLDOVER STARTED, NOT TRACKING GPS\r\nLOG 222:20070108.22:40:38:  HOLDOVER",
                ":SYNC:TINT?" => " 3.0E+000",
                ":DIAG:ROSC:EFC:REL?" => " +2",
                _ => null,
            });

            (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(transport, clock);
            await using DeviceSessionService _ = session;
            await using PollingService poller = new(session, store, clock, trends: trends);

            poller.Start();
            await WaitFor(clock, () => poller.FastSweeps >= 2, () => poller.FastSweeps);
            await poller.StopAsync();

                long now = clock.GetUtcNow().UtcTicks;
                stored = trends.Read(now - TimeSpan.FromDays(1).Ticks, now + TimeSpan.TicksPerDay);
            }

            Assert.Empty(stored);
        }
        finally
        {
            Discard(database);
        }
    }

    /// <summary>And an ordinary sweep still is, which is the half that proves the guard is narrow.</summary>
    [Fact]
    public async Task AnOrdinarySweepIsStillStored()
    {
        FakeTimeProvider clock = new();
        string database = Path.Combine(Path.GetTempPath(), $"trend-{Guid.NewGuid():N}.db");

        try
        {
            IReadOnlyList<TrendRecord> stored;
            using (TrendStore trends = new(database))
            {

            (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
            await using DeviceSessionService _ = session;
            await using PollingService poller = new(session, store, clock, trends: trends);

            poller.Start();
            await WaitFor(clock, () => poller.FastSweeps >= 1, () => poller.FastSweeps);
            await poller.StopAsync();

                long now = clock.GetUtcNow().UtcTicks;
                stored = trends.Read(now - TimeSpan.FromDays(1).Ticks, now + TimeSpan.TicksPerDay);
            }

            Assert.NotEmpty(stored);
            Assert.Equal("LOCK", stored[0].SyncState);
        }
        finally
        {
            Discard(database);
        }
    }

    /// <summary>
    /// The full tier is the only source of the satellite table — §7.3's whole reason for having two
    /// cadences — so a full sweep must land a parsed screen, not just scalars.
    /// </summary>
    [Fact]
    public async Task AFullSweepLandsAParsedStatusScreen()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        await WaitFor(clock, () => poller.FullSweeps >= 1);
        await poller.StopAsync();

        Assert.NotNull(store.Status);
        Assert.Single(store.Status.Tracked);
        Assert.Equal(9, store.Status.NotTracked.Count);
        Assert.Equal(10, store.Status.ElevationMaskDegrees);
        Assert.NotNull(store.LastFullPoll);
    }

    /// <summary>
    /// §9.7.4's F5 takes a full screen ahead of the 10 s cadence.
    /// </summary>
    /// <remarks>
    /// The flag is read on the next fast tick rather than acted on where it is set, because the UI
    /// thread issuing a screen alongside a sweep already in flight is the overlap the single-timer
    /// design exists to prevent. What that costs is at most one fast interval, and what it buys is
    /// that "refresh now" cannot corrupt the cadence it interrupts.
    /// </remarks>
    [Fact]
    public async Task RequestingAFullSweepTakesOneAheadOfTheCadence()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;

        // A cadence long enough that a second scheduled screen cannot explain the result.
        await using PollingService poller = new(session, store, clock)
        {
            FullInterval = TimeSpan.FromMinutes(5),
        };

        poller.Start();
        await WaitFor(clock, () => poller.FullSweeps >= 1);
        Assert.Equal(1, poller.FullSweeps);

        poller.RequestFullSweep();
        await WaitFor(clock, () => poller.FullSweeps >= 2);
        await poller.StopAsync();

        Assert.Equal(2, poller.FullSweeps);
    }

    /// <summary>
    /// Asking twice before the next tick asks once.
    /// </summary>
    /// <remarks>
    /// Two screens back to back would starve the fast tier for about seven seconds on a 9600 baud
    /// link, and there is nothing the second one tells the user that the first did not. A user
    /// leaning on F5 must not be able to do that.
    /// </remarks>
    [Fact]
    public async Task AskingTwiceBeforeTheNextTickAsksOnce()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock)
        {
            FullInterval = TimeSpan.FromMinutes(5),
        };

        poller.Start();
        await WaitFor(clock, () => poller.FullSweeps >= 1);

        poller.RequestFullSweep();
        poller.RequestFullSweep();
        poller.RequestFullSweep();

        await WaitFor(clock, () => poller.FullSweeps >= 2);

        // Several more ticks with nothing outstanding.
        for (int i = 0; i < 5; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, CancellationToken.None);
        }

        await poller.StopAsync();

        Assert.Equal(2, poller.FullSweeps);
    }

    /// <summary>
    /// The first screen arrives with the first readings rather than a full interval later. The
    /// satellite table is most of what a user is waiting to see, and ten seconds of an empty table
    /// on connect reads as a broken app.
    /// </summary>
    [Fact]
    public async Task TheFullScreenIsFetchedOnTheFirstSweepRatherThanAfterOneInterval()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        await WaitFor(clock, () => poller.FastSweeps >= 1);
        await poller.StopAsync();

        Assert.Equal(1, poller.FullSweeps);
        Assert.NotNull(store.Status);
    }

    /// <summary>
    /// §7.3: the fast tier is roughly ten times the full tier. Asserting the ratio rather than exact
    /// counts keeps this from being a test of the fake clock's stepping.
    /// </summary>
    [Fact]
    public async Task TheFastTierRunsAboutTenTimesPerFullScreen()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        await WaitFor(clock, () => poller.FullSweeps >= 3, () => poller.FastSweeps + poller.FullSweeps);
        await poller.StopAsync();

        Assert.True(
            poller.FastSweeps >= poller.FullSweeps * 5,
            $"Expected the fast tier to dominate; saw {poller.FastSweeps} fast to {poller.FullSweeps} full.");
    }

    /// <summary>
    /// §9.10.2's medallion draws sixty samples, and §9.11 keeps stale readings rather than blanking
    /// them, so the ring has to be bounded and ordered oldest-first.
    /// </summary>
    [Fact]
    public async Task TheTimeIntervalRingKeepsTheLastSixtySamplesInOrder()
    {
        ReceiverStateStore store = new(new FakeTimeProvider());

        for (int i = 0; i < ReceiverStateStore.TimeIntervalWindow + 15; i++)
        {
            store.UpdateFast("LOCK", 3, 1, i, -16.8, 1);
        }

        IReadOnlyList<double?> ring = store.RecentTimeInterval;

        Assert.Equal(ReceiverStateStore.TimeIntervalWindow, ring.Count);
        Assert.Equal(15d, ring[0]);
        Assert.Equal(ReceiverStateStore.TimeIntervalWindow + 14d, ring[^1]);
    }

    /// <summary>
    /// §9.11: a field the receiver stopped answering goes to an em dash rather than keeping the last
    /// number, which would be a fabrication. The timestamp is what tells the user the rest is old.
    /// </summary>
    [Fact]
    public async Task AValueTheReceiverStopsAnsweringIsClearedRatherThanHeld()
    {
        FakeTimeProvider clock = new();
        bool answerTfom = true;
        ControllableTransport transport = Receiver(command =>
            command == ":SYNC:TFOM?" && !answerTfom ? "E-113" : null);

        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(transport, clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        await WaitFor(clock, () => poller.FastSweeps >= 1);
        Assert.Equal(3, store.Tfom);

        answerTfom = false;
        int sweepsSoFar = poller.FastSweeps;
        await WaitFor(clock, () => poller.FastSweeps >= sweepsSoFar + 2);
        await poller.StopAsync();

        Assert.Null(store.Tfom);

        // The rest of the sweep is unaffected, so one bad field does not blank the screen.
        Assert.Equal("LOCK", store.SyncState);
    }

    /// <summary>
    /// The link dropping must not kill the loop. The session owns reconnect; the poller keeps its
    /// cadence and simply finds nothing, and the store's timestamps carry the staleness (§9.11).
    /// </summary>
    [Fact]
    public async Task ALostLinkDoesNotStopTheLoop()
    {
        FakeTimeProvider clock = new();
        ControllableTransport transport = Receiver();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(transport, clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        await WaitFor(clock, () => poller.FastSweeps >= 1);

        transport.Behaviour = TransportBehaviour.Faulting;
        int sweepsAtFault = poller.FastSweeps;

        await WaitFor(clock, () => poller.FastSweeps >= sweepsAtFault + 2);

        Assert.True(poller.IsRunning);
        await poller.StopAsync();
        Assert.False(poller.IsRunning);
    }

    [Fact]
    public async Task StoppingIsIdempotentAndStartingTwiceDoesNotRunTwoLoops()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService _ = session;
        await using PollingService poller = new(session, store, clock);

        poller.Start();
        poller.Start();
        await WaitFor(clock, () => poller.FastSweeps >= 1);

        await poller.StopAsync();
        await poller.StopAsync();

        Assert.False(poller.IsRunning);
    }

    /// <summary>
    /// §12: view models bind to the store, never the poller — so the store has to raise change
    /// notifications for what it writes.
    /// </summary>
    [Fact]
    public void TheStoreNotifiesWhatItChangesAndStaysQuietWhenNothingDid()
    {
        ReceiverStateStore store = new(new FakeTimeProvider());
        List<string?> changed = [];
        store.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 1);

        Assert.Contains(nameof(ReceiverStateStore.SyncState), changed);
        Assert.Contains(nameof(ReceiverStateStore.Tfom), changed);
        Assert.Contains(nameof(ReceiverStateStore.RecentTimeInterval), changed);
        Assert.Contains(nameof(ReceiverStateStore.LastFastPoll), changed);

        changed.Clear();
        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 1);

        // The scalars are unchanged, so only the ring and the timestamp should speak up.
        Assert.DoesNotContain(nameof(ReceiverStateStore.SyncState), changed);
        Assert.DoesNotContain(nameof(ReceiverStateStore.Tfom), changed);
    }

    [Fact]
    public void AgeIsMeasuredAgainstTheInjectedClock()
    {
        FakeTimeProvider clock = new();
        ReceiverStateStore store = new(clock);

        store.UpdateFast("LOCK", 3, 1, -5.4, -16.8, 1);
        DateTimeOffset? taken = store.LastFastPoll;

        clock.Advance(TimeSpan.FromSeconds(42));

        Assert.NotNull(taken);
        Assert.Equal(TimeSpan.FromSeconds(42), store.AgeOf(taken));
        Assert.Null(store.AgeOf(null));
    }

    // -------------------------------------------------------------------------------------
    // #155: a reading the receiver refuses is not asked for again until its state changes
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// An unlocked receiver refuses the time-interval query, and the poller stops asking.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The bench receiver answers <c>:SYNC:TINT?</c> with nothing at all and <c>E-230</c> in the
    /// prompt while it has no 1 PPS to measure against. Asked once a second it filled the error
    /// queue until the receiver answered <c>E-350</c>, queue overflow, and the Diagnostics page
    /// could not drain it faster than the poll refilled it.
    /// </para>
    /// <para>
    /// This is the fix's whole claim: after the first refusal the sweep stops asking, so the count
    /// of skips grows while the count of asks does not.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task ARefusedReadingIsNotAskedForAgain()
    {
        int asked = 0;
        ControllableTransport transport = new(command =>
        {
            if (command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase))
            {
                return Identity;
            }

            if (command == ":SYNC:TINT?")
            {
                asked++;
                return null;
            }

            return command switch
            {
                ":SYNC:STAT?" => " WAIT",
                ":SYST:STAT?" => StatusScreen(),
                _ => " +0",
            };
        })
        {
            Banner = Identity,
            PromptFor = command => command == ":SYNC:TINT?" ? "E-230> " : null,
        };

        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(transport, clock);
        await using DeviceSessionService owned = session;

        await using PollingService poller = new(owned, store, clock);
        poller.Start();

        await WaitFor(clock, () => poller.RefusedQuerySkips >= 3, () => (int)poller.FastSweeps);

        // Asked once, refused once, and never asked again while the state stayed the same.
        Assert.Equal(1, asked);
    }

    /// <summary>
    /// And it is asked again the moment the receiver's state changes, without anything knowing
    /// which states support the reading.
    /// </summary>
    [Fact]
    public async Task ItIsAskedAgainWhenTheReceiversStateChanges()
    {
        int asked = 0;
        string state = " WAIT";

        ControllableTransport transport = new(command =>
        {
            if (command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase))
            {
                return Identity;
            }

            if (command == ":SYNC:TINT?")
            {
                asked++;
                return state == " LOCK" ? " -5.4E-009" : null;
            }

            return command switch
            {
                ":SYNC:STAT?" => state,
                ":SYST:STAT?" => StatusScreen(),
                _ => " +0",
            };
        })
        {
            Banner = Identity,
            PromptFor = command => command == ":SYNC:TINT?" && state != " LOCK" ? "E-230> " : null,
        };

        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(transport, clock);
        await using DeviceSessionService owned = session;

        await using PollingService poller = new(owned, store, clock);
        poller.Start();

        await WaitFor(clock, () => poller.RefusedQuerySkips >= 2, () => (int)poller.FastSweeps);
        Assert.Equal(1, asked);

        state = " LOCK";

        await WaitFor(clock, () => asked >= 2, () => asked);

        // Asked again on the new state, accepted this time, and not suppressed thereafter.
        long skipsAfterRecovery = poller.RefusedQuerySkips;
        await WaitFor(clock, () => asked >= 4, () => asked);

        Assert.Equal(skipsAfterRecovery, poller.RefusedQuerySkips);
    }

    /// <summary>
    /// A reading the receiver answers normally is never suppressed, however many sweeps run. The
    /// suppression must be provoked by a refusal and by nothing else.
    /// </summary>
    [Fact]
    public async Task AReadingThatAnswersIsNeverSkipped()
    {
        FakeTimeProvider clock = new();
        (DeviceSessionService session, ReceiverStateStore store) = await ConnectedAsync(Receiver(), clock);
        await using DeviceSessionService owned = session;

        await using PollingService poller = new(owned, store, clock);
        poller.Start();

        await WaitFor(clock, () => poller.FastSweeps >= 3, () => (int)poller.FastSweeps);

        Assert.Equal(0, poller.RefusedQuerySkips);
    }
}
