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
        Assert.Equal("LOCK\r\nE-113> ", Send(engine, ":SYNC:STAT?"));
        Assert.EndsWith("\r\nE-113> ", Send(engine, ":GPS:SAT:TRAC:COUN?"), StringComparison.Ordinal);
    }

    [Fact]
    public void ThePromptShowsTheNewestErrorWhileTheQueueGivesUpTheOldest()
    {
        (ScpiEngine engine, _, _) = Bench();

        Send(engine, ":NO:SUCH:NODE?");                    // -113
        Assert.Equal("E-109> ", Send(engine, ":GPS:REF:ADEL")); // -109, no value given

        Assert.Equal("-113,\"Undefined header\"\r\nE-109> ", Send(engine, ":SYST:ERR?"));

        // The read that empties the queue already comes back with the ordinary prompt.
        Assert.Equal("-109,\"Missing parameter\"\r\nscpi > ", Send(engine, ":SYST:ERR?"));

        // The empty queue's answer, confirmed on the bench unit on 2 Oct 2026.
        Assert.Equal("+0,\"No error\"\r\nscpi > ", Send(engine, ":SYST:ERR?"));
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

        Assert.Equal(":SYNC:TFOM?\r\n+3\r\nscpi > ", Send(engine, ":SYNC:TFOM?"));
    }

    [Theory]
    [InlineData(":SYNC:STAT?")]
    [InlineData(":SYNCHRONIZATION:STATE?")]
    [InlineData("SYNC:STAT?")]
    [InlineData(":sync:stat?")]
    public void LongShortAndLowerCaseFormsAreTheSameCommand(string command)
    {
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal("LOCK\r\nscpi > ", Send(engine, command));
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

    [Theory]
    [InlineData(-0.01e-9)]
    [InlineData(-0.049e-9)]
    [InlineData(-0.0)]
    [InlineData(0.01e-9)]
    public void AnIntervalThatRoundsToZeroFromBelowIsPlainZero(double seconds)
    {
        // #776: rounding to 0.1 ns made negative zero, which is not below zero and so took a "+",
        // and .NET then wrote its own "-" — "+-0.0E+000", which the app rightly read as a dash.
        Assert.Equal("+0.0E+000", ScpiEngine.Short(seconds));
    }

    [Fact]
    public void ZeroIsNeverSignedTwiceInAnyFormat()
    {
        // #776's shape in the other two signed reals: negative zero is not below zero, and the
        // uncertainty below zero rounds to zero at one decimal.
        Assert.Equal("+0.00000E+000", ScpiEngine.Real(-0.0));
        Assert.Equal("+0.0E-006", ScpiEngine.Micro(-0.0));
        Assert.Equal("+0.0E-006", ScpiEngine.Micro(-0.01));
        Assert.Equal("-0.1E-006", ScpiEngine.Micro(-0.06));
    }

    [Fact]
    public void ValuesCarryNoLeadingSpace()
    {
        // §7.2 recorded a space before every value. The bench unit, compared on 2 Oct 2026, sent
        // none in 70 replies, and the simulator follows the receiver.
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal("+3\r\nscpi > ", Send(engine, ":SYNC:TFOM?"));

        engine.LeadingSpace = true;
        Assert.Equal(" +3\r\nscpi > ", Send(engine, ":SYNC:TFOM?"));
    }

    [Fact]
    public void TheTrackingListsAreOnTheFirstLine()
    {
        // 2 Oct 2026: no blank line before the inclusion list, and an empty list is +0.
        (ScpiEngine engine, _, _) = Bench();

        Assert.StartsWith("+1,+2,+3,", Send(engine, ":GPS:SAT:TRAC:INCL?"), StringComparison.Ordinal);
        Assert.Equal("+0\r\nscpi > ", Send(engine, ":GPS:SAT:TRAC:IGN?"));
    }

    [Theory]
    [InlineData(":SYNC:HOLD:DUR:THR?", "+86400")]
    [InlineData(":SYNC:HOLD:WAIT?", "NONE")]
    [InlineData(":SYST:COMM?", "SER1")]
    [InlineData(":GPS:POS:HOLD:STAT?", "1")]
    [InlineData(":GPS:POS:SURV:STAT?", "0")]
    [InlineData(":GPS:POS:SURV:STAT:POW?", "1")]
    [InlineData("*SRE?", "+136")]
    [InlineData(":STAT:OPER:COND?", "+90")]
    [InlineData(":STAT:OPER:POW:COND?", "+7")]
    public void TheseAnswerAsTheBenchUnitDid(string query, string expected)
    {
        // Each was a guess until the comparison of 2 Oct 2026, and each guess was wrong.
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal(expected + "\r\nscpi > ", Send(engine, query));
    }

    [Theory]
    [InlineData(":SYNC:HOLD:TUNC:PRES?")]
    [InlineData(":GPS:POS:SURV:PROG?")]
    public void OutsideTheirStateTheseAreASettingsConflict(string query)
    {
        // -221 on the bench unit, where the 58503A guide gives -230 for the first.
        (ScpiEngine engine, _, _) = Bench();

        Assert.Equal("E-221> ", Send(engine, query));
    }

    [Fact]
    public void ThePredictedUncertaintyIsMicrosecondsOverAFixedExponent()
    {
        // "+0.8E-006,0" on the bench unit.
        (ScpiEngine engine, _, _) = Bench();

        Assert.Matches(@"^\+\d+\.\dE-006,0\r\nscpi > $", Send(engine, ":SYNC:HOLD:TUNC:PRED?"));
    }

    [Fact]
    public void TheLogReadsInBothOfTheBenchUnitsForms()
    {
        (ScpiEngine engine, _, _) = Bench();
        engine.Log.Add("GPS lock started");

        string all = Send(engine, ":DIAG:LOG:READ:ALL?");
        string one = Send(engine, ":DIAG:LOG:READ? 1");

        // The whole log: a status line, a blank line, unquoted entries with two spaces, blank lines.
        Assert.StartsWith("Log status: 1 entries\r\n\r\nLog 001:", all, StringComparison.Ordinal);
        Assert.EndsWith(":  GPS lock started\r\n\r\n\r\nscpi > ", all, StringComparison.Ordinal);

        // One entry: quoted, with one space.
        Assert.Matches(@"^""Log 001:\d{8}\.\d\d:\d\d:\d\d: GPS lock started""\r\nscpi > $", one);
    }

    [Fact]
    public void WithNoPowerNothingIsAnsweredAndTheFirstCommandBackIsLost()
    {
        // 2 Oct 2026: silence while off, then a bare prompt to whatever came first, with no error.
        (ScpiEngine engine, SimulatedReceiver receiver, _) = Bench();

        receiver.PowerOff();
        Assert.Null(engine.Receive("*IDN?"));

        receiver.PowerOn();
        Assert.Equal("scpi > ", Send(engine, ":SYNC:STAT?"));
        Assert.Empty(engine.QueuedErrors);
        Assert.Equal("POW\r\nscpi > ", Send(engine, ":SYNC:STAT?"));
    }

    [Fact]
    public void TheFirstScreensAfterPowerUpAreSlow()
    {
        // Over 15 s for the first and 7.3 s end to end for the second, on the bench unit.
        (ScpiEngine engine, SimulatedReceiver receiver, _) = Bench();
        receiver.PowerCycle();
        Send(engine, "*CLS");

        Assert.Equal(receiver.Timing.FirstScreen, engine.Receive(":SYST:STAT?")!.Delay);
        Assert.Equal(receiver.Timing.SecondScreen, engine.Receive(":SYST:STAT?")!.Delay);
        Assert.True(engine.Receive(":SYST:STAT?")!.Delay < TimeSpan.FromSeconds(2));
    }

    [Theory]
    [InlineData(":SYST:DATE?")]
    [InlineData(":SYST:TIME?")]
    [InlineData(":PTIM:DATE?")]
    [InlineData(":PTIM:TIME?")]
    [InlineData(":PTIM:TIME:STR?")]
    [InlineData(":PTIM:LEAP:ACC?")]
    [InlineData(":SYNC:HOLD:TUNC:PRED?")]
    [InlineData(":GPS:POS?")]
    [InlineData(":DIAG:IDEN:GPS?")]
    [InlineData(":GPS:SAT:TRAC:COUN?")]
    public void ThesePowerUpRefusalsAreTheBenchUnits(string query)
    {
        (ScpiEngine engine, SimulatedReceiver receiver, _) = Bench();
        receiver.PowerCycle();
        Send(engine, "*CLS");

        Assert.Equal("E-230> ", Send(engine, query));
    }

    [Fact]
    public void AQueryResponseRepeatsThePreviousAnswer()
    {
        // It answered the running hours straight after :DIAG:LIF:COUN?, and the GPS engine's
        // identity straight after :DIAG:IDEN:GPS? (2 Oct 2026).
        (ScpiEngine engine, _, _) = Bench();

        Send(engine, ":DIAG:LIF:COUN?");
        Assert.Equal("+37015\r\nscpi > ", Send(engine, ":DIAG:QUER:RESP?"));
        Send(engine, ":DIAG:IDEN:GPS?");
        Assert.StartsWith("\"--\",\"SFTW P/N", Send(engine, ":DIAG:QUER:RESP?"), StringComparison.Ordinal);
    }

    [Fact]
    public void AHoldoverFromLosingGpsWaitsForGps()
    {
        (ScpiEngine engine, SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        receiver.AntennaConnected = false;
        clock.Advance(receiver.Timing.CoastBeforeHoldover + TimeSpan.FromSeconds(1));

        Assert.Equal("WAIT\r\nscpi > ", Send(engine, ":SYNC:STAT?"));
        Assert.Equal("GPS\r\nscpi > ", Send(engine, ":SYNC:HOLD:WAIT?"));
        Assert.Equal("0\r\nscpi > ", Send(engine, ":GPS:REF:VAL?"));
        Assert.Equal("0\r\nscpi > ", Send(engine, ":LED:GPSL?"));
        Assert.Equal("1\r\nscpi > ", Send(engine, ":LED:HOLD?"));
        Assert.Equal("+72\r\nscpi > ", Send(engine, ":STAT:OPER:COND?"));
        Assert.Equal("+2\r\nscpi > ", Send(engine, ":STAT:OPER:HOLD:COND?"));
    }

    [Fact]
    public void ASurveyAtPowerUpAnswersOnceAndItsProgressToADecimal()
    {
        (ScpiEngine engine, SimulatedReceiver receiver, FakeTimeProvider clock) = Bench();
        receiver.PowerCycle();
        Send(engine, "*CLS");

        Assert.Equal("ONCE\r\nscpi > ", Send(engine, ":GPS:POS:SURV:STAT?"));
        Assert.Equal("0\r\nscpi > ", Send(engine, ":GPS:POS:HOLD:STAT?"));

        clock.Advance(receiver.Timing.Acquisition + TimeSpan.FromMinutes(2));
        Assert.Matches(@"^\+\d+\.\d\r\nscpi > $", Send(engine, ":GPS:POS:SURV:PROG?"));
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
