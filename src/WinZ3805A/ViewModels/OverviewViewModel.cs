using System.ComponentModel;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.4 Overview page: synchronisation, holdover uncertainty, health, and oscillator control.
/// </summary>
/// <remarks>
/// <para>
/// Reads the same <see cref="ReceiverStateStore"/> the main window reads (§12) rather than issuing
/// anything of its own. The Overview page is a second view of one receiver, not a second reader of
/// it — everything here is last-known state, and the page says how old that is rather than blanking
/// when a poll is late (§9.11).
/// </para>
/// <para>
/// Separate from <see cref="MainViewModel"/> despite the overlap. The shared part is the mode
/// mapping, which both take from <see cref="ReceiverModes"/>; the rest of each is what its own
/// window shows, and merging them would give the main window a holdover-uncertainty property it has
/// no place for and this page a connect button it does not own.
/// </para>
/// </remarks>
public sealed class OverviewViewModel : INotifyPropertyChanged
{
    private readonly ReceiverStateStore _store;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;

    /// <summary>Creates a view model over the shared store.</summary>
    public OverviewViewModel(ReceiverStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _store.PropertyChanged += (_, _) => RaiseAll();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the session stands, which decides whether anything here means anything.</summary>
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

    private ReceiverStatus? Status => _store.Status;

    // ---- Synchronisation card -------------------------------------------------------------

    /// <summary>The §10.3 mode, which the medallion and the glyph both come from.</summary>
    public ReceiverMode Mode => Connection == ConnectionStatus.Connected
        ? ReceiverModes.FromSyncState(_store.SyncState)
        : ReceiverMode.Disconnected;

    /// <summary>The mode in words.</summary>
    public string ModeText => ReceiverModes.TextOf(Mode);

    /// <summary>
    /// The sub-line: what the receiver is doing within the mode.
    /// </summary>
    /// <remarks>
    /// The full screen's own detail text, which is where "Stabilizing frequency" comes from — HP's
    /// spelling, kept verbatim per Appendix B.
    /// </remarks>
    public string? ModeDetail => Connection == ConnectionStatus.Connected ? Status?.ModeDetail : null;

    /// <summary>Whether the 10 MHz and 1 PPS outputs are to be trusted (§11.2).</summary>
    public OutputValidity Outputs => Connection == ConnectionStatus.Connected
        ? Status?.Outputs ?? OutputValidity.Unknown
        : OutputValidity.Unknown;

    /// <summary>The outputs badge text, which §9.4.3 requires alongside the colour and shape.</summary>
    public string OutputsText => Outputs switch
    {
        OutputValidity.Valid => "Outputs valid",
        OutputValidity.ValidReduced => "Outputs valid, reduced accuracy",
        OutputValidity.Invalid => "Outputs invalid",
        _ => "Outputs unknown",
    };

    /// <summary>How bad the outputs state is.</summary>
    /// <remarks>
    /// <c>ValidReduced</c> is a caution rather than a success: the outputs are usable but the
    /// accuracy specification no longer holds, and a green badge over that is a lie a lab user
    /// would act on.
    /// </remarks>
    public Severity OutputsSeverity => Outputs switch
    {
        OutputValidity.Valid => Severity.Success,
        OutputValidity.ValidReduced => Severity.Caution,
        OutputValidity.Invalid => Severity.Critical,
        _ => Severity.Neutral,
    };

    /// <summary>Time figure of merit. Lower is better.</summary>
    public int? Tfom => Connection == ConnectionStatus.Connected ? _store.Tfom : null;

    /// <summary>Frequency figure of merit. Lower is better.</summary>
    public int? Ffom => Connection == ConnectionStatus.Connected ? _store.Ffom : null;

    /// <summary>
    /// The 1 PPS time error the TFOM stands for — the §10.4 wireframe's "100ns–1µs" sub-caption.
    /// </summary>
    /// <remarks>
    /// The number on its own tells a user nothing; the range behind it is what they came to find
    /// out. Sourced from the 58503A guide (#34), not inferred.
    /// </remarks>
    public string TfomDetail => FiguresOfMerit.TimeError(Tfom) ?? ReadoutFormatter.NoValue;

    /// <summary>What the FFOM says about the 10 MHz output — the wireframe's "PLL stable".</summary>
    public string FfomDetail => FiguresOfMerit.PllState(Ffom) ?? ReadoutFormatter.NoValue;

    /// <summary>The longer FFOM explanation, for a tooltip.</summary>
    public string? FfomTooltip => FiguresOfMerit.PllDetail(Ffom);

    /// <summary>
    /// How bad a figure of merit is, for the pill it renders through.
    /// </summary>
    /// <remarks>
    /// The thresholds are the same ones the main window uses, kept here rather than shared because
    /// they are a display judgement rather than a device fact: the guide gives ranges, not a verdict
    /// on which range is acceptable, and a lab user's answer depends on what they are measuring.
    /// </remarks>
    public static Severity SeverityOfMerit(int? value) => value switch
    {
        null => Severity.Neutral,
        <= 3 => Severity.Success,
        <= 6 => Severity.Caution,
        _ => Severity.Critical,
    };

    /// <summary>1 PPS time interval against GPS, in nanoseconds.</summary>
    public double? TimeIntervalNanoseconds =>
        Connection == ConnectionStatus.Connected ? _store.OnePpsTiNanoseconds : null;

    /// <summary>The medallion's 60-sample ring.</summary>
    public IReadOnlyList<double?> TimeIntervalSamples => _store.RecentTimeInterval;

    /// <summary>How many satellites are being tracked.</summary>
    public int? SatelliteCount => Connection == ConnectionStatus.Connected ? _store.TrackedCount : null;

    /// <summary>Locked while tracking nothing — the §10.3 diagnostic, repeated here.</summary>
    public bool IsCoasting => Mode == ReceiverMode.Locked && SatelliteCount == 0;

    // ---- Holdover card --------------------------------------------------------------------

    /// <summary>Predicted holdover uncertainty over 24 hours, formatted with its unit.</summary>
    public (string Value, string Unit) HoldoverPredicted =>
        ReadoutFormatter.Seconds(Connected(Status?.HoldoverPredictedSeconds));

    /// <summary>The threshold the receiver compares that prediction against.</summary>
    /// <remarks>
    /// Three decimals, because it is a setting rather than a measurement: the wireframe shows
    /// 1.000 µs, and a threshold that displayed as "1 µs" would hide the difference between the
    /// value that was set and the value that took effect.
    /// </remarks>
    public (string Value, string Unit) HoldoverThreshold =>
        ReadoutFormatter.Seconds(Connected(Status?.HoldThresholdSeconds), decimalPlaces: 3);

    /// <summary>How long the receiver has been in holdover, or why that is not a number.</summary>
    /// <remarks>
    /// Never blank. "Not in holdover" is the useful answer for a receiver that is locked, and it is
    /// a different statement from "—", which means the field could not be read (§11.1).
    /// </remarks>
    public string HoldoverDuration
    {
        get
        {
            if (Connection != ConnectionStatus.Connected)
            {
                return ReadoutFormatter.NoValue;
            }

            if (Mode != ReceiverMode.Holdover)
            {
                return "Not in holdover";
            }

            // §11.1: HoldoverDuration has no known screen label and is unparsed pending #4, so the
            // present-uncertainty figure is what there is. It says how bad holdover has become,
            // which is what the duration was being read for anyway.
            (string value, string unit) = ReadoutFormatter.Seconds(Status?.HoldoverPresentSeconds);
            return unit.Length == 0 ? value : $"{value}{ReadoutFormatter.HairSpace}{unit}";
        }
    }

    // ---- Health card ----------------------------------------------------------------------

    /// <summary>Each health monitor item and whether it passed.</summary>
    /// <remarks>
    /// Straight from the parsed HEALTH MONITOR block, in the order the receiver prints it. The
    /// §10.4 wireframe names six; the screen decides how many there are, and inventing a fixed six
    /// would hide a seventh that a future firmware prints.
    /// </remarks>
    public IReadOnlyList<HealthItem> Health => Connection == ConnectionStatus.Connected && Status is not null
        ? [.. Status.HealthItems.Select(item => new HealthItem(item.Key, item.Value))]
        : [];

    /// <summary>Whether every health item passed.</summary>
    public bool HealthOk => Connection == ConnectionStatus.Connected && (Status?.HealthOk ?? false);

    /// <summary>The health summary line, which carries the state in text as §9.4.3 requires.</summary>
    public string HealthSummary
    {
        get
        {
            if (Connection != ConnectionStatus.Connected || Status is null)
            {
                return "No health data";
            }

            int failed = Status.HealthItems.Count(item => !item.Value);
            return failed switch
            {
                0 when Status.HealthItems.Count == 0 => "No health data",
                0 => "All checks passing",
                1 => "1 check failing",
                _ => $"{failed} checks failing",
            };
        }
    }

    // ---- Oscillator card ------------------------------------------------------------------

    /// <summary>Electronic frequency control, as a percentage of the oscillator's range.</summary>
    /// <remarks>
    /// The value only, not the §10.4 trend plot: charting is P1-1 and is blocked on OQ-5 (#38).
    /// A number that is right beats an empty chart frame that implies data is coming.
    /// </remarks>
    public double? OscillatorControl =>
        Connection == ConnectionStatus.Connected ? _store.OscillatorControl : null;

    // ---- Staleness ------------------------------------------------------------------------

    /// <summary>How old the fast-tier readings are.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFastPoll);

    /// <summary>That age in words (§9.11).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>How bad that age is.</summary>
    public Severity AgeSeverity => Staleness.SeverityOf(Age);

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    private double? Connected(double? value) =>
        Connection == ConnectionStatus.Connected ? value : null;
}

/// <summary>One line of the §10.4 health monitor.</summary>
/// <param name="Name">What the receiver calls it, verbatim.</param>
/// <param name="IsOk">Whether it passed.</param>
public readonly record struct HealthItem(string Name, bool IsOk)
{
    /// <summary>How it renders through <c>SeverityPill</c>, which is the only route §9.4.3 allows.</summary>
    public Severity Severity => IsOk ? Severity.Success : Severity.Critical;
}
