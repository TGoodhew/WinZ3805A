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

    // -------------------------------------------------------------------------------------
    // The position-commit class (#256)
    // -------------------------------------------------------------------------------------

    /// <summary>The command that was measured, in the spelling the catalog uses.</summary>
    /// <remarks>
    /// <b>The regression this exists for.</b> On 28 Aug 2026 pressing Cancel survey reported
    /// "Couldn't restore last position. The receiver did not answer within 3 seconds" while the
    /// receiver had done exactly what was asked — the survey ended and the held position came back
    /// to the digit. Timing the same command directly gave 9.67 s to a clean prompt, against the
    /// 3 s default it was getting.
    /// </remarks>
    [Fact]
    public void RestoringTheLastPositionGetsTheCommitClass() =>
        Assert.Equal(TransactionTimeouts.PositionCommit, TransactionTimeouts.For(":GPS:POSition LAST"));

    /// <summary>Adopt gets it too, on reasoning rather than measurement.</summary>
    /// <remarks>
    /// It ends a survey and commits a position by the same route as the command that was measured,
    /// and is reachable only from the same state. It has never been run against hardware, so leaving
    /// it at 3 s would mean rediscovering #256 the first time somebody adopts a surveyed position —
    /// by watching a working command report failure, which is the whole defect.
    /// </remarks>
    [Fact]
    public void AdoptingASurveyedPositionGetsTheCommitClass() =>
        Assert.Equal(TransactionTimeouts.PositionCommit, TransactionTimeouts.For(":GPS:POSition SURVey"));

    /// <summary>So does the manual setter, which commits a position the same way.</summary>
    [Fact]
    public void SettingAFixedPositionGetsTheCommitClass() =>
        Assert.Equal(TransactionTimeouts.PositionCommit, TransactionTimeouts.For(":GPS:POSition"));

    /// <summary>Every legal spelling of each, because SCPI lets a caller mix long and short nodes.</summary>
    [Theory]
    [InlineData(":GPS:POS LAST")]
    [InlineData("GPS:POSITION LAST")]
    [InlineData(":gps:position last")]
    [InlineData(":GPS:POS SURV")]
    [InlineData(":GPS:POSition SURVey")]
    [InlineData(":GPS:POS")]
    public void EverySpellingOfACommitCommandGetsTheCommitClass(string command) =>
        Assert.Equal(TransactionTimeouts.PositionCommit, TransactionTimeouts.For(command));

    /// <summary>Starting a survey is deliberately not in the class.</summary>
    /// <remarks>
    /// <b>The boundary is the point.</b> It shares the subtree and it is a tier C position command,
    /// so the tempting rule is "everything under :GPS:POSition". But it answers promptly — observed
    /// four times returning −300 well inside the default — and starting an accumulation is not the
    /// same work as tearing one down. A class that swallowed it would give a genuinely dead link
    /// thirty seconds to look alive in, for no evidence.
    /// </remarks>
    [Fact]
    public void StartingASurveyKeepsTheDefault() =>
        Assert.Equal(
            TransactionTimeouts.Default,
            TransactionTimeouts.For(":GPS:POSition:SURVey:STATe ONCE"));

    /// <summary>The header alone is not a licence for everything beneath it.</summary>
    /// <remarks>
    /// The same reasoning the existing tests apply to <c>:SYST:STAT:LENG?</c>, which is a cheap
    /// scalar sharing two nodes with the expensive screen. Matching is exact, not by prefix.
    /// </remarks>
    [Theory]
    [InlineData(":GPS:POSition:SURVey:STATe?")]
    [InlineData(":GPS:POSition?")]
    public void NeighboursInTheSameSubtreeKeepTheDefault(string command) =>
        Assert.Equal(TransactionTimeouts.Default, TransactionTimeouts.For(command));

    /// <summary>The class is well clear of what was measured, and of the default.</summary>
    /// <remarks>
    /// One sample on one unit in one state is enough to prove 3 s wrong and nowhere near enough to
    /// characterise the distribution, so the margin is deliberate rather than fitted. Pinned so a
    /// later tightening back towards 9.67 s has to argue with this comment first.
    /// </remarks>
    [Fact]
    public void TheCommitClassKeepsAMarginOverTheMeasurement()
    {
        TimeSpan measured = TimeSpan.FromSeconds(9.67);

        Assert.True(
            TransactionTimeouts.PositionCommit >= measured * 3,
            $"expected at least three times the measured 9.67 s, got {TransactionTimeouts.PositionCommit}");
        Assert.True(TransactionTimeouts.PositionCommit > TransactionTimeouts.Default);
    }

}
