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
    private bool? _surveyAtPowerUp;

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

    // ---- §10.6's survey commands -----------------------------------------------------------

    /// <summary>
    /// Whether a survey can be started. Not while one is already running — §8.3 offers no
    /// "restart", and <c>STATe ONCE</c> sent mid-survey is a command with no stated meaning.
    /// </summary>
    public bool CanStartSurvey => Connection == ConnectionStatus.Connected && !IsSurveying;

    /// <summary>
    /// Whether the survey's own answer can be adopted, and whether it can be abandoned. Both are
    /// only meaningful while one is running, and §8.3 words them for exactly that case — "stop
    /// surveying and adopt", "cancel survey and restore".
    /// </summary>
    public bool CanEndSurvey => Connection == ConnectionStatus.Connected && IsSurveying;

    /// <summary>
    /// Whether the receiver surveys automatically at power-up, or null while unread.
    /// </summary>
    /// <remarks>
    /// Read on demand rather than polled: it is a setting rather than a reading, it changes only
    /// when someone changes it, and §7.3's two cadences have no business carrying it.
    /// </remarks>
    public bool? SurveyAtPowerUp
    {
        get => _surveyAtPowerUp;
        set
        {
            if (_surveyAtPowerUp != value)
            {
                _surveyAtPowerUp = value;
                RaiseAll();
            }
        }
    }

    /// <summary>
    /// The position the receiver is holding, for the entry card to copy from.
    /// </summary>
    /// <remarks>
    /// Exposed rather than the whole status: the entry card needs three numbers, and handing a page
    /// the parsed screen invites it to reach past the view model for the rest.
    /// </remarks>
    public GeoPosition? ReceiverPosition => Status?.Position;

    /// <summary>Whether a position may be set by hand at all.</summary>
    public bool CanSetPosition => Connection == ConnectionStatus.Connected;

    /// <summary>
    /// The note beside the height entry field, naming the datum the receiver reported.
    /// </summary>
    /// <remarks>
    /// The 58503B manual is not consistent with itself: the same command takes "height above mean
    /// sea level" in its syntax line and WGS-84 in its prose, two paragraphs apart, and those differ
    /// by the geoid separation — tens of metres in most of the world. **#114 settled it in favour of
    /// asserting neither.** §10.6 no longer annotates the field "WGS-84, GPS ellipsoid"; the page
    /// states which datum the receiver said it was reporting and asks for the value on the same one.
    /// That is knowable and checkable, where picking a side between the manual's two halves is not.
    /// </remarks>
    public string HeightEntryNote => Status?.HeightDatum switch
    {
        HeightDatum.Msl => "The receiver is reporting height above mean sea level. Enter it on the same datum.",
        HeightDatum.GpsEllipsoid => "The receiver is reporting height above the WGS-84 ellipsoid. Enter it on the same datum.",
        _ => "The receiver has not said which height datum it is using. Match whatever it reports above.",
    };

    /// <summary>How old the position is — it arrives only on a full sweep.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFullPoll);

    /// <summary>That age in words (§9.11).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
