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
/// The full sentence §9.10.2 requires the marker's automation peer to carry — "PRN 19, elevation 65
/// degrees, azimuth 52 degrees, C/N 49 of 55, tracked." Supplied by the caller rather than built
/// here, so the plot and the table say the same thing about the same satellite.
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
}
