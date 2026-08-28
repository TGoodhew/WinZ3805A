using System.Globalization;

namespace WinZ3805A.Services;

/// <summary>
/// Whether a fast sweep could have come from the instrument at all (#209, #241, #237).
/// </summary>
/// <remarks>
/// <para>
/// #209 established the rule and the reason: a sweep whose sync state is not a state this receiver
/// reports is somebody else's reply, and storing it puts values in a durable seven-day series the
/// instrument cannot produce. It also considered a range check and deliberately did not use one —
/// the sweep that prompted it carried an EFC of <b>+2 %</b>, inside the oscillator's control range
/// and indistinguishable from a real reading by magnitude alone.
/// </para>
/// <para>
/// <b>That argument is about one field being wrong, not about the sweep being someone else's.</b>
/// The sync state is read on its own before the loop that reads everything else, so a slip that
/// begins <i>inside</i> that loop leaves the sync state correct and shifts every later answer —
/// and #209's discriminator, which asks only about the sync state, passes it. That is #237's
/// remaining risk in one sentence: "a slip that leaves a plausible sync state while corrupting the
/// others is stored in full, and nothing would show it."
/// </para>
/// <para>
/// So each field is checked against a bound it cannot cross. Every bound here is <b>documented or
/// physical</b>, never observed — a limit fitted to the data would reject a receiver in a state this
/// application has not seen yet, which is exactly the reading a diagnostic tool must not discard.
/// </para>
/// <list type="table">
/// <listheader><term>Field</term><description>Bound, and where it comes from</description></listheader>
/// <item><term>1 PPS time interval</term><description>±0.5 s — a phase offset against a 1 Hz signal; beyond half a second the nearer pulse is the next one</description></item>
/// <item><term>TFOM</term><description>0–9 — "a number between 0 (best) and 9" (Z3801A guide)</description></item>
/// <item><term>FFOM</term><description>0–3 — the guide's table lists exactly those four values</description></item>
/// <item><term>EFC</term><description>−100 to +100 % — ":DIAG:ROSC:EFC:REL? outputs EFC value in %, range -100% to +100%"</description></item>
/// <item><term>Tracked satellites</term><description>0–32 — there are 32 PRNs; a receiver cannot track more satellites than exist</description></item>
/// </list>
/// <para>
/// A missing value is always plausible. §11.1 makes an unparseable field null and the receiver
/// legitimately declines some queries in some states, so absence says nothing about a slip.
/// </para>
/// </remarks>
public static class ReadingPlausibility
{
    /// <summary>The largest 1 PPS time interval that is physically meaningful, in nanoseconds.</summary>
    /// <remarks>
    /// Half a second. The measurement is the offset of the receiver's pulse from GPS, and at more
    /// than half a second the nearer pulse is the next one — so a larger reading is not a big offset,
    /// it is not an offset.
    /// </remarks>
    public const double TimeIntervalBoundNanoseconds = 5e8;

    /// <summary>The worst Time Figure of Merit the receiver reports.</summary>
    public const int WorstTfom = 9;

    /// <summary>The worst Frequency Figure of Merit the receiver reports.</summary>
    public const int WorstFfom = 3;

    /// <summary>The full-scale oscillator control range, as a percentage.</summary>
    public const double EfcPercentBound = 100;

    /// <summary>How many satellites the constellation has, and so the most any receiver can track.</summary>
    public const int MostTrackableSatellites = 32;

    /// <summary>Whether a 1 PPS time interval could have come from the instrument.</summary>
    /// <param name="nanoseconds">The parsed reading, or null if there was not one.</param>
    public static bool IsPossibleTimeInterval(double? nanoseconds) =>
        nanoseconds is not double value
        || (double.IsFinite(value) && Math.Abs(value) <= TimeIntervalBoundNanoseconds);

    /// <summary>
    /// Why this sweep cannot be a reading, or <see langword="null"/> if nothing rules it out.
    /// </summary>
    /// <remarks>
    /// Returns the sentence rather than a boolean because the caller logs it, and a guard that drops
    /// readings while looking healthy is worse than no guard — #209 made the same choice for the
    /// same reason. Each message names the value and the bound, so a field report says which field
    /// slipped rather than only that something did.
    /// </remarks>
    /// <param name="timeIntervalNanoseconds">1 PPS time interval.</param>
    /// <param name="tfom">Time Figure of Merit.</param>
    /// <param name="ffom">Frequency Figure of Merit.</param>
    /// <param name="efcPercent">Oscillator control, as a percentage of full scale.</param>
    /// <param name="trackedCount">Satellites being tracked.</param>
    public static string? Implausible(
        double? timeIntervalNanoseconds,
        int? tfom,
        int? ffom,
        double? efcPercent,
        int? trackedCount)
    {
        if (!IsPossibleTimeInterval(timeIntervalNanoseconds))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the 1 PPS time interval read {timeIntervalNanoseconds:F0} ns, outside the " +
                $"±{TimeIntervalBoundNanoseconds:F0} ns a phase offset against a 1 Hz signal can take");
        }

        if (tfom is int t && (t < 0 || t > WorstTfom))
        {
            return string.Create(CultureInfo.InvariantCulture, $"TFOM read {t}, outside 0–{WorstTfom}");
        }

        if (ffom is int f && (f < 0 || f > WorstFfom))
        {
            return string.Create(CultureInfo.InvariantCulture, $"FFOM read {f}, outside 0–{WorstFfom}");
        }

        if (efcPercent is double e && (!double.IsFinite(e) || Math.Abs(e) > EfcPercentBound))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"EFC read {e:F2} %, outside ±{EfcPercentBound:F0} %");
        }

        if (trackedCount is int n && (n < 0 || n > MostTrackableSatellites))
        {
            return string.Create(
                CultureInfo.InvariantCulture,
                $"the tracked count read {n}, and there are only {MostTrackableSatellites} satellites");
        }

        return null;
    }
}
