using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;

namespace WinZ3805A.Tests.Drivers;

/// <summary>
/// Which families can ever report the three fix-quality readings #435 added, and which say so.
/// </summary>
/// <remarks>
/// <para>
/// <b>The dangerous direction is a wrong <see langword="false"/></b> — the interface's own remarks
/// say so: telling a user their receiver can never report something it reports perfectly well stops
/// them looking, where a blank only leaves them waiting. So the SmartClock's three declines are
/// pinned here against the reason they were made, which is the captured status screen: it prints
/// <c>Tracking:</c> and <c>Not Tracking:</c> counts and <c>HGT +38.00 m (MSL)</c>, and carries no
/// dilution figure and no geoid separation anywhere.
/// </para>
/// <para>
/// The UCCM family is deliberately absent from these tests. It has never met a receiver outside one
/// bench sitting, and its own remarks warn against claims made from a source tree — so it stays at
/// the interface default, which shows an em dash and asserts nothing.
/// </para>
/// </remarks>
public sealed class FixQualityDeclarationTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);

    private static readonly ReceiverReading[] FixQuality =
    [
        ReceiverReading.DilutionOfPrecision,
        ReceiverReading.GeoidSeparation,
        ReceiverReading.SatellitesUsed,
    ];

    /// <summary>
    /// The talker reports all three, which is the half that matters most: these exist as readings
    /// at all because NMEA broadcasts them (#435).
    /// </summary>
    /// <remarks>
    /// The SmartClock's side of this — that it declines exactly these three and nothing else — is
    /// pinned by <c>ReceiverReadingTests.TheSmartClockReportsEveryReadingItsStatusScreenCarries</c>,
    /// which checks every member rather than only the three and so catches a driver that began
    /// declining a fourth.
    /// </remarks>
    [Theory]
    [MemberData(nameof(TheThree))]
    public void ATalkerReportsAllThree(ReceiverReading reading)
    {
        IReceiverDriver driver = new NmeaDriver(new FakeTimeProvider(Now));

        Assert.True(driver.Reports(reading));
    }

    public static TheoryData<ReceiverReading> TheThree()
    {
        TheoryData<ReceiverReading> data = [];
        foreach (ReceiverReading reading in FixQuality)
        {
            data.Add(reading);
        }

        return data;
    }
}
