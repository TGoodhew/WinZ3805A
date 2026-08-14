using WinZ3805A.Controls;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The §10.5 polar projection (P0-9).
/// </summary>
/// <remarks>
/// The half of the sky plot that can be wrong silently. A marker at the wrong azimuth still looks
/// like a sky plot — nothing about the picture says the east and west halves have been swapped —
/// so the positions are checked against hand-computed values rather than against a screenshot.
/// </remarks>
public class SkyPlotGeometryTests
{
    private const double Radius = 100;
    private const double Tolerance = 1e-9;

    private static SignalStrengthScale CarrierToNoise =>
        SignalStrengthScale.For(SignalStrengthKind.CarrierToNoise);

    // -------------------------------------------------------------------------------------
    // The projection
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// North up, and clockwise from there. Screen Y grows downward, so north is negative Y and
    /// south positive — the inversion that turns a plot upside down if it is missed.
    /// </summary>
    [Theory]
    [InlineData(0, 0, -100)]      // north, at the rim
    [InlineData(90, 100, 0)]      // east
    [InlineData(180, 0, 100)]     // south
    [InlineData(270, -100, 0)]    // west
    public void AzimuthRunsClockwiseFromNorthAtTheHorizon(double azimuth, double x, double y)
    {
        (double actualX, double actualY) = SkyPlotGeometry.Project(0, azimuth, Radius);

        Assert.Equal(x, actualX, Tolerance);
        Assert.Equal(y, actualY, Tolerance);
    }

    /// <summary>
    /// 0° elevation at the rim and 90° at the centre. Radius runs <em>inward</em> as elevation
    /// rises, which is the other inversion.
    /// </summary>
    [Theory]
    [InlineData(0, 100)]
    [InlineData(30, 200.0 / 3)]
    [InlineData(45, 50)]
    [InlineData(60, 100.0 / 3)]
    [InlineData(90, 0)]
    public void ElevationRunsInwardFromTheRim(double elevation, double expectedDistance)
    {
        (double x, double y) = SkyPlotGeometry.Project(elevation, 0, Radius);

        Assert.Equal(expectedDistance, Math.Sqrt((x * x) + (y * y)), 1e-9);
    }

    /// <summary>The zenith is one point at the centre, not a ring, whatever azimuth comes with it.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(137)]
    [InlineData(359)]
    public void TheZenithIsTheCentreWhateverTheAzimuth(double azimuth)
    {
        (double x, double y) = SkyPlotGeometry.Project(90, azimuth, Radius);

        Assert.Equal(0, x, Tolerance);
        Assert.Equal(0, y, Tolerance);
    }

    /// <summary>
    /// P0-9's own example, from §10.5's wireframe: PRN 19 at elevation 65°, azimuth 52°. Worked by
    /// hand — distance is 100 × (1 − 65/90) = 27.7778, then east 27.7778 sin 52° and north
    /// 27.7778 cos 52°. Both are inside the upper-right quadrant, which is where 52° belongs.
    /// </summary>
    [Fact]
    public void PlacesTheWireframesOwnExample()
    {
        (double x, double y) = SkyPlotGeometry.Project(65, 52, Radius);

        Assert.Equal(21.8892, x, 1e-4);
        Assert.Equal(-17.1017, y, 1e-4);

        // North-east: right of centre and above it.
        Assert.True(x > 0 && y < 0);
    }

    /// <summary>A receiver reporting a wrapped or negative azimuth still plots where it should.</summary>
    [Theory]
    [InlineData(370, 10)]
    [InlineData(-10, 350)]
    [InlineData(720, 0)]
    public void AzimuthIsWrappedRatherThanRejected(double reported, double equivalent)
    {
        (double x1, double y1) = SkyPlotGeometry.Project(20, reported, Radius);
        (double x2, double y2) = SkyPlotGeometry.Project(20, equivalent, Radius);

        Assert.Equal(x2, x1, 1e-9);
        Assert.Equal(y2, y1, 1e-9);
    }

    /// <summary>Out-of-range elevations clamp rather than escaping the plot.</summary>
    [Theory]
    [InlineData(-5, 100)]
    [InlineData(120, 0)]
    public void ElevationIsClampedToTheVisibleHemisphere(double elevation, double expectedDistance)
    {
        (double x, double y) = SkyPlotGeometry.Project(elevation, 0, Radius);

        Assert.Equal(expectedDistance, Math.Sqrt((x * x) + (y * y)), 1e-9);
    }

    // -------------------------------------------------------------------------------------
    // The elevation-mask circle
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The mask circle uses the same projection as a satellite at that elevation. That is the whole
    /// point of drawing it: a marker inside the circle is above the mask and one outside is below,
    /// with no arithmetic asked of the reader.
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(10)]
    [InlineData(15)]
    [InlineData(40)]
    public void TheMaskCircleSitsWhereASatelliteAtThatElevationWould(double mask)
    {
        (double x, double y) = SkyPlotGeometry.Project(mask, 0, Radius);

        Assert.Equal(Math.Sqrt((x * x) + (y * y)), SkyPlotGeometry.MaskRadius(mask, Radius), 1e-9);
    }

    // -------------------------------------------------------------------------------------
    // Marker size
    // -------------------------------------------------------------------------------------

    /// <summary>The bounds are exactly the bounds — a full-scale reading is the maximum radius.</summary>
    [Fact]
    public void MarkerRadiusSpansExactlyTheGivenBounds()
    {
        Assert.Equal(4, SkyPlotGeometry.MarkerRadius(26, CarrierToNoise, 4, 9), 1e-9);
        Assert.Equal(9, SkyPlotGeometry.MarkerRadius(55, CarrierToNoise, 4, 9), 1e-9);
    }

    /// <summary>
    /// <b>Area scales, not radius.</b> §9.10.2 says area, and it matters: apparent size is judged
    /// by area, so a linear radius would make a mid-scale satellite look far stronger than it is.
    /// At the midpoint the marker's area is the mean of the two bounding areas.
    /// </summary>
    [Fact]
    public void MarkerAreaScalesLinearlyWithStrength()
    {
        // The scale runs 26-55, so its exact midpoint is 40.5 and no integer reading sits on it.
        // The assertion is therefore against the fraction the reading actually represents, which
        // is the property under test rather than a happened-to-be-round number.
        const int reading = 40;
        double fraction = SkyPlotGeometry.Normalise(reading, CarrierToNoise);
        double radius = SkyPlotGeometry.MarkerRadius(reading, CarrierToNoise, 4, 9);

        double expectedArea = (4 * 4) + (fraction * ((9 * 9) - (4 * 4)));
        Assert.Equal(expectedArea, radius * radius, 1e-9);

        // The linear-radius answer would be 4 + fraction x 5; scaling area gives a larger radius,
        // and that difference is the whole point of §9.10.2 saying area.
        Assert.True(
            radius > 4 + (fraction * 5),
            $"Radius {radius} suggests the radius, not the area, is being scaled.");
    }

    /// <summary>An unreported reading takes the smallest marker rather than none at all.</summary>
    [Fact]
    public void AnUnreportedStrengthDrawsTheSmallestMarker() =>
        Assert.Equal(4, SkyPlotGeometry.MarkerRadius(null, CarrierToNoise, 4, 9), 1e-9);

    /// <summary>A reading past either end of the scale cannot overflow the marker.</summary>
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    public void MarkerRadiusStaysInsideItsBounds(int strength)
    {
        double radius = SkyPlotGeometry.MarkerRadius(strength, CarrierToNoise, 4, 9);

        Assert.InRange(radius, 4, 9);
    }

    // -------------------------------------------------------------------------------------
    // The sequential ramp
    // -------------------------------------------------------------------------------------

    /// <summary>Seven steps, and both ends reachable.</summary>
    [Fact]
    public void TheRampSpansAllSevenSteps()
    {
        Assert.Equal(1, SkyPlotGeometry.RampStep(26, CarrierToNoise));
        Assert.Equal(7, SkyPlotGeometry.RampStep(55, CarrierToNoise));
    }

    /// <summary>It never leaves the range, whatever the receiver reports.</summary>
    [Theory]
    [InlineData(-100)]
    [InlineData(0)]
    [InlineData(40)]
    [InlineData(9999)]
    public void TheRampStepIsAlwaysInRange(int strength) =>
        Assert.InRange(SkyPlotGeometry.RampStep(strength, CarrierToNoise), 1, 7);

    /// <summary>
    /// Monotonic. The ramp's whole value is that it has an order, so a stronger satellite must
    /// never come out a lighter colour than a weaker one.
    /// </summary>
    [Fact]
    public void TheRampNeverGoesBackwards()
    {
        int previous = 0;

        for (int strength = 26; strength <= 55; strength++)
        {
            int step = SkyPlotGeometry.RampStep(strength, CarrierToNoise);
            Assert.True(step >= previous, $"C/N {strength} stepped back from {previous} to {step}.");
            previous = step;
        }
    }

    /// <summary>
    /// The two scales are never mixed. C/N 30 is a weak signal near the bottom of 26–55; SS 30 is
    /// near the bottom of 0–255 as well, but the same number on the wrong scale would land five
    /// steps apart — which is the mistake §9.10.2 warns about for the strength bar and which the
    /// plot would repeat.
    /// </summary>
    [Fact]
    public void EachScaleIsNormalisedAgainstItsOwnRange()
    {
        SignalStrengthScale ss = SignalStrengthScale.For(SignalStrengthKind.SignalStrength);

        Assert.Equal(0.1379, SkyPlotGeometry.Normalise(30, CarrierToNoise), 1e-4);
        Assert.Equal(0.1176, SkyPlotGeometry.Normalise(30, ss), 1e-4);

        // And a reading that is strong on one scale is weak on the other.
        Assert.Equal(1.0, SkyPlotGeometry.Normalise(55, CarrierToNoise), 1e-9);
        Assert.Equal(0.2157, SkyPlotGeometry.Normalise(55, ss), 1e-4);
    }

    /// <summary>An unknown scale normalises to zero rather than dividing by its own emptiness.</summary>
    [Fact]
    public void AnUnknownScaleNormalisesToZero() =>
        Assert.Equal(0, SkyPlotGeometry.Normalise(49, SignalStrengthScale.For(SignalStrengthKind.Unknown)));

    /// <summary>A ramp of no steps is a programming error, not a rounding one.</summary>
    [Fact]
    public void ARampNeedsAtLeastOneStep() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => SkyPlotGeometry.RampStep(40, CarrierToNoise, steps: 0));
}
