using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>How confident the app is that forcing holdover is safe (§10.8).</summary>
public enum PowerUpSafety
{
    /// <summary>Not enough is known to say. §10.8 requires the extra acknowledgement here.</summary>
    Unknown = 0,

    /// <summary>The receiver has certainly been up for at least 24 hours.</summary>
    Safe,

    /// <summary>The receiver powered up under this app less than 24 hours ago.</summary>
    TooSoon,
}

/// <summary>
/// Tracks how long the receiver has been powered up, for §10.8's manual-holdover guard.
/// </summary>
/// <remarks>
/// <para>
/// Forcing holdover within 24 hours of power-up corrupts SmartClock oscillator learning, which
/// takes days to rebuild and which nothing in the interface would show going wrong. §10.8 therefore
/// puts the elapsed time on the card and requires the extra acknowledgement whenever it cannot be
/// determined.
/// </para>
/// <para>
/// <b>The distinction between "too soon" and "unknown" is the whole point of this class</b>, and it
/// is not symmetric. An observation can only ever establish a <i>lower</i> bound on uptime: an app
/// that has watched a locked receiver for three days knows it has been up at least three days, but
/// an app started ten minutes ago knows nothing — the receiver may have been running for a year.
/// So a lower bound at or past 24 hours is <see cref="PowerUpSafety.Safe"/>, while a lower bound
/// short of it is <see cref="PowerUpSafety.Unknown"/> and not
/// <see cref="PowerUpSafety.TooSoon"/>. Only a power-up this app actually watched happen gives an
/// exact figure, and only that can say "too soon".
/// </para>
/// <para>
/// §10.8 names the diagnostic log's power-on entries as the other source. That is deferred: the
/// entry's wording is not in any captured fixture (#4) and the guard would be keying a safety
/// decision on a string nobody has seen the receiver print. Until a capture exists it degrades to
/// <see cref="PowerUpSafety.Unknown"/>, which is the behaviour §10.8 specifies for exactly this
/// case, so nothing is silently weakened by the gap.
/// </para>
/// </remarks>
public sealed class PowerUpGuard
{
    /// <summary>§10.8's threshold, and §8.3's confirmation text quotes the same figure.</summary>
    public static readonly TimeSpan LearningPeriod = TimeSpan.FromHours(24);

    private readonly TimeProvider _timeProvider;

    /// <summary>When this app last saw the receiver report power-up, or null if it never has.</summary>
    private DateTimeOffset? _poweredUpAt;

    /// <summary>When unbroken observation of a running receiver began, or null while disconnected.</summary>
    private DateTimeOffset? _watchingSince;

    /// <summary>Creates a guard on the given clock.</summary>
    public PowerUpGuard(TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        _timeProvider = timeProvider;
    }

    /// <summary>How long the receiver is known to have been up, or null when nothing is known.</summary>
    public TimeSpan? Elapsed
    {
        get
        {
            DateTimeOffset now = _timeProvider.GetUtcNow();

            if (_poweredUpAt is DateTimeOffset poweredUp)
            {
                return now - poweredUp;
            }

            return _watchingSince is DateTimeOffset since ? now - since : null;
        }
    }

    /// <summary>True when <see cref="Elapsed"/> is a floor rather than the actual uptime.</summary>
    public bool IsLowerBound => _poweredUpAt is null;

    /// <summary>What the guard can say about forcing holdover now.</summary>
    public PowerUpSafety Safety
    {
        get
        {
            if (Elapsed is not TimeSpan elapsed)
            {
                return PowerUpSafety.Unknown;
            }

            if (elapsed >= LearningPeriod)
            {
                return PowerUpSafety.Safe;
            }

            // Short of the threshold, only an observed power-up is evidence of anything.
            return IsLowerBound ? PowerUpSafety.Unknown : PowerUpSafety.TooSoon;
        }
    }

    /// <summary>
    /// Records what the receiver is currently reporting. Called on every full poll.
    /// </summary>
    public void Observe(SmartClockMode mode)
    {
        DateTimeOffset now = _timeProvider.GetUtcNow();

        if (mode == SmartClockMode.PowerUp)
        {
            // Keep the first sighting, not the latest: the receiver stays in this mode for minutes,
            // and re-stamping on each poll would hold the elapsed time near zero for the whole of it.
            _poweredUpAt ??= now;
            _watchingSince ??= now;
            return;
        }

        if (mode == SmartClockMode.Unknown)
        {
            // An unparsed mode line is not evidence the receiver is running. Ignoring it keeps a
            // dropped screen from starting a watch that would later be reported as uptime.
            return;
        }

        _watchingSince ??= now;
    }

    /// <summary>
    /// Records that observation has broken — a disconnect, a fault, or a reconnect.
    /// </summary>
    /// <remarks>
    /// The lower bound cannot survive a gap: the receiver may have been power-cycled during it, and
    /// that is precisely the case the guard exists to catch. An observed power-up does survive,
    /// because a receiver that came up at a known instant did not un-come-up while the app was
    /// looking away.
    /// </remarks>
    public void ObservationBroken() => _watchingSince = null;

    /// <summary>Forgets everything, for a session pointed at a different receiver.</summary>
    public void Reset()
    {
        _poweredUpAt = null;
        _watchingSince = null;
    }
}
