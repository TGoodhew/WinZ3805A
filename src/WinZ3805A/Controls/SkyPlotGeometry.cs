namespace WinZ3805A.Controls;

/// <summary>
/// The polar projection behind <c>SkyPlotControl</c>, and the two scales it draws with.
/// </summary>
/// <remarks>
/// <para>
/// Separated from the control because it is the half that can be wrong silently. A marker at the
/// wrong azimuth still looks like a sky plot, and P0-9's acceptance criterion is that six
/// satellites land at correct polar positions — which is a statement about arithmetic, testable
/// without a window.
/// </para>
/// <para>
/// The projection is §10.5's: <b>north up, 0° elevation at the rim, 90° at the centre</b>, azimuth
/// clockwise from north. That is the convention every GPS receiver's own screen uses, and it is
/// not the mathematical one — azimuth runs clockwise where a polar plot's angle runs
/// anticlockwise, and the radius runs <i>inward</i> as elevation rises. Both inversions are here,
/// once.
/// </para>
/// </remarks>
public static class SkyPlotGeometry
{
    /// <summary>Elevation at the rim of the plot.</summary>
    public const double HorizonDegrees = 0;

    /// <summary>Elevation at the centre of the plot.</summary>
    public const double ZenithDegrees = 90;

    /// <summary>
    /// Projects an elevation and azimuth onto the plot, as an offset from its centre in pixels.
    /// </summary>
    /// <param name="elevationDegrees">Degrees above the horizon. Clamped to 0–90.</param>
    /// <param name="azimuthDegrees">Degrees clockwise from north. Wrapped into 0–360.</param>
    /// <param name="radius">The plot's radius in pixels — the distance from centre to horizon.</param>
    /// <returns>An offset in pixels, x rightward (east) and y downward (south).</returns>
    public static (double X, double Y) Project(double elevationDegrees, double azimuthDegrees, double radius)
    {
        double elevation = Math.Clamp(elevationDegrees, HorizonDegrees, ZenithDegrees);

        // Inward as elevation rises: the zenith is one point at the centre, not a ring at the rim.
        double distance = radius * (1 - (elevation / ZenithDegrees));

        // Azimuth is clockwise from north, so it is measured from -Y and turns toward +X. Screen Y
        // grows downward, which is why the cosine term is subtracted rather than added.
        double radians = Wrap(azimuthDegrees) * Math.PI / 180;

        return (distance * Math.Sin(radians), -distance * Math.Cos(radians));
    }

    /// <summary>
    /// The radius of the dashed elevation-mask circle, in pixels from the centre.
    /// </summary>
    /// <remarks>
    /// The same projection as a satellite at that elevation, which is the point: a marker inside
    /// this circle is above the mask and one outside it is below, with no arithmetic asked of the
    /// reader.
    /// </remarks>
    public static double MaskRadius(double maskDegrees, double radius) =>
        radius * (1 - (Math.Clamp(maskDegrees, HorizonDegrees, ZenithDegrees) / ZenithDegrees));

    /// <summary>
    /// The radius of a satellite marker, in pixels.
    /// </summary>
    /// <param name="strength">The reading, or null when the receiver did not report one.</param>
    /// <param name="scale">Which scale that reading is on.</param>
    /// <param name="minimum">Marker radius at the bottom of the scale.</param>
    /// <param name="maximum">Marker radius at the top.</param>
    /// <remarks>
    /// <b>Area scales with strength, not radius</b> — §9.10.2 says area, and it is the right
    /// choice: apparent size is judged by area, so scaling the radius linearly makes a strong
    /// satellite look far stronger than it is. Radius therefore goes as the square root of the
    /// normalised reading, between the two bounding areas.
    /// </remarks>
    public static double MarkerRadius(int? strength, SignalStrengthScale scale, double minimum, double maximum)
    {
        double fraction = Normalise(strength, scale);

        double minimumArea = minimum * minimum;
        double maximumArea = maximum * maximum;

        return Math.Sqrt(minimumArea + (fraction * (maximumArea - minimumArea)));
    }

    /// <summary>
    /// Which step of §9.4.4's seven-step sequential ramp a reading falls on, from 1 to 7.
    /// </summary>
    /// <remarks>
    /// The ramp is read by lightness and is monotonic, so the mapping is a plain linear bucket. An
    /// unreported reading takes the lowest step rather than a separate colour: the marker's
    /// <i>shape</i> already says whether it is tracked, and inventing an eighth colour for "unknown"
    /// would break a ramp whose whole value is that it has an order.
    /// </remarks>
    public static int RampStep(int? strength, SignalStrengthScale scale, int steps = 7)
    {
        if (steps < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(steps), steps, "A ramp needs at least one step.");
        }

        double fraction = Normalise(strength, scale);
        int step = (int)Math.Floor(fraction * steps) + 1;

        return Math.Clamp(step, 1, steps);
    }

    /// <summary>
    /// A reading as a fraction of its scale, 0 to 1. Null and unknown scales read as 0.
    /// </summary>
    public static double Normalise(int? strength, SignalStrengthScale scale)
    {
        if (!scale.IsKnown || strength is null)
        {
            return 0;
        }

        double span = scale.Maximum - scale.Minimum;
        if (span <= 0)
        {
            return 0;
        }

        return Math.Clamp((scale.Clamp(strength) - scale.Minimum) / span, 0, 1);
    }

    /// <summary>Brings an azimuth into 0–360, so a receiver reporting 370 or −10 still plots.</summary>
    private static double Wrap(double azimuthDegrees)
    {
        double wrapped = azimuthDegrees % 360;
        return wrapped < 0 ? wrapped + 360 : wrapped;
    }
}
