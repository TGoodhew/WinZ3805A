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
/// <b>Specified in §10.14</b>, which was written after this page rather than before it. §10.2
/// required the destination, §9.7.1 drew it in the pane and §15 step 8 put it in the build order,
/// but no §10.x section described it — so this was built from what the specification <i>does</i>
/// define for the data, and the gap was filed as #111 rather than invented around.
/// </para>
/// <para>
/// That reading turned out to be the one §10.14 adopted: §7.4's week rollover, §11.2's time fields,
/// and #95's display zone. It is the §10.3 clock line with its workings shown — which is what a
/// page called Time is for, and what a user checking a suspect date needs.
/// </para>
/// <para>
/// <b>The leap-second card's accumulated offset does not pass through here.</b> §10.14's GPS−UTC
/// offset from <c>:PTIM:LEAP:ACC?</c> is a query rather than a status-screen field, so
/// <c>TimePage</c>'s code-behind asks for it and fills <c>AccumulatedText</c> itself (#149, closed
/// 20 Aug 2026); this view model carries only the status screen's pending flag. The receiver's own
/// answers to all four queries are recorded in §10.14.
/// </para>
/// </remarks>
public sealed class TimeViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly ReceiverStateStore _store;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;
    private TimeZoneInfo _displayZone = TimeZoneInfo.Local;

    /// <summary>Creates a view model over the shared store.</summary>
    public TimeViewModel(ReceiverStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _store.PropertyChanged += OnStoreChanged;
    }

    /// <summary>Lets go of the store, which outlives every page (#388).</summary>
    /// <remarks>
    /// The store is registered for the application's lifetime, so a model that subscribes to it and
    /// never unsubscribes is rooted for that lifetime - and so is the page holding the model. A
    /// lambda cannot be unsubscribed, which is why this handler has a name. See
    /// <see cref="OverviewViewModel.Dispose"/> for the measurement that found it.
    /// </remarks>
    public void Dispose() => _store.PropertyChanged -= OnStoreChanged;

    private void OnStoreChanged(object? sender, PropertyChangedEventArgs e) => RaiseAll();

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

    /// <summary>Whether the receiver marked its time as the provisional power-up value (#245).</summary>
    public bool IsTimeProvisional => Status?.DeviceTimeIsProvisional ?? false;

    /// <summary>
    /// What the power-up marker means, in the terms the manual uses.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Written out rather than reduced to a pill, because the marker is a statement about how far
    /// the reading may be from the truth and that range is enormous. The screen captured from this
    /// unit was accurate to the minute — the oscillator held time across the power cycle — while the
    /// Z3801A guide's own example is <c>12:00:00[?] 01 JAN 1996</c>, a default that is arbitrarily
    /// wrong. Nothing on the screen distinguishes those two cases, so the honest thing to say is
    /// that it is unverified, not to guess which one this is.
    /// </para>
    /// <para>
    /// Independent of <see cref="IsDateCorrected"/>. A provisional time still gets §7.4's rollover
    /// arithmetic applied on top, and both caveats can be in force at once — which is exactly the
    /// power-up state, and why they are separate sentences rather than one combined message.
    /// </para>
    /// </remarks>
    public string? ProvisionalText => IsTimeProvisional
        ? "The receiver marked this time as its power-up default, not yet corrected from GPS. It is "
          + "corrected once the first satellite is tracked, and may be wrong by any amount until then."
        : null;

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

            // The last sentence is the one a user actually needs, and #10 names it as a
            // criterion rather than a nicety: someone who has just been told their timing
            // reference thinks it is 2006 wants to know whether the output they are
            // disciplining to is wrong. It is not. Only the calendar date wraps - the
            // time of day and the 1 PPS are unaffected, and saying so here is the
            // difference between an explanation and an alarm.
            return total + " GPS transmits the week number in ten bits, so it wraps about every "
                + "19.6 years and a receiver of this age reports a date that far in the past. "
                + "The time of day and the 1 PPS output are unaffected: only the date wraps.";
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
