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
    private static async Task WaitFor(FakeTimeProvider clock, Func<bool> condition)
    {
        using CancellationTokenSource giveUp = new(TestTimeout);
        while (!condition() && !giveUp.IsCancellationRequested)
        {
            clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5, CancellationToken.None);
        }

        Assert.True(condition(), "The condition never held.");
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
        await WaitFor(clock, () => poller.FullSweeps >= 3);
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
}
