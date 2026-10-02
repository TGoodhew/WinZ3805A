using System.IO.Pipelines;
using System.Text;

using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;
using WinZ3805A.Simulation.SmartClock;

namespace WinZ3805A.Tests.SmartClock;

/// <summary>
/// The application's own protocol and driver against the simulated Z3805A (#639).
/// </summary>
/// <remarks>
/// <para>
/// <b>What these prove depends on the simulator being right, and that is checked separately.</b> Its
/// screens match the captures byte for byte (<see cref="StatusScreenWriterTests"/>) and its wire
/// behaviour follows §7.2 (<see cref="ScpiEngineTests"/>); where it guesses, its README says so. Over
/// those, this is the first end-to-end run of the SmartClock path that does not need the bench unit:
/// the connect sequence, the sweep and the screen, through the same <see cref="LineProtocol"/> the
/// application uses, in every state the receiver can be put in.
/// </para>
/// <para>
/// Unlike the UCCM simulator, this one shares no code with the parser, so agreement here is two
/// independent readings agreeing rather than one reading agreeing with itself.
/// </para>
/// </remarks>
public sealed class SmartClockSimulatedSessionTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static (LineProtocol Protocol, SimulatorTransport Transport, SimulatedReceiver Receiver, FakeTimeProvider Clock) Bench()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        SimulatedReceiver receiver = new(clock);
        receiver.StartLocked();
        SimulatorTransport transport = new(new ScpiEngine(receiver, clock));
        return (new LineProtocol(transport, TimeProvider.System), transport, receiver, clock);
    }

    [Fact]
    public async Task TheConnectSequenceAbsorbsTheBannerAndSpendsTheGlitch()
    {
        (LineProtocol protocol, SimulatorTransport transport, _, _) = Bench();
        await transport.OpenAsync();

        Transaction banner = await protocol.SynchroniseAsync(Timeout);
        Transaction identity = await protocol.ExecuteAsync("*IDN?", Timeout);

        Assert.Contains("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A", banner.Lines);
        Assert.True(identity.Succeeded);
        Assert.Equal("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A", identity.FirstLine);
        Assert.False(identity.ErrorQueueNotEmpty);
    }

    [Fact]
    public async Task ALockedSweepReadsEveryField()
    {
        (LineProtocol protocol, SimulatorTransport transport, _, FakeTimeProvider clock) = Bench();
        SmartClockDriver driver = new(clock);
        await Connect(protocol, transport);

        SweepInterpretation sweep = driver.InterpretSweep(await Sweep(protocol, driver));

        Assert.Null(sweep.Rejection);
        Assert.Equal(ReceiverMode.Locked, driver.InterpretSyncState(sweep.Readings.SyncState));
        Assert.Equal(3, sweep.Readings.Tfom);
        Assert.Equal(0, sweep.Readings.Ffom);
        Assert.NotNull(sweep.Readings.TimeIntervalNanoseconds);
        Assert.InRange(sweep.Readings.EfcPercent!.Value, -17, -16);
        Assert.True(sweep.Readings.SatellitesTracked > 0);
        Assert.NotNull(sweep.Readings.TimeOfDay);
    }

    [Fact]
    public async Task AnUnlockedSweepIsRefusedTheTimeIntervalAndOnlyThat()
    {
        // §7.3.1, end to end: the one refusable reading is refused, under E-230, and the rest arrive.
        (LineProtocol protocol, SimulatorTransport transport, SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        SmartClockDriver driver = new(clock);
        await Connect(protocol, transport);
        receiver.AntennaConnected = false;
        clock.Advance(receiver.Timing.CoastBeforeHoldover + TimeSpan.FromSeconds(2));

        Transaction refused = await protocol.ExecuteAsync(":SYNC:TINT?", Timeout);
        SweepInterpretation sweep = driver.InterpretSweep(await Sweep(protocol, driver));

        Assert.True(refused.WasRejected);
        Assert.Equal("E-230", refused.PromptStatus);
        Assert.Null(sweep.Rejection);
        Assert.Equal("WAIT", sweep.Readings.SyncState);
        Assert.Null(sweep.Readings.TimeIntervalNanoseconds);
    }

    /// <summary>
    /// The first screen after a power-up arrives after its timeout: the sweep it lands in is rejected
    /// rather than stored, and the next sweep is aligned again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// On 2 Oct 2026 the bench unit took more than fifteen seconds to start its first screen after a
    /// power cycle, which is longer than <c>TransactionTimeouts</c> allows a screen. The watch tool,
    /// which reads naively, then read that screen as the next command's answer, and every answer
    /// after it was one command late for a whole cycle. The application reads through
    /// <see cref="LineProtocol"/>, which resynchronises after a timeout (#209), so the question was
    /// whether that is enough when what arrives late is a 1.9 kB screen.
    /// </para>
    /// <para>
    /// <b>It is not enough, and this test pins what is.</b> <see cref="LineProtocol"/> resynchronises
    /// only after a timeout that received part of a reply; one that received nothing is taken to be
    /// a silent receiver, so as not to slow a reconnect. A screen that is slow to <i>start</i> looks
    /// exactly like that, so its bytes arrive after the next command and are read as its answer, and
    /// every answer after it is one late until the link pauses. What holds the line is the sweep
    /// guard (#209): the shifted sweep reads a screen line as its sync state and is rejected whole,
    /// and the pause before the next sweep drains the stray reply. The defect is #643;
    /// this test stays true either way.
    /// </para>
    /// <para>
    /// Scaled down so it runs in seconds: the first screen is made 1.5 s late against a 1 s timeout,
    /// the same shape as 16.5 s against 15.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AScreenArrivingAfterItsTimeoutCostsOneRejectedSweepAndNoBadReadings()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        SimulatedReceiver receiver = new(clock)
        {
            Timing = new ReceiverTiming { FirstScreen = TimeSpan.FromSeconds(1.5), SecondScreen = TimeSpan.FromMilliseconds(200) },
        };
        receiver.StartLocked();
        SimulatorTransport transport = new(new ScpiEngine(receiver, clock)) { HonourDelays = true };
        LineProtocol protocol = new(transport, TimeProvider.System);
        await Connect(protocol, transport);

        SmartClockDriver driver = new(clock);
        receiver.PowerCycle();
        await protocol.ExecuteAsync("*CLS", Timeout);
        Transaction late = await protocol.ExecuteAsync(":SYST:STAT?", TimeSpan.FromSeconds(1));
        SweepInterpretation shifted = driver.InterpretSweep(await Sweep(protocol, driver));

        // The poll cadence leaves a pause before the next sweep; that is what drains the stray reply.
        await Task.Delay(TimeSpan.FromMilliseconds(500));
        SweepInterpretation aligned = driver.InterpretSweep(await Sweep(protocol, driver));
        Transaction screen = await protocol.ExecuteAsync(":SYST:STAT?", Timeout);

        Assert.Equal(TransactionOutcome.TimedOut, late.Outcome);
        Assert.NotNull(shifted.Rejection);
        Assert.Null(aligned.Rejection);
        Assert.Equal("POW", aligned.Readings.SyncState);
        Assert.Equal(9, aligned.Readings.Tfom);
        Assert.Equal(SmartClockMode.PowerUp, driver.Parse(screen.Text).Mode);
    }

    public static TheoryData<string> States => new(["powerup", "cold-powerup", "fine", "stabilizing", "locked", "coasting", "holdover", "signal-back", "recovery", "manual", "health"]);

    [Theory]
    [MemberData(nameof(States))]
    public async Task TheScreenParsesCleanlyInEveryState(string state)
    {
        (LineProtocol protocol, SimulatorTransport transport, SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        SmartClockDriver driver = new(clock);
        await Connect(protocol, transport);
        SmartClockMode expected = Arrange(state, receiver, clock);

        // A power cycle loses the first command after it, as the bench unit did; spend it.
        await protocol.ExecuteAsync("*CLS", Timeout);
        Transaction screen = await protocol.ExecuteAsync(":SYST:STAT?", Timeout);
        ReceiverStatus status = driver.Parse(screen.Text);
        ScreenSnapshot truth = receiver.Snapshot();

        Assert.True(screen.Succeeded);
        Assert.Empty(status.ParseWarnings);
        Assert.Equal(expected, status.Mode);
        Assert.Equal(truth.Tfom, status.Tfom);
        Assert.Equal(truth.Ffom, status.Ffom);
        Assert.Equal(truth.Tracked.Count, status.Tracked.Count);
        Assert.Equal(truth.NotTracked.Count, status.NotTracked.Count);
        Assert.Equal(truth.GpsOnePpsValid, status.GpsOnePpsValid);
        Assert.Equal(truth.HoldoverDuration, status.HoldoverDuration);
        Assert.Equal(truth.Health.AllOk, status.HealthOk);
        Assert.Equal(1, status.WeekRolloverEpochs);
    }

    private static SmartClockMode Arrange(string state, SimulatedReceiver receiver, FakeTimeProvider clock)
    {
        switch (state)
        {
            case "powerup":
                receiver.PowerCycle();
                return SmartClockMode.PowerUp;
            case "fine":
                receiver.PowerCycle();
                clock.Advance(receiver.Timing.Acquisition);
                return SmartClockMode.PowerUp;
            case "stabilizing":
                receiver.PowerCycle();
                clock.Advance(receiver.Timing.Acquisition + receiver.Timing.FineFrequency);
                return SmartClockMode.Locked;
            case "locked":
                return SmartClockMode.Locked;
            case "coasting":
                // The minute after the antenna goes: still locked, nothing tracked.
                receiver.AntennaConnected = false;
                clock.Advance(TimeSpan.FromSeconds(30));
                return SmartClockMode.Locked;
            case "cold-powerup":
                receiver.PowerCycle(cold: true);
                clock.Advance(receiver.Timing.Boot + TimeSpan.FromSeconds(5));
                return SmartClockMode.PowerUp;
            case "holdover":
                receiver.AntennaConnected = false;
                clock.Advance(TimeSpan.FromMinutes(11));
                return SmartClockMode.Holdover;
            case "signal-back":
                receiver.AntennaConnected = false;
                clock.Advance(TimeSpan.FromMinutes(5));
                receiver.AntennaConnected = true;
                clock.Advance(TimeSpan.FromSeconds(2));
                return SmartClockMode.Holdover;
            case "recovery":
                receiver.AntennaConnected = false;
                clock.Advance(TimeSpan.FromMinutes(5));
                receiver.AntennaConnected = true;
                clock.Advance(receiver.Timing.HoldoverRelease + TimeSpan.FromSeconds(1));
                return SmartClockMode.Recovery;
            case "manual":
                receiver.ForceHoldover();
                clock.Advance(TimeSpan.FromMinutes(2));
                return SmartClockMode.Holdover;
            default:
                receiver.Health = new HealthPanel { Efc = false };
                return SmartClockMode.Locked;
        }
    }

    private static async Task Connect(LineProtocol protocol, SimulatorTransport transport)
    {
        await transport.OpenAsync();
        await protocol.SynchroniseAsync(Timeout);
    }

    private static async Task<IReadOnlyList<string?>> Sweep(LineProtocol protocol, SmartClockDriver driver)
    {
        List<string?> answers = [];
        foreach (string command in driver.Plan.FastTier)
        {
            Transaction t = await protocol.ExecuteAsync(command, Timeout);
            answers.Add(t.FirstLine);
        }

        return answers;
    }
}

/// <summary>
/// An <see cref="ITransport"/> whose far end is the simulated receiver, answering at once.
/// </summary>
/// <remarks>
/// The receiver's latencies are not reproduced here: the engine's clock is the test's fake one, and
/// a session test is about what is said, not how long it takes. <c>SimulatorLink</c> reproduces the
/// timing for a real port.
/// </remarks>
internal sealed class SimulatorTransport(ScpiEngine engine) : ITransport
{
    private readonly Pipe _pipe = new();
    private readonly LineAssembler _lines = new();
    private Task _sending = Task.CompletedTask;
    private bool _open;

    /// <summary>
    /// Sends each reply after the receiver's own latency, in real time and in order, instead of at
    /// once. For the tests about timing; everything else wants the replies immediately.
    /// </summary>
    public bool HonourDelays { get; init; }

    public string Description => "simulated Z3805A";

    public bool IsOpen => _open;

    public PipeReader Input => _pipe.Reader;

    public async ValueTask OpenAsync(CancellationToken cancellationToken = default)
    {
        _open = true;
        await EmitAsync(engine.Connected().Text, cancellationToken);
    }

    public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        foreach (string line in _lines.Feed(buffer.Span))
        {
            if (engine.Receive(line) is Reply reply)
            {
                if (HonourDelays)
                {
                    _sending = SendLaterAsync(_sending, reply);
                }
                else
                {
                    await EmitAsync(reply.Text, cancellationToken);
                }
            }
        }
    }

    public void DiscardInput()
    {
    }

    public async ValueTask DisposeAsync()
    {
        _open = false;
        await _pipe.Writer.CompleteAsync();
    }

    private async Task SendLaterAsync(Task before, Reply reply)
    {
        await before;
        await Task.Delay(reply.Delay);
        await EmitAsync(reply.Text, CancellationToken.None);
    }

    private async ValueTask EmitAsync(string text, CancellationToken cancellationToken)
    {
        await _pipe.Writer.WriteAsync(Encoding.Latin1.GetBytes(text), cancellationToken);
    }
}
