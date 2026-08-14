using System.ComponentModel;
using System.Globalization;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The Time page.
/// </summary>
/// <remarks>
/// <para>
/// <b>§10.2 requires this destination and no §10.x section describes it.</b> The window inventory
/// lists "Time &amp; Leap Seconds — Page in Details", §9.7.1 draws it in the pane, and §15 step 8
/// puts it in the build order, but the numbered sections run §10.9 Diagnostics, §10.10 Status
/// Registers, §10.11 Advanced Console with nothing in between. Filed rather than invented around.
/// </para>
/// <para>
/// So the content comes from what the specification <i>does</i> define for this data: §7.4's week
/// rollover, §11.2's time fields, and #95's display-zone requirement. It is the §10.3 clock line
/// with its workings shown — which is what a page called Time is for, and what a user checking a
/// suspect date needs.
/// </para>
/// </remarks>
public sealed class TimeViewModel : INotifyPropertyChanged
{
    private readonly ReceiverStateStore _store;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;
    private TimeZoneInfo _displayZone = TimeZoneInfo.Local;

    /// <summary>Creates a view model over the shared store.</summary>
    public TimeViewModel(ReceiverStateStore store)
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

    /// <summary>Which zone the shown time is converted into (#95).</summary>
    public TimeZoneInfo DisplayZone
    {
        get => _displayZone;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!_displayZone.Equals(value))
            {
                _displayZone = value;
                RaiseAll();
            }
        }
    }

    private ReceiverStatus? Status =>
        Connection == ConnectionStatus.Connected ? _store.Status : null;

    /// <summary>Which scale the receiver's clock is referenced to.</summary>
    public TimeScale TimeScale => Status?.TimeScale ?? TimeScale.Unknown;

    /// <summary>That scale in words.</summary>
    public string TimeScaleText => TimeScale switch
    {
        TimeScale.Utc => "UTC",
        TimeScale.Gps => "GPS time",
        TimeScale.Local => "Local time, derived from UTC",
        TimeScale.LocalGps => "Local time, derived from GPS",
        _ => ReadoutFormatter.NoValue,
    };

    /// <summary>
    /// What the difference between the scales means, when the receiver is on GPS time.
    /// </summary>
    /// <remarks>
    /// GPS time does not include leap seconds and is ahead of UTC by their accumulated count. A
    /// user reading a timestamp off this receiver and comparing it against a UTC source needs to
    /// know that, and the page is the only place that can tell them.
    /// </remarks>
    public string? TimeScaleNote => TimeScale switch
    {
        TimeScale.Gps or TimeScale.LocalGps =>
            "GPS time does not include leap seconds, so it runs ahead of UTC by their accumulated count.",
        _ => null,
    };

    /// <summary>The time the page shows, converted into <see cref="DisplayZone"/>.</summary>
    public DisplayTime? ShownTime => DisplayTimeConverter.Convert(
        Status?.CorrectedDateTime ?? Status?.DeviceDateTime,
        TimeScale,
        DisplayZone);

    /// <summary>That time, formatted.</summary>
    public string ShownTimeText => ShownTime is DisplayTime shown
        ? shown.Value.ToString("HH:mm:ss", CultureInfo.InvariantCulture)
          + $" {shown.ZoneLabel}"
          + shown.Value.ToString(" · dd MMM yyyy", CultureInfo.CurrentCulture)
        : ReadoutFormatter.NoValue;

    /// <summary>What the receiver itself reported, before any correction.</summary>
    public string DeviceTimeText => Status?.DeviceDateTime is DateTimeOffset raw
        ? raw.ToString("HH:mm:ss · dd MMM yyyy", CultureInfo.CurrentCulture)
        : ReadoutFormatter.NoValue;

    /// <summary>Whether §7.4's week-rollover correction is being applied.</summary>
    public bool IsDateCorrected => (Status?.WeekRolloverEpochs ?? 0) != 0;

    /// <summary>How many 1024-week epochs the correction adds.</summary>
    public int RolloverEpochs => Status?.WeekRolloverEpochs ?? 0;

    /// <summary>
    /// The rollover explanation, in full.
    /// </summary>
    /// <remarks>
    /// §7.4 requires the corrected date to be shown with the raw one available rather than
    /// substituted silently — "a user who sees the wrong year and no explanation reasonably
    /// concludes the receiver has failed". This page is where the whole of that reasoning fits.
    /// </remarks>
    public string RolloverText
    {
        get
        {
            if (Status is null)
            {
                return ReadoutFormatter.NoValue;
            }

            if (!IsDateCorrected)
            {
                return "None. The receiver's own date is being shown unchanged.";
            }

            // The total is only worth stating when it differs from the epoch length; "1 epoch of
            // 1024 weeks (1024 weeks)" is the same number twice.
            string total = RolloverEpochs == 1
                ? "1 epoch of 1024 weeks added."
                : $"{RolloverEpochs} epochs of 1024 weeks — {RolloverEpochs * 1024} weeks — added.";

            return total + " GPS transmits the week number in ten bits, so it wraps about every "
                + "19.6 years and a receiver of this age reports a date that far in the past.";
        }
    }

    /// <summary>How bad the rollover state is.</summary>
    /// <remarks>
    /// Informational, not a caution. A corrected date is the app working as designed, and §9.4.3's
    /// severities are for the receiver's condition rather than for the app's own arithmetic.
    /// </remarks>
    public Severity RolloverSeverity => IsDateCorrected ? Severity.Info : Severity.Neutral;

    /// <summary>Whether a leap second is announced for the end of the current UTC month.</summary>
    public LeapSecondPending LeapPending => Status?.LeapPending ?? LeapSecondPending.None;

    /// <summary>That announcement in words.</summary>
    public string LeapPendingText => LeapPending switch
    {
        LeapSecondPending.Plus => "A leap second will be inserted at the end of the current UTC month.",
        LeapSecondPending.Minus => "A leap second will be removed at the end of the current UTC month.",
        _ => "None announced.",
    };

    /// <summary>How bad that is.</summary>
    /// <remarks>
    /// A pending leap second is a caution rather than neutral: it is a step the 1 PPS will take
    /// that anything downstream counting seconds needs to expect.
    /// </remarks>
    public Severity LeapSeverity =>
        LeapPending == LeapSecondPending.None ? Severity.Neutral : Severity.Caution;

    /// <summary>The 1 PPS clock advisory the status screen printed (§11.3).</summary>
    public ClockAdvisory Advisory => Status?.OnePpsClockAdvisory ?? ClockAdvisory.None;

    /// <summary>How old these readings are — they arrive on the full sweep.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFullPoll);

    /// <summary>That age in words (§9.11).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
