using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// Which sweeps are readings at all, and which are somebody else's reply (#209).
/// </summary>
/// <remarks>
/// <para>
/// §11.1 turns an <i>unparseable</i> field into null, and that is right for a field. It is no help
/// against a sweep that parsed perfectly and means nothing: after a link misalignment on 24 Aug 2026
/// the sync state held a diagnostic log dump, and the same sweep carried a time interval of two
/// seconds and an EFC of <b>+2 %</b>.
/// </para>
/// <para>
/// <b>That second value is why the discriminator is the sync state rather than a range check.</b>
/// +2 % is inside the oscillator's control range and indistinguishable from a real reading by
/// magnitude alone. What identifies it is the company it keeps — the same sweep's sync state was not
/// a state this receiver reports.
/// </para>
/// <para>
/// The rule is exercised here through <c>ReceiverModes.FromSyncState</c>, which holds the closed set
/// and is what <c>PollingService</c> asks. Keeping the test on that boundary means it says what the
/// guard means — "is this a state the receiver has?" — rather than restating the guard's code.
/// </para>
/// </remarks>
public class IncoherentReadingTests
{
    /// <summary>Everything the receiver actually reports (§7.3, §11.1).</summary>
    [Theory]
    [InlineData("LOCK")]
    [InlineData("REC")]
    [InlineData("WAIT")]
    [InlineData("HOLD")]
    [InlineData("POW")]
    [InlineData("OFF")]
    public void EveryStateTheReceiverReportsIsAReading(string syncState) =>
        Assert.NotEqual(ReceiverMode.Disconnected, ReceiverModes.FromSyncState(syncState));

    /// <summary>Whitespace and case are the receiver's, not ours (§7.2: replies carry a leading space).</summary>
    [Theory]
    [InlineData(" LOCK")]
    [InlineData("LOCK ")]
    [InlineData("lock")]
    [InlineData(" Lock ")]
    public void TheReceiversOwnSpacingAndCaseStillCount(string syncState) =>
        Assert.Equal(ReceiverMode.Locked, ReceiverModes.FromSyncState(syncState));

    /// <remarks>
    /// The value that was actually stored on 24 Aug, truncated at the front because the read began
    /// mid-stream — the leading H of HOLDOVER is missing, which is the tell.
    /// </remarks>
    [Fact]
    public void TheSweepThatStartedThisIsNotAReading()
    {
        const string what = "OLDOVER STARTED, NOT TRACKING GPS\r\n"
            + "LOG 215:20070108.12:04:16:  GPS LOCK STARTED\r\n"
            + "LOG 216:20070108.14:55:09:  HOLDOVER STARTED, NOT TRACKING GPS";

        Assert.Equal(ReceiverMode.Disconnected, ReceiverModes.FromSyncState(what));
    }

    /// <remarks>
    /// And the sweep after it, which carried an EFC value where the sync state belongs. This is the
    /// one a range check cannot catch: its EFC of +2 % is a number the oscillator could genuinely
    /// sit at.
    /// </remarks>
    [Theory]
    [InlineData("-1.68368E+001")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("LOG 222:20070108.22:40:38:  HOLDOVER STARTED")]
    public void NeitherIsAnythingElseThatArrivedInItsPlace(string? syncState) =>
        Assert.Equal(ReceiverMode.Disconnected, ReceiverModes.FromSyncState(syncState));

    /// <remarks>
    /// <b>The cost of the rule, stated rather than discovered.</b> A state this application has not
    /// been taught reads as incoherent, so its sweeps are dropped — a receiver reporting something
    /// unfamiliar would log nothing while looking healthy. That is why every rejection is logged at
    /// Information with what it saw, and why this test exists to name the trade rather than to
    /// assert it is free.
    /// </remarks>
    [Theory]
    [InlineData("ACQ")]
    [InlineData("SURV")]
    public void AStateThisApplicationHasNotBeenTaughtIsAlsoDropped(string syncState) =>
        Assert.Equal(ReceiverMode.Disconnected, ReceiverModes.FromSyncState(syncState));
}
