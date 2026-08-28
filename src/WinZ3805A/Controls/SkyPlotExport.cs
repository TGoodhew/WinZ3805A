using System.Globalization;

namespace WinZ3805A.Controls;

/// <summary>
/// The text and sizing decisions behind §10.5's sky-plot image export (OQ-D6, #47).
/// </summary>
/// <remarks>
/// <para>
/// OQ-D6 assumed no image export in v1 and that export would be "CSV only". That assumption was
/// overturned on 28 Aug 2026, because it answered a different question than the one asked. A CSV
/// of azimuth, elevation and signal strength is a table of numbers; a calibration record wants
/// evidence of <b>what the sky looked like from this antenna</b>, and #185's obstruction finding
/// — rack mean 1.94 satellites against backyard 6.59 — is an argument nobody makes with a
/// spreadsheet. The picture is the artefact.
/// </para>
/// <para>
/// Separated from the page so the parts that can be wrong quietly — the timestamp format, the
/// counts, the render scale — are unit-testable. What is left in the page is a
/// <c>RenderTargetBitmap</c> call over the card that is already on screen, which is what keeps
/// this from becoming a second renderer that can disagree with the first one.
/// </para>
/// </remarks>
public static class SkyPlotExport
{
    /// <summary>The largest multiple of on-screen size the capture is scaled up by.</summary>
    /// <remarks>
    /// <para>
    /// A 360 px plot pasted into a document at 96 dpi is a thumbnail, so the capture is rendered
    /// larger than the screen copy. It is capped rather than unbounded because
    /// <c>RenderTargetBitmap</c> re-rasterises the subtree at the requested size and the cost is
    /// quadratic, and because the input is vector geometry — beyond about 3x the extra pixels
    /// stop carrying information a reader can use.
    /// </para>
    /// </remarks>
    public const int MaximumScale = 3;

    /// <summary>Longest edge, in pixels, the capture is allowed to reach.</summary>
    /// <remarks>
    /// Well inside <c>RenderTargetBitmap</c>'s own limit, which is hardware-dependent and
    /// documented only as "maximum texture size". Exceeding it does not throw — it silently
    /// returns a truncated bitmap, which is far worse than a slightly smaller image, so the
    /// budget here is deliberately conservative.
    /// </remarks>
    public const int MaximumEdgePixels = 2400;

    /// <summary>
    /// Chooses how far to scale the capture above its on-screen size.
    /// </summary>
    /// <param name="widthPixels">The card's rendered width, in physical pixels.</param>
    /// <param name="heightPixels">The card's rendered height, in physical pixels.</param>
    /// <returns>A whole-number scale of at least 1.</returns>
    /// <remarks>
    /// A whole number rather than a fitted fraction, because non-integer scaling of a plot whose
    /// content is 1 px and 1.5 px strokes (§9.2) is where hairlines disappear. Falling back to 1
    /// is correct: a card already larger than the budget is exported at the size it is, not
    /// refused.
    /// </remarks>
    public static int ScaleFor(double widthPixels, double heightPixels)
    {
        double longest = Math.Max(widthPixels, heightPixels);
        if (longest <= 0 || double.IsNaN(longest) || double.IsInfinity(longest))
        {
            return 1;
        }

        int affordable = (int)Math.Floor(MaximumEdgePixels / longest);
        return Math.Clamp(affordable, 1, MaximumScale);
    }

    /// <summary>
    /// The line written under the plot in the exported image, and only there.
    /// </summary>
    /// <param name="displayName">The product name, read from the package (§6.3).</param>
    /// <param name="captured">When the capture was taken.</param>
    /// <param name="tracked">Satellites the receiver is tracking.</param>
    /// <param name="notTracked">Satellites predicted in view but not tracked.</param>
    /// <param name="elevationMaskDegrees">The §8.3 mask, or null when it is not known.</param>
    /// <remarks>
    /// <para>
    /// UTC, spelled out, with no local-time alternative offered. A calibration record compared
    /// against another site's a year later cannot be read if the reader has to know which zone
    /// the machine was in, and a picture carries no metadata a person will look at.
    /// </para>
    /// <para>
    /// The mask is on the line because it changes what the picture <i>means</i>: the same sky
    /// with a 10° mask and a 25° mask produces two legitimate plots with different satellites
    /// missing, and a record that does not say which was in force cannot be compared with
    /// anything.
    /// </para>
    /// </remarks>
    public static string Caption(
        string displayName,
        DateTimeOffset captured,
        int tracked,
        int notTracked,
        int? elevationMaskDegrees)
    {
        string when = captured.ToUniversalTime()
            .ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture);

        string counts = notTracked > 0
            ? $"{Plural(tracked, "satellite")} tracked, {notTracked} more predicted in view"
            : $"{Plural(tracked, "satellite")} tracked";

        string mask = elevationMaskDegrees is int degrees
            ? $" · elevation mask {degrees}°"
            : string.Empty;

        return $"{displayName} · {when} · {counts}{mask}";
    }

    /// <summary>
    /// The name offered in the save dialog.
    /// </summary>
    /// <param name="displayName">The product name, read from the package (§6.3).</param>
    /// <param name="captured">When the capture was taken.</param>
    /// <remarks>
    /// Sortable, UTC, and free of the characters Windows forbids in a file name — the colon in
    /// an ISO time being the one that would otherwise be silently mangled by the picker. Several
    /// exports taken minutes apart during an antenna move are the normal case, so the minute is
    /// in the name rather than the day alone.
    /// </remarks>
    public static string SuggestedFileName(string displayName, DateTimeOffset captured)
    {
        string when = captured.ToUniversalTime()
            .ToString("yyyy-MM-dd HHmm'Z'", CultureInfo.InvariantCulture);

        return $"{Sanitise(displayName)} sky plot {when}";
    }

    /// <summary>
    /// Composites a captured surface onto an opaque background, in place.
    /// </summary>
    /// <param name="premultipliedBgra">Pixels as <c>RenderTargetBitmap</c> hands them over.</param>
    /// <param name="blue">Background blue channel.</param>
    /// <param name="green">Background green channel.</param>
    /// <param name="red">Background red channel.</param>
    /// <remarks>
    /// <para>
    /// <b>Not cosmetic — this is what makes the file readable anywhere.</b> §9.4.1's surface tokens
    /// map onto stock Fluent colours and <b>almost every one of them is semi-transparent</b>; the
    /// card the sky plot sits on resolves to <c>CardBackgroundFillColorDefault</c>, which is not
    /// opaque. A capture of it therefore carries an alpha channel, and an alpha channel is an
    /// instruction to composite over <i>whatever the viewer happens to be using</i>. The same file
    /// then looks correct in a white image viewer and washes out in a dark one, or in a document —
    /// and it looks correct in whichever one the person exporting it tried.
    /// </para>
    /// <para>
    /// The background is the page's own opaque fallback rather than white, so the flattened result
    /// is the surface the card genuinely sits on in that theme. Under high contrast that is the
    /// user's window colour, which is the only defensible choice there.
    /// </para>
    /// <para>
    /// The source is <b>premultiplied</b>, which is what <c>RenderTargetBitmap</c> produces, so the
    /// composite is <c>src + dst × (1 − α)</c> with no division to undo the premultiplication first.
    /// Getting this wrong does not fail loudly: un-premultiplied maths over the same data produces
    /// an image that is merely a little washed out, which reads as a rendering quirk.
    /// </para>
    /// </remarks>
    public static void Flatten(byte[] premultipliedBgra, byte blue, byte green, byte red)
    {
        ArgumentNullException.ThrowIfNull(premultipliedBgra);

        for (int i = 0; i + 3 < premultipliedBgra.Length; i += 4)
        {
            int inverse = 255 - premultipliedBgra[i + 3];
            if (inverse == 0)
            {
                continue;
            }

            premultipliedBgra[i] = Add(premultipliedBgra[i], blue, inverse);
            premultipliedBgra[i + 1] = Add(premultipliedBgra[i + 1], green, inverse);
            premultipliedBgra[i + 2] = Add(premultipliedBgra[i + 2], red, inverse);
            premultipliedBgra[i + 3] = 255;
        }
    }

    /// <summary>One channel of <c>src + dst × (1 − α)</c>, rounded and clamped.</summary>
    private static byte Add(byte source, byte background, int inverseAlpha) =>
        (byte)Math.Min(255, source + (((background * inverseAlpha) + 127) / 255));

    /// <summary>Replaces anything Windows will not accept in a file name with a space.</summary>
    private static string Sanitise(string name)
    {
        char[] invalid = Path.GetInvalidFileNameChars();
        string cleaned = new(name.Select(c => Array.IndexOf(invalid, c) >= 0 ? ' ' : c).ToArray());

        return string.Join(' ', cleaned.Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary>Counts a noun without the "1 satellites" that gives a readout away as generated.</summary>
    private static string Plural(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}
