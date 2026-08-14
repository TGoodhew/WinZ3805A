using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// Rendering decimal degrees back into the degrees–minutes–seconds form §10.6 shows.
/// </summary>
/// <remarks>
/// The carry is the whole difficulty. Rounding seconds to three decimals can produce 60.000, which
/// is the next minute rather than a number of seconds, and that carry can cascade into the degree.
/// Getting it wrong shifts a position by a minute of arc — about 1.8 km of latitude — silently, in
/// the one field a timing receiver exists to hold fixed.
/// </remarks>
public sealed class CoordinatesTests
{
    /// <remarks>
    /// The captured fixture's own position, printed by the receiver as
    /// <c>N  47:31:18.822</c> / <c>W 122:12:22.152</c>.
    /// </remarks>
    [Fact]
    public void TheFixturePositionRoundTripsToWhatTheReceiverPrinted()
    {
        Assert.Equal("N 47° 31′ 18.822″", Coordinates.Latitude(47 + (31 / 60.0) + (18.822 / 3600.0)));
        Assert.Equal("W 122° 12′ 22.152″", Coordinates.Longitude(-(122 + (12 / 60.0) + (22.152 / 3600.0))));
    }

    [Theory]
    [InlineData(47.5, "N 47° 30′ 00.000″")]
    [InlineData(-47.5, "S 47° 30′ 00.000″")]
    [InlineData(0.0, "N 0° 00′ 00.000″")]
    [InlineData(90.0, "N 90° 00′ 00.000″")]
    public void LatitudeCarriesItsHemisphere(double degrees, string expected) =>
        Assert.Equal(expected, Coordinates.Latitude(degrees));

    [Theory]
    [InlineData(122.25, "E 122° 15′ 00.000″")]
    [InlineData(-122.25, "W 122° 15′ 00.000″")]
    [InlineData(180.0, "E 180° 00′ 00.000″")]
    public void LongitudeCarriesItsHemisphere(double degrees, string expected) =>
        Assert.Equal(expected, Coordinates.Longitude(degrees));

    /// <remarks>
    /// There is no negative zero hemisphere. A receiver sitting on the equator or the prime
    /// meridian must not flip its letter on measurement noise.
    /// </remarks>
    [Fact]
    public void ZeroIsOnThePositiveSide()
    {
        Assert.StartsWith("N", Coordinates.Latitude(0.0), StringComparison.Ordinal);
        Assert.StartsWith("E", Coordinates.Longitude(0.0), StringComparison.Ordinal);
        Assert.StartsWith("N", Coordinates.Latitude(-0.0), StringComparison.Ordinal);
    }

    /// <summary>
    /// Seconds that round to 60 become the next minute, and 60 minutes become the next degree.
    /// </summary>
    /// <remarks>
    /// The failure this guards against does not look like a bug — it prints "47° 31′ 60.000″",
    /// which is a real-looking coordinate that no instrument would ever display.
    /// </remarks>
    [Fact]
    public void SecondsRoundingToSixtyCarryIntoTheMinute()
    {
        // 47° 31′ 59.9996″ — rounds to 60.000 seconds.
        double degrees = 47 + (31 / 60.0) + (59.9996 / 3600.0);

        Assert.Equal("N 47° 32′ 00.000″", Coordinates.Latitude(degrees));
    }

    [Fact]
    public void TheCarryCascadesThroughTheMinuteIntoTheDegree()
    {
        // 47° 59′ 59.9999″ — the seconds carry, then the minutes carry.
        double degrees = 47 + (59 / 60.0) + (59.9999 / 3600.0);

        Assert.Equal("N 48° 00′ 00.000″", Coordinates.Latitude(degrees));
    }

    /// <remarks>
    /// §11.1 forbids throwing anywhere in this path, so a value past the pole degrades to no value
    /// exactly as an unparsed field does. It cannot be rendered honestly and must not be guessed at.
    /// </remarks>
    [Theory]
    [InlineData(91.0)]
    [InlineData(-90.5)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(null)]
    public void AnImpossibleLatitudeHasNoRendering(double? degrees) =>
        Assert.Null(Coordinates.Latitude(degrees));

    [Theory]
    [InlineData(181.0)]
    [InlineData(-180.5)]
    [InlineData(null)]
    public void AnImpossibleLongitudeHasNoRendering(double? degrees) =>
        Assert.Null(Coordinates.Longitude(degrees));

    /// <remarks>
    /// A carry at exactly the pole would produce 90° 00′ 00.000″, which is legitimate, but one
    /// past it must not be rounded back into range and presented as if it were fine.
    /// </remarks>
    [Fact]
    public void ACarryToExactlyThePoleIsStillValid()
    {
        double degrees = 89 + (59 / 60.0) + (59.9999 / 3600.0);

        Assert.Equal("N 90° 00′ 00.000″", Coordinates.Latitude(degrees));
    }

    /// <remarks>
    /// Prime and double prime, not apostrophe and quotation mark. The typewriter marks are a
    /// different pair of characters and read as a quotation in the middle of a number.
    /// </remarks>
    [Fact]
    public void TheMarksAreTypographic()
    {
        string rendered = Coordinates.Latitude(47.5)!;

        Assert.Contains("′", rendered, StringComparison.Ordinal);
        Assert.Contains("″", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("'", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("\"", rendered, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Fixed widths, so a column of coordinates stays aligned (§9.5.3 rule 6). Minutes and seconds
    /// are always two integer digits; degrees are not padded, because they are the significant part
    /// and a leading zero on a latitude reads as an octal escape to nobody but looks wrong.
    /// </remarks>
    [Fact]
    public void MinutesAndSecondsAreZeroPadded() =>
        Assert.Equal("N 5° 04′ 03.200″", Coordinates.Latitude(5 + (4 / 60.0) + (3.2 / 3600.0)));

    [Fact]
    public void SplitReportsTheComponents()
    {
        (string hemisphere, int degrees, int minutes, double seconds) =
            Coordinates.Split(-47.5, "N", "S", 90)!.Value;

        Assert.Equal("S", hemisphere);
        Assert.Equal(47, degrees);
        Assert.Equal(30, minutes);
        Assert.Equal(0.0, seconds, 3);
    }
}
