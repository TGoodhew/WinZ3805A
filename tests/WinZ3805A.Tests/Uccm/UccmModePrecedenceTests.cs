using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// Which line of the status screen decides the mode, when more than one has an opinion (#534).
/// </summary>
/// <remarks>
/// <para>
/// <b>The screen can say two things at once, and it did.</b> The mode markers are scattered across
/// an 80-column screen and nothing stops several matching. The parser used to assign on every match
/// with no break, so the last one down the page won — an ordering nobody chose and which no comment
/// claimed.
/// </para>
/// <para>
/// The screens below are the real ones, from the 13 Sep 2026 transition sitting archived in
/// <c>Uccm/Captures/transitions-13sep2026.replies.txt</c>, trimmed to the lines that carry a marker.
/// </para>
/// </remarks>
public sealed class UccmModePrecedenceTests
{
    private static readonly DateTimeOffset Whenever = new(2026, 9, 13, 0, 30, 0, TimeSpan.Zero);

    /// <summary>The holdover frame: leap 18, PPS stable, antenna open, settling, date invalid.</summary>
    private static readonly byte[] HoldoverFrame =
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xD0, 0xB0, 0x62, 0x00, 0x12,
        0x60, 0x0C, 0x4F, 0x90, 0x00, 0x00, 0x00, 0x00, 0xC7, 0x74, 0xCA,
    ];

    /// <summary>The cold frame: no leap, PPS settling, antenna absent, settling, date invalid.</summary>
    private static readonly byte[] ColdFrame =
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x24, 0xEA, 0x00, 0x02, 0x00, 0x00,
        0x41, 0x08, 0x4F, 0x90, 0x00, 0x00, 0x00, 0x00, 0x47, 0xE7, 0xCA,
    ];

    private static string Screen(string statusWord, string gpsLine) =>
        string.Join(
            "\r\n",
            " 57964-80     serial number  40896646     firmware ver  2.0.1.6-01 LINK    mode",
            " Reference Status _______________________   Reference Outputs ______________",
            $" XX Ref 8KHz 1: [LOS]                          TFOM     2            FFOM      2",
            $" XX Ref 8KHz 2: [LOS]                          UCCM A Status[{statusWord}]",
            $" {gpsLine}",
            " ACQUISITION ..................................................[GPS 1PPS Valid]",
            " Tracking: 0 ____   Not Tracking: 12 _______   Time ___________________________",
            "Command complete");

    /// <summary>
    /// <b>Warming up beats a settling line further down the page.</b>
    /// </summary>
    /// <remarks>
    /// This is the screen a Trimble UCCM-P printed at 10:07 on 13 Sep 2026, four minutes after a
    /// cold power-up with the antenna freshly reconnected. It says <c>OCXO WARMUP</c> — its own word
    /// for its own state — and three lines later says the phase loop is settling. Both are true. The
    /// module had never locked, so <c>Recovery</c>, whose summary is "reacquiring after a loss of
    /// GPS", is the one reading that is definitely wrong.
    /// </remarks>
    [Fact]
    public void WarmupOutranksASettlingLineLowerDownTheScreen()
    {
        ReceiverStatus status = UccmStatusParser.Parse(
            Screen("OCXO WARMUP", ">> GPS: [phase:+0.0E+00, settling]"),
            Whenever,
            UccmProfile.Unknown);

        Assert.Equal(SmartClockMode.PowerUp, status.Mode);
        Assert.Equal("Warming up", status.ModeDetail);
    }

    /// <summary>
    /// And it beats a missing reference too, which is the same screen with the antenna still out.
    /// </summary>
    [Fact]
    public void WarmupOutranksAMissingReference()
    {
        ReceiverStatus status = UccmStatusParser.Parse(
            Screen("OCXO WARMUP", "XX GPS: [No Ref]"),
            Whenever,
            UccmProfile.Unknown);

        Assert.Equal(SmartClockMode.PowerUp, status.Mode);
        Assert.Equal("Warming up", status.ModeDetail);
    }

    /// <summary>
    /// <b>Holdover, which this parser could not previously report at all.</b>
    /// </summary>
    /// <remarks>
    /// The screen is verbatim what the module printed through fourteen minutes with its antenna
    /// physically disconnected: it still called itself <c>ACTIVE</c>, and the only thing that
    /// changed was the GPS line. Read as text that is <c>PowerUp</c> — "no reference" — because a
    /// receiver waiting for GPS and one that has lost it print the same words. The frame knows the
    /// difference.
    /// </remarks>
    [Fact]
    public void AHoldoverFrameOverridesAScreenThatStillCallsItselfActive()
    {
        ReceiverStatus status = UccmStatusParser.Parse(
            Screen("ACTIVE", "XX GPS: [No Ref]"),
            Whenever,
            UccmProfile.Unknown,
            UccmTimeCode.TryParse(HoldoverFrame));

        Assert.Equal(SmartClockMode.Holdover, status.Mode);
    }

    /// <summary>
    /// The identical screen, from a receiver that has never locked, is not holdover.
    /// </summary>
    /// <remarks>
    /// <b>Same screen, same lock byte, same date byte — different frame.</b> This is what stops the
    /// change above from turning every unreferenced receiver into a holdover report, which would be
    /// the same defect pointing the other way.
    /// </remarks>
    [Fact]
    public void TheSameScreenWithAColdFrameIsNotHoldover()
    {
        ReceiverStatus status = UccmStatusParser.Parse(
            Screen("ACTIVE", "XX GPS: [No Ref]"),
            Whenever,
            UccmProfile.Unknown,
            UccmTimeCode.TryParse(ColdFrame));

        Assert.NotEqual(SmartClockMode.Holdover, status.Mode);
    }
}
