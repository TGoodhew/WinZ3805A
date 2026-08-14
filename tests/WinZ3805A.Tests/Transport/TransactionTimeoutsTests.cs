using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>The §7.2 timeout classes, and the command-to-class mapping.</summary>
public class TransactionTimeoutsTests
{
    [Fact]
    public void TheThreeClassesAreTheFiguresSpecifiedInSection72()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(3000), TransactionTimeouts.Default);
        Assert.Equal(TimeSpan.FromMilliseconds(15000), TransactionTimeouts.StatusScreen);
        Assert.Equal(TimeSpan.FromMilliseconds(30000), TransactionTimeouts.SelfTest);
    }

    /// <summary>§10.12 gives auto-detect 2 s per attempt, and eight attempts have to fit inside P0-1's 20 s.</summary>
    [Fact]
    public void EightAutoDetectProbesFitInsideTheTwentySecondBudget()
    {
        TimeSpan total = TransactionTimeouts.AutoDetectProbe * SerialSettings.AutoDetectSequence.Count;

        Assert.Equal(TimeSpan.FromMilliseconds(2000), TransactionTimeouts.AutoDetectProbe);
        Assert.True(total <= TimeSpan.FromSeconds(20), $"eight probes would take {total.TotalSeconds:F0} s");
    }

    /// <summary>SCPI lets short and long node spellings be mixed freely, so every combination maps alike.</summary>
    [Theory]
    [InlineData(":SYST:STAT?")]
    [InlineData(":SYSTem:STATus?")]
    [InlineData(":SYST:STATUS?")]
    [InlineData(":SYSTEM:STAT?")]
    [InlineData("SYST:STAT?")]
    [InlineData(":syst:stat?")]
    [InlineData("  :SYST:STAT?  ")]
    public void TheStatusScreenGetsTheLongTimeoutInEverySpelling(string command)
        => Assert.Equal(TransactionTimeouts.StatusScreen, TransactionTimeouts.For(command));

    [Theory]
    [InlineData("*TST?")]
    [InlineData(":DIAG:TEST?")]
    [InlineData(":DIAGnostic:TEST?")]
    public void TestCommandsGetTheThirtySecondTimeout(string command)
        => Assert.Equal(TransactionTimeouts.SelfTest, TransactionTimeouts.For(command));

    /// <summary>
    /// <c>:SYST:STAT:LENG?</c> is the trap: a cheap scalar sharing two nodes with the expensive
    /// screen. Prefix matching would hand it fifteen seconds to stall the poller in.
    /// </summary>
    [Theory]
    [InlineData(":SYST:STAT:LENG?")]
    [InlineData(":SYNC:TINT?")]
    [InlineData(":GPS:SAT:TRAC:COUN?")]
    [InlineData("*IDN?")]
    [InlineData(":SYST:ERR?")]
    public void EverythingElseGetsTheDefaultTimeout(string command)
        => Assert.Equal(TransactionTimeouts.Default, TransactionTimeouts.For(command));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void AnEmptyCommandIsRejected(string command)
        => Assert.Throws<ArgumentException>(() => TransactionTimeouts.For(command));

    /// <summary>
    /// The whole diagnostic log gets a minute, because it is far larger than anything else.
    /// </summary>
    /// <remarks>
    /// Found by the 3 s default timing out against the reference unit, whose log was full at the
    /// documented maximum of 222 entries — about 15 kB, or 16 seconds at 9600 baud. §7.2 names
    /// three timeout classes and none of them covers this.
    /// </remarks>
    [Theory]
    [InlineData(":DIAG:LOG:READ:ALL?")]
    [InlineData(":DIAGNOSTIC:LOG:READ:ALL?")]
    [InlineData("diag:log:read:all?")]
    public void TheWholeLogReadGetsItsOwnTimeout(string command) =>
        Assert.Equal(TransactionTimeouts.DiagnosticLog, TransactionTimeouts.For(command));

    /// <remarks>
    /// Reading one entry is a scalar by any measure. Sharing the whole-log class would give a cheap
    /// query a minute to hang the caller in — the same trap :SYST:STAT:LENG? is kept out of.
    /// </remarks>
    [Fact]
    public void ASingleLogEntryKeepsTheDefault()
    {
        Assert.Equal(TransactionTimeouts.Default, TransactionTimeouts.For(":DIAG:LOG:READ?"));
        Assert.Equal(TransactionTimeouts.Default, TransactionTimeouts.For(":DIAG:LOG:COUN?"));
    }
}
