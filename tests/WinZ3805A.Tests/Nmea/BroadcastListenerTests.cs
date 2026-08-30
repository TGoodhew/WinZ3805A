using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Transport;
using WinZ3805A.Simulation;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The read side of a broadcast link (#310): cycles, the answers they give, and silence.
/// </summary>
/// <remarks>
/// <para>
/// Every whole-line emission waits for the listener to consume it
/// (<see cref="FakeTransport.WaitForReaderToConsume"/>), so the assertions read a state the
/// listener has definitely reached rather than one it will reach shortly — the ordering is
/// forced, never slept for.
/// </para>
/// <para>
/// The one test that emits <i>half</i> a line cannot use that option: the listener correctly holds
/// a partial line back until its ending arrives, so a writer waiting to be consumed would wait
/// forever — the deadlock <see cref="FakeTransport.WaitForReaderToConsume"/>'s own remarks warn
/// of, and the one that hung the full test run once. That test uses a plain transport and a
/// bounded settle instead.
/// </para>
/// </remarks>
public sealed class BroadcastListenerTests : IAsyncDisposable
{
    private static readonly DateTimeOffset Start = new(2026, 8, 29, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan Silence = TimeSpan.FromSeconds(3);

    private readonly FakeTimeProvider _clock = new(Start);
    private readonly NmeaDriver _driver;
    private readonly NmeaTalkerSimulator _talker;
    private FakeTransport? _transport;
    private BroadcastListener? _listener;

    public BroadcastListenerTests()
    {
        _driver = new NmeaDriver(_clock);
        _talker = new NmeaTalkerSimulator(_clock);
    }

    public async ValueTask DisposeAsync()
    {
        if (_listener is not null)
        {
            await _listener.DisposeAsync();
        }

        if (_transport is not null)
        {
            await _transport.DisposeAsync();
        }
    }

    /// <summary>Opens a silent transport and starts a listener over it.</summary>
    private async Task<(FakeTransport Transport, BroadcastListener Listener)> StartAsync(bool waitForReaderToConsume = true)
    {
        _transport = new FakeTransport
        {
            Silent = true,
            EchoCommands = false,
            EmitPrompt = false,
            WaitForReaderToConsume = waitForReaderToConsume,
        };
        await _transport.OpenAsync();
        _listener = new BroadcastListener(_transport, _driver, _clock);
        _listener.Start();
        return (_transport, _listener);
    }

    /// <summary>
    /// A listener that has heard nothing is not yet a talker that has stopped: it answers empty
    /// until its silence timeout has passed, and only then as a timeout.
    /// </summary>
    /// <remarks>
    /// The first sweep after connect asks within milliseconds of the listener starting; three empty
    /// answers read as timeouts would reconnect a healthy link before its first cycle arrived —
    /// which they did, one run in three, until this grace period existed.
    /// </remarks>
    [Fact]
    public async Task AFreshListenerIsEmptyNotStaleUntilItsTimeoutPasses()
    {
        (_, BroadcastListener listener) = await StartAsync();

        Transaction early = listener.Answer("$--RMC", Silence);
        Assert.Equal(TransactionOutcome.Completed, early.Outcome);
        Assert.Empty(early.Lines);

        _clock.Advance(Silence + TimeSpan.FromMilliseconds(1));

        Assert.Equal(TransactionOutcome.TimedOut, listener.Answer("$--RMC", Silence).Outcome);
    }

    /// <summary>What the connect probe heard is replayed, so the first poll has a cycle to read.</summary>
    [Fact]
    public async Task SeedingReplaysWhatTheProbeHeard()
    {
        List<string> probeHeard = _talker.NextCycle().ToList();
        _clock.Advance(TimeSpan.FromSeconds(1));
        probeHeard.AddRange(_talker.NextCycle());

        (_, BroadcastListener listener) = await StartAsync();
        listener.Seed(probeHeard);

        Assert.Equal(1, listener.CyclesHeard);
        Assert.Equal(3, listener.Answer("$--GSV", Silence).Lines.Count);
        Assert.Equal(7, listener.Answer(PollPlan.WholeCycle, Silence).Lines.Count);
        Assert.Equal(TransactionOutcome.Completed, listener.Answer("$--RMC", Silence).Outcome);
    }

    /// <summary>A key other than the boundary is answered from the last complete cycle, so GSV is never half a table.</summary>
    [Fact]
    public async Task AKeyIsAnsweredFromTheLastCompleteCycle()
    {
        (FakeTransport transport, BroadcastListener listener) = await StartAsync();
        await transport.EmitAsync(_talker.NextCycleText());
        _clock.Advance(TimeSpan.FromSeconds(1));
        string secondRmc = _talker.NextCycle()[0];
        await transport.EmitAsync(secondRmc + "\r\n");

        Assert.Equal(1, listener.CyclesHeard);

        Transaction gsv = listener.Answer("$--GSV", Silence);
        Assert.Equal(TransactionOutcome.Completed, gsv.Outcome);
        Assert.Equal(3, gsv.Lines.Count);
        Assert.All(gsv.Lines, line => Assert.StartsWith("$GPGSV", line, StringComparison.Ordinal));

        Transaction rmc = listener.Answer("$--RMC", Silence);
        Assert.Equal([secondRmc], rmc.Lines);

        Transaction whole = listener.Answer(PollPlan.WholeCycle, Silence);
        Assert.Equal(7, whole.Lines.Count);
        Assert.StartsWith("$GPRMC,120000.00", whole.Lines[0], StringComparison.Ordinal);
    }

    /// <summary>With no complete cycle yet, the current one is better than nothing.</summary>
    [Fact]
    public async Task APartialFirstCycleStillAnswers()
    {
        (FakeTransport transport, BroadcastListener listener) = await StartAsync();
        List<string> cycle = _talker.NextCycle().ToList();
        await transport.EmitAsync(cycle[0] + "\r\n" + cycle[1] + "\r\n");

        Assert.Equal(0, listener.CyclesHeard);
        Assert.Equal([cycle[1]], listener.Answer("$--GGA", Silence).Lines);
        Assert.Equal([cycle[0]], listener.Answer("$--RMC", Silence).Lines);
        Assert.Empty(listener.Answer("$--GSV", Silence).Lines);
        Assert.Equal(TransactionOutcome.Completed, listener.Answer("$--GSV", Silence).Outcome);
    }

    [Fact]
    public async Task ATalkerThatStopsReadsAsATimeout()
    {
        (FakeTransport transport, BroadcastListener listener) = await StartAsync();
        await transport.EmitAsync(_talker.NextCycleText());
        Assert.Equal(TransactionOutcome.Completed, listener.Answer("$--RMC", Silence).Outcome);

        _clock.Advance(Silence + TimeSpan.FromMilliseconds(1));

        Assert.Equal(TransactionOutcome.TimedOut, listener.Answer("$--RMC", Silence).Outcome);
    }

    /// <summary>
    /// A talker whose boundary sentence never arrives does not accumulate for the life of the link.
    /// </summary>
    /// <remarks>
    /// The driver claims any sentence it understands, while its plan names one of them — RMC — as
    /// the cycle boundary, so a talker configured to send GGA and not RMC is claimed and can never
    /// complete a cycle. The silence timeout does not catch it, because lines are arriving. Before
    /// #319 the current cycle grew without limit and <c>WholeCycle</c> answered with all of it,
    /// marked <c>Completed</c>: an unbounded buffer and a parser fed a blob that grew by a sentence
    /// a second. Now the cycle is abandoned at the cap and counted.
    /// </remarks>
    [Fact]
    public async Task ACycleWhoseBoundaryNeverArrivesIsAbandonedRatherThanGrowing()
    {
        // No consume-wait, and one emit rather than hundreds: this test's whole point is a volume
        // of lines, and a writer that must be drained between every one of them is both slow and
        // the deadlock FakeTransport's own remarks warn about.
        (FakeTransport transport, BroadcastListener listener) = await StartAsync(waitForReaderToConsume: false);

        string gga = NmeaSentence.Format(
            "GP", "GGA", "120000.00", "4737.2300", "N", "12220.9580", "W", "1", "07", "1.0", "50.0", "M", "-17.0", "M", null, null);

        // One more than the cap, so the cycle is abandoned exactly once. None of them is an RMC.
        string burst = string.Concat(Enumerable.Repeat(gga + "\r\n", BroadcastListener.MaximumCycleLines + 1));

        await transport.EmitAsync(burst);
        await SettleAsync(() => listener.CyclesAbandoned, abandoned => abandoned > 0);

        Assert.Equal(1, listener.CyclesAbandoned);
        Assert.Equal(0, listener.CyclesHeard);

        // Every line was claimed, so none was discarded as noise — and the answer is not the
        // growing blob it would have been, because the cycle was dropped rather than accumulated.
        Assert.Equal(0, listener.LinesDiscarded);
        Assert.True(listener.Answer(PollPlan.WholeCycle, Silence).Lines.Count <= BroadcastListener.MaximumCycleLines);
    }

    [Fact]
    public async Task NoiseIsCountedAndDiscarded()
    {
        (FakeTransport transport, BroadcastListener listener) = await StartAsync();
        await transport.EmitAsync("\0ÿgarbage\r\nscpi > \r\n$GPGGA,bad*00\r\n");

        Assert.Equal(3, listener.LinesDiscarded);
        Assert.Equal(0, listener.CyclesHeard);
    }

    /// <summary>
    /// Half a sentence in one read and the rest in the next is one sentence — the case a serial
    /// port at 4800 baud produces on every line.
    /// </summary>
    [Fact]
    public async Task ALineSplitAcrossReadsIsStillOneLine()
    {
        // No consume-wait here: the first emit is half a line, which the listener rightly holds
        // back, and a writer waiting for it to be consumed would never return.
        (FakeTransport transport, BroadcastListener listener) = await StartAsync(waitForReaderToConsume: false);
        string rmc = _talker.NextCycle()[0];
        await transport.EmitAsync(rmc[..10]);
        await transport.EmitAsync(rmc[10..] + "\r\n");

        Transaction answer = await SettleAsync(() => listener.Answer("$--RMC", Silence), a => a.Lines.Count > 0);

        Assert.Equal([rmc], answer.Lines);
        Assert.Equal(0, listener.LinesDiscarded);
    }

    [Fact]
    public async Task ATransportThatClosesReadsAsAFault()
    {
        (FakeTransport transport, BroadcastListener listener) = await StartAsync();
        await transport.EmitAsync(_talker.NextCycleText());
        transport.Fail(new IOException("The device is not connected."));

        await SettleAsync(() => listener.Ended, ended => ended);

        Assert.True(listener.Ended);
        Assert.Equal(TransactionOutcome.Faulted, listener.Answer("$--RMC", Silence).Outcome);
    }

    [Fact]
    public void ADriverWithAnEmptyPlanCannotBeListenedFor() =>
        Assert.Throws<ArgumentException>(() => new BroadcastListener(new FakeTransport(), new EmptyPlanDriver(), _clock));

    /// <summary>Polls a read until it satisfies the condition, bounded — for the two cases the pipe cannot signal.</summary>
    private static async Task<T> SettleAsync<T>(Func<T> read, Func<T, bool> done)
    {
        T value = read();
        for (int attempt = 0; attempt < 200 && !done(value); attempt++)
        {
            await Task.Delay(10);
            value = read();
        }

        return value;
    }

    private sealed class EmptyPlanDriver : IReceiverDriver
    {
        public string Family => "Empty";
        public IReadOnlyList<Device.Commands.ScpiCommand> Commands => [];
        public PollCadence Cadence => new(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2));
        public PollPlan Plan => new([], null, "*");
        public IReadOnlyList<SerialSettings> AutoDetectSequence => [];
        public LinkStyle Link => LinkStyle.Broadcast;
        public bool Recognises(Device.Models.DeviceIdentity? identity) => false;
        public Device.Commands.ScpiCommand? Find(string? mnemonic) => null;
        public bool IsBlocked(string? header) => false;
        public TimeSpan TimeoutFor(string? mnemonic) => TimeSpan.FromSeconds(1);
        public Device.Models.ReceiverStatus Parse(string? response) => new();
        public SweepInterpretation InterpretSweep(IReadOnlyList<string?> answers) => new(new FastReadings(null, null, null, null, null, null), "empty");
    }
}
