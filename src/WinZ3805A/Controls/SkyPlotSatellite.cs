using WinZ3805A.Device.Models;

namespace WinZ3805A.Controls;

/// <summary>How a satellite is drawn on the sky plot.</summary>
/// <remarks>
/// <para>
/// Two shapes, not §10.5's three. Its legend reads "⬤ tracked ○ predicted ✱ trying", but the
/// receiver's Not Tracking table prints PRN, elevation and azimuth and no status column — so
/// "acquiring" and "trying" are not on the wire and cannot be drawn without inventing them. Below
/// the mask <i>is</i> derivable, from the same elevation the marker is plotted at, and it explains
/// most of the satellites a user will wonder about.
/// </para>
/// <para>
/// The distinction is carried by shape, and colour only reinforces it (§9.4.3, A11Y-12). A tracked
/// satellite is a filled disc sized by signal strength; a predicted one is a hollow ring of fixed
/// size, because there is no strength to size it by.
/// </para>
/// </remarks>
public enum SkyPlotMarkerKind
{
    /// <summary>Being tracked, with a signal-strength reading. Filled.</summary>
    Tracked = 0,

    /// <summary>Predicted to be up, not being tracked. Hollow.</summary>
    Predicted,

    /// <summary>Predicted, and below the elevation mask — so not a candidate. Hollow and dimmed.</summary>
    BelowMask,

    /// <summary>
    /// The receiver is trying to acquire this one. Hollow, with a heavier stroke (§10.5, #320).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Not derived and not guessed: the status screen prints an asterisk before the PRN and its own
    /// legend explains it as <c>*attempting to track</c>. §10.5's wireframe lists <i>acquiring</i>
    /// and <i>✱ trying</i> as though they were two states; they are one, and the wireframe is
    /// showing the same fact as a word in the table and as a marker on the plot.
    /// </para>
    /// <para>
    /// The stroke weight is the second channel §9.4.3 requires — a heavier ring survives every
    /// dichromacy, greyscale and high contrast, where a hue would not. The list view says the word
    /// instead, which is what A11Y-11 asks of it.
    /// </para>
    /// </remarks>
    Acquiring,

    /// <summary>Excluded from tracking by the operator (§10.5's <i>ignored</i>). Hollow and dimmed.</summary>
    /// <remarks>
    /// From <c>:GPS:SAT:TRAC:IGN?</c> rather than from the sweep — the status screen has no way to
    /// say it, because from the screen's point of view an excluded satellite is simply one that is
    /// not being tracked. It takes precedence over <see cref="BelowMask"/> in the status column: the
    /// operator's decision explains the row whatever the elevation happens to be.
    /// </remarks>
    Ignored,
}

/// <summary>
/// One satellite as the sky plot needs it.
/// </summary>
/// <remarks>
/// Deliberately not the page's row types. <c>SkyPlotControl</c> draws what it is given and
/// knows nothing about tables, selection models or the status screen, which is what lets its
/// geometry be tested against hand-computed positions rather than against a parsed fixture.
/// </remarks>
/// <param name="Prn">The satellite's PRN, which is also its identity and its keyboard order.</param>
/// <param name="ElevationDegrees">Degrees above the horizon, or null if the receiver did not say.</param>
/// <param name="AzimuthDegrees">Degrees clockwise from north, or null.</param>
/// <param name="SignalStrength">The reading, on <paramref name="Kind"/>'s scale, or null.</param>
/// <param name="Kind">Which scale <paramref name="SignalStrength"/> is on.</param>
/// <param name="Marker">How it is drawn.</param>
/// <param name="Description">
/// The full sentence §9.10.2 requires the marker's automation peer to carry. The specification's
/// example reads "PRN 19, elevation 65 degrees, azimuth 52 degrees, carrier to noise 49, tracked.";
/// the application's own sentence names the scale as well, as "C/N 49 of 55", which is
/// <c>SignalStrengthScale</c>'s wording. Supplied by the caller rather than built here, so the plot
/// and the table say the same thing about the same satellite.
/// </param>
public sealed record SkyPlotSatellite(
    int Prn,
    int? ElevationDegrees,
    int? AzimuthDegrees,
    int? SignalStrength,
    SignalStrengthKind Kind,
    SkyPlotMarkerKind Marker,
    string Description)
{
    /// <summary>
    /// Whether this satellite can be placed at all. One without both angles is not drawn — a
    /// marker at a guessed position is worse than an absent one on a plot read for geometry.
    /// </summary>
    public bool CanPlot => ElevationDegrees is not null && AzimuthDegrees is not null;

    /// <summary>The PRN as it is shown.</summary>
    public string PrnText => Prn.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Elevation as it is shown, with the degree sign.</summary>
    public string ElevationText => ReadoutFormatter.Degrees(ElevationDegrees);

    /// <summary>Azimuth as it is shown.</summary>
    public string AzimuthText => ReadoutFormatter.Degrees(AzimuthDegrees);

    /// <summary>
    /// What the marker's shape says, in words (A11Y-11).
    /// </summary>
    /// <remarks>
    /// The plot carries this distinction as shape and reinforces it with colour (§9.4.3). A list has
    /// neither, so the alternate view has to say it — and a list that dropped it would be a summary
    /// of the plot rather than the same data in another form, which is what #60 rules out.
    /// </remarks>
    public string StateText => Marker switch
    {
        SkyPlotMarkerKind.Tracked => "Tracked",
        SkyPlotMarkerKind.Predicted => "Predicted",
        SkyPlotMarkerKind.BelowMask => "Below mask",
        SkyPlotMarkerKind.Acquiring => "Acquiring",
        SkyPlotMarkerKind.Ignored => "Ignored",
        _ => ReadoutFormatter.NoValue,
    };

    /// <summary>
    /// What the plot cannot show about this satellite, or empty when there is nothing to add.
    /// </summary>
    /// <remarks>
    /// A satellite the receiver reported without both angles is absent from the plot, because there
    /// is nowhere honest to draw it. The list has no such constraint and shows it with em dashes for
    /// the angles — but then the two views hold different numbers of rows, and a user comparing them
    /// deserves to be told why rather than left to wonder which one is broken.
    /// </remarks>
    public string PlotNote => CanPlot ? string.Empty : "not on the plot: no position reported";
}
