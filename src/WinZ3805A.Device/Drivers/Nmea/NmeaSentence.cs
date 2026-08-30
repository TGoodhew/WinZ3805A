using System.Globalization;
using System.Text;

namespace WinZ3805A.Device.Drivers.Nmea;

/// <summary>
/// One NMEA 0183 sentence, taken apart or put together (#310).
/// </summary>
/// <remarks>
/// <para>
/// The standard's general format, from the copy attached to #310: <c>$ttsss,d1,d2,…*hh&lt;CR&gt;&lt;LF&gt;</c>
/// — a two-letter talker, a three-letter sentence identifier, comma-separated fields with an empty
/// field for data not available, and an optional checksum that is the exclusive-OR of every
/// character between the <c>$</c> and the <c>*</c>. A sentence is at most 80 characters plus the
/// <c>$</c> and the line ending. Proprietary sentences start <c>$P</c> plus a three-letter
/// manufacturer code.
/// </para>
/// <para>
/// This is the one piece both halves of the tutorial share: the driver reads with it and the
/// simulator writes with it, so a checksum the simulator computes is a checksum the driver
/// checks the same way — which is exactly the property that would make a bug invisible if the
/// two were written separately. The tests therefore check the checksum against the standard's
/// own worked value as well.
/// </para>
/// </remarks>
public sealed record NmeaSentence
{
    /// <summary>
    /// The longest sentence the standard allows, counting the <c>$</c> but not the line ending, is
    /// 81 characters — 80 plus the <c>$</c>. This constant is one over, and the driver never reads
    /// it; only the simulator's test does. Left as it is because that test pins the value, and
    /// recorded as an audit finding (#316).
    /// </summary>
    public const int MaximumLength = 82;

    private NmeaSentence(string talker, string identifier, IReadOnlyList<string> fields, bool hasChecksum, bool checksumValid, string raw)
    {
        Talker = talker;
        Identifier = identifier;
        Fields = fields;
        HasChecksum = hasChecksum;
        ChecksumValid = checksumValid;
        Raw = raw;
    }

    /// <summary>The talker identifier — <c>GP</c> for a GPS receiver, <c>GN</c> for a multi-constellation one, <c>P</c> for proprietary.</summary>
    public string Talker { get; }

    /// <summary>The three-letter sentence identifier — <c>GGA</c>, <c>RMC</c>, <c>GSV</c>…</summary>
    public string Identifier { get; }

    /// <summary>The data fields after the address, in order. An empty string is a field the talker left blank.</summary>
    public IReadOnlyList<string> Fields { get; }

    /// <summary>Whether the sentence carried a <c>*hh</c> checksum at all.</summary>
    public bool HasChecksum { get; }

    /// <summary>Whether the checksum was present and matched. A sentence without one is not evidence of anything.</summary>
    public bool ChecksumValid { get; }

    /// <summary>The line as received, without its line ending.</summary>
    public string Raw { get; }

    /// <summary>
    /// The key a sentence of this kind answers to in a driver's plan: the identifier with a
    /// talker-agnostic <c>$--</c> prefix, the way the standard itself writes a sentence's format.
    /// </summary>
    public string Key => KeyFor(Identifier);

    /// <summary>The plan key for a sentence identifier — <c>$--GGA</c> for <c>GGA</c>.</summary>
    public static string KeyFor(string identifier) => "$--" + identifier;

    /// <summary>A field by index, or <see langword="null"/> when it is absent or blank.</summary>
    public string? Field(int index) =>
        index >= 0 && index < Fields.Count && Fields[index].Length > 0 ? Fields[index] : null;

    /// <summary>
    /// Takes a received line apart. Returns <see langword="null"/> for anything that is not shaped
    /// like a sentence; a sentence with a wrong checksum is returned with
    /// <see cref="ChecksumValid"/> false, so the caller can say what it saw.
    /// </summary>
    public static NmeaSentence? TryParse(string? line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return null;
        }

        string raw = line.Trim();
        if (raw.Length < 7 || raw[0] != '$')
        {
            return null;
        }

        int star = raw.LastIndexOf('*');
        string body;
        bool hasChecksum = false;
        bool checksumValid = false;

        if (star > 0 && star == raw.Length - 3)
        {
            body = raw[1..star];
            hasChecksum = true;
            checksumValid = byte.TryParse(raw.AsSpan(star + 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte declared)
                && declared == Checksum(body);
        }
        else if (star < 0)
        {
            body = raw[1..];
        }
        else
        {
            return null;
        }

        int comma = body.IndexOf(',');
        string address = comma < 0 ? body : body[..comma];

        string talker;
        string identifier;
        if (address.Length == 5 && IsUpperAlnum(address))
        {
            talker = address[..2];
            identifier = address[2..];
        }
        else if (address.Length == 4 && address[0] == 'P' && IsUpperAlnum(address))
        {
            talker = "P";
            identifier = address[1..];
        }
        else
        {
            return null;
        }

        string[] fields = comma < 0 ? [] : body[(comma + 1)..].Split(',');
        return new NmeaSentence(talker, identifier, fields, hasChecksum, checksumValid, raw);
    }

    /// <summary>The checksum the standard defines: every character between <c>$</c> and <c>*</c>, exclusive-ORed.</summary>
    public static byte Checksum(ReadOnlySpan<char> payload)
    {
        byte checksum = 0;
        foreach (char c in payload)
        {
            checksum ^= (byte)c;
        }

        return checksum;
    }

    /// <summary>
    /// Puts a sentence together with its checksum, without the line ending — <c>$GPGGA,…*hh</c>.
    /// </summary>
    /// <remarks>
    /// Empty and null fields are sent as empty, which is how the standard says "no data": the
    /// delimiting commas stay and nothing sits between them.
    /// </remarks>
    public static string Format(string talker, string identifier, params string?[] fields)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(talker);
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        ArgumentNullException.ThrowIfNull(fields);

        StringBuilder body = new(talker.Length + identifier.Length + (fields.Length * 8));
        body.Append(talker).Append(identifier);
        foreach (string? field in fields)
        {
            body.Append(',').Append(field ?? string.Empty);
        }

        string payload = body.ToString();
        return "$" + payload + "*" + Checksum(payload).ToString("X2", CultureInfo.InvariantCulture);
    }

    private static bool IsUpperAlnum(string value)
    {
        foreach (char c in value)
        {
            if (!char.IsAsciiLetterUpper(c) && !char.IsAsciiDigit(c))
            {
                return false;
            }
        }

        return true;
    }
}
