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
        clock.Advance(TimeSpan.FromSeconds(2));

        Transaction refused = await protocol.ExecuteAsync(":SYNC:TINT?", Timeout);
        SweepInterpretation sweep = driver.InterpretSweep(await Sweep(protocol, driver));

        Assert.True(refused.WasRejected);
        Assert.Equal("E-230", refused.PromptStatus);
        Assert.Null(sweep.Rejection);
        Assert.Equal(ReceiverMode.Holdover, driver.InterpretSyncState(sweep.Readings.SyncState));
        Assert.Null(sweep.Readings.TimeIntervalNanoseconds);
    }

    public static TheoryData<string> States => new(["powerup", "fine", "stabilizing", "locked", "holdover", "signal-back", "recovery", "manual", "health"]);

    [Theory]
    [MemberData(nameof(States))]
    public async Task TheScreenParsesCleanlyInEveryState(string state)
    {
        (LineProtocol protocol, SimulatorTransport transport, SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        SmartClockDriver driver = new(clock);
        await Connect(protocol, transport);
        SmartClockMode expected = Arrange(state, receiver, clock);

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
    private bool _open;

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
                await EmitAsync(reply.Text, cancellationToken);
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

    private async ValueTask EmitAsync(string text, CancellationToken cancellationToken)
    {
        await _pipe.Writer.WriteAsync(Encoding.Latin1.GetBytes(text), cancellationToken);
    }
}
