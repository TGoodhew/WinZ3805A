using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Nmea;

/// <summary>
/// The fix-quality figures that were arriving every second and being discarded (#435): dilution of
/// precision, geoid separation, satellites used, and the differential correction's age and station.
/// </summary>
/// <remarks>
/// <para>
/// The #435 audit's §4 is the source. Each of these was already in the committed captures — the
/// parser read <c>GSA</c> for its 2D/3D mode and nothing else, and <c>GGA</c> for everything except
/// the four fields after the altitude.
/// </para>
/// <para>
/// <b>The offsets are the whole risk here</b>, so they are pinned against the real captures as well
/// as against synthetic sentences. A field index that is wrong by one still parses, still produces a
/// plausible number, and would be believed.
/// </para>
/// </remarks>
public sealed class NmeaFixQualityTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 6, 20, 0, 0, TimeSpan.Zero);

    private static string Sentence(string body)
    {
        byte checksum = 0;
        foreach (char c in body)
        {
            checksum ^= (byte)c;
        }

        return $"${body}*{checksum:X2}";
    }

    /// <summary>A cycle copied field for field from <c>vk162-steady-state.nmea</c>.</summary>
    private static string BenchCycle() => string.Join('\n',
    [
        Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
        Sentence("GNGGA,015954.00,4731.31483,N,12212.36635,W,1,12,0.77,26.1,M,-18.8,M,,"),
        Sentence("GNGSA,A,3,07,05,08,22,14,15,30,17,21,09,,,1.25,0.77,0.98,1"),
        Sentence("GNGSA,A,3,24,26,06,25,,,,,,,,,1.25,0.77,0.98,4"),
    ]);

    [Fact]
    public void TheThreeDilutionFiguresAreRead()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        Assert.NotNull(status.Dop);
        Assert.Equal(1.25, status.Dop.Position);
        Assert.Equal(0.77, status.Dop.Horizontal);
        Assert.Equal(0.98, status.Dop.Vertical);
    }

    /// <summary>
    /// The offsets do not move when the twelve satellite slots are not full. A GSA always carries
    /// all twelve, empty or not, and a reader that counted commas instead would drift.
    /// </summary>
    [Fact]
    public void TheDilutionFiguresSurviveAMostlyEmptySatelliteList()
    {
        string sparse = Sentence("GNGSA,A,3,24,26,06,25,,,,,,,,,1.25,0.77,0.98,4");

        ReceiverStatus status = NmeaStatusParser.Parse(
            string.Join('\n', [Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"), sparse]),
            Now);

        Assert.NotNull(status.Dop);
        Assert.Equal(1.25, status.Dop.Position);
        Assert.Equal(0.98, status.Dop.Vertical);
    }

    [Fact]
    public void ACycleWithNoGsaReportsNoDilutionRatherThanZeroes()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(
            Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
            Now);

        Assert.Null(status.Dop);
    }

    /// <summary>An empty record would read as "all three are zero", which is an excellent fix.</summary>
    [Fact]
    public void AGsaWithNoDilutionFieldsReportsNothingRatherThanAnEmptyRecord()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(
            string.Join('\n',
            [
                Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
                Sentence("GNGSA,A,3,07,05,08,22,14,15,30,17,21,09,,,,,,1"),
            ]),
            Now);

        Assert.Null(status.Dop);
    }

    [Fact]
    public void GeoidSeparationIsReadFromGgaAndIsNegativeOnTheBench()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        Assert.Equal(-18.8, status.Position?.GeoidSeparationMetres);

        // And it is not the altitude, which sits two fields earlier and is positive here.
        Assert.Equal(26.1, status.Position?.HeightMetres);
    }

    /// <summary>
    /// GNS has no separation field. Reading one from GGA's offset would take whatever follows.
    /// </summary>
    [Fact]
    public void ACycleWithGnsAndNoGgaReportsNoSeparation()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(
            string.Join('\n',
            [
                Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
                Sentence("GNGNS,015954.00,4731.31483,N,12212.36635,W,DN,12,0.77,26.1,-18.8,,"),
            ]),
            Now);

        Assert.Equal(26.1, status.Position?.HeightMetres);
        Assert.Null(status.Position?.GeoidSeparationMetres);
    }

    /// <summary>
    /// Satellites used is not satellites tracked, and the bench cycle shows both — twelve used
    /// against a GSV list this cycle does not even carry.
    /// </summary>
    [Fact]
    public void SatellitesUsedIsReadAndIsSeparateFromTracked()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        Assert.Equal(12, status.SatellitesUsed);
        Assert.Empty(status.Tracked);
    }

    [Fact]
    public void ADifferentialFixReportsTheCorrectionsAgeAndStation()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(
            string.Join('\n',
            [
                Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
                Sentence("GNGGA,015954.00,4731.31483,N,12212.36635,W,2,12,0.77,26.1,M,-18.8,M,6.0,0123"),
            ]),
            Now);

        Assert.Equal(6.0, status.DifferentialAgeSeconds);
        Assert.Equal("0123", status.DifferentialStationId);
    }

    /// <summary>
    /// The station id keeps its leading zeros, because it is an identifier and never arithmetic.
    /// </summary>
    [Fact]
    public void TheStationIdIsTextRatherThanANumber()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(
            string.Join('\n',
            [
                Sentence("GNRMC,015954.00,A,4731.31483,N,12212.36635,W,0.02,,080926,,,A"),
                Sentence("GNGGA,015954.00,4731.31483,N,12212.36635,W,2,12,0.77,26.1,M,-18.8,M,6.0,0007"),
            ]),
            Now);

        Assert.Equal("0007", status.DifferentialStationId);
    }

    /// <summary>An ordinary fix carries neither, and empty fields must not become a value.</summary>
    [Fact]
    public void ANonDifferentialFixReportsNeitherAgeNorStation()
    {
        ReceiverStatus status = NmeaStatusParser.Parse(BenchCycle(), Now);

        Assert.Null(status.DifferentialAgeSeconds);
        Assert.Null(status.DifferentialStationId);
    }

    /// <summary>
    /// Against every committed capture: whatever is read must be credible, because a wrong offset
    /// produces a plausible number rather than a failure.
    /// </summary>
    [Theory]
    [MemberData(nameof(NmeaCaptureReplayTests.Captures), MemberType = typeof(NmeaCaptureReplayTests))]
    public void EveryCaptureYieldsCredibleFixQuality(string capture)
    {
        string text = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Nmea", "Captures", capture));

        ReceiverStatus status = NmeaStatusParser.Parse(text, Now);

        if (status.Dop is { } dop)
        {
            // Dimensionless and positive. Above 50 is not a fix anyone would use, and is what an
            // off-by-one into a satellite id or a system id would look like.
            foreach (double? figure in new[] { dop.Position, dop.Horizontal, dop.Vertical })
            {
                if (figure is { } value)
                {
                    Assert.InRange(value, 0.1, 50.0);
                }
            }

            // Geometry: a 3D figure cannot be better than the horizontal one it contains.
            if (dop is { Position: { } pdop, Horizontal: { } hdop })
            {
                Assert.True(pdop >= hdop, $"PDOP {pdop} is better than HDOP {hdop} in {capture}");
            }
        }

        if (status.SatellitesUsed is { } used)
        {
            Assert.InRange(used, 0, 64);
        }

        if (status.Position?.GeoidSeparationMetres is { } separation)
        {
            // The geoid runs about -107 m to +85 m against the ellipsoid, worldwide.
            Assert.InRange(separation, -120.0, 100.0);
        }
    }
}
