using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Simulation.SmartClock;

namespace WinZ3805A.Tests.SmartClock;

/// <summary>
/// The simulator's control channel, where a test harness changes the simulated world.
/// </summary>
public sealed class ControlCommandsTests
{
    private static (SimulatedReceiver Receiver, ScpiEngine Engine, SimulatorLink Link) Bench()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 3, 12, 0, 0, TimeSpan.Zero));
        SimulatedReceiver receiver = new(clock);
        ScpiEngine engine = new(receiver, clock);
        return (receiver, engine, new SimulatorLink(engine, clock));
    }

    /// <remarks>
    /// Manual QA section 21 imports a history under a receiver other than the one it came from. The
    /// serial must keep its case: the control line is lowered to match words, and a serial read from
    /// the lowered words would be a different receiver again.
    /// </remarks>
    [Fact]
    public void SerialPutsADifferentUnitOnTheCableKeepingTheRestOfTheIdentity()
    {
        (SimulatedReceiver receiver, ScpiEngine engine, SimulatorLink link) = Bench();

        string answer = ControlCommands.Apply("serial 3625A99999", receiver, engine, link);

        Assert.Equal("SYMMETRICOM,Z3805A,3625A99999,1.01.03-A", receiver.Identity);
        Assert.StartsWith("ok:", answer, StringComparison.Ordinal);
    }

    [Fact]
    public void HelpNamesTheSerialCommand() =>
        Assert.Contains("serial <number>", ControlCommands.Help, StringComparison.Ordinal);
}
