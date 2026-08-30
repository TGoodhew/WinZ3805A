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

    // The rows the tables hold, kept alive between reads - see EnsureRows.
    private ReceiverStatus? _rowsBuiltFrom;
    private int? _rowsBuiltForMask;
    private IReadOnlySet<int> _excluded = new HashSet<int>();
    private IReadOnlySet<int> _rowsBuiltForExclusions = new HashSet<int>();
    private IReadOnlyList<TrackedSatelliteRow> _tracked = [];
    private IReadOnlyList<PredictedSatelliteRow> _notTracked = [];
    private IReadOnlyList<SkyPlotSatellite> _skyPlot = [];

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

    /// <summary>
    /// The PRNs the operator has excluded from tracking, for §10.5's <i>ignored</i> status (#320).
    /// </summary>
    /// <remarks>
    /// <para>
    /// Set by the page from <c>:GPS:SAT:TRAC:IGN?</c> rather than parsed from the sweep, because the
    /// status screen has no way to say it: from the screen's point of view an excluded satellite is
    /// simply one that is not being tracked, which is exactly the confusion this column removes.
    /// </para>
    /// <para>
    /// Read on navigation and after the Manage dialog closes, and not on a timer. The list changes
    /// only when someone changes it, and putting a second query on the 1 s sweep to catch an event
    /// that happens twice a year would be paying wire time for nothing (§7.3).
    /// </para>
    /// </remarks>
    public IReadOnlySet<int> ExcludedPrns
    {
        get => _excluded;
        set
        {
            IReadOnlySet<int> next = value ?? new HashSet<int>();
            if (!_excluded.SetEquals(next))
            {
                _excluded = next;
                RaiseAll();
            }
        }
    }

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
    public IReadOnlyList<TrackedSatelliteRow> Tracked
    {
        get
        {
            EnsureRows();
            return _tracked;
        }
    }

    /// <summary>The not-tracked table.</summary>
    public IReadOnlyList<PredictedSatelliteRow> NotTracked
    {
        get
        {
            EnsureRows();
            return _notTracked;
        }
    }

    /// <summary>
    /// Rebuilds the row objects, but only when the screen they came from has actually changed.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The identity of these objects is load-bearing.</b> They are what the two <c>ListView</c>s
    /// hold and what <c>SelectedItem</c> is compared against, so a property that built a fresh list
    /// on every read gave the page nothing it could select: the row handed to <c>SelectedItem</c>
    /// was equal to one in the list and was not the same object, and setting it silently did
    /// nothing.
    /// </para>
    /// <para>
    /// It is also what stops the tables being rebuilt once a second. The staleness footer ticks on
    /// a <c>DispatcherTimer</c> and raises every property with it, so an uncached list meant twelve
    /// rows and twelve strength bars thrown away and remade every second — and any selection the
    /// user had made thrown away with them, which is the same bug wearing different clothes.
    /// </para>
    /// </remarks>
    private void EnsureRows()
    {
        if (ReferenceEquals(_rowsBuiltFrom, Status)
            && _rowsBuiltForMask == ElevationMaskDegrees
            && _rowsBuiltForExclusions.SetEquals(_excluded))
        {
            return;
        }

        _rowsBuiltFrom = Status;
        _rowsBuiltForMask = ElevationMaskDegrees;
        _rowsBuiltForExclusions = new HashSet<int>(_excluded);

        _tracked = Status is null
            ? []
            : [.. Status.Tracked.Select(satellite => new TrackedSatelliteRow(satellite, SignalStrengthKind))];

        _notTracked = Status is null
            ? []
            : [.. Status.NotTracked.Select(satellite => new PredictedSatelliteRow(satellite, ElevationMaskDegrees)
            {
                IsAcquiring = satellite.AttemptingToTrack,
                IsIgnored = _excluded.Contains(satellite.Prn),
            })];

        _skyPlot = BuildSkyPlot();
    }

    /// <summary>
    /// Everything the §10.5 sky plot draws, tracked and predicted together.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Built here rather than in the control, so the plot and the tables agree by construction: the
    /// sentence a marker carries for assistive technology is the same
    /// <c>Description</c> the table row uses, and a satellite is judged below the mask by the same
    /// rule in both places.
    /// </para>
    /// <para>
    /// Both lists feed one plot. A satellite the receiver has predicted but is not tracking is
    /// exactly what the plot is for — it is how a user sees that the sky is full and the receiver
    /// is still only holding four, which no table row states in so many words.
    /// </para>
    /// </remarks>
    public IReadOnlyList<SkyPlotSatellite> SkyPlotSatellites
    {
        get
        {
            EnsureRows();
            return _skyPlot;
        }
    }

    /// <summary>
    /// Builds the plot's markers from the rows just built.
    /// </summary>
    /// <remarks>
    /// Reads the fields rather than the <c>Tracked</c> and <c>NotTracked</c> properties, which
    /// would call back into <c>EnsureRows</c>. The guard there makes that harmless today, purely
    /// because the fields are assigned before this is called — which is a fact about statement
    /// order, and not one worth depending on.
    /// </remarks>
    private IReadOnlyList<SkyPlotSatellite> BuildSkyPlot()
    {
        {
            List<SkyPlotSatellite> markers = new(_tracked.Count + _notTracked.Count);

            foreach (TrackedSatelliteRow row in _tracked)
            {
                markers.Add(new SkyPlotSatellite(
                    row.Prn,
                    row.ElevationDegrees,
                    row.AzimuthDegrees,
                    row.SignalStrength,
                    row.Kind,
                    SkyPlotMarkerKind.Tracked,
                    $"{row.Description}, tracked"));
            }

            foreach (PredictedSatelliteRow row in _notTracked)
            {
                markers.Add(new SkyPlotSatellite(
                    row.Prn,
                    row.ElevationDegrees,
                    row.AzimuthDegrees,
                    null,
                    SignalStrengthKind.Unknown,
                    row.Marker,
                    $"{row.Description}, not tracked"));
            }

            return markers;
        }
    }

    /// <summary>
    /// What the plot says when it has nothing to draw, or null when it has something.
    /// </summary>
    /// <remarks>
    /// A plot of rings and no markers looks like a plot that failed rather than a receiver that can
    /// see nothing, so §9.11's empty state applies to it as much as to a table — and it describes
    /// what will appear there rather than what is absent.
    /// </remarks>
    public string? SkyPlotEmptyMessage
    {
        get
        {
            if (Connection != ConnectionStatus.Connected)
            {
                return EmptyMessage;
            }

            if (SkyPlotSatellites.Count == 0)
            {
                return "No satellites yet. Tracked and predicted satellites appear here as the receiver finds them.";
            }

            return SkyPlotSatellites.Any(satellite => satellite.CanPlot)
                ? null
                : "The receiver has not reported elevation and azimuth for any satellite yet, so none can be placed.";
        }
    }

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

    /// <summary>
    /// One sentence naming every column, for the row's automation name and the sky plot's marker.
    /// </summary>
    /// <remarks>
    /// The scale's label keeps its case. It used to be lower-cased for sentence flow, which reads
    /// as "c/n 35 of 55" — and C/N is an abbreviation for carrier-to-noise, not a word. That was
    /// invisible while this sentence was only spoken aloud by a screen reader; the sky plot puts it
    /// on screen under the plot, where it is simply wrong.
    /// </remarks>
    public string Description =>
        $"PRN {Prn}, elevation {Describe(ElevationDegrees)}, azimuth {Describe(AzimuthDegrees)}, "
        + SignalStrengthScale.For(Kind).Describe(SignalStrength);

    internal static string Degrees(int? value) => ReadoutFormatter.Degrees(value);

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
    /// Derived rather than reported: the receiver's Not Tracking table prints only PRN, elevation
    /// and azimuth, and there is no status column on the wire. It is the weakest of the three
    /// answers below and the last one tried.
    /// </remarks>
    public bool IsBelowMask =>
        ElevationDegrees is int elevation && ElevationMaskDegrees is int mask && elevation < mask;

    /// <summary>Whether the receiver says it is trying to acquire this one.</summary>
    /// <remarks>
    /// The receiver's own claim, not a derivation: the status screen prints an asterisk before the
    /// PRN and its legend explains it as <c>*attempting to track</c>. Parsed since #4 and read by
    /// nothing until #320.
    /// </remarks>
    public bool IsAcquiring { get; init; }

    /// <summary>Whether the operator has excluded this one from tracking.</summary>
    /// <remarks>
    /// From <c>:GPS:SAT:TRAC:IGN?</c>, read alongside the sweep rather than parsed from it — the
    /// screen has no way to say it, an excluded satellite being simply one that is not tracked.
    /// </remarks>
    public bool IsIgnored { get; init; }

    /// <summary>
    /// The status column §10.5 specifies, in full (#320).
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Most authoritative answer first.</b> <i>Ignored</i> is a decision the operator made and
    /// explains the row whatever else is true of it. <i>Acquiring</i> is the receiver's own
    /// statement about what it is doing. <i>Below mask</i> is this application's inference from two
    /// numbers, and comes last because it is the only one that could be wrong.
    /// </para>
    /// <para>
    /// §10.5's wireframe lists <i>acquiring</i> and <i>✱ trying</i> as though they were separate
    /// states. They are one state shown two ways — a word in the table, a marker on the plot — and
    /// the receiver's own legend settles it by calling the asterisk "attempting to track".
    /// </para>
    /// <para>
    /// Empty when none of the three applies, which is honest: the satellite is up, eligible, not
    /// excluded, and the receiver has not said why it is not tracking it.
    /// </para>
    /// </remarks>
    public string StatusText => this switch
    {
        { IsIgnored: true } => "ignored",
        { IsAcquiring: true } => "acquiring",
        { IsBelowMask: true } => "below mask",
        _ => string.Empty,
    };

    /// <summary>How the sky plot draws this satellite.</summary>
    /// <remarks>
    /// The same precedence <see cref="StatusText"/> uses, so the plot and the table never disagree
    /// about one satellite — which is the property #60 asks of the two views generally.
    /// </remarks>
    public SkyPlotMarkerKind Marker => this switch
    {
        { IsIgnored: true } => SkyPlotMarkerKind.Ignored,
        { IsAcquiring: true } => SkyPlotMarkerKind.Acquiring,
        { IsBelowMask: true } => SkyPlotMarkerKind.BelowMask,
        _ => SkyPlotMarkerKind.Predicted,
    };

    /// <summary>One sentence naming every column, for the row's automation name.</summary>
    public string Description
    {
        get
        {
            string basics = $"PRN {Prn}, elevation {Describe(ElevationDegrees)}, azimuth {Describe(AzimuthDegrees)}";

            return this switch
            {
                { IsIgnored: true } => $"{basics}, excluded from tracking",
                { IsAcquiring: true } => $"{basics}, the receiver is trying to acquire it",
                { IsBelowMask: true } => $"{basics}, below the elevation mask",
                _ => basics,
            };
        }
    }

    private static string Describe(int? value) => value is int degrees
        ? $"{degrees} degrees"
        : "not reported";
}
