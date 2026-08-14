using System.ComponentModel;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.5 Satellites page.
/// </summary>
/// <remarks>
/// Everything here comes from <c>:SYST:STAT?</c> and nothing else — §10.5 says so, and §11.1
/// explains why: elevation, azimuth and signal strength have no individual query, they exist only
/// inside the status screen. So this page is exactly as fresh as the last full sweep, which is why
/// it reports that age rather than the fast tier's.
/// </remarks>
public sealed class SatellitesViewModel : INotifyPropertyChanged
{
    private readonly ReceiverStateStore _store;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;

    /// <summary>Creates a view model over the shared store.</summary>
    public SatellitesViewModel(ReceiverStateStore store)
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

    /// <summary>Which signal-strength scale the receiver printed.</summary>
    public SignalStrengthKind SignalStrengthKind => Status?.SignalStrengthKind ?? SignalStrengthKind.Unknown;

    /// <summary>The elevation below which the receiver ignores satellites.</summary>
    public int? ElevationMaskDegrees => Status?.ElevationMaskDegrees;

    /// <summary>How many satellites are being tracked.</summary>
    public int TrackedCount => Status?.Tracked.Count ?? 0;

    /// <summary>How many the receiver expects to see but is not tracking.</summary>
    public int NotTrackedCount => Status?.NotTracked.Count ?? 0;

    /// <summary>
    /// The §10.5 header line.
    /// </summary>
    /// <remarks>
    /// The wireframe says "Tracking 6 · Visible 12". "Visible" is not what the receiver reports —
    /// the second table is headed "Not Tracking" — and summing the two would claim a visibility
    /// count the device never made. The counts are named for what they are.
    /// </remarks>
    public string CountSummary => Connection == ConnectionStatus.Connected
        ? $"Tracking {TrackedCount} · not tracking {NotTrackedCount}"
        : "Not connected";

    /// <summary>The tracked table, in the receiver's own order.</summary>
    public IReadOnlyList<TrackedSatelliteRow> Tracked => Status is null
        ? []
        : [.. Status.Tracked.Select(satellite => new TrackedSatelliteRow(satellite, SignalStrengthKind))];

    /// <summary>The not-tracked table.</summary>
    public IReadOnlyList<PredictedSatelliteRow> NotTracked => Status is null
        ? []
        : [.. Status.NotTracked.Select(satellite => new PredictedSatelliteRow(satellite, ElevationMaskDegrees))];

    /// <summary>Whether there is anything at all to show.</summary>
    public bool HasSatellites => TrackedCount > 0 || NotTrackedCount > 0;

    /// <summary>
    /// What the page says when it has no rows.
    /// </summary>
    /// <remarks>
    /// §9.11 wants an empty state to be an invitation rather than a shrug, and the three reasons a
    /// table is empty are not the same problem: no link, no screen yet, or a receiver that really
    /// is seeing nothing. The last is a genuine fault worth naming — it is the antenna.
    /// </remarks>
    public string EmptyMessage => Connection switch
    {
        ConnectionStatus.Connected when _store.Status is null =>
            "Waiting for the first full status screen.",
        ConnectionStatus.Connected =>
            "The receiver is not tracking any satellites and expects none in view. "
            + "That usually means the antenna, its cable, or its bias tee.",
        _ => "Connect to a receiver to see the satellites it is tracking.",
    };

    /// <summary>How old the satellite table is — the full sweep's age, not the fast tier's.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFullPoll);

    /// <summary>That age in words (§9.11).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>One row of the §10.5 tracked table.</summary>
public sealed class TrackedSatelliteRow
{
    /// <summary>Wraps a tracked satellite for display.</summary>
    public TrackedSatelliteRow(TrackedSatellite satellite, SignalStrengthKind kind)
    {
        ArgumentNullException.ThrowIfNull(satellite);

        Prn = satellite.Prn;
        ElevationDegrees = satellite.ElevationDegrees;
        AzimuthDegrees = satellite.AzimuthDegrees;
        SignalStrength = satellite.SignalStrength;
        Kind = kind;
    }

    /// <summary>The satellite's PRN.</summary>
    public int Prn { get; }

    /// <summary>Elevation in degrees, or <see langword="null"/>.</summary>
    public int? ElevationDegrees { get; }

    /// <summary>Azimuth in degrees, or <see langword="null"/>.</summary>
    public int? AzimuthDegrees { get; }

    /// <summary>The signal reading on <see cref="Kind"/>'s scale.</summary>
    public int? SignalStrength { get; }

    /// <summary>Which scale that reading is on.</summary>
    public SignalStrengthKind Kind { get; }

    /// <summary>The PRN as it is shown.</summary>
    public string PrnText => Prn.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Elevation as it is shown, with the degree sign.</summary>
    public string ElevationText => Degrees(ElevationDegrees);

    /// <summary>Azimuth as it is shown.</summary>
    public string AzimuthText => Degrees(AzimuthDegrees);

    /// <summary>One sentence naming every column, for the row's automation name.</summary>
    public string Description =>
        $"PRN {Prn}, elevation {Describe(ElevationDegrees)}, azimuth {Describe(AzimuthDegrees)}, "
        + SignalStrengthScale.For(Kind).Describe(SignalStrength).ToLowerInvariant();

    internal static string Degrees(int? value) => value is int degrees
        ? $"{degrees.ToString(System.Globalization.CultureInfo.CurrentCulture)}°"
        : ReadoutFormatter.NoValue;

    private static string Describe(int? value) => value is int degrees
        ? $"{degrees} degrees"
        : "not reported";
}

/// <summary>One row of the §10.5 not-tracked table.</summary>
public sealed class PredictedSatelliteRow
{
    /// <summary>Wraps a predicted satellite for display.</summary>
    public PredictedSatelliteRow(PredictedSatellite satellite, int? elevationMaskDegrees)
    {
        ArgumentNullException.ThrowIfNull(satellite);

        Prn = satellite.Prn;
        ElevationDegrees = satellite.ElevationDegrees;
        AzimuthDegrees = satellite.AzimuthDegrees;
        ElevationMaskDegrees = elevationMaskDegrees;
    }

    /// <summary>The satellite's PRN.</summary>
    public int Prn { get; }

    /// <summary>Predicted elevation in degrees, or <see langword="null"/>.</summary>
    public int? ElevationDegrees { get; }

    /// <summary>Predicted azimuth in degrees, or <see langword="null"/>.</summary>
    public int? AzimuthDegrees { get; }

    /// <summary>The mask this satellite is being judged against.</summary>
    public int? ElevationMaskDegrees { get; }

    /// <summary>The PRN as it is shown.</summary>
    public string PrnText => Prn.ToString(System.Globalization.CultureInfo.CurrentCulture);

    /// <summary>Elevation as it is shown.</summary>
    public string ElevationText => TrackedSatelliteRow.Degrees(ElevationDegrees);

    /// <summary>Azimuth as it is shown.</summary>
    public string AzimuthText => TrackedSatelliteRow.Degrees(AzimuthDegrees);

    /// <summary>Whether this satellite sits below the elevation mask.</summary>
    /// <remarks>
    /// Derived, not reported. §10.5's wireframe shows a status column reading "acquiring", "below
    /// mask" or "ignored", but the receiver's Not Tracking table prints only PRN, elevation and
    /// azimuth — there is no status column on the wire. Below-mask is the one of the three that
    /// follows from what is printed, and it is the one that explains most empty rows. The others
    /// are not invented.
    /// </remarks>
    public bool IsBelowMask =>
        ElevationDegrees is int elevation && ElevationMaskDegrees is int mask && elevation < mask;

    /// <summary>The status column, empty when nothing can be said.</summary>
    public string StatusText => IsBelowMask ? "below mask" : string.Empty;

    /// <summary>One sentence naming every column, for the row's automation name.</summary>
    public string Description
    {
        get
        {
            string basics = $"PRN {Prn}, elevation {Describe(ElevationDegrees)}, azimuth {Describe(AzimuthDegrees)}";
            return IsBelowMask ? $"{basics}, below the elevation mask" : basics;
        }
    }

    private static string Describe(int? value) => value is int degrees
        ? $"{degrees} degrees"
        : "not reported";
}
