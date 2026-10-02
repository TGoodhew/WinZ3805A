using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Simulation.SmartClock;

namespace WinZ3805A.Tests.SmartClock;

/// <summary>
/// The comparison mode, which is the only part of the simulator that talks to real hardware (#639).
/// </summary>
/// <remarks>
/// It is run against the bench receiver by hand, so what can be checked here is that it cannot do
/// anything but read: every query it would send is read-only, and the guard refuses the two queries
/// that act on the receiver and every setter.
/// </remarks>
public sealed class ComparisonTests
{
    [Fact]
    public void EveryQueryTheComparisonSendsIsReadOnly()
    {
        Assert.All(Comparison.QueryList, query => Assert.True(Comparison.IsReadOnly(query), query));
    }

    [Theory]
    [InlineData("*TST?")]
    [InlineData(":DIAG:TEST? GPS")]
    [InlineData(":DIAGNOSTIC:TEST? ALL")]
    [InlineData(":GPS:REF:ADEL 7.7E-8")]
    [InlineData(":SYNC:HOLD:INIT")]
    [InlineData("*CLS")]
    public void TheGuardRefusesAnythingThatActs(string command)
    {
        Assert.False(Comparison.IsReadOnly(command));
    }

    [Fact]
    public void EveryQueryTheComparisonSendsIsOneTheSimulatorKnows()
    {
        FakeTimeProvider clock = new(new DateTimeOffset(2026, 10, 2, 12, 0, 0, TimeSpan.Zero));
        SimulatedReceiver receiver = new(clock);
        receiver.StartLocked();
        ScpiEngine engine = new(receiver, clock);

        foreach (string query in Comparison.QueryList)
        {
            engine.Receive(query);
        }

        Assert.DoesNotContain(-113, engine.QueuedErrors);
    }

    [Theory]
    [InlineData(" +3\r\nscpi > ", " +7\r\nscpi > ")]
    [InlineData(" -5.4E-009\r\nscpi > ", " +3.5E-008\r\nscpi > ")]
    [InlineData(" +9\r\nscpi > ", " +10\r\nscpi > ")]
    [InlineData(" +6.00000E+002,0\r\nscpi > ", " +1.23456E+003,1\r\nscpi > ")]
    public void ValuesThatDifferOnlyInDigitsAndSignHaveTheSameShape(string a, string b)
    {
        Assert.Equal(Comparison.Shape(a), Comparison.Shape(b));
    }

    [Theory]
    [InlineData(" +3\r\nscpi > ", "+3\r\nscpi > ")]
    [InlineData(" +6.00000E+002\r\nscpi > ", " +6.0E+02\r\nscpi > ")]
    [InlineData(" 0\r\nscpi > ", " +0\r\nscpi > ")]
    public void AFormatDifferenceIsADifferentShape(string a, string b)
    {
        // A missing leading space, a shorter exponent, an unsigned boolean: exactly the guesses the
        // comparison exists to test.
        Assert.NotEqual(Comparison.Shape(a), Comparison.Shape(b));
    }
}
