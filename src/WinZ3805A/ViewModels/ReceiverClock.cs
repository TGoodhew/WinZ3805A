namespace WinZ3805A.ViewModels;

/// <summary>
/// The receiver's time now, for the main window's clock, from its latest readings (#560).
/// </summary>
/// <remarks>
/// <para>
/// <b>Before this the clock ticked every ten seconds.</b> It showed the time printed on the full
/// status screen, which §7.3 reads every ten seconds, and held it until the next one arrived. Lady
/// Heather's clock ticks every second because half its poll is the receiver's time code.
/// </para>
/// <para>
/// <b>Three sources, each for what it is good for</b> (Tony's choice of #560's option 3):
/// </para>
/// <list type="bullet">
/// <item><b>The date comes from the screen</b>, already corrected for §7.4's week rollover. The
/// receiver's own date query is a 1024-week epoch behind on the bench Z3805A, so nothing else may
/// supply it.</item>
/// <item><b>The time of day comes from the one-second sweep's <c>:PTIM:TIME?</c></b>, on the same time
/// scale as the screen, and re-anchors the clock each second.</item>
/// <item><b>Between readings the clock counts forward</b> on the PC's clock from the newest anchor,
/// so it keeps ticking through the three-second pause while the screen is read.</item>
/// </list>
/// <para>
/// <b>It never invents time for readings that are overdue.</b> Past
/// <see cref="Staleness.CautionThreshold"/> it stops counting and shows the last time it was given:
/// §9.11 dims and timestamps stale data, and a clock that kept ticking would hide the fact that
/// nothing has been heard. The footer says overdue at the same moment.
/// </para>
/// <para>
/// Whole seconds are shown, so the second changes when this computer's one-second redraw runs,
/// which can be up to a second after the receiver's own second turns. Pacing on the receiver's
/// time code is #560's option 4, left open.
/// </para>
/// </remarks>
public static class ReceiverClock
{
    /// <summary>A time of day this far behind the screen's has crossed midnight since it.</summary>
    private static readonly TimeSpan HalfADay = TimeSpan.FromHours(12);

    /// <summary>Returns the receiver's time now, or null when there is no date to put it on.</summary>
    /// <param name="screenTime">The screen's date and time, rollover-corrected, or null.</param>
    /// <param name="screenReadAt">When the screen arrived, on the clock <paramref name="now"/> uses.</param>
    /// <param name="timeOfDay">The latest time of day from the one-second sweep, or null.</param>
    /// <param name="timeOfDayReadAt">When that time of day arrived.</param>
    /// <param name="now">Now, on the same clock as the two arrival times.</param>
    public static DateTimeOffset? Now(
        DateTimeOffset? screenTime,
        DateTimeOffset? screenReadAt,
        TimeSpan? timeOfDay,
        DateTimeOffset? timeOfDayReadAt,
        DateTimeOffset now)
    {
        if (screenTime is not DateTimeOffset screen)
        {
            // No date yet. A time of day alone cannot be shown, because the clock line carries the
            // date, and today's date from this computer would be a guess.
            return null;
        }

        DateTimeOffset anchor = screen;
        DateTimeOffset anchoredAt = screenReadAt ?? now;

        // The sweep's time of day wins whenever it is fresh, even when a screen arrived after it.
        // The screen's time is stamped as the receiver starts sending it, and the screen takes up to
        // three and a half seconds to arrive, so on arrival its time is seconds old. Anchoring on it
        // because it was the newest thing heard moved the clock back two seconds at every screen,
        // seen on the bench on 29 Sep 2026; the one-line time reply is the better of the two. The
        // screen's time is used only when there is no fresh time of day: a driver that does not read
        // one, or one the receiver has stopped answering while its screen still comes.
        if (timeOfDay is TimeSpan time
            && timeOfDayReadAt is DateTimeOffset timeReadAt
            && (now - timeReadAt < Staleness.CautionThreshold
                || screenReadAt is not DateTimeOffset readAt
                || timeReadAt >= readAt))
        {
            DateTimeOffset onScreensDate = new(screen.Date + time, screen.Offset);
            TimeSpan difference = onScreensDate - screen;

            // The screen can be up to ten seconds old, so across midnight its date is yesterday's
            // while the time of day is already just after 00:00. The reverse also happens: a screen
            // stamped just after midnight, with the sweep before it still just before.
            if (difference < -HalfADay)
            {
                onScreensDate = onScreensDate.AddDays(1);
            }
            else if (difference > HalfADay)
            {
                onScreensDate = onScreensDate.AddDays(-1);
            }

            anchor = onScreensDate;
            anchoredAt = timeReadAt;
        }

        TimeSpan elapsed = now - anchoredAt;

        if (elapsed <= TimeSpan.Zero)
        {
            return anchor;
        }

        // Overdue: show the last time given, not one this computer made up.
        return elapsed >= Staleness.CautionThreshold ? anchor : anchor + elapsed;
    }
}
