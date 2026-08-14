using WinZ3805A.Controls;

namespace WinZ3805A.ViewModels;

/// <summary>
/// How old a reading is allowed to get before the interface says so (§9.11, §10.3).
/// </summary>
/// <remarks>
/// <para>
/// Stale data is dimmed and timestamped, never blanked. An old reading with an honest age beats an
/// empty field, which tells the user nothing about whether the value or the link is the problem.
/// </para>
/// <para>
/// The thresholds are §10.3's: tertiary normally, caution past 15 seconds, critical past 60. Kept
/// here as pure logic rather than in the view, because "how stale is too stale" is a judgement the
/// application makes once and a binding converter would scatter.
/// </para>
/// </remarks>
public static class Staleness
{
    /// <summary>Past this, the footer turns caution (§10.3).</summary>
    public static readonly TimeSpan CautionThreshold = TimeSpan.FromSeconds(15);

    /// <summary>Past this, the footer turns critical (§10.3).</summary>
    public static readonly TimeSpan CriticalThreshold = TimeSpan.FromSeconds(60);

    /// <summary>Which severity an age of <paramref name="age"/> should be drawn in.</summary>
    /// <remarks>
    /// A never-updated reading is <see cref="Severity.Neutral"/> rather than critical: nothing has
    /// gone stale if nothing has arrived, and a fresh window shouting about an alarm before its
    /// first poll would be crying wolf.
    /// </remarks>
    public static Severity SeverityOf(TimeSpan? age) => age switch
    {
        null => Severity.Neutral,
        TimeSpan value when value >= CriticalThreshold => Severity.Critical,
        TimeSpan value when value >= CautionThreshold => Severity.Caution,
        _ => Severity.Neutral,
    };

    /// <summary>
    /// The age in words, as §10.3 puts it in the footer: <em>updated 1 s ago</em>.
    /// </summary>
    /// <remarks>
    /// Coarse on purpose, and coarser as it gets older. Nobody reading a footer needs to know a
    /// reading is 97 seconds old rather than 96 — they need to know it is about a minute and a half
    /// behind. Exact seconds would also make the line rewrite itself every second, which is motion
    /// on a window that is meant to sit still.
    /// </remarks>
    public static string Describe(TimeSpan? age)
    {
        if (age is not TimeSpan value)
        {
            return "never updated";
        }

        if (value < TimeSpan.Zero)
        {
            // A clock that stepped backwards. Saying "in 3 seconds" would be worse than vague.
            return "updated just now";
        }

        if (value < TimeSpan.FromSeconds(2))
        {
            return "updated just now";
        }

        if (value < TimeSpan.FromMinutes(1))
        {
            return $"updated {(int)value.TotalSeconds} seconds ago";
        }

        if (value < TimeSpan.FromMinutes(2))
        {
            return "updated a minute ago";
        }

        if (value < TimeSpan.FromHours(1))
        {
            return $"updated {(int)value.TotalMinutes} minutes ago";
        }

        if (value < TimeSpan.FromHours(2))
        {
            return "updated an hour ago";
        }

        if (value < TimeSpan.FromDays(1))
        {
            return $"updated {(int)value.TotalHours} hours ago";
        }

        return "updated more than a day ago";
    }

    /// <summary>
    /// An elapsed time in the two-unit form §10.8's wireframe uses — <em>6 d 14 h</em>.
    /// </summary>
    /// <remarks>
    /// Two units and never three. The figure this formats is read against a 24-hour threshold, and
    /// the minutes on the end of "6 d 14 h 12 min" carry nothing for that decision while making the
    /// two units that do matter harder to find. Distinct from <see cref="Describe"/>, which is
    /// about a reading going stale and rounds far harder near the top of its range.
    /// </remarks>
    public static string DescribeDuration(TimeSpan elapsed)
    {
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return $"{(int)elapsed.TotalSeconds} s";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"{elapsed.Minutes} min";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return elapsed.Minutes == 0
                ? $"{(int)elapsed.TotalHours} h"
                : $"{(int)elapsed.TotalHours} h {elapsed.Minutes} min";
        }

        return elapsed.Hours == 0
            ? $"{(int)elapsed.TotalDays} d"
            : $"{(int)elapsed.TotalDays} d {elapsed.Hours} h";
    }
}
