using System.Globalization;

namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>
/// The <c>C5</c> time-code line a UCCM emits — 44 two-digit hex values on one line (#416).
/// </summary>
/// <remarks>
/// <para>
/// <b>Everything here is reverse-engineered and none of it is vendor-documented.</b> The field
/// meanings come from Lady Heather's comments in <c>parse_uccm_time()</c> (heathgps.cpp), MIT
/// licensed, © 2008-2016 Mark S. Sims. Heather's own comments hedge several of them with question
/// marks, and they are reproduced with those doubts intact rather than tidied into false confidence.
/// <b>Nothing in this class has been checked against hardware.</b>
/// </para>
/// <para>
/// <b>The code is not only a response, and where it lands has now been measured.</b> Heather has a
/// dedicated <c>uccm_time_line()</c> whose comment says it "handles the case where the Symmetricom
/// units send a time code packet in the middle of another message's response". That was carried here
/// as established fact until 12 Sep 2026, when it was put to a receiver — see
/// <c>tests/WinZ3805A.Tests/Uccm/Captures/hypothesis2-12sep2026.md</c>.
/// </para>
/// <para>
/// <b>A Trimble UCCM-P does not interleave, and does not drop.</b> Fifty <c>SYST:STAT?</c> reads,
/// 38% of the sitting spent with a reply on the wire, 25 codes arriving where ~25 were due — and
/// <b>none</b> mid-reply against ~9 expected by chance, reproduced three times. Every code arrived
/// and none collided, so this firmware <i>defers</i> a broadcast to the end of the reply rather than
/// interleaving it or suppressing it. That is why every code seen across four sittings has trailed
/// the <c>UCCM-P &gt;</c> prompt.
/// </para>
/// <para>
/// <b>Heather's claim is about Symmetricom units and remains untested</b>, this being a Trimble. So
/// the tolerance stays: the transport lifts a frame out of the byte stream wherever it sits, which
/// costs nothing, and removing it on another vendor's measured behaviour would be the same reasoning
/// that put the claim here unexamined, running backwards.
/// </para>
/// <para>
/// The line is scanned from the <c>C5 </c> marker, three characters per value — two hex digits and a
/// separator — so value 0 is the <c>C5</c> marker itself and the indices below are Heather's.
/// </para>
/// </remarks>
public sealed record UccmTimeCode
{
    /// <summary>GPS time began at midnight UTC on 6 January 1980.</summary>
    private static readonly DateTimeOffset GpsEpoch = new(1980, 1, 6, 0, 0, 0, TimeSpan.Zero);

    /// <summary>How many values the line carries, at three characters each.</summary>
    private const int ValueCount = 44;

    private UccmTimeCode(IReadOnlyList<int> values)
    {
        Values = values;
    }

    /// <summary>Every value as read, so a field nobody has decoded yet is still available.</summary>
    /// <remarks>
    /// Kept whole deliberately. Two thirds of these 44 values have no known meaning, and a field
    /// report about an unfamiliar firmware is only actionable if the raw line survived the parse.
    /// </remarks>
    public IReadOnlyList<int> Values { get; }

    /// <summary>Seconds since the GPS epoch, from values 27 to 30, most significant first.</summary>
    public long GpsSeconds { get; private init; }

    /// <summary>The receiver's time on the GPS scale.</summary>
    public DateTimeOffset GpsTime => GpsEpoch.AddSeconds(GpsSeconds);

    /// <summary>
    /// The receiver's time on the UTC scale, or <see langword="null"/> when the leap offset is
    /// absent and the two scales therefore cannot be related.
    /// </summary>
    /// <remarks>
    /// GPS time runs ahead of UTC by the current leap-second count, so UTC is the GPS reading less
    /// <see cref="LeapSecondOffset"/>. Heather applies exactly this correction and only when it is
    /// in a UTC timing mode. A zero offset is treated as absent rather than as a real zero, because
    /// Heather itself refuses to adopt it (<c>if(!user_set_utc_ofs &amp;&amp; vals[32])</c>) and the
    /// offset has not been zero since 1980.
    /// </remarks>
    public DateTimeOffset? UtcTime =>
        LeapSecondOffset is int offset ? GpsTime.AddSeconds(-offset) : null;

    /// <summary>Leap seconds between GPS and UTC (value 32), or null when the receiver reported 0.</summary>
    public int? LeapSecondOffset { get; private init; }

    /// <summary>The PPS and phase state byte (value 33), undecoded.</summary>
    /// <remarks>
    /// Heather: <c>40</c> PPS validity, <c>41</c> phase settling, <c>50</c> PPS invalid, <c>60</c>
    /// stable, <c>62</c> stable with a leap pending. Power-up runs <c>41 → 43 → 63 → 60/62</c>.
    /// </remarks>
    public int PpsState { get; private init; }

    /// <summary>
    /// Whether a leap second is pending, from bit <c>0x02</c> of <see cref="PpsState"/>.
    /// </summary>
    /// <remarks>
    /// <b>Believed unreliable during power-up and deliberately reported anyway.</b> Heather reads
    /// this single bit, but its own recorded power-up sequence passes through <c>43</c> and
    /// <c>63</c>, both of which have that bit set while the receiver is plainly still warming up.
    /// So this is expected to read true spuriously for a few seconds after power-on. It is surfaced
    /// as-is rather than suppressed because suppressing it would mean inventing a rule no
    /// observation supports; the caller decides what to do with it, and the hardware sitting will
    /// settle whether the bit means what Heather guessed.
    /// </remarks>
    public bool LeapPending => (PpsState & 0x02) != 0;

    /// <summary>The antenna state byte (value 34), undecoded.</summary>
    /// <remarks>Heather: <c>00</c> at power-up, <c>04</c> normal, <c>0C</c> open or shorted, <c>06</c> normal(?).</remarks>
    public int AntennaState { get; private init; }

    /// <summary>Whether the antenna reads as connected and healthy.</summary>
    public bool? AntennaOk => AntennaState switch
    {
        0x04 or 0x06 => true,
        0x0C => false,
        _ => null,
    };

    /// <summary>The manufacturer-dependent lock byte (value 35), undecoded.</summary>
    /// <remarks>
    /// <b>The single most vendor-specific value on the line.</b> Symmetricom: <c>8F</c> settling,
    /// FFOM above zero, or no antenna; <c>85</c> locked with FFOM zero. Trimble: <c>41</c> power-up,
    /// <c>4F</c> settling, <c>45</c> locked. A driver hard-coding Symmetricom's <c>85</c> reports a
    /// locked Trimble as unlocked, which is the failure #418 exists to prevent.
    /// </remarks>
    public int LockState { get; private init; }

    /// <summary>The date-validity byte (value 36), undecoded.</summary>
    /// <remarks>Heather: Symmetricom <c>40</c> valid, <c>50</c>/<c>60</c> invalid; Trimble <c>80</c> valid, <c>90</c> invalid.</remarks>
    public int DateValidityState { get; private init; }

    /// <summary>
    /// What the lock and date bytes suggest about the vendor, or <see cref="UccmVendor.Unknown"/>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A hypothesis drawn from five observations, and labelled as one.</b> Across every value
    /// Heather records, the low nibble of <see cref="LockState"/> carries the state — <c>F</c>
    /// settling, <c>5</c> locked, <c>1</c> powering up — while the high nibble is constant per
    /// vendor: <c>8</c> for Symmetricom, <c>4</c> for Trimble.
    /// </para>
    /// <para>
    /// <b>And the high nibble of the date byte is the other way round</b> — Symmetricom <c>4</c>,
    /// Trimble <c>8</c> — so "high nibble means vendor" is not a rule that generalises across the
    /// line, and reading it as one would silently invert the answer. Both bytes are checked here
    /// and they must agree before this returns a vendor.
    /// </para>
    /// <para>
    /// This is a <i>suggestion</i>. The authoritative signal is <c>DIAG:LOOP?</c>'s shape, which is
    /// a structural difference rather than an inference from five samples. This exists because the
    /// status line arrives first and often, so it can offer an early guess that the loop reply then
    /// confirms or overrides — and if hardware disagrees with it, this method is wrong and the loop
    /// reply is still right.
    /// </para>
    /// </remarks>
    public UccmVendor SuggestedVendor
    {
        get
        {
            UccmVendor byLock = (LockState & 0xF0) switch
            {
                0x80 => UccmVendor.Symmetricom,
                0x40 => UccmVendor.Trimble,
                _ => UccmVendor.Unknown,
            };

            UccmVendor byDate = (DateValidityState & 0xF0) switch
            {
                0x40 or 0x50 or 0x60 => UccmVendor.Symmetricom,
                0x80 or 0x90 => UccmVendor.Trimble,
                _ => UccmVendor.Unknown,
            };

            return byLock == byDate ? byLock : UccmVendor.Unknown;
        }
    }

    /// <summary>Whether a line looks like a time code, without parsing it.</summary>
    /// <remarks>
    /// <b>This is the hex-text form, which no module sends.</b> Heather renders time codes as hex
    /// text in her own logs and a transcript pasted from there should still classify, so this is
    /// kept — but the hardware broadcasts <see cref="TryParse(ReadOnlySpan{byte})"/>'s shape, and a
    /// text search for <c>C5</c> against those bytes can only ever answer "no time code", which is
    /// indistinguishable from a quiet receiver (#481). Do not use this to decide whether a receiver
    /// is sending them.
    /// </remarks>
    public static bool IsTimeCodeLine(string? line) => MarkerIndex(line) >= 0;

    /// <summary>
    /// Reads a time code from the raw bytes the receiver actually broadcasts (#481).
    /// </summary>
    /// <remarks>
    /// <para>
    /// The frame is 44 bytes, <c>0xC5</c> through <c>0xCA</c>, and each byte <i>is</i> a value —
    /// there is no hex text and no separator, so the indices here are the same ones
    /// <see cref="TryParse(string?)"/> reaches three characters at a time. The transport hands these
    /// over whole through <c>Transaction.BinaryFrames</c>; nothing here has to find them in a line,
    /// which is the entire difference.
    /// </para>
    /// <para>
    /// Never throws (§11.1). A frame of the wrong length, or one not bounded by the two marker
    /// bytes, comes back <see langword="null"/> — "no reading", never an error.
    /// </para>
    /// </remarks>
    public static UccmTimeCode? TryParse(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != ValueCount || frame[0] != 0xC5 || frame[ValueCount - 1] != 0xCA)
        {
            return null;
        }

        int[] values = new int[ValueCount];
        for (int value = 0; value < ValueCount; value++)
        {
            values[value] = frame[value];
        }

        return FromValues(values);
    }

    /// <summary>
    /// Reads a time-code line, or returns <see langword="null"/> when it is not one or is truncated.
    /// </summary>
    /// <remarks>
    /// Never throws (§11.1). A short line, a line with non-hex where a value should be, or a line
    /// that simply is not a time code all come back as <see langword="null"/>; the caller treats
    /// that as "no reading", never as an error.
    /// </remarks>
    public static UccmTimeCode? TryParse(string? line)
    {
        int start = MarkerIndex(line);
        if (start < 0 || line is null)
        {
            return null;
        }

        int[] values = new int[ValueCount];
        for (int value = 0; value < ValueCount; value++)
        {
            int at = start + (value * 3);
            if (at + 2 > line.Length ||
                !int.TryParse(
                    line.AsSpan(at, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out values[value]))
            {
                // A truncated or corrupt line is not half a reading. Heather reads a fixed 131
                // characters and would take whatever followed; refusing outright is the §11.1
                // answer, because a time built from a partly-read counter is wrong rather than
                // absent, and wrong is the one thing a clock display must not be.
                return null;
            }
        }

        return FromValues(values);
    }

    /// <summary>
    /// The field meanings, in one place, so the byte and hex-text paths cannot drift apart.
    /// </summary>
    /// <remarks>
    /// Heather's indices, and they are the same whether each value arrived as a byte or as two hex
    /// characters. <b>Offsets 27 to 30 are confirmed against hardware</b>: on the 12 Sep 2026
    /// capture they read <c>0x57CF27A4</c>, which is 2026-09-11 20:31:32 counted from the GPS epoch
    /// — within twenty seconds of when the capture was taken, out of 1.47 billion. The state bytes
    /// below are <b>not</b> confirmed: the receiver was locked and settled throughout, so none of
    /// them moved.
    /// </remarks>
    private static UccmTimeCode FromValues(int[] values)
    {
        int leap = values[32];

        return new UccmTimeCode(values)
        {
            // Assembled through uint so the compiler is not asked to or a sign-extended int with an
            // unsigned one. Each value is a byte by construction, so nothing can be lost.
            GpsSeconds =
                ((uint)values[27] << 24) | ((uint)values[28] << 16) | ((uint)values[29] << 8) | (uint)values[30],
            LeapSecondOffset = leap == 0 ? null : leap,
            PpsState = values[33],
            AntennaState = values[34],
            LockState = values[35],
            DateValidityState = values[36],
        };
    }

    /// <summary>Where the <c>C5</c> marker starts, or -1.</summary>
    /// <remarks>
    /// Heather looks for the literal <c>"C5 "</c> with its separator. We accept the marker at the
    /// start of a line too, since <c>decode_uccm_msg</c> also tests <c>[0]=='C'</c> and
    /// <c>[1]=='5'</c> on a line it has already trimmed — the two checks disagree in Heather itself,
    /// and accepting both is the tolerant reading.
    /// </remarks>
    private static int MarkerIndex(string? line)
    {
        if (string.IsNullOrEmpty(line))
        {
            return -1;
        }

        int at = line.IndexOf("C5 ", StringComparison.OrdinalIgnoreCase);
        if (at >= 0)
        {
            return at;
        }

        return line.StartsWith("C5", StringComparison.OrdinalIgnoreCase) ? 0 : -1;
    }
}
