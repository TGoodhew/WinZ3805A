using System.Globalization;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>
/// The single-value queries a UCCM answers, each read from its comma-separated reply (#416).
/// </summary>
/// <remarks>
/// <para>
/// Field layouts are from Lady Heather's <c>parse_scpi_*</c> and <c>parse_uccm_*</c> routines
/// (heathgps.cpp), MIT licensed, © 2008-2016 Mark S. Sims. Its <c>get_nmea_field()</c> splits on
/// commas, so every reply below is comma-separated. <b>None of this has been seen from a
/// receiver.</b>
/// </para>
/// <para>
/// <b>Nothing here polls, and that is a real gap rather than an oversight.</b>
/// <see cref="PollPlan"/> expresses two tiers — a fast sweep and one full-status query — and this
/// family needs a third: Heather rotates through ten parameter queries, one per poll, alternating
/// with the time query. These values change slowly (a cable delay never), so putting them in the
/// fast tier would spend most of a 9600 baud link re-asking constants, and the interface has no
/// slower tier to put them in. Widening <see cref="PollPlan"/> is a §7.3 decision that wants a
/// receiver in front of it — how fast these modules will actually answer is unmeasured — so the
/// reading code exists and is tested here, and the scheduling question is left open deliberately.
/// </para>
/// <para>
/// Every method returns absence rather than throwing, and absence rather than a default (§11.1).
/// </para>
/// </remarks>
public static class UccmScalars
{
    /// <summary>
    /// Reads <c>GPS:POS?</c> — hemisphere, degrees, minutes, seconds for each axis, then altitude.
    /// </summary>
    /// <remarks>
    /// <b>Degrees, minutes and seconds, not decimal degrees.</b> Heather accumulates
    /// <c>deg + min/60 + sec/3600</c> and negates for S and W. A reader that took the first number
    /// as a latitude would be wrong by up to a degree — about 110 km — and would look plausible,
    /// which is the dangerous kind of wrong.
    /// </remarks>
    public static GeoPosition? ReadPosition(string? response)
    {
        string[] f = Fields(response, "GPS:POS?");
        if (f.Length < 8)
        {
            return null;
        }

        double? latitude = Sexagesimal(f[0], f[1], f[2], f[3], negative: "S");
        double? longitude = Sexagesimal(f[4], f[5], f[6], f[7], negative: "W");
        double? height = f.Length > 8 ? Decimal(f[8]) : null;

        return latitude is null && longitude is null && height is null
            ? null
            : new GeoPosition
            {
                LatitudeDegrees = latitude,
                LongitudeDegrees = longitude,
                HeightMetres = height,
            };
    }

    /// <summary>Reads <c>GPS:REF:ADEL?</c> — the antenna cable delay.</summary>
    /// <remarks>
    /// <b>The unit is assumed to be seconds and that assumption is not verified.</b> Heather stores
    /// the value into a variable it elsewhere treats as seconds, but the reply carries no unit and
    /// no vendor document was available. A cable delay wrong by a factor of a billion is
    /// immediately obvious on screen, which is the one mercy here — check it against a known cable
    /// length on the day.
    /// </remarks>
    public static double? ReadCableDelayNanoseconds(string? response) =>
        Decimal(First(response, "GPS:REF:ADEL?")) * 1.0E9;

    /// <summary>Reads <c>GPS:SAT:TRAC:EMAN?</c> — the elevation mask, in degrees.</summary>
    public static int? ReadElevationMaskDegrees(string? response)
    {
        double? mask = Decimal(First(response, "GPS:SAT:TRAC:EMAN?"));
        return mask is double value && double.IsFinite(value) && value is >= -90 and <= 90
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;
    }

    /// <summary>
    /// Reads <c>:ROSC:HOLD:DUR?</c> — how long holdover has run, and whether it is running.
    /// UCCM-P only.
    /// </summary>
    /// <remarks>
    /// Two fields: a duration in seconds, then a flag whose non-zero value means the receiver is in
    /// holdover now. A plain UCCM answers this with an undefined-header error, which
    /// <see cref="UccmReply"/> classifies as an error, so both come back absent.
    /// </remarks>
    public static (TimeSpan? Duration, bool? InHoldover) ReadHoldover(string? response)
    {
        string[] f = Fields(response, ":ROSC:HOLD:DUR?");
        if (f.Length == 0)
        {
            return (null, null);
        }

        double? seconds = Decimal(f[0]);
        TimeSpan? duration = seconds is double value && double.IsFinite(value) && value >= 0
            ? TimeSpan.FromSeconds(value)
            : null;

        bool? active = f.Length > 1 && Decimal(f[1]) is double flag ? flag != 0.0 : null;

        return (duration, active);
    }

    /// <summary>
    /// Reads <c>:GPS:POS:SURV:STAT?</c> — whether a position survey is running. UCCM-P only.
    /// </summary>
    /// <remarks>Heather treats the literal <c>ONCE</c> or a <c>1</c> as surveying.</remarks>
    public static bool? ReadSurveying(string? response)
    {
        string? field = First(response, ":GPS:POS:SURV:STAT?");
        if (string.IsNullOrEmpty(field))
        {
            return null;
        }

        return field.Contains("ONCE", StringComparison.OrdinalIgnoreCase) ||
               field.Trim() == "1";
    }

    /// <summary>
    /// Reads <c>:GPS:POS:SURV:PROG?</c> — survey progress as a percentage. UCCM-P only.
    /// </summary>
    public static double? ReadSurveyPercent(string? response)
    {
        double? percent = Decimal(First(response, ":GPS:POS:SURV:PROG?"));
        return percent is double value && double.IsFinite(value) && value is >= 0 and <= 100
            ? value
            : null;
    }

    /// <summary>
    /// Reads <c>DIAG:ROSC:EFC:DATA?</c> — the absolute control value, and any state word with it.
    /// </summary>
    /// <remarks>
    /// <b>Hexadecimal, and read from the third character onward.</b> Heather does
    /// <c>sscanf(&amp;nmea_field[2], "%x", &amp;val)</c>, which skips a two-character prefix — most
    /// likely an <c>0x</c>. It also recognises two words in place of a number: <c>TO GPS</c> while
    /// recovering from manual control, and <c>RANGE</c> when the value is out of range. Both mean
    /// there is no reading, and both are reported as such rather than as zero.
    /// </remarks>
    public static (double? Value, string? State) ReadEfcData(string? response)
    {
        string? field = First(response, "DIAG:ROSC:EFC:DATA?");
        if (string.IsNullOrEmpty(field))
        {
            return (null, null);
        }

        if (field.Contains("TO GPS", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Recovering to GPS control");
        }

        if (field.Contains("RANGE", StringComparison.OrdinalIgnoreCase))
        {
            return (null, "Out of range");
        }

        string text = field.Trim();
        if (text.Length <= 2)
        {
            return (null, null);
        }

        return uint.TryParse(
            text.AsSpan(2),
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out uint value)
            ? (value, null)
            : (null, null);
    }

    /// <summary>Reads <c>OUTP:STAT?</c> — whether the pulse output is enabled.</summary>
    /// <remarks>Heather reads <c>BLOCK</c> as disabled and everything else it names as enabled.</remarks>
    public static bool? ReadOutputEnabled(string? response)
    {
        string? field = First(response, "OUTP:STAT?");
        if (string.IsNullOrEmpty(field))
        {
            return null;
        }

        if (field.Contains("BLOCK", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return field.Contains("ACTIVE", StringComparison.OrdinalIgnoreCase) ||
               field.Contains("NORMAL", StringComparison.OrdinalIgnoreCase) ||
               field.Contains("UNLOCK", StringComparison.OrdinalIgnoreCase)
            ? true
            : null;
    }

    /// <summary>Reads <c>OUTP:TP:SEL?</c> — the pulse rate, one pulse per second or per two.</summary>
    public static string? ReadPulseRate(string? response)
    {
        string? field = First(response, "OUTP:TP:SEL?");
        if (string.IsNullOrEmpty(field))
        {
            return null;
        }

        if (field.Contains("PP1S", StringComparison.OrdinalIgnoreCase))
        {
            return "1 PPS";
        }

        return field.Contains("PP2S", StringComparison.OrdinalIgnoreCase) ? "1 pulse per 2 s" : null;
    }

    /// <summary>Degrees, minutes and seconds into signed decimal degrees.</summary>
    private static double? Sexagesimal(
        string hemisphere, string degrees, string minutes, string seconds, string negative)
    {
        if (Decimal(degrees) is not double d)
        {
            return null;
        }

        double value = d + ((Decimal(minutes) ?? 0.0) / 60.0) + ((Decimal(seconds) ?? 0.0) / 3600.0);
        return hemisphere.Trim().StartsWith(negative, StringComparison.OrdinalIgnoreCase)
            ? -value
            : value;
    }

    private static double? Decimal(string? text) =>
        !string.IsNullOrWhiteSpace(text) &&
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value)
            ? value
            : null;

    /// <summary>The reply's first payload line split on commas, or empty.</summary>
    private static string[] Fields(string? response, string sent)
    {
        string? payload = UccmReply.FirstPayload(response, sent);
        return string.IsNullOrEmpty(payload)
            ? []
            : payload.Split(',', StringSplitOptions.TrimEntries);
    }

    private static string? First(string? response, string sent)
    {
        string[] fields = Fields(response, sent);
        return fields.Length > 0 ? fields[0] : null;
    }
}
