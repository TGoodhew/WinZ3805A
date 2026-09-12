using System.Buffers;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// Lifts whole binary frames out of a byte buffer before it is split into lines (#481).
/// </summary>
/// <remarks>
/// <para>
/// Pure and allocation-visible, so the framing rules are unit-tested directly rather than through a
/// serial port. <c>build/Capture-Uccm.ps1</c>'s <c>Split-UccmStream</c> is the same algorithm in
/// PowerShell, and the two are deliberately separate implementations: if they ever disagree about a
/// captured sitting, that disagreement is the finding.
/// </para>
/// <para>
/// <b>A frame that has not fully arrived stops the scan rather than being guessed at.</b> The bytes
/// from an incomplete marker onwards are left unprocessed so the caller waits for the rest, which is
/// the only answer that cannot corrupt either the frame or the line it would otherwise be glued to.
/// </para>
/// <para>
/// <b>A marker that does not open a well-formed frame is left in the text, not swallowed.</b> The
/// length is one family's observation across seven frames, and a stray <c>0xC5</c> is evidence that
/// it is wrong somewhere — hiding it would destroy exactly the signal that would say so.
/// </para>
/// </remarks>
internal static class BinaryFrameStripper
{
    /// <summary>The result of a strip: the bytes to read as text, and the frames taken out of them.</summary>
    /// <param name="Clean">The buffer with every whole frame removed.</param>
    /// <param name="Frames">Each frame removed, in the order it appeared.</param>
    /// <param name="FrameOffsets">Where each frame sat in <paramref name="Clean"/>, ascending.</param>
    /// <param name="OriginalLength">
    /// How much of the original buffer <paramref name="Clean"/> accounts for. Less than the whole
    /// when the buffer ended part-way through a frame.
    /// </param>
    internal readonly record struct Result(
        byte[] Clean,
        IReadOnlyList<byte[]> Frames,
        IReadOnlyList<int> FrameOffsets,
        int OriginalLength);

    /// <summary>Removes every whole frame <paramref name="grammar"/> recognises.</summary>
    internal static Result Strip(in ReadOnlySequence<byte> buffer, BinaryFrameGrammar grammar)
    {
        ArgumentNullException.ThrowIfNull(grammar);

        byte[] raw = buffer.ToArray();
        if (grammar.IsNone)
        {
            return new Result(raw, [], [], raw.Length);
        }

        List<byte[]>? frames = null;
        List<int>? offsets = null;
        byte[]? clean = null;
        int cleanLength = 0;
        int copiedTo = 0;
        int at = 0;

        while (at < raw.Length)
        {
            int marker = Array.IndexOf(raw, grammar.Marker, at);
            if (marker < 0)
            {
                break;
            }

            if (marker + grammar.Length > raw.Length)
            {
                // The frame is still arriving. Everything from the marker on is undecidable, so it
                // is neither text nor a frame yet: stop, and let the caller read more.
                clean ??= new byte[raw.Length];
                Buffer.BlockCopy(raw, copiedTo, clean, cleanLength, marker - copiedTo);
                cleanLength += marker - copiedTo;
                return new Result(
                    Trim(clean, cleanLength),
                    (IReadOnlyList<byte[]>?)frames ?? [],
                    (IReadOnlyList<int>?)offsets ?? [],
                    marker);
            }

            if (!grammar.IsFrame(raw.AsSpan(marker, grammar.Length)))
            {
                // Not a frame. Leave the byte where it is and carry on looking after it, so a stray
                // marker reaches the line reader and shows up as the anomaly it is.
                at = marker + 1;
                continue;
            }

            clean ??= new byte[raw.Length];
            frames ??= [];
            offsets ??= [];

            Buffer.BlockCopy(raw, copiedTo, clean, cleanLength, marker - copiedTo);
            cleanLength += marker - copiedTo;

            offsets.Add(cleanLength);
            frames.Add(raw.AsSpan(marker, grammar.Length).ToArray());

            copiedTo = marker + grammar.Length;
            at = copiedTo;
        }

        if (clean is null)
        {
            return new Result(raw, [], [], raw.Length);
        }

        Buffer.BlockCopy(raw, copiedTo, clean, cleanLength, raw.Length - copiedTo);
        cleanLength += raw.Length - copiedTo;

        return new Result(
            Trim(clean, cleanLength),
            frames ?? (IReadOnlyList<byte[]>)[],
            offsets ?? (IReadOnlyList<int>)[],
            raw.Length);
    }

    /// <summary>
    /// The offset in the original buffer that <paramref name="cleanOffset"/> corresponds to.
    /// </summary>
    /// <remarks>
    /// A frame sitting exactly at <paramref name="cleanOffset"/> counts as passed. It has already
    /// been lifted out and handed to the caller, so leaving its bytes unconsumed would hand it over
    /// a second time on the next read.
    /// </remarks>
    internal static int ToOriginalOffset(in Result result, int cleanOffset, int frameLength)
    {
        int original = cleanOffset;
        foreach (int offset in result.FrameOffsets)
        {
            if (offset <= cleanOffset)
            {
                original += frameLength;
            }
        }

        return original;
    }

    /// <summary>How many frames sit at or before <paramref name="cleanOffset"/>.</summary>
    /// <remarks>
    /// Frames past the point the caller consumed are <b>not</b> reported: their bytes stay in the
    /// buffer, so they will be found again next time, and reporting them now would double-count.
    /// </remarks>
    internal static int FramesUpTo(in Result result, int cleanOffset)
    {
        int count = 0;
        foreach (int offset in result.FrameOffsets)
        {
            if (offset <= cleanOffset)
            {
                count++;
            }
        }

        return count;
    }

    private static byte[] Trim(byte[] buffer, int length) =>
        length == buffer.Length ? buffer : buffer.AsSpan(0, length).ToArray();
}
