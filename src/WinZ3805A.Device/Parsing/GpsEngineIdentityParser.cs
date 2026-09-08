namespace WinZ3805A.Device.Parsing;

/// <summary>
/// Parses <c>:DIAG:IDEN:GPS?</c> — the identity of the GPS receiver inside the instrument.
/// </summary>
/// <remarks>
/// <para>
/// <b>The command is documented; its reply is not.</b> Z3801A User's Guide, Table 4-2, gives
/// <c>:DIAGnostic:IDENtification:GPSystem?</c> as returning "a sequence of quoted strings" and
/// describes them as "the model number, serial number, and revision of the internal GPS receiver".
/// It does not say how many strings, in what order, or which is which.
/// </para>
/// <para>
/// <b>What the bench receiver actually prints</b> — Z3805A, serial 3625A02931, firmware
/// <c>1.01.03-A</c>, captured 7 Sep 2026 with the error queue clean before and after:
/// </para>
/// <code>
/// "--","SFTW P/N # 4850266","SOFTWARE VER # 005","--","--","MODEL # FURUNO GT-80","--","--","--","--"
/// </code>
/// <para>
/// Ten fields, seven of them <c>--</c>. Note that the manual promises a serial number and this
/// receiver does not give one: every populated field is a label the receiver wrote itself, and the
/// slot it left empty is the one the documentation was most specific about. So this parser
/// <b>assigns no meaning to position</b>. It returns the populated fields in the order they
/// arrived, exactly as the receiver spelled them, and the surface shows them as a list rather than
/// as a form with a row per fact. A firmware that fills two more slots gains two more lines; one
/// that reorders them loses nothing.
/// </para>
/// <para>
/// <b>Nothing here throws</b> (§11.1). A reply that is absent, empty, or entirely placeholders
/// yields an empty list, which the caller renders as §11.1's em dash — a statement about the read,
/// never a claim about the hardware.
/// </para>
/// </remarks>
public static class GpsEngineIdentityParser
{
    /// <summary>
    /// The receiver's own "this field is not populated" marker.
    /// </summary>
    /// <remarks>
    /// Seven of the ten fields carry it. It is not an error and not an empty string — the receiver
    /// is answering, and saying it has nothing for that slot.
    /// </remarks>
    private const string Placeholder = "--";

    /// <summary>
    /// Splits a reply into the fields that actually say something.
    /// </summary>
    /// <param name="reply">The raw reply, or null when the read failed.</param>
    /// <returns>
    /// The populated fields in receiver order, verbatim and unquoted. Empty when there are none,
    /// which is also what an unread or unparseable reply gives.
    /// </returns>
    public static IReadOnlyList<string> Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return [];
        }

        List<string> fields = [];

        foreach (string field in SplitOutsideQuotes(reply))
        {
            string value = Unquote(field);

            // Whitespace-only and the receiver's own placeholder are the same thing to a reader:
            // this slot holds nothing. Neither earns a line.
            if (value.Length == 0 || value == Placeholder)
            {
                continue;
            }

            fields.Add(value);
        }

        return fields;
    }

    /// <summary>
    /// Splits on commas that are not inside quotes.
    /// </summary>
    /// <remarks>
    /// A plain <c>Split(',')</c> would be right for every reply seen so far and wrong the first
    /// time a vendor puts a comma in a part number. The receiver quotes each field, so honouring
    /// the quoting costs one bool and removes the question.
    /// </remarks>
    private static List<string> SplitOutsideQuotes(string reply)
    {
        List<string> parts = [];
        bool inQuotes = false;
        int start = 0;

        for (int i = 0; i < reply.Length; i++)
        {
            char c = reply[i];

            if (c == '"')
            {
                inQuotes = !inQuotes;
            }
            else if (c == ',' && !inQuotes)
            {
                parts.Add(reply[start..i]);
                start = i + 1;
            }
        }

        parts.Add(reply[start..]);

        return parts;
    }

    /// <summary>Strips the surrounding quotes and any padding, leaving the text as written.</summary>
    private static string Unquote(string field)
    {
        string trimmed = field.Trim();

        if (trimmed.Length >= 2 && trimmed[0] == '"' && trimmed[^1] == '"')
        {
            trimmed = trimmed[1..^1];
        }

        return trimmed.Trim();
    }
}
