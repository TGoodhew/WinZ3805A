namespace WinZ3805A.Device.Transport;

/// <summary>
/// An unsolicited fixed-length binary frame a receiver broadcasts between replies (#481).
/// </summary>
/// <remarks>
/// <para>
/// <b>Framing has to happen on bytes, before anything is decoded or split into lines</b>, and that
/// is why this lives in the transport rather than in a driver's parser. A frame carries no line
/// terminator, so the line reader glues it onto whatever it lands next to; worse, a frame whose
/// payload happens to contain <c>0x0D</c> or <c>0x0A</c> is *split in half* by the line reader and
/// cannot be reassembled afterwards, because CRLF collapsing has by then discarded which byte the
/// delimiter was.
/// </para>
/// <para>
/// <b>That is measured, not hypothetical.</b> Of the seven UCCM-P time codes captured under
/// <c>tests/WinZ3805A.Tests/Uccm/Captures/</c>, one — in the 10 Sep 2026 sitting — carries
/// <c>0x0A</c> at offset 41, in what looks like a checksum byte. A checksum takes arbitrary values,
/// so this is not a rarity to be tolerated: roughly one frame in sixty will contain a terminator in
/// those two bytes alone, before counting the timestamp.
/// </para>
/// <para>
/// The shape follows <see cref="PromptGrammar"/> deliberately: a record with a static default that
/// does nothing, selected per driver through <c>IReceiverDriver</c>, so a family that broadcasts
/// nothing pays nothing and its behaviour is bit-for-bit unchanged.
/// </para>
/// </remarks>
public sealed record BinaryFrameGrammar
{
    /// <summary>A receiver that speaks only when spoken to. The default.</summary>
    /// <remarks>
    /// <see cref="Length"/> of zero is the "off" switch, and the transport tests it before doing any
    /// work at all — a family with no broadcast keeps the original byte path untouched.
    /// </remarks>
    public static BinaryFrameGrammar None { get; } = new()
    {
        Marker = 0,
        Terminator = 0,
        Length = 0,
    };

    /// <summary>
    /// The UCCM time code: 44 bytes, <c>0xC5</c> through <c>0xCA</c>, broadcast about every 2 s.
    /// </summary>
    /// <remarks>
    /// <b>Measured across three sittings, not documented.</b> Seven frames, every one 44 bytes long
    /// and every one bounded by those two bytes. Lady Heather describes the code but renders it as
    /// hex text in her own logs, which is the shape no module actually sends.
    /// </remarks>
    public static BinaryFrameGrammar UccmTimeCode { get; } = new()
    {
        Marker = 0xC5,
        Terminator = 0xCA,
        Length = 44,
    };

    /// <summary>The byte that opens a frame.</summary>
    public required byte Marker { get; init; }

    /// <summary>The byte that must close it, <see cref="Length"/> bytes later.</summary>
    public required byte Terminator { get; init; }

    /// <summary>The frame's total length in bytes, marker and terminator included. Zero disables.</summary>
    public required int Length { get; init; }

    /// <summary>Whether this grammar recognises anything at all.</summary>
    public bool IsNone => Length <= 0;

    /// <summary>
    /// Whether <paramref name="candidate"/> opens with a whole, well-formed frame.
    /// </summary>
    /// <remarks>
    /// <b>Length first, terminator verified — never a scan to the first terminator byte.</b> The
    /// terminator's value can legitimately occur inside a frame (the payload includes a checksum and
    /// a counter, both of which take arbitrary values), so scanning would truncate the frame and
    /// leave its tail to be read as text. Checking a fixed length and then confirming the closing
    /// byte cannot make that mistake.
    /// </remarks>
    /// <param name="candidate">Bytes beginning at a <see cref="Marker"/>.</param>
    public bool IsFrame(ReadOnlySpan<byte> candidate) =>
        !IsNone
        && candidate.Length >= Length
        && candidate[0] == Marker
        && candidate[Length - 1] == Terminator;
}
