using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.Tests.Services;
using WinZ3805A.Simulation;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The seam end to end for a talker (#310): the real session hears the simulator, selects the
/// NMEA driver without asking it anything, and the real poller reads it through the listener.
/// </summary>
/// <remarks>
/// Every wait advances the fake clock a second at a time and emits a cycle when the talker is
/// talking, which is the same loop the selection tests use — the clock is the only thing that
/// makes the probe time out, the poll timer fire and the listener's silence detection trip, so
/// nothing here depends on wall time.
/// </remarks>
public sealed class NmeaSessionTests
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan TestTimeout = TimeSpan.FromSeconds(15);

    private static IReadOnlyList<IReceiverDriver> BothFamilies(FakeTimeProvider clock) =>
        [new SmartClockDriver(clock), new NmeaDriver(clock)];

    [Fact]
    public async Task ATalkerIsOverheardAndNeverAskedForItsIdentity()
    {
        await using Bench bench = new();

        Assert.True(await bench.RunAsync(bench.Session.ConnectAsync("COM7", bench.Session.AutoDetectPlan[^1])));

        Assert.Equal(ConnectionStatus.Connected, bench.Session.Status);
        Assert.Equal(NmeaDriver.FamilyName, bench.Session.Driver.Family);
        Assert.Equal("NMEA 0183,GP talker,,", bench.Session.Identity);
        Assert.Equal(LinkStyle.Broadcast, bench.Session.Driver.Link);

        // The synchronise step's *CLS is the one thing a query/response probe writes before it
        // knows what it is talking to; the identity query is never sent to a talker.
        Assert.DoesNotContain(bench.Transport.CommandsWritten, written => written.Contains("IDN", StringComparison.OrdinalIgnoreCase));
        Assert.All(bench.Transport.CommandsWritten, written => Assert.Equal("*CLS", written));
    }

    [Fact]
    public async Task ThePollerReadsTheTalkerThroughTheListener()
    {
        await using Bench bench = new();
        Assert.True(await bench.RunAsync(bench.Session.ConnectAsync("COM7", bench.Session.AutoDetectPlan[^1])));

        ReceiverStateStore store = new(bench.Clock);
        await using PollingService poller = new(bench.Session, store, bench.Clock);
        poller.Start();

        await bench.UntilAsync(() => poller.FastSweeps >= 3 && poller.FullSweeps >= 1);

        Assert.Equal(NmeaDriver.NoFixToken, store.SyncState);
        Assert.Equal(6, store.TrackedCount);
        Assert.NotNull(store.Status);
        Assert.Equal("no fix", store.Status.ModeDetail);
        Assert.Equal(6, store.Status.Tracked.Count);
        Assert.Null(store.Status.Position);

        // Forty seconds on, the simulated receiver has its 3D fix and the store follows — the 2D
        // fix at twenty already carries a position, so the wait is for the phase, not the position.
        await bench.UntilAsync(() => store.SyncState == NmeaDriver.FixToken && store.Status?.ModeDetail == "GPS fix (3D)");

        Assert.Equal(8, store.TrackedCount);
        Assert.Equal("GPS fix (3D)", store.Status!.ModeDetail);
        Assert.Equal(47.6205, store.Status.Position!.LatitudeDegrees!.Value, 3);

        await poller.StopAsync();

        // Still nothing but the synchronise step's *CLS: polling a talker writes nothing.
        Assert.All(bench.Transport.CommandsWritten, written => Assert.Equal("*CLS", written));
    }

    [Fact]
    public async Task ATalkerThatFallsSilentFaultsTheSession()
    {
        await using Bench bench = new();
        bench.Session.StayConnected = false;
        Assert.True(await bench.RunAsync(bench.Session.ConnectAsync("COM7", bench.Session.AutoDetectPlan[^1])));

        ReceiverStateStore store = new(bench.Clock);
        await using PollingService poller = new(bench.Session, store, bench.Clock);
        poller.Start();
        await bench.UntilAsync(() => poller.FastSweeps >= 2);

        bench.Talking = false;
        await bench.UntilAsync(() => bench.Session.Status == ConnectionStatus.Faulted);

        await poller.StopAsync();
    }

    /// <summary>Registering the talker's driver changes nothing for the family already shipped.</summary>
    [Fact]
    public async Task ASmartClockIsStillTheSmartClocksWithTheTalkerDriverRegistered()
    {
        FakeTimeProvider clock = new(Start);
        const string identity = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";
        ControllableTransport transport = new(command => command.StartsWith("*IDN", StringComparison.OrdinalIgnoreCase) ? identity : " LOCK")
        {
            Banner = identity,
        };

        await using DeviceSessionService session = new((_, _) => transport, clock, drivers: BothFamilies(clock));

        Assert.True(await session.ConnectAsync("COM3", SerialSettings.Default).WaitAsync(TestTimeout));
        Assert.Equal("SmartClock", session.Driver.Family);
        Assert.Equal(identity, session.Identity);
        Assert.Equal(LinkStyle.QueryResponse, session.Driver.Link);
    }

    /// <summary>The talker's baud rates join the walk after the SmartClock's, and the two 9600-8-N-1 entries are one.</summary>
    [Fact]
    public async Task TheAutoDetectWalkGainsTheTalkersRatesAtTheEnd()
    {
        FakeTimeProvider clock = new(Start);
        await using DeviceSessionService session = new((_, _) => new FakeTransport { Silent = true }, clock, drivers: BothFamilies(clock));

        int smartClock = SerialSettings.AutoDetectSequence.Count;
        Assert.Equal(SerialSettings.AutoDetectSequence, session.AutoDetectPlan.Take(smartClock));
        Assert.Equal([4800, 38400], session.AutoDetectPlan.Skip(smartClock).Select(s => s.BaudRate));
    }

    /// <summary>A talker on the desk: the simulator, a silent transport, and the session over both.</summary>
    private sealed class Bench : IAsyncDisposable
    {
        public Bench()
        {
            Clock = new FakeTimeProvider(Start);

            // WaitForReaderToConsume forces the ordering the whole bench depends on: a cycle is
            // in the listener before the clock moves on. Without it a parallel test run could let
            // the listener fall three fake seconds behind the emits, the poller would read that as
            // a talker gone silent, and the session would reconnect onto a fake transport it had
            // already disposed — a failure that appeared only in the full run and never alone.
            Transport = new FakeTransport { Silent = true, EchoCommands = false, EmitPrompt = false, WaitForReaderToConsume = true };
            Talker = new NmeaTalkerSimulator(Clock);
            Session = new DeviceSessionService((_, _) => Transport, Clock, drivers: BothFamilies(Clock));
        }

        public FakeTimeProvider Clock { get; }

        public FakeTransport Transport { get; }

        public NmeaTalkerSimulator Talker { get; }

        public DeviceSessionService Session { get; }

        public bool Talking { get; set; } = true;

        /// <summary>
        /// How many simulated seconds a wait is given. <b>Ticks, not wall time (#510).</b>
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Both loops below used to give up on a real-time budget, and that is what made
        /// <c>ThePollerReadsTheTalkerThroughTheListener</c> fail about once in fifty full-suite
        /// runs.</b> The thing being waited for advances on the <i>fake</i> clock — one simulated
        /// second per tick — while the budget ran on the real one. Each tick costs at least two
        /// 5 ms delays, and xUnit runs collections in parallel, so under contention those delays
        /// stretch and fewer simulated seconds fit into the same fifteen. The test then failed for
        /// having been slow rather than for being wrong, and it took 15.75 s to say so.
        /// </para>
        /// <para>
        /// Counting ticks removes the machine from the question entirely: the same number of
        /// simulated seconds elapses on a loaded runner as on an idle one. The bound is still a
        /// bound — a condition that never holds still fails rather than spinning — which is the
        /// distinction #445 turned on.
        /// </para>
        /// <para>
        /// <b>This is not the guard against a hung tick, and must not be mistaken for one.</b> An
        /// emit that never drains blocks inside <see cref="TickAsync"/> and is never re-checked by
        /// any loop; that case is bounded on the await itself, where it has to be. Five minutes of
        /// simulated time is far more than any test here needs, so reaching this budget means the
        /// condition is wrong rather than slow.
        /// </para>
        /// </remarks>
        private const int TickBudget = 300;

        public async Task<T> RunAsync<T>(Task<T> pending)
        {
            for (int tick = 0; tick < TickBudget && !pending.IsCompleted; tick++)
            {
                await TickAsync();
            }

            return await pending.WaitAsync(TestTimeout);
        }

        public async Task UntilAsync(Func<bool> condition)
        {
            for (int tick = 0; tick < TickBudget && !condition(); tick++)
            {
                await TickAsync();
            }

            Assert.True(
                condition(),
                $"The condition never held in {TickBudget} simulated seconds; the session is {Session.Status}.");
        }

        /// <summary>
        /// One fake second: a cycle from the talker, then the clock moved on.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>The emit is bounded, and that is not belt and braces (#449).</b>
        /// <c>WaitForReaderToConsume</c> builds the pipe with a one-byte
        /// <c>pauseWriterThreshold</c>, so <c>EmitAsync</c> does not return until something drains
        /// it. If the listener has stopped — the session faulted, the poller was stopped, the
        /// transport was disposed under it — nothing ever will, and the emit waits for ever.
        /// </para>
        /// <para>
        /// <b>The loops above cannot save it.</b> <see cref="UntilAsync"/> and
        /// <see cref="RunAsync"/> both carry a budget, but they check it between ticks; a tick that
        /// never returns is never re-checked. So the bound has to be on the await, not on the loop
        /// around it. Without it the test does not fail — the host dies five minutes later with
        /// nothing marked failed, which is #445 in a second place and #381 in a third.
        /// </para>
        /// <para>
        /// <b>It is also why those loops count ticks rather than seconds (#510).</b> This budget is
        /// real time because a hung emit is a real-time event; theirs is simulated time because what
        /// they wait for advances on the fake clock. Giving both the same kind of budget is what
        /// made one of them fail for being slow.
        /// </para>
        /// </remarks>
        private async Task TickAsync()
        {
            if (Talking && Transport.IsOpen)
            {
                try
                {
                    await Transport.EmitAsync(Talker.NextCycleText()).AsTask().WaitAsync(TestTimeout);
                }
                catch (TimeoutException)
                {
                    Assert.Fail(
                        "A talker cycle was never consumed, so the emit blocked for "
                        + $"{TestTimeout.TotalSeconds:N0} s. The pipe pauses its writer after one byte, "
                        + $"so nothing is reading the transport — the session is {Session.Status}.");
                }
            }

            await Task.Delay(5);
            Clock.Advance(TimeSpan.FromSeconds(1));
            await Task.Delay(5);
        }

        public async ValueTask DisposeAsync()
        {
            await Session.DisposeAsync();
            await Transport.DisposeAsync();
        }
    }
}
