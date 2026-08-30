namespace WinZ3805A.Device.Models;

/// <summary>
/// One entry from the receiver's diagnostic log.
/// </summary>
/// <remarks>
/// The log is the receiver's own account of what has happened to it — power cycles, mode changes,
/// faults — and is the only history that survives the app not running. §10.9 puts it on the
/// Diagnostics page with a filter and an export.
/// </remarks>
public sealed record DiagnosticLogEntry
{
    /// <summary>Exactly what the receiver returned, before any interpretation.</summary>
    /// <remarks>
    /// Kept whole so an entry this version cannot decompose is still exportable and still readable.
    /// §11.1's rule that the parser never throws is only useful if what it could not parse survives.
    /// </remarks>
    public required string RawText { get; init; }

    /// <summary>The entry number, or <see langword="null"/> if the prefix did not parse.</summary>
    public int? Number { get; init; }

    /// <summary>
    /// When the entry was recorded, or <see langword="null"/> if the timestamp did not parse.
    /// </summary>
    /// <remarks>
    /// <b>On the receiver's own time scale, whichever it is set to</b> — the GPS or UTC that
    /// §11.2's <c>ReceiverStatus.TimeScale</c> reports — and with no offset attached, because the
    /// log does not carry one. It is also subject to the §7.4 week rollover, so an entry from a
    /// receiver that has not been corrected may be 1024 weeks adrift.
    /// </remarks>
    public DateTime? Timestamp { get; init; }

    /// <summary>What the receiver logged.</summary>
    public required string Message { get; init; }

    /// <summary>Whether the prefix parsed into a number and a timestamp.</summary>
    public bool IsStructured => Number is not null && Timestamp is not null;
}
