using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The power-on banner a talker prints once (#515): what it says, and the two ways reading it could
/// go wrong.
/// </summary>
/// <remarks>
/// <para>
/// Measured on the bench before any of this was written: one burst of twelve <c>$GxTXT</c> sentences
/// and then nothing across the next five cycles. <b>That is the whole difficulty</b> — the value has
/// to survive cycles that say nothing, without a cycle that says nothing being read as a receiver
/// that has gone quiet.
/// </para>
/// <para>
/// The second trap is subtler: <c>TXT</c> is free text with a talker prefix, so a device could emit
/// one saying anything. It must never be what claims a receiver.
/// </para>
/// </remarks>
public sealed class NmeaBannerTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);

    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }

    /// <summary>The forM8N's own burst, copied from <c>form8n-fix-lost.nmea</c>.</summary>
    private static string PowerOnBurst() => string.Join('\n',
    [
        Sentence("GNTXT,01,01,02,u-blox AG - www.u-blox.com"),
        Sentence("GNTXT,01,01,02,HW UBX-M8130 00080000"),
        Sentence("GNTXT,01,01,02,ROM CORE 3.01 (107888)"),
        Sentence("GNTXT,01,01,02,FWVER=SPG 3.01"),
        Sentence("GNTXT,01,01,02,PROTVER=18.00"),
        Sentence("GNTXT,01,01,02,ANTSUPERV=AC SD PDoS SR"),
        Sentence("GNTXT,01,01,02,ANTSTATUS=OK"),
        Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
    ]);

    [Fact]
    public void TheBurstYieldsHardwareFirmwareProtocolAndAntenna()
    {
        TalkerBanner? banner = NmeaStatusParser.Parse(PowerOnBurst(), Now).Banner;

        Assert.NotNull(banner);
        Assert.Equal("UBX-M8130", banner.Hardware);
        Assert.Equal("SPG 3.01", banner.Firmware);
        Assert.Equal("18.00", banner.ProtocolVersion);
        Assert.Equal(AntennaState.Ok, banner.Antenna);
    }

    /// <summary>
    /// The ROM checksum after the part number identifies a build, not a receiver, and would make
    /// every comparison unique.
    /// </summary>
    [Fact]
    public void TheHardwareIsThePartNumberWithoutItsChecksum()
    {
        TalkerBanner? banner = NmeaStatusParser.Parse(PowerOnBurst(), Now).Banner;

        Assert.Equal("UBX-M8130", banner?.Hardware);
    }

    /// <summary>An ordinary cycle says nothing about the receiver, and must say so as null.</summary>
    /// <remarks>
    /// Null rather than an empty banner is what lets the store tell "this cycle did not say" from
    /// "this receiver has nothing to say", and therefore what stops a remembered banner being wiped
    /// a second after it arrives.
    /// </remarks>
    [Fact]
    public void ACycleWithNoBannerReportsNone()
    {
        string ordinary = string.Join('\n',
        [
            Sentence("GNRMC,015955.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
            Sentence("GNGGA,015955.00,4731.31483,N,12212.36635,W,1,12,0.77,26.1,M,-18.8,M,,"),
        ]);

        Assert.Null(NmeaStatusParser.Parse(ordinary, Now).Banner);
    }

    /// <summary>
    /// Only the notice type is read. An error-class TXT is the receiver complaining, and filing a
    /// complaint as an identity would be reading a fault as a fact.
    /// </summary>
    [Theory]
    [InlineData("00")]
    [InlineData("01")]
    [InlineData("07")]
    public void OnlyANoticeIsTreatedAsABanner(string messageType)
    {
        string cycle = string.Join('\n',
        [
            Sentence($"GNTXT,01,01,{messageType},ANTSTATUS=SHORT"),
            Sentence("GNRMC,015955.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
        ]);

        Assert.Null(NmeaStatusParser.Parse(cycle, Now).Banner);
    }

    [Theory]
    [InlineData("OK", AntennaState.Ok)]
    [InlineData("INIT", AntennaState.Initialising)]
    [InlineData("SHORT", AntennaState.ShortCircuit)]
    [InlineData("OPEN", AntennaState.OpenCircuit)]
    [InlineData("DONTKNOW", AntennaState.DoNotKnow)]
    public void EveryAntennaWordIsRead(string word, AntennaState expected)
    {
        string cycle = string.Join('\n',
        [
            Sentence($"GNTXT,01,01,02,ANTSTATUS={word}"),
            Sentence("GNRMC,015955.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
        ]);

        Assert.Equal(expected, NmeaStatusParser.Parse(cycle, Now).Banner?.Antenna);
    }

    // -------------------------------------------------------------------------------------
    // Retention, which is the point of the whole exercise
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The banner survives the cycles that say nothing — otherwise it would show for one second a
    /// session and be gone.
    /// </summary>
    [Fact]
    public void TheStoreRemembersABannerAcrossSilentCycles()
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Now));

        store.UpdateFull(NmeaStatusParser.Parse(PowerOnBurst(), Now), FastFields.None);
        for (int cycle = 0; cycle < 5; cycle++)
        {
            store.UpdateFull(new ReceiverStatus { CapturedAt = Now }, FastFields.None);
        }

        Assert.Equal("UBX-M8130", store.Banner.Hardware);
        Assert.Equal(AntennaState.Ok, store.Banner.Antenna);
    }

    /// <summary>
    /// <c>ANTSTATUS</c> is re-sent on change and arrives alone. Taking the new record wholesale would
    /// throw away the hardware and firmware that came with the power-on burst.
    /// </summary>
    [Fact]
    public void AnAntennaChangeUpdatesTheAntennaAndKeepsTheRest()
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Now));
        store.UpdateFull(NmeaStatusParser.Parse(PowerOnBurst(), Now), FastFields.None);

        string later = string.Join('\n',
        [
            Sentence("GNTXT,01,01,02,ANTSTATUS=OPEN"),
            Sentence("GNRMC,015959.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
        ]);
        store.UpdateFull(NmeaStatusParser.Parse(later, Now), FastFields.None);

        Assert.Equal(AntennaState.OpenCircuit, store.Banner.Antenna);
        Assert.Equal("UBX-M8130", store.Banner.Hardware);
        Assert.Equal("SPG 3.01", store.Banner.Firmware);
    }

    /// <summary>
    /// #492's lesson applied: a different receiver must not inherit the previous one's banner. This
    /// is why retention lives in the store and not on the driver, which is a singleton.
    /// </summary>
    [Fact]
    public void ChangingDeviceForgetsTheBanner()
    {
        ReceiverStateStore store = new(new FakeTimeProvider(Now));
        store.BeginDevice(ReceiverStateStore.KeyFor("COM5", null));
        store.UpdateFull(NmeaStatusParser.Parse(PowerOnBurst(), Now), FastFields.None);

        Assert.Equal("UBX-M8130", store.Banner.Hardware);

        store.BeginDevice(ReceiverStateStore.KeyFor("COM7", null));

        Assert.True(store.Banner.IsEmpty);
    }

    // -------------------------------------------------------------------------------------
    // TXT must never be what claims a receiver
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The banner is kept for reading and is not evidence. A device emitting only free text with a
    /// GNSS prefix is not thereby a GNSS receiver.
    /// </summary>
    [Fact]
    public void ABannerAloneDoesNotClaimAReceiver()
    {
        IReceiverDriver driver = new NmeaDriver(new FakeTimeProvider(Now));

        string bannerOnly = string.Join('\n',
        [
            Sentence("GNTXT,01,01,02,HW UBX-M8130 00080000"),
            Sentence("GNTXT,01,01,02,ANTSTATUS=OK"),
        ]);

        Assert.Null(driver.Overhear([.. bannerOnly.Split('\n')]));
    }

    /// <summary>But it is kept, or a session connecting during the burst would discard it.</summary>
    [Fact]
    public void ABannerLineIsStillKeptForTheParser()
    {
        IReceiverDriver driver = new NmeaDriver(new FakeTimeProvider(Now));

        Assert.NotNull(driver.ClassifyLine(Sentence("GNTXT,01,01,02,ANTSTATUS=OK")));
    }
}
