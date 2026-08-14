using System.ComponentModel;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.6 Position page.
/// </summary>
/// <remarks>
/// Read-only for now. Every control on this page that changes anything — starting a survey,
/// adopting its result, setting a position by hand — is a §8.3 tier C command needing a
/// confirmation dialog, and that infrastructure is §15 step 10. What is here is the half that
/// answers "where does the receiver think it is", which is what a user opens the page for.
/// </remarks>
public sealed class PositionViewModel : INotifyPropertyChanged
{
    private readonly ReceiverStateStore _store;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;

    /// <summary>Creates a view model over the shared store.</summary>
    public PositionViewModel(ReceiverStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _store.PropertyChanged += (_, _) => RaiseAll();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the session stands.</summary>
    public ConnectionStatus Connection
    {
        get => _connection;
        set
        {
            if (_connection != value)
            {
                _connection = value;
                RaiseAll();
            }
        }
    }

    private ReceiverStatus? Status =>
        Connection == ConnectionStatus.Connected ? _store.Status : null;

    /// <summary>Whether the receiver is holding a position or surveying for one.</summary>
    public PositionMode Mode => Status?.PositionMode ?? PositionMode.Unknown;

    /// <summary>The mode in words.</summary>
    public string ModeText => Mode switch
    {
        PositionMode.Hold => "Position hold",
        PositionMode.Survey => "Surveying",
        _ => "Unknown",
    };

    /// <summary>Whether a survey is running, which is what the survey card keys off.</summary>
    public bool IsSurveying => Mode == PositionMode.Survey;

    /// <summary>Latitude as the receiver prints it.</summary>
    public string LatitudeText => Coordinates.Latitude(Status?.Position?.LatitudeDegrees)
        ?? ReadoutFormatter.NoValue;

    /// <summary>Longitude as the receiver prints it.</summary>
    public string LongitudeText => Coordinates.Longitude(Status?.Position?.LongitudeDegrees)
        ?? ReadoutFormatter.NoValue;

    /// <summary>Height with its unit and datum.</summary>
    /// <remarks>
    /// The datum is never dropped. MSL and the WGS-84 ellipsoid differ by the geoid separation,
    /// which is tens of metres in places, and a height without one is a number a user cannot check
    /// against anything.
    /// </remarks>
    public string HeightText
    {
        get
        {
            if (Status?.Position?.HeightMetres is not double metres)
            {
                return ReadoutFormatter.NoValue;
            }

            string value = ReadoutFormatter.Format(metres, decimalPlaces: 2);
            string datum = Status.HeightDatum switch
            {
                HeightDatum.Msl => " (MSL)",
                HeightDatum.GpsEllipsoid => " (WGS-84 ellipsoid)",
                _ => string.Empty,
            };

            return $"{value}{ReadoutFormatter.HairSpace}m{datum}";
        }
    }

    /// <summary>Whether there is a position to show at all.</summary>
    public bool HasPosition => Status?.Position is
        { LatitudeDegrees: not null, LongitudeDegrees: not null };

    /// <summary>
    /// The position in the plain decimal form that pastes usefully elsewhere.
    /// </summary>
    /// <remarks>
    /// Decimal degrees rather than the displayed DMS: what a user pastes this into — a map, a
    /// spreadsheet, another receiver's setup — almost always wants decimal, and re-parsing DMS out
    /// of prime and double-prime characters is exactly the chore worth saving them.
    /// </remarks>
    public string? CopyText
    {
        get
        {
            if (Status?.Position is not GeoPosition position ||
                position.LatitudeDegrees is not double latitude ||
                position.LongitudeDegrees is not double longitude)
            {
                return null;
            }

            string height = position.HeightMetres is double metres
                ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $", {metres:0.00} m")
                : string.Empty;

            return string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"{latitude:0.000000}, {longitude:0.000000}{height}");
        }
    }

    /// <summary>How far a survey has got, as a percentage.</summary>
    public double? SurveyPercentComplete => Status?.SurveyPercentComplete;

    /// <summary>Why the survey has stopped making progress, if it has.</summary>
    public SurveySuspendedReason SurveySuspendedReason =>
        Status?.SurveySuspendedReason ?? SurveySuspendedReason.None;

    /// <summary>Whether the survey is suspended.</summary>
    public bool IsSurveySuspended => SurveySuspendedReason != SurveySuspendedReason.None;

    /// <summary>
    /// What the survey card says.
    /// </summary>
    /// <remarks>
    /// §10.6's wireframe promises "Estimated 51 min remaining". The receiver reports a percentage
    /// and nothing else — there is no rate on the wire, and a remaining time computed from a single
    /// percentage would be a guess presented as a measurement. The advice a suspended survey needs
    /// is the reason, which the receiver does report.
    /// </remarks>
    public string SurveyStatusText => SurveySuspendedReason switch
    {
        SurveySuspendedReason.TooFewSatellites =>
            "Suspended — a three-dimensional fix needs at least four satellites.",
        SurveySuspendedReason.PoorGeometry =>
            "Suspended — enough satellites, but their geometry gives too weak a solution.",
        SurveySuspendedReason.NoTrackData =>
            "Suspended — no tracking data available.",
        SurveySuspendedReason.Other =>
            "Suspended for a reason this version does not recognise. The receiver's own wording is in the parse warnings.",
        _ when IsSurveying => "In progress.",
        _ when Mode == PositionMode.Hold => "Not surveying — the receiver is holding a fixed position.",
        _ => ReadoutFormatter.NoValue,
    };

    /// <summary>The severity of the survey state, for the pill it renders through.</summary>
    public Severity SurveySeverity => IsSurveySuspended ? Severity.Caution : Severity.Neutral;

    /// <summary>How old the position is — it arrives only on a full sweep.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFullPoll);

    /// <summary>That age in words (§9.11).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
