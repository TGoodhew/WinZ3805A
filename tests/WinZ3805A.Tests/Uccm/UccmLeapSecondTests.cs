using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// GPS − UTC reaches the model from the broadcast time code (#481).
/// </summary>
/// <remarks>
/// <para>
/// The UCCM answers none of §10.14's <c>:PTIM:LEAP</c> queries, so before this the Time page showed
/// an em dash for GPS − UTC. It states the offset in every binary time code instead, and the whole
/// point of parsing those frames is that the figure is then available without asking.
/// </para>
/// <para>
/// <b>18 is the real answer, not a fixture convention.</b> GPS − UTC has stood at 18 s since 2017,
/// and the frame below is the one captured on 12 Sep 2026 from
/// <c>TRIMBLE,57964-80,40896646,V2.0.1.6-01</c>.
/// </para>
/// </remarks>
public class UccmLeapSecondTests
{
    private static readonly DateTimeOffset Whenever = new(2026, 9, 11, 20, 31, 32, TimeSpan.Zero);

    /// <summary>The captured frame, whose offset 32 reads 0x12.</summary>
    private static ReadOnlySpan<byte> CapturedFrame =>
    [
        0xC5, 0x00, 0x80, 0x00, 0x00, 0x00, 0x00, 0x28, 0x1C, 0x52, 0x00,
        0x00, 0x20, 0x60, 0xC1, 0x91, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        0x00, 0x00, 0x00, 0x00, 0x00, 0x57, 0xCF, 0x27, 0xA4, 0x00, 0x12,
        0x60, 0x04, 0x45, 0x80, 0x00, 0x00, 0x00, 0x00, 0x10, 0xAA, 0xCA,
    ];

    /// <summary>A broadcast code puts the offset on the status the rest of the app reads.</summary>
    [Fact]
    public void TheBroadcastOffsetReachesTheStatus()
    {
        UccmTimeCode? code = UccmTimeCode.TryParse(CapturedFrame);
        Assert.NotNull(code);

        ReceiverStatus status = UccmStatusParser.Parse(null, Whenever, UccmProfile.Unknown, code);

        Assert.Equal(18, status.GpsUtcOffsetSeconds);
    }

    /// <summary>
    /// With no code yet, the offset is null rather than zero.
    /// </summary>
    /// <remarks>
    /// Zero is a legal GPS − UTC and was the true one until 1981, so it cannot double as "not
    /// known". §11.1's em dash is what an unread value looks like.
    /// </remarks>
    [Fact]
    public void NoBroadcastMeansNoOffsetRatherThanZero()
    {
        ReceiverStatus status = UccmStatusParser.Parse(null, Whenever, UccmProfile.Unknown);

        Assert.Null(status.GpsUtcOffsetSeconds);
    }

    /// <summary>
    /// The driver carries the offset through from a frame it was handed.
    /// </summary>
    /// <remarks>
    /// The path the application actually uses: the transport lifts a frame, the session hands it to
    /// <see cref="IReceiverDriver.Observe"/>, and the next parse carries it. Asserted end to end
    /// because each half of it was written separately.
    /// </remarks>
    [Fact]
    public void TheDriverCarriesTheOffsetFromAnObservedFrame()
    {
        IReceiverDriver driver = new UccmDriver(TimeProvider.System);

        Assert.Null(driver.Parse(null).GpsUtcOffsetSeconds);

        driver.Observe([CapturedFrame.ToArray()]);

        Assert.Equal(18, driver.Parse(null).GpsUtcOffsetSeconds);
    }

    /// <summary>
    /// A malformed frame teaches the driver nothing, and does not throw.
    /// </summary>
    /// <remarks>
    /// §11.1 applies to a broadcast as it does to a reply. A frame that cannot be read is no
    /// reading — never a partial one, because an offset built from a misread byte is wrong rather
    /// than absent.
    /// </remarks>
    [Fact]
    public void AMalformedFrameIsIgnoredRatherThanTrusted()
    {
        IReceiverDriver driver = new UccmDriver(TimeProvider.System);

        byte[] broken = CapturedFrame.ToArray();
        broken[^1] = 0x00;

        driver.Observe([broken]);

        Assert.Null(driver.Parse(null).GpsUtcOffsetSeconds);
    }

    /// <summary>
    /// The SmartClock reports nothing here, which is what makes the query the authority for it.
    /// </summary>
    /// <remarks>
    /// The guard on the fallback. §10.14's <c>:PTIM:LEAP:ACC?</c> is a query whose answer belongs to
    /// the page that asked; nothing about it reaches <see cref="ReceiverStatus"/>. If this ever
    /// started returning a value, the Time page's precedence would silently begin choosing between
    /// two sources rather than falling back to one.
    /// </remarks>
    [Fact]
    public void TheSmartClockStatusCarriesNoBroadcastOffset()
    {
        IReceiverDriver driver = new SmartClockDriver(TimeProvider.System);

        Assert.Null(driver.Parse(null).GpsUtcOffsetSeconds);
    }
}
