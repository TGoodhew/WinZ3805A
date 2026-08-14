using System.ComponentModel;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.8 Holdover page.
/// </summary>
/// <remarks>
/// Read-only. Every control that acts here — applying a threshold, forcing holdover, recovering,
/// ignoring the recovery limit — is §8.3 tier C, and forcing holdover carries a guard of its own
/// that §15 step 10 has to build with the dialog.
/// </remarks>
public sealed class HoldoverViewModel : INotifyPropertyChanged
{
    private readonly ReceiverStateStore _store;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;

    /// <summary>Creates a view model over the shared store.</summary>
    public HoldoverViewModel(ReceiverStateStore store)
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

    /// <summary>The receiver's synchronisation mode.</summary>
    public ReceiverMode Mode => Connection == ConnectionStatus.Connected
        ? ReceiverModes.FromSyncState(_store.SyncState)
        : ReceiverMode.Disconnected;

    /// <summary>
    /// Whether the receiver is in holdover in any of its three forms.
    /// </summary>
    /// <remarks>
    /// Holding, Waiting to Recover and Recovering are three separate bits of the Holdover status
    /// register (#34) and three separate <c>:SYNC:STAT?</c> answers, but all three mean the 10 MHz
    /// is running on the oscillator's own memory rather than on GPS. A page that only counted
    /// <c>HOLD</c> would report "not in holdover" while the receiver was recovering from one.
    /// </remarks>
    public bool IsInHoldover => Mode is ReceiverMode.Holdover or ReceiverMode.Waiting or ReceiverMode.Recovering;

    /// <summary>The state line at the top of the page.</summary>
    public string StateText => Mode switch
    {
        ReceiverMode.Locked => "Locked to GPS — not in holdover",
        ReceiverMode.Holdover => "In holdover — running on the oscillator alone",
        ReceiverMode.Waiting => "Waiting to recover from holdover",
        ReceiverMode.Recovering => "Recovering from holdover",
        ReceiverMode.PowerUp => "Powering up",
        ReceiverMode.Off => "Diagnostic or off",
        _ => "Not connected",
    };

    /// <summary>How bad that state is.</summary>
    /// <remarks>
    /// Recovering and waiting are cautions rather than criticals: the outputs are still usable and
    /// the receiver is on its way back. Holding is critical because the error grows for as long as
    /// it lasts, and nothing downstream will say so.
    /// </remarks>
    public Severity StateSeverity => Mode switch
    {
        ReceiverMode.Locked => Severity.Success,
        ReceiverMode.Holdover => Severity.Critical,
        ReceiverMode.Waiting or ReceiverMode.Recovering => Severity.Caution,
        _ => Severity.Neutral,
    };

    /// <summary>Predicted 24-hour uncertainty, given the current state of SmartClock learning.</summary>
    public (string Value, string Unit) Predicted =>
        ReadoutFormatter.Seconds(Status?.HoldoverPredictedSeconds);

    /// <summary>
    /// The time error accumulated so far in this holdover.
    /// </summary>
    /// <remarks>
    /// Only meaningful while in holdover. The 58503A guide is explicit that
    /// <c>:SYNC:HOLD:TUNC:PRESent?</c> answers error −230 when the receiver is not in holdover, so
    /// a page that showed this unconditionally would be showing a figure the device declines to
    /// give (#34).
    /// </remarks>
    public (string Value, string Unit) PresentError => IsInHoldover
        ? ReadoutFormatter.Seconds(Status?.HoldoverPresentSeconds)
        : (ReadoutFormatter.NoValue, string.Empty);

    /// <summary>How long this holdover has lasted.</summary>
    /// <remarks>
    /// Unparsed pending #4: §11.2's <c>HoldoverDuration</c> has no known screen label, and the
    /// fixture that would settle it is one of the captures still waiting for bench time. It shows
    /// the §11.1 dash rather than a zero, because a zero would read as "no time has passed".
    /// </remarks>
    public string DurationText => Status?.HoldoverDuration is TimeSpan duration
        ? Staleness.Describe(duration)
        : ReadoutFormatter.NoValue;

    /// <summary>
    /// Why the receiver is waiting rather than recovering.
    /// </summary>
    /// <remarks>
    /// §10.3 takes this from <c>:SYNC:HOLD:WAIT?</c>. Nothing queries it yet — the §7.3 fast tier
    /// is six commands and this is not one of them — so what is shown is the status screen's own
    /// mode detail, which carries the same sentence when the receiver prints one.
    /// </remarks>
    public string WaitingReasonText => IsInHoldover && !string.IsNullOrWhiteSpace(Status?.ModeDetail)
        ? Status.ModeDetail
        : ReadoutFormatter.NoValue;

    /// <summary>The 1 PPS time interval at which the receiver enters holdover.</summary>
    public (string Value, string Unit) Threshold =>
        ReadoutFormatter.Seconds(Status?.HoldThresholdSeconds, decimalPlaces: 3);

    /// <summary>
    /// Whether the predicted uncertainty is past the threshold the receiver is holding it to.
    /// </summary>
    /// <remarks>
    /// The §10.8 wireframe's "Currently exceeded" row. Both figures are in seconds and comparable
    /// directly; the display units may differ, which is exactly why the comparison is made here
    /// rather than left to a reader looking at "2.0 µs" beside "1.000 µs".
    /// </remarks>
    public bool? IsThresholdExceeded =>
        Status?.HoldoverPredictedSeconds is double predicted && Status.HoldThresholdSeconds is double threshold
            ? predicted > threshold
            : null;

    /// <summary>That comparison in words.</summary>
    public string ThresholdExceededText => IsThresholdExceeded switch
    {
        true => "Yes — the predicted uncertainty is past the threshold",
        false => "No",
        _ => ReadoutFormatter.NoValue,
    };

    /// <summary>How bad that is.</summary>
    public Severity ThresholdSeverity => IsThresholdExceeded switch
    {
        true => Severity.Caution,
        false => Severity.Success,
        _ => Severity.Neutral,
    };

    /// <summary>How old these readings are — they arrive on the full sweep.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFullPoll);

    /// <summary>The guard §10.8 puts above the manual-control buttons, or null when there is none.</summary>
    public PowerUpGuard? PowerUp { get; init; }

    /// <summary>
    /// §10.8's "time since power-up" line — the figure, and how much of a figure it is.
    /// </summary>
    /// <remarks>
    /// A lower bound is labelled as one rather than rounded off into a plain duration. "At least
    /// 3 h" and "3 h" look alike and mean quite different things here: the first is compatible with
    /// a receiver that came up a year ago, and it is the second that would justify the word "safe".
    /// </remarks>
    public string PowerUpText
    {
        get
        {
            if (PowerUp is not PowerUpGuard guard || guard.Elapsed is not TimeSpan elapsed)
            {
                return "Unknown";
            }

            string duration = Staleness.DescribeDuration(elapsed);
            return guard.IsLowerBound ? $"At least {duration}" : duration;
        }
    }

    /// <summary>Whether forcing holdover is known to be safe, known to be too soon, or neither.</summary>
    public PowerUpSafety PowerUpSafety => PowerUp?.Safety ?? Services.PowerUpSafety.Unknown;

    /// <summary>§9.4.3 needs the guard's verdict in words as well as in colour.</summary>
    public string PowerUpVerdictText => PowerUpSafety switch
    {
        Services.PowerUpSafety.Safe => "Safe",
        Services.PowerUpSafety.TooSoon => "Too soon",
        _ => "Unverified",
    };

    /// <summary>The pill beside the verdict.</summary>
    public Severity PowerUpSeverity => PowerUpSafety switch
    {
        Services.PowerUpSafety.Safe => Severity.Success,
        Services.PowerUpSafety.TooSoon => Severity.Critical,
        _ => Severity.Caution,
    };

    /// <summary>
    /// The extra warning the confirmation dialog carries, or null when the guard is satisfied.
    /// </summary>
    /// <remarks>
    /// §10.8 requires the extra acknowledgement whenever the elapsed time cannot be determined.
    /// <c>:SYNC:HOLDover:INITiate</c> is one of §9.7.4's four strong variants and so always carries
    /// a tick regardless — this only changes what the user is ticking against.
    /// </remarks>
    public string? PowerUpCaution => PowerUpSafety switch
    {
        Services.PowerUpSafety.Safe => null,
        Services.PowerUpSafety.TooSoon =>
            $"This receiver powered up {PowerUpText.ToLowerInvariant()} ago, inside the 24-hour "
            + "SmartClock learning period. Forcing holdover now corrupts that learning.",
        _ =>
            "How long this receiver has been powered up could not be determined, so the 24-hour "
            + "SmartClock learning period cannot be ruled out.",
    };

    /// <summary>Whether the receiver can be asked to recover, per §10.8's button row.</summary>
    public bool CanRecover => Connection == ConnectionStatus.Connected && IsInHoldover;

    /// <summary>Whether holdover can be forced — which is to say, whether it is not already in it.</summary>
    public bool CanForceHoldover => Connection == ConnectionStatus.Connected && !IsInHoldover;

    /// <summary>That age in words (§9.11).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
