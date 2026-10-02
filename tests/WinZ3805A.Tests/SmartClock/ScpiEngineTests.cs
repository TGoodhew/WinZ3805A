using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Commands;
using WinZ3805A.Simulation.SmartClock;

namespace WinZ3805A.Tests.SmartClock;

/// <summary>
/// The simulator's wire behaviour against §7.2, which was written from the bench unit (#639).
/// </summary>
/// <remarks>
/// Each of these is a rule a client got wrong at least once on real hardware, which is why each is
/// worth a simulator that reproduces it: the prompt that names the error queue rather than the last
/// command, the framing glitch on the first command, the reading that has no answer while unlocked.
/// </remarks>
public sealed class ScpiEngineTests
{
    private static (ScpiEngine Engine, SimulatedReceiver Receiver, FakeTimeProvider Clock) Bench(bool locked = true)
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        SimulatedReceiver receiver = new(clock);
        if (locked)
        {
            receiver.StartLocked();
        }

        return (new ScpiEngine(receiver, clock), receiver, clock);
    }

    private static string Send(ScpiEngine engine, string command) => engine.Receive(command)!.Text;

    [Fact]
    public void AClearQueueAnswersBehindTheOrdinaryPrompt()
    {
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A\r\nscpi > ", Send(engine, "*IDN?"));
    }

    [Fact]
    public void TheFirstCommandAfterOpeningIsLostToTheFramingGlitch()
    {
        // §7.2: "open the port and send *IDN? first and it answers E-362> with no identity string".
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A\r\nscpi > ", engine.Connected().Text);
        Assert.Equal("E-362> ", Send(engine, "*IDN?"));

        // The connect sequence's second *CLS clears it, and the session is aligned from then on.
        Assert.Equal("scpi > ", Send(engine, "*CLS"));
        Assert.StartsWith("SYMMETRICOM,", Send(engine, "*IDN?"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheErrorPromptNamesTheQueueNotTheCommandThatJustRan()
    {
        // §7.2's measured table: one error queued, then three successful commands, every one of
        // them answering correctly under E-113.
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal("E-113> ", Send(engine, ":NO:SUCH:NODE?"));
        Assert.Equal("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A\r\nE-113> ", Send(engine, "*IDN?"));
        Assert.Equal(" LOCK\r\nE-113> ", Send(engine, ":SYNC:STAT?"));
        Assert.EndsWith("\r\nE-113> ", Send(engine, ":GPS:SAT:TRAC:COUN?"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptShowsTheNewestErrorWhileTheQueueGivesUpTheOldest()
    {
        (ScpiEngine engine, _, _) = Bench();

        Send(engine, ":NO:SUCH:NODE?");                    // -113
        Assert.Equal("E-109> ", Send(engine, ":GPS:REF:ADEL")); // -109, no value given

        Assert.Equal(" -113,\"Undefined header\"\r\nE-109> ", Send(engine, ":SYST:ERR?"));

        // The read that empties the queue already comes back with the ordinary prompt.
        Assert.Equal(" -109,\"Missing parameter\"\r\nscpi > ", Send(engine, ":SYST:ERR?"));
        Assert.Equal(" +0,\"No error\"\r\nscpi > ", Send(engine, ":SYST:ERR?"));
    }

    [Fact]
    public void AnUnlockedReceiverHasNoTimeIntervalToGive()
    {
        // §7.3.1: no 1 PPS to measure against, so no data and E-230.
        (ScpiEngine engine, _, _) = Bench(locked: false);

        Assert.Equal("E-230> ", Send(engine, ":SYNC:TINT?"));
    }

    [Fact]
    public void AFullQueueReplacesItsNewestEntryWithAnOverflow()
    {
        // §7.3.1 saw the queue reach E-350 under a once-a-second refusal.
        (ScpiEngine engine, _, _) = Bench(locked: false);

        for (int i = 0; i <= engine.ErrorQueueCapacity; i++)
        {
            Send(engine, ":SYNC:TINT?");
        }

        Assert.Equal(engine.ErrorQueueCapacity, engine.QueuedErrors.Count);
        Assert.Equal(-350, engine.QueuedErrors.Last());
        Assert.Equal("E-350> ", Send(engine, ":SYNC:TINT?"));
    }

    [Fact]
    public void WithEchoOnTheCommandComesBackFirst()
    {
        (ScpiEngine engine, _, _) = Bench();

        Send(engine, ":SYST:COMM:SER1:FDUP ON");

        Assert.Equal(":SYNC:TFOM?\r\n +3\r\nscpi > ", Send(engine, ":SYNC:TFOM?"));
    }

    [Theory]
    [InlineData(":SYNC:STAT?")]
    [InlineData(":SYNCHRONIZATION:STATE?")]
    [InlineData("SYNC:STAT?")]
    [InlineData(":sync:stat?")]
    public void LongShortAndLowerCaseFormsAreTheSameCommand(string command)
    {
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal(" LOCK\r\nscpi > ", Send(engine, command));
    }

    [Fact]
    public void TheFormatsAreTheBenchUnits()
    {
        // Fixtures/README.md's scalar queries, from the first sitting.
        Assert.Equal("+3", ScpiEngine.Int(3));
        Assert.Equal("+6.00000E+002", ScpiEngine.Real(600));
        Assert.Equal("-1.68528E+001", ScpiEngine.Real(-16.8528));
        Assert.Equal("+7.70000E-008", ScpiEngine.Real(77e-9));
        Assert.Equal("-5.4E-009", ScpiEngine.Short(-5.4e-9));
        Assert.Equal("-3.56E-008", ScpiEngine.Short(-35.6e-9));
    }

    [Fact]
    public void ANonEmptyInclusionListArrivesOnTheSecondLine()
    {
        // SatelliteTrackingParser, from the bench unit on 20 Aug 2026.
        (ScpiEngine engine, _, _) = Bench();

        string[] lines = Send(engine, ":GPS:SAT:TRAC:INCL?").Split("\r\n");

        Assert.Equal(string.Empty, lines[0]);
        Assert.StartsWith("+1,+2,+3,", lines[1], StringComparison.Ordinal);
        Assert.Equal(" +0\r\nscpi > ", Send(engine, ":GPS:SAT:TRAC:IGN?"));
    }

    [Fact]
    public void AHeldPositionRefusesASurvey()
    {
        // #229: -300, and the route into a survey is power-up.
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal("E-300> ", Send(engine, ":GPS:POSition:SURVey:STATe ONCE"));
    }

    [Fact]
    public void TheTimeCodeIsFormatT2WithItsChecksum()
    {
        (ScpiEngine engine, _, _) = Bench();

        string code = Send(engine, ":PTIM:TCOD?").Split("\r\n")[0];

        // #37: 23 characters, the checksum the sum of the 21 before it, mod 256, in hex.
        Assert.Equal(23, code.Length);
        Assert.StartsWith("T2", code, StringComparison.Ordinal);
        Assert.Equal((code[..21].Sum(c => c) % 256).ToString("X2", System.Globalization.CultureInfo.InvariantCulture), code[21..]);
    }

    [Fact]
    public void EveryUndocumentedQueryIsAnUndefinedHeader()
    {
        // §8.5: the bench unit answered E-113 to each of them, and so does the simulator.
        foreach (ScpiCommand command in CommandCatalog.Experimental)
        {
            (ScpiEngine engine, _, _) = Bench();
            Assert.Equal("E-113> ", Send(engine, command.Mnemonic));
        }
    }

    [Fact]
    public void NoCataloguedCommandIsUnknownToTheSimulator()
    {
        // Every command the application can send is one the simulator understands. A gap here is
        // a page that would show nothing against the simulator while working on hardware.
        List<string> unknown = [];
        foreach (ScpiCommand command in CommandCatalog.All.Where(c => !c.IsExperimental))
        {
            (ScpiEngine engine, _, _) = Bench();
            string line = command.Mnemonic + Argument(command);
            Send(engine, line);
            if (engine.QueuedErrors.Contains(-113))
            {
                unknown.Add(line);
            }
        }

        Assert.Empty(unknown);
    }

    /// <summary>A plausible argument for a command that takes one, so it reaches its handler.</summary>
    private static string Argument(ScpiCommand command) => command.Parameters.Count == 0 || command.Parameters[0].IsOptional
        ? string.Empty
        : " " + string.Join(",", command.Parameters.Select(p => p.Choices is { Count: > 0 } choices
            ? choices[0]
            : p.Kind == ParameterKind.PrnList ? "5" : p.Minimum?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "1"));
}
