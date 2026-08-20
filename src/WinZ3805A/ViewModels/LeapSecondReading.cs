using WinZ3805A.Device.Models;

namespace WinZ3805A.ViewModels;

/// <summary>
/// What the <c>:PTIM:LEAP:</c> subsystem answered, per §10.14.
/// </summary>
/// <param name="AccumulatedSeconds">
/// GPS − UTC in whole seconds, from <c>:PTIM:LEAP:ACC?</c>, or null if it could not be read. The one
/// figure here that is always available, and the one anyone comparing GPS time to UTC needs.
/// </param>
/// <param name="Pending">Whether one is announced, and in which direction.</param>
/// <param name="AnnouncedDate">
/// The announced leap second's date, or null. Null both when nothing is announced and when the
/// query could not be read — §11.1's rule, and the two are not distinguished because the display
/// is the same either way.
/// </param>
/// <param name="Error">Why the read did not complete, or null.</param>
public readonly record struct LeapSecondReading(
    int? AccumulatedSeconds,
    LeapSecondPending Pending,
    DateOnly? AnnouncedDate,
    string? Error)
{
    /// <summary>Nothing read yet.</summary>
    public static LeapSecondReading Unknown { get; } = new(null, LeapSecondPending.None, null, null);
}

/// <summary>
/// Which <c>:PTIM:LEAP:</c> queries to ask, and in what order (§10.14).
/// </summary>
/// <remarks>
/// <para>
/// <b>The date and the direction answer only while an announcement stands.</b> On the bench
/// receiver with nothing announced, <c>:PTIM:LEAP:ACC?</c> answers <c>+18</c> and
/// <c>:PTIM:LEAP:STAT?</c> answers <c>0</c>, while <c>:PTIM:LEAP:DATE?</c> and
/// <c>:PTIM:LEAP:DUR?</c> are both rejected with <c>E-230</c>. There is no announcement to have a
/// date, and this receiver treats the question as an error rather than answering null.
/// </para>
/// <para>
/// So the order is not decoration. A page that asked all four on arrival would put two errors in
/// the receiver's error queue every time it was opened — a side effect a read-only page has no
/// business having, and one that would then surface on the Diagnostics page as if something had
/// gone wrong.
/// </para>
/// <para>
/// This type holds the decision and none of the transport, so the rule can be asserted without a
/// receiver: <see cref="NeedsAnnouncementDetail"/> is the whole of it.
/// </para>
/// </remarks>
public static class LeapSecondQueries
{
    /// <summary>Always safe to ask: the accumulated GPS − UTC offset.</summary>
    public const string Accumulated = ":PTIM:LEAP:ACC?";

    /// <summary>Always safe to ask: whether one is announced.</summary>
    public const string Status = ":PTIM:LEAP:STAT?";

    /// <summary>Only when one is announced.</summary>
    public const string Date = ":PTIM:LEAP:DATE?";

    /// <summary>Only when one is announced.</summary>
    public const string Direction = ":PTIM:LEAP:DUR?";

    /// <summary>Whether the date and direction may be asked for.</summary>
    public static bool NeedsAnnouncementDetail(LeapSecondPending pending) =>
        pending != LeapSecondPending.None;

    /// <summary>
    /// Decodes <c>:PTIM:LEAP:STAT?</c> and <c>:PTIM:LEAP:DUR?</c> into the announcement.
    /// </summary>
    /// <param name="status">What <see cref="Status"/> answered, or null if it could not be read.</param>
    /// <param name="direction">
    /// What <see cref="Direction"/> answered, or null when it was not asked. Positive inserts a
    /// second, negative removes one.
    /// </param>
    /// <remarks>
    /// A status of zero is "none announced" and is the only value that means that. Anything else is
    /// an announcement whose direction comes from <paramref name="direction"/> — and where the
    /// direction was not read, the announcement is still reported, because "a leap second is coming
    /// and I could not read which way" is a great deal more useful than silence.
    /// </remarks>
    public static LeapSecondPending Decode(int? status, int? direction)
    {
        if (status is null or 0)
        {
            return LeapSecondPending.None;
        }

        return direction < 0 ? LeapSecondPending.Minus : LeapSecondPending.Plus;
    }

    /// <summary>
    /// Turns <c>:PTIM:LEAP:DATE?</c>'s year, month and day into a date, or null.
    /// </summary>
    /// <remarks>
    /// <b>Not corrected for §7.4's week rollover.</b> This is a date the receiver computed from the
    /// almanac rather than one it read off its own clock, and the two are not the same kind of
    /// number — applying the epoch correction to it would move a date that was never wrong. Until a
    /// leap second is actually announced there is no way to check that on this receiver, so it is
    /// stated here rather than assumed silently either way.
    /// </remarks>
    public static DateOnly? ParseDate(IReadOnlyList<string>? lines)
    {
        if (lines is null || lines.Count == 0)
        {
            return null;
        }

        string[] parts = lines[0].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length < 3)
        {
            return null;
        }

        if (!int.TryParse(parts[0], out int year)
            || !int.TryParse(parts[1], out int month)
            || !int.TryParse(parts[2], out int day))
        {
            return null;
        }

        // §11.1: an implausible date becomes null rather than an exception or a guess. The day is
        // checked against the month's own length, not against 31 — 31 June is as unreadable as
        // month 13, and substituting a nearby day for it would invent a date the receiver never
        // sent, which is the one thing the parser may not do.
        if (year is < 1980 or > 2200 || month is < 1 or > 12)
        {
            return null;
        }

        return day >= 1 && day <= DateTime.DaysInMonth(year, month)
            ? new DateOnly(year, month, day)
            : null;
    }
}
