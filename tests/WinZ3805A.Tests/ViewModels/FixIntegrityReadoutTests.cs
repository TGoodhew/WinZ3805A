using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// What the position uncertainty and the integrity report say on screen (#516).
/// </summary>
/// <remarks>
/// <para>
/// Two readings a talker can supply and a status screen never carries, once the receiver has been
/// asked for <c>GST</c> and <c>GBS</c>. The parser tests pin the numbers; these pin the two things a
/// number alone cannot get right — the confidence the deviations are quoted at, and the difference
/// between good news and no news.
/// </para>
/// <para>
/// <b>A sigma with no stated confidence is wrong by a factor of two to half its readers.</b> Survey
/// figures are usually quoted at 95%, these are 1σ at about 65%, and the two differ by roughly
/// double. The text has to say which, and that is an assertion rather than a matter of taste.
/// </para>
/// </remarks>
public sealed class FixIntegrityReadoutTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 12, 17, 0, 0, TimeSpan.Zero);

    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }

    private static ReceiverStateStore Store(params string[] bodies)
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Now));

        string[] cycle =
        [
            "GNRMC,004546.00,A,4731.31405,N,12212.36602,W,0.02,,120926,,,A",
            "GNGGA,004546.00,4731.31405,N,12212.36602,W,1,12,0.74,24.9,M,-18.8,M,,",
            "GNGSA,A,3,08,30,07,14,04,27,09,05,22,,,,1.36,0.74,1.14,1",
            .. bodies,
        ];

        store.UpdateFull(
            NmeaStatusParser.Parse(string.Join('\n', cycle.Select(Sentence)), Now),
            FastFields.None);

        return store;
    }

    // Connected, or every readout reports an em dash whatever the store holds - which would make
    // the absent cases below pass for the wrong reason.
    private static PositionViewModel Position(params string[] bodies) =>
        new(Store(bodies)) { Connection = ConnectionStatus.Connected };

    private static OverviewViewModel Overview(params string[] bodies) =>
        new(Store(bodies), new NmeaDriver(new FakeTimeProvider(Now)))
        {
            Connection = ConnectionStatus.Connected,
        };

    // -------------------------------------------------------------------------------------
    // Position uncertainty
    // -------------------------------------------------------------------------------------

    /// <summary>The bench figures, combined and labelled.</summary>
    /// <remarks>
    /// 1.8 m and 2.6 m combine to 3.2 m horizontally, and the altitude deviation is reported as it
    /// stands. The confidence is in the string because a reader has no other way to know it.
    /// </remarks>
    [Fact]
    public void TheUncertaintyReadsAsMetresWithItsConfidence()
    {
        string text = Position("GNGST,004546.00,23,,,,1.8,2.6,4.0").UncertaintyText;

        Assert.Contains("3.2 m", text, StringComparison.Ordinal);
        Assert.Contains("4.0 m", text, StringComparison.Ordinal);
        Assert.Contains("1σ", text, StringComparison.Ordinal);
        Assert.Contains("65%", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A receiver that sends no <c>GST</c> shows the same em dash as any other absent reading.
    /// </summary>
    /// <remarks>
    /// This is the ordinary state, because nothing in the application enables the sentence. It must
    /// not read as a fault, and it must not read as a position known to be perfect.
    /// </remarks>
    [Fact]
    public void NoGstReadsAsNoValue() =>
        Assert.Equal("—", Position().UncertaintyText);

    /// <summary>
    /// The uncertainty is not the dilution, and the bench cycle is the case that proves it matters.
    /// </summary>
    /// <remarks>
    /// A horizontal dilution of 0.74 reads as an excellent fix, and the same cycle's horizontal
    /// error is over three metres. Anyone shown only the first would draw the wrong conclusion,
    /// which is the argument #516 makes for carrying both.
    /// </remarks>
    [Fact]
    public void TheTwoFiguresAreDifferentAndBothAreShown()
    {
        PositionViewModel model = Position("GNGST,004546.00,23,,,,1.8,2.6,4.0");

        Assert.Contains("H 0.74", model.DilutionText, StringComparison.Ordinal);
        Assert.Contains("3.2 m", model.UncertaintyText, StringComparison.Ordinal);
    }

    // -------------------------------------------------------------------------------------
    // Integrity
    // -------------------------------------------------------------------------------------

    /// <summary>A clean report says so, in words, and is not an error state.</summary>
    [Fact]
    public void ACleanIntegrityReportReadsAsCheckedAndOk()
    {
        OverviewViewModel model = Overview("GNGBS,004546.00,1.8,2.6,4.0,,,,,,");

        Assert.Equal("Integrity checked — no fault", model.IntegrityText);
        Assert.True(model.IntegrityOk);
    }

    /// <summary>A suspect satellite is named, and the pill is not a success.</summary>
    /// <remarks>
    /// Naming it is the point: sending a user to the sky plot to look for "a faulty satellite" with
    /// no number is sending them to look at thirty dots.
    /// </remarks>
    [Fact]
    public void AFlaggedSatelliteIsNamed()
    {
        OverviewViewModel model = Overview("GNGBS,015509.00,-0.031,-0.006,0.008,21,0.000,-33.5,5.1");

        Assert.Equal("Satellite 21 flagged faulty", model.IntegrityText);
        Assert.False(model.IntegrityOk);
    }

    /// <summary>
    /// <b>No report at all shows nothing, rather than showing good news nobody gave.</b>
    /// </summary>
    /// <remarks>
    /// The assertion this file exists for. A receiver that was never asked for <c>GBS</c> has no
    /// satellite to name, exactly like a receiver that looked and found nothing — so a pill driven
    /// off <c>FaultSuspected</c> alone would tell every user in the world their constellation had
    /// been checked and passed. Null hides the pill; only a real report shows one.
    /// </remarks>
    [Fact]
    public void AReceiverNeverAskedShowsNoIntegrityPillAtAll()
    {
        OverviewViewModel model = Overview();

        Assert.Null(model.IntegrityText);

        // And the severity flag on its own would have said "fine", which is why the text is what
        // decides whether anything is shown.
        Assert.True(model.IntegrityOk);
    }

    /// <summary>A disconnected session reports nothing, whatever the last cycle held.</summary>
    [Fact]
    public void ADisconnectedSessionShowsNoIntegrity()
    {
        OverviewViewModel model = new(
            Store("GNGBS,004546.00,1.8,2.6,4.0,,,,,,"),
            new NmeaDriver(new FakeTimeProvider(Now)))
        {
            Connection = ConnectionStatus.Disconnected,
        };

        Assert.Null(model.IntegrityText);
    }
}
