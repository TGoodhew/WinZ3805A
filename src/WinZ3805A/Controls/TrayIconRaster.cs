namespace WinZ3805A.Controls;

/// <summary>
/// Draws §9.4.3's severity shapes as pixels, for the P1-10 tray icon and the #274 taskbar overlay.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not XAML.</b> Every other severity surface in the application goes through
/// <c>SeverityPill</c>, and §9.13 is emphatic that it should. The shell's two surfaces — the tray
/// icon, and since #274 the taskbar overlay — are the places that cannot: the shell wants an
/// <c>HICON</c>, which is a block of pixels, and there is no path from a <c>Path</c> in a visual
/// tree to one. So the shapes are restated here — the same circle, triangle, hexagon and ring, from
/// the same table — and rasterised directly.
/// </para>
/// <para>
/// <b>The shape is doing the work, not the colour.</b> §9.4.3's rule applies everywhere, but in the
/// tray it is not a courtesy to colour-blind users alone: the icon is 16 logical pixels on a strip
/// the user is not looking at, often against a background whose brightness they did not choose. A
/// caution amber and a critical red at that size, seen peripherally, are one colour. A triangle and
/// a hexagon are not.
/// </para>
/// <para>
/// <b>It is pure arithmetic on purpose.</b> Everything here is a function from a severity and a size
/// to a pixel buffer, so the shapes can be asserted distinct in a headless test rather than squinted
/// at in a screenshot of a taskbar.
/// </para>
/// </remarks>
public static class TrayIconRaster
{
    /// <summary>Sub-samples per axis, so each pixel is averaged over this many squared points.</summary>
    /// <remarks>
    /// Four is enough. The shapes are convex and the icon is tiny, so the only visible artefact is
    /// staircasing on the triangle's slopes, and 16 samples removes it. Higher costs work no user
    /// will ever see: the buffer is redrawn when the receiver changes mode, not per frame.
    /// </remarks>
    private const int SubSamples = 4;

    /// <summary>
    /// Renders one severity shape as a premultiplied BGRA buffer, top row first.
    /// </summary>
    /// <param name="severity">Which shape to draw.</param>
    /// <param name="size">Width and height in pixels.</param>
    /// <param name="fill">The shape's colour.</param>
    /// <returns><paramref name="size"/> squared pixels, 0xAARRGGBB per entry.</returns>
    /// <remarks>
    /// Premultiplied because that is what <c>CreateIconIndirect</c> wants of a 32-bit DIB, and
    /// handing it straight alpha produces an icon with a pale halo on a dark taskbar — which looks
    /// like a rendering bug rather than the format mistake it is.
    /// </remarks>
    public static uint[] Render(Severity severity, int size, Rgb fill)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(size, 4);

        uint[] pixels = new uint[size * size];
        double step = 1.0 / (SubSamples * size);

        for (int y = 0; y < size; y++)
        {
            for (int x = 0; x < size; x++)
            {
                int hits = 0;

                for (int sy = 0; sy < SubSamples; sy++)
                {
                    for (int sx = 0; sx < SubSamples; sx++)
                    {
                        // Centre of this sub-sample, mapped to -1..1 with the shape inscribed.
                        double u = (((x * SubSamples) + sx + 0.5) * step * 2) - 1;
                        double v = (((y * SubSamples) + sy + 0.5) * step * 2) - 1;

                        if (IsInside(severity, u, v))
                        {
                            hits++;
                        }
                    }
                }

                if (hits == 0)
                {
                    continue;
                }

                // Premultiply: at partial coverage the colour scales with the alpha.
                byte alpha = (byte)(hits * 255 / (SubSamples * SubSamples));

                pixels[(y * size) + x] =
                    ((uint)alpha << 24)
                    | ((uint)(fill.R * alpha / 255) << 16)
                    | ((uint)(fill.G * alpha / 255) << 8)
                    | (uint)(fill.B * alpha / 255);
            }
        }

        return pixels;
    }

    /// <summary>
    /// Whether a point in the -1..1 square falls inside the shape §9.4.3 gives this severity.
    /// </summary>
    /// <remarks>
    /// The circle, ring and circled i are inset to 0.92 rather than filling the square, and the
    /// triangle's apex sits on the same inset. A tray icon is composited against a strip whose
    /// height it nearly matches, and a triangle whose apex touches the top edge reads as clipped.
    /// The triangle's base reaches 0.95 and the hexagon's circumradius is 0.96; both are noted
    /// where they are declared.
    /// </remarks>
    private static bool IsInside(Severity severity, double x, double y) => severity switch
    {
        // ● A filled circle. Locked.
        Severity.Success => (x * x) + (y * y) <= 0.92 * 0.92,

        // ○ A ring. Unknown or powering up - hollow because nothing is being asserted.
        Severity.Neutral =>
            (x * x) + (y * y) <= 0.92 * 0.92 && (x * x) + (y * y) >= 0.52 * 0.52,

        // ▲ A triangle, apex up. Recovering or waiting.
        Severity.Caution => InsidePolygon(Triangle, x, y),

        // ⬢ A hexagon. Holdover or failure.
        Severity.Critical => InsidePolygon(Hexagon, x, y),

        // ⓘ A ring with a bar, the nearest a 16-pixel icon gets to a circled "i". Never reached
        // from a receiver mode - no mode maps to Info - but the enum has five values and a
        // rasteriser that threw on one of them would be a crash waiting for a sixth caller.
        Severity.Info =>
            ((x * x) + (y * y) <= 0.92 * 0.92 && (x * x) + (y * y) >= 0.62 * 0.62)
            || (Math.Abs(x) <= 0.16 && y >= -0.30 && y <= 0.45),

        _ => false,
    };

    /// <summary>
    /// A triangle, apex up, sized to the box rather than inscribed in the circle.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Given explicitly instead of as a regular polygon, for two reasons that pull the same way.
    /// An equilateral triangle inscribed in the same circle as the other shapes covers barely half
    /// their area, so "recovering" would read as a smaller, quieter symbol than "unknown" through
    /// size alone — and enlarging it to compensate pushes the apex past the top of the icon, where
    /// the shell clips it flat. That clipping is visible: it was, before these numbers replaced a
    /// circumradius of 1.06.
    /// </para>
    /// <para>
    /// So the apex sits on the same 0.92 inset as everything else and the base is widened instead.
    /// Not equilateral, slightly broader than tall, and the right shape for a 16-pixel triangle.
    /// </para>
    /// </remarks>
    private static (double X, double Y)[] Triangle { get; } =
        [(0, -0.92), (-0.95, 0.72), (0.95, 0.72)];

    /// <summary>
    /// A regular hexagon with a vertex at the top, circumradius 0.96 — a little past the 0.92 inset
    /// the round shapes use.
    /// </summary>
    private static (double X, double Y)[] Hexagon { get; } = Regular(6, -Math.PI / 2, 0.96);

    /// <summary>The vertices of a regular polygon.</summary>
    /// <param name="sides">How many.</param>
    /// <param name="rotation">Where the first vertex sits, in radians.</param>
    /// <param name="radius">Circumradius.</param>
    private static (double X, double Y)[] Regular(int sides, double rotation, double radius)
    {
        (double X, double Y)[] vertices = new (double, double)[sides];

        for (int i = 0; i < sides; i++)
        {
            double angle = rotation + (2 * Math.PI * i / sides);
            vertices[i] = (radius * Math.Cos(angle), radius * Math.Sin(angle));
        }

        return vertices;
    }

    /// <summary>Whether a point is inside a convex polygon wound consistently.</summary>
    /// <remarks>
    /// The cross product of each edge with the point-to-vertex vector has the same sign for every
    /// edge exactly when the point is inside. Only valid for convex polygons, which all of these
    /// are.
    /// </remarks>
    private static bool InsidePolygon((double X, double Y)[] vertices, double x, double y)
    {
        bool negative = false;
        bool positive = false;

        for (int i = 0; i < vertices.Length; i++)
        {
            (double ax, double ay) = vertices[i];
            (double bx, double by) = vertices[(i + 1) % vertices.Length];

            double cross = ((bx - ax) * (y - ay)) - ((by - ay) * (x - ax));

            if (cross > 0)
            {
                positive = true;
            }
            else if (cross < 0)
            {
                negative = true;
            }

            if (positive && negative)
            {
                return false;
            }
        }

        return true;
    }
}
