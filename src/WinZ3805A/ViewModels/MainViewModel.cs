using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// What the §10.3 main window shows, derived from the state store.
/// </summary>
/// <remarks>
/// <para>
/// Binds to <see cref="ReceiverStateStore"/> and never to the poller (§12), so the window depends
/// on what was found rather than on when it was looked for.
/// </para>
/// <para>
/// Plain <see cref="INotifyPropertyChanged"/> rather than the MVVM toolkit's source generator, for
/// the same reason as the store: no dependency beyond the Device library, so the whole mapping —
/// which is the part with judgement in it — compiles into the headless test project by link.
/// </para>
/// <para>
/// Everything here is a projection. It holds no state of its own beyond a cached snapshot, so a
/// disagreement between the window and the device is always the store's to explain.
/// </para>
/// </remarks>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly ReceiverStateStore _store;
    private readonly TimeProvider _timeProvider;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;
    private string? _portDescription;
    private TimeZoneInfo _displayZone = TimeZoneInfo.Local;

    /// <summary>Creates a view model over a store.</summary>
    public MainViewModel(ReceiverStateStore store, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _store = store;
        _timeProvider = timeProvider;
        _store.PropertyChanged += (_, _) => RaiseAll();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the session stands. Set by whoever owns the session.</summary>
    public ConnectionStatus Connection
    {
        get => _connection;
        set
        {
            if (_connection == value)
            {
                return;
            }

            _connection = value;
            RaiseAll();
        }
    }

    /// <summary>The port and line settings for the footer, such as <c>COM3 · 9600-8-N-1</c>.</summary>
    public string? PortDescription
    {
        get => _portDescription;
        set
        {
            if (_portDescription == value)
            {
                return;
            }

            _portDescription = value;
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The mode the medallion shows.
    /// </summary>
    /// <remarks>
    /// A session that is not connected reports <see cref="ReceiverMode.Disconnected"/> whatever the
    /// store last held. The readings stay on screen and go stale honestly (§9.11), but the mode is
    /// a claim about *now* and must not outlive the link that justified it.
    /// </remarks>
    public ReceiverMode Mode => Connection == ConnectionStatus.Connected
        ? ReceiverModes.FromSyncState(_store.SyncState)
        : ReceiverMode.Disconnected;

    /// <summary>The mode text beside the medallion (§10.3).</summary>
    public string ModeText => ReceiverModes.TextOf(Mode);

    /// <summary>
    /// The sub-line under the mode: the parsed detail, or the reconnect state when there is no link.
    /// </summary>
    public string? ModeDetail => Connection switch
    {
        ConnectionStatus.Reconnecting => "Reconnecting",
        ConnectionStatus.Connecting => "Connecting",
        ConnectionStatus.Faulted => "Connection lost",
        ConnectionStatus.Disconnected => null,
        _ => _store.Status?.ModeDetail,
    };

    /// <summary>Satellites tracked, or <see langword="null"/> before the first poll.</summary>
    public int? SatelliteCount => _store.TrackedCount;

    /// <summary>The 1 PPS time interval in nanoseconds.</summary>
    public double? TimeIntervalNanoseconds => _store.OnePpsTiNanoseconds;

    /// <summary>The medallion's sample window.</summary>
    public IReadOnlyList<double?> TimeIntervalSamples => _store.RecentTimeInterval;

    /// <summary>Time figure of merit.</summary>
    public int? Tfom => _store.Tfom;

    /// <summary>Frequency figure of merit.</summary>
    public int? Ffom => _store.Ffom;

    /// <summary>
    /// Whether the receiver claims lock while tracking nothing.
    /// </summary>
    /// <remarks>
    /// §10.3 calls this the single most useful diagnostic the application surfaces, and it is why
    /// the satellite count shares top billing with the mode. It appears on real units with antenna
    /// or bias-tee faults: the receiver is coasting on a 1 PPS it can no longer verify, and every
    /// other indicator still says everything is fine.
    /// </remarks>
    public bool IsCoasting => Mode == ReceiverMode.Locked && SatelliteCount == 0;

    /// <summary>The §10.3 tooltip for the coasting pill, spelled out rather than implied.</summary>
    public string CoastingTooltip =>
        "Locked but tracking no satellites. The receiver is coasting on a 1 PPS it can no longer verify.";

    /// <summary>How old the fast readings are.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFastPoll);

    /// <summary>The footer's age in words (§10.3).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>What severity the footer should be drawn in (§9.11).</summary>
    public Severity AgeSeverity => Staleness.SeverityOf(Age);

    /// <summary>
    /// The zone times are shown in. Defaults to this computer, per #95.
    /// </summary>
    /// <remarks>
    /// Setting this changes only what is displayed. The receiver has its own offset, set by a tier-C
    /// command, and a display preference must never reconfigure an instrument to satisfy itself.
    /// </remarks>
    public TimeZoneInfo DisplayZone
    {
        get => _displayZone;
        set
        {
            ArgumentNullException.ThrowIfNull(value);
            if (_displayZone.Id == value.Id)
            {
                return;
            }

            _displayZone = value;
            RaiseAll();
        }
    }

    /// <summary>
    /// The date and time to show: rollover-corrected where §7.4 applies, then converted into
    /// <see cref="DisplayZone"/>.
    /// </summary>
    /// <remarks>
    /// Correction first, conversion second. §7.4 fixes which 1024-week epoch the reading belongs to,
    /// which is a question about the instant; the zone is a question about how to render it. Doing
    /// them the other way round would convert a date two decades out and then move it.
    /// </remarks>
    public DisplayTime? ShownTime => DisplayTimeConverter.Convert(
        _store.Status?.CorrectedDateTime ?? _store.Status?.DeviceDateTime,
        TimeScale,
        DisplayZone);

    /// <summary>The uncorrected, unconverted value, kept for the §7.4 tooltip.</summary>
    public DateTimeOffset? DisplayTime => _store.Status?.CorrectedDateTime ?? _store.Status?.DeviceDateTime;

    /// <summary>Whether the shown date has been corrected, which earns the §7.4 info glyph.</summary>
    public bool IsDateCorrected => (_store.Status?.WeekRolloverEpochs ?? 0) != 0;

    /// <summary>
    /// What the receiver actually said, for the §7.4 tooltip behind the corrected date.
    /// </summary>
    /// <remarks>
    /// §7.4 forbids silently substituting the correction. A user who sees a date two decades out
    /// with no way to check what the hardware reported reasonably concludes the app is lying, or
    /// that the receiver has failed.
    /// </remarks>
    public string? RawDeviceDate => _store.Status?.DeviceDateTime is DateTimeOffset raw
        ? $"Receiver reports {raw.ToString("dd MMM yyyy HH:mm:ss", CultureInfo.CurrentCulture)}"
        : null;

    /// <summary>
    /// Everything the §7.4 badge has to say: what the receiver reported, what was added,
    /// and what is <i>not</i> wrong.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The badge used to carry only <see cref="RawDeviceDate"/>. That satisfied §7.4's rule
    /// against silent substitution - the user could see what the hardware said - but not
    /// #10's other two criteria, that the badge explain the offset and state plainly that
    /// the time of day and the 1 PPS are unaffected.
    /// </para>
    /// <para>
    /// That last clause is the point of the whole feature. A user glancing at a timing
    /// reference and seeing 2006 has one question, and it is not about ten-bit week
    /// numbers: it is whether the output they are disciplining to is wrong. It is not. A
    /// badge that explained the arithmetic without answering that would be technically
    /// complete and useless at the moment it is read.
    /// </para>
    /// </remarks>
    public string? RolloverExplanation
    {
        get
        {
            if (!IsDateCorrected || RawDeviceDate is not string raw)
            {
                return null;
            }

            int epochs = _store.Status?.WeekRolloverEpochs ?? 0;
            string added = epochs == 1
                ? "1024 weeks have been added"
                : $"{epochs * 1024} weeks have been added";

            return $"{raw}. GPS wraps its week number about every 19.6 years, so {added} "
                + "to show the true date. The time of day and the 1 PPS output are "
                + "unaffected: only the date wraps.";
        }
    }

    /// <summary>The time scale the clock is on, for the line beside it.</summary>
    public TimeScale TimeScale => _store.Status?.TimeScale ?? TimeScale.Unknown;

    /// <summary>Whether the window should offer Connect rather than Disconnect.</summary>
    public bool CanConnect => Connection is ConnectionStatus.Disconnected or ConnectionStatus.Faulted;

    /// <summary>
    /// Recomputes everything. Called on every store change and on each tick of the staleness clock.
    /// </summary>
    /// <remarks>
    /// Coarse on purpose: this is a handful of projections over a record that changes at most once a
    /// second, and naming each dependency individually would be a list to get wrong rather than a
    /// saving worth having.
    /// </remarks>
    public void RaiseAll() => OnPropertyChanged(null);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
