using WinZ3805A.Device.Models;

namespace WinZ3805A.ViewModels;

/// <summary>
/// What <c>:PTIM:TCOD:FORMat?</c> answered, per §10.14.
/// </summary>
/// <param name="Format">
/// The format the receiver emits, or <see cref="TimeCodeFormat.Unknown"/> when it could not be read.
/// </param>
/// <param name="Error">Why the read did not complete, or null.</param>
public readonly record struct TimeCodeReading(TimeCodeFormat Format, string? Error)
{
    /// <summary>Nothing read yet.</summary>
    public static TimeCodeReading Unknown { get; } = new(TimeCodeFormat.Unknown, null);

    /// <summary>The format in words, or <c>—</c>.</summary>
    /// <remarks>
    /// The receiver's own spelling of the parameter is shown beside the header the message carries,
    /// because they differ — <c>F2</c> selects the format whose messages begin <c>T2</c> — and a
    /// user comparing this page against a raw time code needs to see both to recognise them as the
    /// same thing.
    /// </remarks>
    public string FormatText => Format switch
    {
        TimeCodeFormat.T1 => "F1 — messages begin T1",
        TimeCodeFormat.T2 => "F2 — messages begin T2",
        _ => "—",
    };

    /// <summary>What a message in this format contains, or null when the format is unknown.</summary>
    public string? ContentText => Format switch
    {
        TimeCodeFormat.T1 =>
            "Seconds since the GPS epoch of 6 January 1980, in hexadecimal. 19 characters.",
        TimeCodeFormat.T2 =>
            "Calendar date and time of the next 1 PPS, on the receiver's selected time scale. 23 characters.",
        _ => null,
    };
}
