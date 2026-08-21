using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>What, if anything, the user should be told (§10.3, P1-9).</summary>
public enum LockAlert
{
    /// <summary>Nothing. The overwhelmingly common answer.</summary>
    None = 0,

    /// <summary>The receiver has stopped disciplining to GPS and has stayed that way.</summary>
    Lost,

    /// <summary>It is locked again, after a loss the user was told about.</summary>
    Regained,
}

/// <summary>
/// Decides when losing lock is worth interrupting someone over (P1-9).
/// </summary>
/// <remarks>
/// <para>
/// <b>The hard part of this feature is not raising a notification; it is not raising one.</b> #57
/// says so in as many words — "a notification on every state change would train the user to ignore
/// them" — and the bench receiver makes the point quantitatively: 109 holdovers in 8.9 days, about
/// twelve a day, median outage 10.3 minutes. Announcing each transition would produce roughly two
/// dozen toasts a day, and the first thing any user would do is switch them off, which leaves them
/// worse informed than before the feature existed.
/// </para>
/// <para>
/// <b>So a loss is announced only once it has lasted.</b> Nothing is said for the first
/// <see cref="Grace"/> of an outage; if the receiver recovers inside that window nothing is ever
/// said at all. On the bench receiver's own numbers a minute of grace stays silent through the
/// brief flaps and still announces the median ten-minute outage — and, more importantly, the
/// distinction it draws is the right one: a receiver that drops out for twenty seconds and comes
/// back has not cost the user anything they need to know about.
/// </para>
/// <para>
/// <b>Recovery is announced only if the loss was.</b> A user told the receiver has lost lock is
/// owed the other half; a user told nothing must not be told "it is back", which would be an alert
/// about an event they were deliberately not alerted to. That symmetry caps an episode at two
/// notifications however long it lasts and however much the state flaps inside it.
/// </para>
/// <para>
/// All decisions are made from an injected clock and a mode, so the whole policy is testable
/// without a receiver, without a timer and without a toast (§12).
/// </para>
/// </remarks>
public sealed class LockWatch
{
    /// <summary>How long a loss must persist before it is worth saying anything.</summary>
    /// <remarks>
    /// One minute. Long enough to swallow the flaps that dominate this receiver's log, short enough
    /// that a real outage is reported while it is still news. Not user-settable: a figure that could
    /// be set to zero would recreate the problem it exists to prevent.
    /// </remarks>
    public static readonly TimeSpan Grace = TimeSpan.FromMinutes(1);

    private readonly TimeProvider _timeProvider;

    private SmartClockMode _mode = SmartClockMode.Unknown;
    private DateTimeOffset? _lostAt;
    private bool _announced;

    /// <summary>Creates a watch over a clock.</summary>
    public LockWatch(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>The mode last observed.</summary>
    public SmartClockMode Mode => _mode;

    /// <summary>Whether a loss is being timed but has not yet been announced.</summary>
    public bool IsWaitingOutGrace => _lostAt is not null && !_announced;

    /// <summary>
    /// Takes an observation and says what to tell the user.
    /// </summary>
    /// <param name="mode">The receiver's mode, from the status screen.</param>
    /// <remarks>
    /// <para>
    /// Called on every full sweep, so it must be cheap and it must be idempotent for an unchanged
    /// mode — the same mode observed ten times running is one event, not ten.
    /// </para>
    /// <para>
    /// <see cref="SmartClockMode.Unknown"/> is not a loss. It means the status screen carried no
    /// mode marker, which happens when a sweep is missed or a line is mangled, and treating it as
    /// an outage would announce a parsing hiccup as a receiver fault (§11.1).
    /// </para>
    /// <para>
    /// <see cref="SmartClockMode.PowerUp"/> is not a loss either. A receiver that has just been
    /// powered on has not <i>lost</i> anything; it has not acquired yet, which the user can see and
    /// did not need waking for.
    /// </para>
    /// </remarks>
    public LockAlert Observe(SmartClockMode mode)
    {
        SmartClockMode previous = _mode;
        _mode = mode;

        if (mode == SmartClockMode.Unknown)
        {
            // Say nothing and change nothing: an unreadable sweep is not evidence either way, so a
            // loss already being timed keeps its clock running.
            _mode = previous;
            return LockAlert.None;
        }

        if (mode == SmartClockMode.Locked)
        {
            bool owed = _announced;

            _lostAt = null;
            _announced = false;

            return owed ? LockAlert.Regained : LockAlert.None;
        }

        if (mode == SmartClockMode.PowerUp)
        {
            _lostAt = null;
            _announced = false;
            return LockAlert.None;
        }

        // Recovery or holdover: the receiver is not disciplining to GPS.
        _lostAt ??= _timeProvider.GetUtcNow();

        if (_announced || _timeProvider.GetUtcNow() - _lostAt < Grace)
        {
            return LockAlert.None;
        }

        _announced = true;
        return LockAlert.Lost;
    }

    /// <summary>
    /// Forgets everything, for a session that has gone away.
    /// </summary>
    /// <remarks>
    /// A disconnect is not a loss of lock — the receiver may be perfectly happy and the cable may
    /// be out — so a pending grace period is dropped rather than allowed to mature into an alert
    /// about a receiver nobody is talking to.
    /// </remarks>
    public void Reset()
    {
        _mode = SmartClockMode.Unknown;
        _lostAt = null;
        _announced = false;
    }

    /// <summary>The §9.11 wording for an alert, or null for <see cref="LockAlert.None"/>.</summary>
    /// <param name="alert">What happened.</param>
    /// <param name="mode">The mode it happened into, which distinguishes holdover from recovery.</param>
    /// <remarks>
    /// §9.11's copy rules: say what happened and what to do next, second person, no apology. The
    /// two losses read differently on purpose — holdover means the oscillator is coasting on its own
    /// and time is drifting, while recovery means the receiver is already working on it.
    /// </remarks>
    public static (string Title, string Body)? Describe(LockAlert alert, SmartClockMode mode) => alert switch
    {
        LockAlert.Lost when mode == SmartClockMode.Holdover => (
            "Receiver in holdover",
            "It is running on its own oscillator with no GPS discipline, so its time is drifting. "
            + "Check the antenna and its cable."),

        LockAlert.Lost => (
            "Receiver has lost GPS lock",
            "It is trying to reacquire. If this lasts, check the antenna and its cable."),

        LockAlert.Regained => (
            "Receiver has regained GPS lock",
            "It is disciplining to GPS again. Nothing to do."),

        _ => null,
    };
}
