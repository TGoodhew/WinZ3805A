namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>What one line of a UCCM reply turned out to be (#416).</summary>
public enum UccmLineKind
{
    /// <summary>An ordinary payload line: the answer, or part of it.</summary>
    Payload = 0,

    /// <summary>The receiver echoing back the command it was sent.</summary>
    Echo,

    /// <summary>The <c>COMMAND COMPLETE</c> terminator.</summary>
    Complete,

    /// <summary>An error the receiver reported, rather than an answer.</summary>
    Error,

    /// <summary>An unsolicited <c>C5</c> time code that arrived mid-reply.</summary>
    TimeCode,
}

/// <summary>
/// The reply conventions of the UCCM family, which differ from the SmartClock's in two ways that
/// change how every answer must be read (#416).
/// </summary>
/// <remarks>
/// <para>
/// <b>First: the receiver echoes the command before answering it.</b> Lady Heather's
/// <c>decode_uccm_msg()</c> is built around this — when it has no pending message id it matches the
/// incoming text against the command mnemonics (<c>"SYNC:TINT?"</c>, <c>"GPS:POS?"</c>, and so on),
/// sets the id from whichever matched, and reads the <i>next</i> message as the answer. So the
/// first line back is the question, not the reply. A reader that takes the first line as the value
/// gets the mnemonic where it expected a number, every time.
/// </para>
/// <para>
/// <b>Second: unsolicited time codes interleave with replies.</b> Heather's
/// <c>uccm_time_line()</c> exists precisely because "the Symmetricom units send a time code packet
/// in the middle of another message's response". A <c>C5</c> line can therefore appear between the
/// echo and the answer, or inside a multi-line status.
/// </para>
/// <para>
/// <b>Both of these need confirming against hardware.</b> They are read out of a third party's
/// source rather than a vendor document or a capture, and the transport this application uses is
/// line-and-prompt oriented (see <c>docs/adding-a-receiver.md</c> step 6 on unsolicited output).
/// How the two interact under our own <c>LineProtocol</c> is the first thing to check when a unit
/// is available.
/// </para>
/// </remarks>
public static class UccmReply
{
    /// <summary>The error and status texts the receiver answers with instead of a value.</summary>
    /// <remarks>
    /// From <c>decode_uccm_msg()</c>. <c>UCCM</c> and <c>UCCM-P</c> appear here because the module
    /// prefixes its error replies with its own name; <c>CORRUPT</c> is Heather's note for "Data
    /// corrupt or stale" on UCCM-P.
    /// </remarks>
    private static readonly string[] ErrorMarkers =
    [
        "COMMAND ERROR",
        "UNDEFINED HEADER",
        "INVALID PARAMETER",
        "CORRUPT",
    ];

    /// <summary>Classifies one line of a reply.</summary>
    /// <param name="line">The line, as received.</param>
    /// <param name="sent">The command it is a reply to, or null when that is not known.</param>
    /// <remarks>Never throws (§11.1); an unrecognisable line is <see cref="UccmLineKind.Payload"/>.</remarks>
    public static UccmLineKind Classify(string? line, string? sent)
    {
        string text = line?.Trim() ?? string.Empty;
        if (text.Length == 0)
        {
            return UccmLineKind.Payload;
        }

        if (UccmTimeCode.IsTimeCodeLine(text))
        {
            return UccmLineKind.TimeCode;
        }

        if (text.Contains("COMMAND COMPLETE", StringComparison.OrdinalIgnoreCase))
        {
            return UccmLineKind.Complete;
        }

        foreach (string marker in ErrorMarkers)
        {
            if (text.Contains(marker, StringComparison.OrdinalIgnoreCase))
            {
                return UccmLineKind.Error;
            }
        }

        if (IsEchoOf(text, sent))
        {
            return UccmLineKind.Echo;
        }

        return UccmLineKind.Payload;
    }

    /// <summary>
    /// The value lines of a reply — echo, terminator, errors and interleaved time codes removed.
    /// </summary>
    public static IReadOnlyList<string> PayloadLines(string? response, string? sent)
    {
        if (string.IsNullOrEmpty(response))
        {
            return [];
        }

        List<string> payload = [];
        foreach (string raw in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            string line = raw.Trim();
            if (line.Length > 0 && Classify(line, sent) == UccmLineKind.Payload)
            {
                payload.Add(line);
            }
        }

        return payload;
    }

    /// <summary>The first value line of a reply, or null when it carried none.</summary>
    public static string? FirstPayload(string? response, string? sent)
    {
        IReadOnlyList<string> lines = PayloadLines(response, sent);
        return lines.Count > 0 ? lines[0] : null;
    }

    /// <summary>Every time code carried by a reply, in the order they arrived.</summary>
    public static IReadOnlyList<UccmTimeCode> TimeCodes(string? response)
    {
        if (string.IsNullOrEmpty(response))
        {
            return [];
        }

        List<UccmTimeCode> found = [];
        foreach (string raw in response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (UccmTimeCode.TryParse(raw) is UccmTimeCode code)
            {
                found.Add(code);
            }
        }

        return found;
    }

    /// <summary>Whether a reply reported an error rather than answering.</summary>
    public static bool IsError(string? response, string? sent) =>
        !string.IsNullOrEmpty(response) &&
        response
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Any(line => Classify(line, sent) == UccmLineKind.Error);

    /// <summary>
    /// Whether a line is the receiver repeating the command back.
    /// </summary>
    /// <remarks>
    /// Compared on the command's characters with separators ignored, so that an echo differing only
    /// in case, leading colon or spacing still matches. <b>An echo is only ever recognised against a
    /// command we actually sent</b> — never by shape — because a line that merely looks command-like
    /// could be a legitimate answer, and silently discarding an answer is worse than passing an echo
    /// through to a parser that will not recognise it.
    /// </remarks>
    private static bool IsEchoOf(string line, string? sent)
    {
        if (string.IsNullOrWhiteSpace(sent))
        {
            return false;
        }

        string wanted = Canonical(sent);
        return wanted.Length > 0 && Canonical(line) == wanted;
    }

    private static string Canonical(string text)
    {
        Span<char> buffer = text.Length <= 128 ? stackalloc char[text.Length] : new char[text.Length];
        int length = 0;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) || c == '?')
            {
                buffer[length++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }
}
