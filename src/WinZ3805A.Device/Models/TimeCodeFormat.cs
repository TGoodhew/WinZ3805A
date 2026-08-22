namespace WinZ3805A.Device.Models;

/// <summary>
/// Which of the two time code formats the receiver emits from <c>:PTIM:TCOD?</c>.
/// </summary>
/// <remarks>
/// The two differ in what they carry, not merely in spelling: T1 gives the time of the next 1 PPS
/// as a hexadecimal count of seconds since the GPS epoch, T2 as calendar fields. Nothing can decode
/// a time code without first knowing which it is looking at.
/// </remarks>
public enum TimeCodeFormat
{
    /// <summary>Not read, or the receiver answered something neither format names.</summary>
    Unknown = 0,

    /// <summary>Seconds since the GPS epoch, hexadecimal — 19 characters.</summary>
    T1,

    /// <summary>Calendar date and time — 23 characters.</summary>
    T2,
}

/// <summary>
/// Reads <c>:PTIM:TCOD:FORMat?</c>'s answer (§8.2).
/// </summary>
/// <remarks>
/// <para>
/// <b>The receiver is not necessarily in the documented default.</b> <c>z3801.pdf</c> states that
/// "T1 is the default time code format", and the bench Z3805A answers <c>F2</c>. A decoder written
/// against the documented default would mis-parse every message that unit sends, so the format is
/// read rather than assumed. That is the whole reason this query is catalogued.
/// </para>
/// <para>
/// <b>The manual names the same two formats two ways</b>, and both spellings are accepted here. The
/// command's parameter is <c>F1</c> or <c>F2</c>, while the header the message itself begins with is
/// <c>T1</c> or <c>T2</c>. Reading back an <c>F</c> and matching it against a <c>T</c> is an easy
/// way to decide a receiver is in an unknown state when it is not, and the cost of accepting both is
/// one extra case.
/// </para>
/// </remarks>
public static class TimeCodeFormats
{
    /// <summary>The query, as §8.2 lists it.</summary>
    public const string Query = ":PTIM:TCOD:FORM?";

    /// <summary>
    /// Decodes one response line into a format, or <see cref="TimeCodeFormat.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// Never throws (§11.1). An unreadable answer is <see cref="TimeCodeFormat.Unknown"/>, which the
    /// page renders as <c>—</c> — the receiver is in *some* format and this did not establish which,
    /// which is a different and more honest claim than naming one.
    /// </remarks>
    public static TimeCodeFormat Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return TimeCodeFormat.Unknown;
        }

        // Every response arrives with a leading space, and the manual describes this one as a
        // quoted string though the bench receiver answers bare. Both are stripped rather than
        // one being assumed.
        string value = response.Trim().Trim('"').Trim();

        return value.ToUpperInvariant() switch
        {
            "F1" or "T1" => TimeCodeFormat.T1,
            "F2" or "T2" => TimeCodeFormat.T2,
            _ => TimeCodeFormat.Unknown,
        };
    }

    /// <summary>How many characters a message in <paramref name="format"/> occupies, or null.</summary>
    /// <remarks>
    /// Excluding the trailing <c>CR LF</c>. Useful as a cheap sanity check on a decoded message, and
    /// null for <see cref="TimeCodeFormat.Unknown"/> because there is nothing to expect.
    /// </remarks>
    public static int? MessageLength(TimeCodeFormat format) => format switch
    {
        TimeCodeFormat.T1 => 19,
        TimeCodeFormat.T2 => 23,
        _ => null,
    };
}
