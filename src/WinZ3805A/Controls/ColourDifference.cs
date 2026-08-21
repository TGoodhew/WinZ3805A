namespace WinZ3805A.Controls;

/// <summary>One colour in the CIE L*a*b* space, which is where perceptual distance is measured.</summary>
/// <param name="L">Lightness, 0 to 100.</param>
/// <param name="A">Green to red.</param>
/// <param name="B">Blue to yellow.</param>
public readonly record struct LabColour(double L, double A, double B);

/// <summary>
/// How far apart two colours look, by CIEDE2000 (§9.4.2).
/// </summary>
/// <remarks>
/// <para>
/// §9.4.2 makes one hard promise: the semantic palette stays unambiguous. A user whose Windows
/// accent is red must not end up with an application where "selected navigation item" and "critical
/// alarm" are the same colour. Enforcing that needs a number for "the same colour", and the number
/// has to match human judgement rather than arithmetic on channel values.
/// </para>
/// <para>
/// <b>Why not simply compare RGB.</b> Euclidean distance in sRGB says <c>#0E7C86</c> and
/// <c>#B22B2B</c> are far apart, and also says two dark navies nobody can tell apart are far apart.
/// It measures the wrong thing: sRGB is a storage format, not a perceptual one. CIEDE2000 is the
/// current CIE recommendation and carries corrections for exactly the places simpler formulae fail —
/// the blue region, near-neutral colours, and lightness at the ends of the range.
/// </para>
/// <para>
/// <b>It is not a small formula and it is not worth approximating.</b> The rotation term for blues
/// and the three weighting functions are what make the answer agree with an eye. A cheaper distance
/// would put the threshold in §9.4.2 somewhere other than where it was calibrated, which is worse
/// than no guard: it would pass an accent that collides and warn about one that does not.
/// </para>
/// </remarks>
public static class ColourDifference
{
    /// <summary>
    /// Below this, §9.4.2 treats an accent as colliding with a semantic colour.
    /// </summary>
    /// <remarks>
    /// Twenty is generous by the standards of the formula — a ΔE₀₀ of 2 is roughly the smallest
    /// difference a person reliably notices side by side. It is set high on purpose: the question
    /// here is not "can these be told apart in a swatch" but "could a glance at a navigation item
    /// be mistaken for an alarm", and a glance is a much weaker instrument than a comparison.
    /// </remarks>
    public const double CollisionThreshold = 20;

    /// <summary>Converts an sRGB triple, 0 to 255, to L*a*b* under the D65 white point.</summary>
    /// <remarks>
    /// Two stages, and the first is the one that is easy to leave out: sRGB values are gamma
    /// encoded, so they have to be linearised before any of the arithmetic means anything. Skipping
    /// it produces numbers that look plausible and are wrong everywhere except pure black and white.
    /// </remarks>
    public static LabColour ToLab(byte red, byte green, byte blue)
    {
        double r = Linearise(red / 255.0);
        double g = Linearise(green / 255.0);
        double b = Linearise(blue / 255.0);

        // sRGB to CIE XYZ, D65.
        double x = (r * 0.4124564) + (g * 0.3575761) + (b * 0.1804375);
        double y = (r * 0.2126729) + (g * 0.7151522) + (b * 0.0721750);
        double z = (r * 0.0193339) + (g * 0.1191920) + (b * 0.9503041);

        // Normalised against the D65 white point.
        double fx = Pivot(x / 0.95047);
        double fy = Pivot(y / 1.00000);
        double fz = Pivot(z / 1.08883);

        return new LabColour(
            (116 * fy) - 16,
            500 * (fx - fy),
            200 * (fy - fz));

        static double Linearise(double channel) =>
            channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);

        static double Pivot(double value) =>
            value > 0.008856 ? Math.Cbrt(value) : ((903.3 * value) + 16) / 116;
    }

    /// <summary>The CIEDE2000 difference between two colours given as sRGB triples.</summary>
    public static double Between(
        (byte R, byte G, byte B) first,
        (byte R, byte G, byte B) second) =>
        Between(
            ToLab(first.R, first.G, first.B),
            ToLab(second.R, second.G, second.B));

    /// <summary>
    /// The CIEDE2000 difference between two L*a*b* colours.
    /// </summary>
    /// <remarks>
    /// Follows the CIE's own formulation with unit weighting factors. The variable names are the
    /// formula's rather than English ones on purpose: this is arithmetic that has to be checked
    /// against the published definition, and renaming its terms makes that harder, not easier.
    /// </remarks>
    public static double Between(LabColour first, LabColour second)
    {
        const double degrees = 180.0 / Math.PI;
        const double radians = Math.PI / 180.0;

        double c1 = Math.Sqrt((first.A * first.A) + (first.B * first.B));
        double c2 = Math.Sqrt((second.A * second.A) + (second.B * second.B));
        double meanC = (c1 + c2) / 2;

        // The chroma correction. 25^7 is a constant of the formula, not a tunable.
        double meanC7 = Math.Pow(meanC, 7);
        double g = 0.5 * (1 - Math.Sqrt(meanC7 / (meanC7 + Math.Pow(25, 7))));

        double a1 = (1 + g) * first.A;
        double a2 = (1 + g) * second.A;

        double cp1 = Math.Sqrt((a1 * a1) + (first.B * first.B));
        double cp2 = Math.Sqrt((a2 * a2) + (second.B * second.B));

        double h1 = Hue(first.B, a1);
        double h2 = Hue(second.B, a2);

        double deltaL = second.L - first.L;
        double deltaC = cp2 - cp1;

        double deltah;
        if (cp1 * cp2 == 0)
        {
            deltah = 0;
        }
        else if (Math.Abs(h2 - h1) <= 180)
        {
            deltah = h2 - h1;
        }
        else
        {
            deltah = h2 > h1 ? h2 - h1 - 360 : h2 - h1 + 360;
        }

        double deltaH = 2 * Math.Sqrt(cp1 * cp2) * Math.Sin(deltah / 2 * radians);

        double meanL = (first.L + second.L) / 2;
        double meanCp = (cp1 + cp2) / 2;

        double meanH;
        if (cp1 * cp2 == 0)
        {
            meanH = h1 + h2;
        }
        else if (Math.Abs(h1 - h2) <= 180)
        {
            meanH = (h1 + h2) / 2;
        }
        else if (h1 + h2 < 360)
        {
            meanH = (h1 + h2 + 360) / 2;
        }
        else
        {
            meanH = (h1 + h2 - 360) / 2;
        }

        double t = 1
            - (0.17 * Math.Cos((meanH - 30) * radians))
            + (0.24 * Math.Cos(2 * meanH * radians))
            + (0.32 * Math.Cos(((3 * meanH) + 6) * radians))
            - (0.20 * Math.Cos(((4 * meanH) - 63) * radians));

        double meanLMinus50 = (meanL - 50) * (meanL - 50);
        double sl = 1 + (0.015 * meanLMinus50 / Math.Sqrt(20 + meanLMinus50));
        double sc = 1 + (0.045 * meanCp);
        double sh = 1 + (0.015 * meanCp * t);

        // The rotation term. Its whole purpose is the blue region, where a formula without it
        // disagrees noticeably with what people report seeing.
        double deltaTheta = 30 * Math.Exp(-Math.Pow((meanH - 275) / 25, 2));
        double meanCp7 = Math.Pow(meanCp, 7);
        double rc = 2 * Math.Sqrt(meanCp7 / (meanCp7 + Math.Pow(25, 7)));
        double rt = -rc * Math.Sin(2 * deltaTheta * radians);

        double termL = deltaL / sl;
        double termC = deltaC / sc;
        double termH = deltaH / sh;

        return Math.Sqrt(
            (termL * termL)
            + (termC * termC)
            + (termH * termH)
            + (rt * termC * termH));

        static double Hue(double b, double a)
        {
            if (a == 0 && b == 0)
            {
                return 0;
            }

            double angle = Math.Atan2(b, a) * degrees;
            return angle >= 0 ? angle : angle + 360;
        }
    }

    /// <summary>Whether two colours are close enough that §9.4.2 calls it a collision.</summary>
    public static bool Collides((byte R, byte G, byte B) accent, (byte R, byte G, byte B) semantic) =>
        Between(accent, semantic) < CollisionThreshold;
}
