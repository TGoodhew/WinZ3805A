using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The §10.6 readouts for the fix quality NMEA was already broadcasting (#435): geoid separation,
/// satellites used, and dilution of precision.
/// </summary>
/// <remarks>
/// A talker that reports none of them must still show three em dashes rather than three zeroes, so
/// the absent cases are tested as carefully as the present ones — that distinction is the whole
/// subject of the issue these come from.
/// </remarks>
public sealed class FixQualityReadoutTests
{
    private static readonly DateTimeOffset Captured = new(2026, 9, 13, 2, 0, 0, TimeSpan.Zero);

    private static PositionViewModel Showing(ReceiverStatus status)
    {
        FakeTimeProvider clock = new(Captured);
        ReceiverStateStore store = new(clock);
        store.UpdateFull(status);

        return new PositionViewModel(store) { Connection = ConnectionStatus.Connected };
    }

    /// <summary>The bench VK-162's own figures.</summary>
    private static ReceiverStatus Bench() => new()
    {
        HeightDatum = HeightDatum.Msl,
        Position = new GeoPosition
        {
            LatitudeDegrees = 47.5219,
            LongitudeDegrees = -122.2061,
            HeightMetres = 26.1,
            GeoidSeparationMetres = -18.8,
        },
        SatellitesUsed = 12,
        Dop = new DilutionOfPrecision { Position = 1.25, Horizontal = 0.77, Vertical = 0.98 },
        CapturedAt = Captured,
    };

    [Fact]
    public void TheSeparationIsShownWithTheEllipsoidalHeightItMakesAvailable()
    {
        string text = Showing(Bench()).GeoidSeparationText;

        // Through the formatter rather than as a literal: it renders a negative with a typographic
        // minus (U+2212), not a hyphen, so a hard-coded "-18.8" tests the wrong string and fails
        // against correct output.
        Assert.Contains(ReadoutFormatter.Format(-18.8, decimalPlaces: 1), text, StringComparison.Ordinal);

        // 26.1 above the geoid, geoid 18.8 below the ellipsoid, so 7.3 above the ellipsoid. The
        // conversion is the reason to show the separation at all.
        Assert.Contains(ReadoutFormatter.Format(7.3, decimalPlaces: 2), text, StringComparison.Ordinal);
        Assert.Contains("ellipsoid", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// Converting only makes sense from MSL. A receiver already reporting ellipsoidal height must
    /// not have the separation added to it a second time.
    /// </summary>
    [Fact]
    public void NoConversionIsOfferedWhenTheHeightIsAlreadyEllipsoidal()
    {
        ReceiverStatus status = Bench() with { HeightDatum = HeightDatum.GpsEllipsoid };

        string text = Showing(status).GeoidSeparationText;

        Assert.Contains(ReadoutFormatter.Format(-18.8, decimalPlaces: 1), text, StringComparison.Ordinal);
        Assert.DoesNotContain("ellipsoid", text, StringComparison.Ordinal);
    }

    [Fact]
    public void AllThreeDilutionFiguresAreShownOnOneLine()
    {
        string text = Showing(Bench()).DilutionText;

        Assert.Contains("1.25", text, StringComparison.Ordinal);
        Assert.Contains("0.77", text, StringComparison.Ordinal);
        Assert.Contains("0.98", text, StringComparison.Ordinal);
    }

    /// <summary>
    /// A receiver that reports two of the three must show the third as absent, not as zero — a
    /// dilution of 0 is a better fix than is physically possible.
    /// </summary>
    [Fact]
    public void AMissingDilutionFigureIsADashRatherThanAZero()
    {
        ReceiverStatus status = Bench() with
        {
            Dop = new DilutionOfPrecision { Position = 1.25, Horizontal = 0.77 },
        };

        string text = Showing(status).DilutionText;

        Assert.Contains("1.25", text, StringComparison.Ordinal);
        Assert.Contains(ReadoutFormatter.NoValue, text, StringComparison.Ordinal);
        Assert.DoesNotContain("0.00", text, StringComparison.Ordinal);
    }

    [Fact]
    public void SatellitesUsedIsShown()
    {
        Assert.Contains("12", Showing(Bench()).SatellitesUsedText, StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole point of #435: a family that cannot report these shows dashes, and a dash is not a
    /// zero, an empty string, or a confident-looking default.
    /// </summary>
    [Fact]
    public void AReceiverThatReportsNoneOfThemShowsThreeDashes()
    {
        PositionViewModel model = Showing(new ReceiverStatus
        {
            Position = new GeoPosition { LatitudeDegrees = 47.5219, LongitudeDegrees = -122.2061 },
            CapturedAt = Captured,
        });

        Assert.Equal(ReadoutFormatter.NoValue, model.GeoidSeparationText);
        Assert.Equal(ReadoutFormatter.NoValue, model.SatellitesUsedText);
        Assert.Equal(ReadoutFormatter.NoValue, model.DilutionText);
    }

    /// <summary>A separation with no height still shows, because it is a reading in its own right.</summary>
    [Fact]
    public void ASeparationWithNoHeightIsStillShown()
    {
        ReceiverStatus status = Bench() with
        {
            Position = new GeoPosition
            {
                LatitudeDegrees = 47.5219,
                LongitudeDegrees = -122.2061,
                GeoidSeparationMetres = -18.8,
            },
        };

        string text = Showing(status).GeoidSeparationText;

        Assert.Contains(ReadoutFormatter.Format(-18.8, decimalPlaces: 1), text, StringComparison.Ordinal);
        Assert.DoesNotContain("ellipsoid", text, StringComparison.Ordinal);
    }
}
