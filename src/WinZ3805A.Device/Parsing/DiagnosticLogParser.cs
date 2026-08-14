using System.Globalization;
using System.Text.RegularExpressions;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Parsing;

/// <summary>
/// Parses the receiver's diagnostic log entries.
/// </summary>
/// <remarks>
/// <para>
/// The format is documented, not guessed. 58503A/59551A guide, Command Reference 5-33
/// (<c>:DIAGnostic:LOG:READ?</c>): <c>"Log NNN: YYYYMMDD.HH:MM:SS: &lt;log_message&gt;"</c>, where
/// <c>NNN</c> is the entry number, the timestamp is the entry's date and time, and the message runs
/// to 255 characters. <c>:DIAG:LOG:READ:ALL?</c> answers with the same strings, comma-separated.
/// </para>
/// <para>
/// <b>Nothing here throws</b> (§11.1). A line that does not match keeps its raw text and loses only
/// its number and timestamp — a firmware revision that reorders the prefix must cost the user the
/// sort order, not the log.
/// </para>
/// </remarks>
public static class DiagnosticLogParser
{
    /// <summary>The timestamp layout the guide gives, fixed width at 17 characters.</summary>
    private const string StampFormat = "yyyyMMdd.HH:mm:ss";

    /// <summary>
    /// Parses one entry.
    /// </summary>
    /// <param name="line">One entry, quoted or not, as the receiver returned it.</param>
    public static DiagnosticLogEntry Parse(string? line)
    {
        string text = Unquote(line ?? string.Empty);

        if (text.Length == 0)
        {
            return new DiagnosticLogEntry { RawText = text, Message = string.Empty };
        }

        if (!text.StartsWith("Log", StringComparison.OrdinalIgnoreCase))
        {
            return Unrecognised(text);
        }

        int afterNumber = text.IndexOf(':', StringComparison.Ordinal);
        if (afterNumber < 0)
        {
            return Unrecognised(text);
        }

        int? number = int.TryParse(
            text.AsSpan(3, afterNumber - 3).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int parsedNumber)
            ? parsedNumber
            : null;

        // Fixed width rather than a split: the timestamp contains colons of its own, so counting
        // separators would cut it in half.
        string remainder = text[(afterNumber + 1)..].TrimStart();

        if (remainder.Length < StampFormat.Length)
        {
            return new DiagnosticLogEntry { RawText = text, Number = number, Message = remainder };
        }

        DateTime? stamp = DateTime.TryParseExact(
            remainder[..StampFormat.Length],
            StampFormat,
            CultureInfo.InvariantCulture,
            DateTimeStyles.None,
            out DateTime parsedStamp)
            ? parsedStamp
            : null;

        if (stamp is null)
        {
            return new DiagnosticLogEntry { RawText = text, Number = number, Message = remainder };
        }

        string message = remainder[StampFormat.Length..].TrimStart();
        if (message.StartsWith(':'))
        {
            message = message[1..].TrimStart();
        }

        return new DiagnosticLogEntry
        {
            RawText = text,
            Number = number,
            Timestamp = stamp,
            Message = message,
        };
    }

    /// <summary>
    /// Splits the answer to <c>:DIAG:LOG:READ:ALL?</c> into entries.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Split on the entry prefix, not on the separator.</b> The guide describes the response as
    /// quoted strings separated by commas — <c>"XYZ", ...</c> — but the Z3805A on the bench returns
    /// them <i>unquoted</i>, wrapped across lines, and its messages contain commas of their own:
    /// "Holdover started, not tracking GPS" is a single entry it emits constantly. Splitting on
    /// commas cut that in half and left the second piece masquerading as an entry.
    /// </para>
    /// <para>
    /// The <c>Log NNN:</c> prefix is the one thing every entry starts with and no message contains,
    /// so it is what the boundary is drawn at. That works for the guide's quoted form and the
    /// unit's unquoted one without having to know which arrived.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<DiagnosticLogEntry> ParseAll(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return [];
        }

        // Quotes and commas between entries become whitespace; a comma inside a message is left
        // alone, because the split below never looks at commas at all.
        string text = response.Replace('"', ' ');

        MatchCollection starts = s_entryPrefix.Matches(text);

        if (starts.Count == 0)
        {
            // No recognisable prefix anywhere: one unparsed entry rather than nothing at all.
            return [Parse(text)];
        }

        List<DiagnosticLogEntry> entries = new(starts.Count);

        for (int i = 0; i < starts.Count; i++)
        {
            int start = starts[i].Index;
            int end = i + 1 < starts.Count ? starts[i + 1].Index : text.Length;

            // Trailing separators belong to the format, not to the message. The guide's form leaves
            // a comma behind after the quotes are stripped; the unit's leaves whitespace.
            string piece = text[start..end].Trim().TrimEnd(',').Trim();
            if (piece.Length > 0)
            {
                entries.Add(Parse(piece));
            }
        }

        return entries;
    }

    /// <summary>The entry prefix: the word Log, an entry number, and the colon that follows it.</summary>
    private static readonly Regex s_entryPrefix = new(
        "Log\\s+\\d+\\s*:",
        RegexOptions.IgnoreCase,
        TimeSpan.FromSeconds(1));

    private static DiagnosticLogEntry Unrecognised(string text) =>
        new() { RawText = text, Message = text };

    private static string Unquote(string text)
    {
        string trimmed = text.Trim();

        return trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"'
            ? trimmed[1..^1].Trim()
            : trimmed;
    }
}
