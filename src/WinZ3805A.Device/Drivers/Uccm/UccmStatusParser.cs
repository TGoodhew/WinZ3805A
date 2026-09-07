using System.Globalization;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>
/// Reads a UCCM <c>SYST:STAT?</c> reply into the common currency (#416).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is not the SmartClock's 80x24 screen and it is not only the hex line either.</b> The
/// reply mixes three things: human-readable state lines (<c>SETTLING</c>, <c>WARMUP</c>,
/// <c>TFOM</c>/<c>FFOM</c>, <c>NO REF</c>, <c>WAIT FOR GPS</c>), a satellite table introduced by a
/// header carrying <c>PRN</c> and <c>EL</c> and closed by an <c>ELEV MASK</c> line, and one or more
/// <c>C5</c> hex time codes which may arrive anywhere in it. Lady Heather's
/// <c>get_uccm_status()</c> reads it as a state machine over those markers and this follows the
/// same shape.
/// </para>
/// <para>
/// Adapted from Lady Heather (heathgps.cpp), MIT licensed, © 2008-2016 Mark S. Sims.
/// <b>Nothing here has been checked against a receiver.</b> §11.1 requires captured fixtures and we
/// have none, so every value this produces is a reading of a third party's reading.
/// </para>
/// <para>
/// It never throws. Everything it cannot make sense of becomes a parse warning and an absent field.
/// </para>
/// </remarks>
public static class UccmStatusParser
{
    /// <summary>Reads a status reply, given what is currently believed about the receiver.</summary>
    /// <param name="response">The whole reply, echo and terminator included.</param>
    /// <param name="capturedAt">The parse stamp, from the driver's <see cref="TimeProvider"/>.</param>
    /// <param name="profile">What the vendor and variant are believed to be.</param>
    public static ReceiverStatus Parse(string? response, DateTimeOffset capturedAt, UccmProfile profile)
    {
        List<string> warnings = [];
        List<TrackedSatellite> tracked = [];

        SmartClockMode mode = SmartClockMode.Unknown;
        string? modeDetail = null;
        int? tfom = null;
        int? ffom = null;
        int? elevationMask = null;
        UccmTimeCode? time = null;
        bool inSatelliteTable = false;
        bool sawAnything = false;

        foreach (string raw in SplitLines(response))
        {
            string line = raw.Trim();
            if (line.Length == 0)
            {
                continue;
            }

            if (UccmTimeCode.TryParse(line) is UccmTimeCode code)
            {
                // Last one wins: a status reply long enough to contain two is one during which the
                // clock advanced, and the later reading is the truer one.
                time = code;
                sawAnything = true;
                continue;
            }

            if (UccmReply.Classify(line, sent: null) is UccmLineKind.Complete or UccmLineKind.Error)
            {
                inSatelliteTable = false;
                continue;
            }

            string upper = line.ToUpperInvariant();

            if (inSatelliteTable)
            {
                if (upper.Contains("ELEV", StringComparison.Ordinal) &&
                    upper.Contains("MASK", StringComparison.Ordinal))
                {
                    inSatelliteTable = false;
                    elevationMask = FirstInteger(line);
                    continue;
                }

                if (TryReadSatellite(line) is TrackedSatellite satellite)
                {
                    tracked.Add(satellite);
                }

                continue;
            }

            if (upper.Contains("PRN", StringComparison.Ordinal) && upper.Contains("EL", StringComparison.Ordinal))
            {
                inSatelliteTable = true;
                sawAnything = true;
                continue;
            }

            if (upper.Contains("TFOM", StringComparison.Ordinal))
            {
                tfom = IntegerAfter(upper, "TFOM");
                ffom = IntegerAfter(upper, "FFOM");
                sawAnything = true;
                continue;
            }

            if (upper.Contains("SETTLING", StringComparison.Ordinal))
            {
                mode = SmartClockMode.Recovery;
                modeDetail = "Settling";
                sawAnything = true;
            }
            else if (upper.Contains("WARMUP", StringComparison.Ordinal))
            {
                mode = SmartClockMode.PowerUp;
                modeDetail = "Warming up";
                sawAnything = true;
            }
            else if (upper.Contains("NO REF", StringComparison.Ordinal) ||
                     upper.Contains("WAIT FOR GPS", StringComparison.Ordinal))
            {
                // Heather maps both of these to its acquiring mode. We have no acquiring member,
                // and PowerUp is the nearest honest one: the receiver is not disciplined and is
                // waiting for GPS, which is what PowerUp describes.
                mode = SmartClockMode.PowerUp;
                modeDetail = upper.Contains("NO REF", StringComparison.Ordinal)
                    ? "No reference"
                    : "Waiting for GPS";
                sawAnything = true;
            }
        }

        // The hex line is the authority on lock, because the text lines only ever say what is
        // WRONG - there is no "LOCKED" line to match. So a reply with a healthy lock byte and no
        // complaint is a locked receiver.
        if (time is not null && mode == SmartClockMode.Unknown)
        {
            mode = ModeFromLockState(time.LockState, profile.Vendor, out string? detail);
            modeDetail ??= detail;
        }

        if (!sawAnything)
        {
            warnings.Add(
                "No UCCM status marker was found in the reply - no time code, no figures of merit, " +
                "no satellite table. The response was either not a status reply or is in a format " +
                "this driver has not met.");
        }

        if (time is not null && !profile.VendorKnown && time.SuggestedVendor == UccmVendor.Unknown)
        {
            warnings.Add(
                $"Vendor is not established and the status bytes do not suggest one " +
                $"(lock {time.LockState:X2}, date {time.DateValidityState:X2}). Vendor-specific " +
                "readings are reported as absent until DIAG:LOOP? settles it.");
        }

        return new ReceiverStatus
        {
            Mode = mode,
            ModeDetail = modeDetail,
            Tfom = tfom,
            Ffom = ffom,
            Tracked = tracked,
            ElevationMaskDegrees = elevationMask,
            SignalStrengthKind = SignalStrengthKind.CarrierToNoise,
            TimeScale = TimeScale.Utc,
            DeviceDateTime = time?.UtcTime,
            GpsOnePpsValid = time?.AntennaOk == true && mode == SmartClockMode.Locked,
            LeapPending = time?.LeapPending == true ? LeapSecondPending.Plus : LeapSecondPending.None,
            CapturedAt = capturedAt,
            ParseWarnings = warnings,
        };
    }

    /// <summary>
    /// Turns the manufacturer-dependent lock byte into a mode (#418).
    /// </summary>
    /// <remarks>
    /// <b>The whole point of #418 in one method.</b> Symmetricom says <c>85</c> for locked and
    /// Trimble says <c>45</c>; a driver that knows only one reports the other's healthy receiver as
    /// unlocked, which the medallion would render as a fault. When the vendor is not yet known the
    /// low nibble is used on its own — it is common to both vendors in every value Heather
    /// records — and that is a documented guess rather than a fact.
    /// </remarks>
    private static SmartClockMode ModeFromLockState(int lockState, UccmVendor vendor, out string? detail)
    {
        detail = null;

        (int locked, int settling, int powerUp) = vendor switch
        {
            UccmVendor.Symmetricom => (0x85, 0x8F, -1),
            UccmVendor.Trimble => (0x45, 0x4F, 0x41),
            _ => (-1, -1, -1),
        };

        if (lockState == locked)
        {
            return SmartClockMode.Locked;
        }

        if (lockState == settling)
        {
            detail = "Settling";
            return SmartClockMode.Recovery;
        }

        if (lockState == powerUp)
        {
            detail = "Powering up";
            return SmartClockMode.PowerUp;
        }

        // Vendor unknown, or a byte neither vendor's table names. The low nibble carries the state
        // in every value Heather recorded, for both vendors - 5 locked, F settling, 1 powering up.
        return (lockState & 0x0F) switch
        {
            0x05 => SmartClockMode.Locked,
            0x0F => Detailed(ref detail, "Settling", SmartClockMode.Recovery),
            0x01 => Detailed(ref detail, "Powering up", SmartClockMode.PowerUp),
            _ => SmartClockMode.Unknown,
        };
    }

    private static SmartClockMode Detailed(ref string? detail, string text, SmartClockMode mode)
    {
        detail = text;
        return mode;
    }

    /// <summary>
    /// Reads one row of the satellite table.
    /// </summary>
    /// <remarks>
    /// The column layout differs across firmware — Heather tracks whether a signal-strength column
    /// is present via an <c>SS</c> marker in the header — so this reads positionally from the
    /// leading numbers rather than by fixed columns, and gives up on a row it cannot make sense of
    /// instead of guessing.
    /// </remarks>
    private static TrackedSatellite? TryReadSatellite(string line)
    {
        string[] parts = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 3 ||
            !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int prn) ||
            prn is <= 0 or > 210)
        {
            return null;
        }

        return new TrackedSatellite
        {
            Prn = prn,
            ElevationDegrees = Number(parts, 1),
            AzimuthDegrees = Number(parts, 2),
            SignalStrength = Number(parts, 3),
        };
    }

    /// <summary>
    /// One column as a whole number, rounded from a decimal where the receiver prints one.
    /// </summary>
    /// <remarks>
    /// The model carries elevation, azimuth and signal strength as integers, which is the
    /// resolution the SmartClock family prints. Rounding rather than refusing keeps a firmware that
    /// prints one decimal place readable; the lost tenth of a degree is below what the sky plot can
    /// draw.
    /// </remarks>
    private static int? Number(string[] parts, int index) =>
        index < parts.Length &&
        double.TryParse(parts[index], NumberStyles.Float, CultureInfo.InvariantCulture, out double value) &&
        double.IsFinite(value) &&
        value is >= int.MinValue and <= int.MaxValue
            ? (int)Math.Round(value, MidpointRounding.AwayFromZero)
            : null;

    private static int? IntegerAfter(string text, string marker)
    {
        int at = text.IndexOf(marker, StringComparison.Ordinal);
        return at < 0 ? null : FirstInteger(text[(at + marker.Length)..]);
    }

    private static int? FirstInteger(string text)
    {
        int at = 0;
        while (at < text.Length && !char.IsAsciiDigit(text[at]))
        {
            at++;
        }

        int end = at;
        while (end < text.Length && char.IsAsciiDigit(text[end]))
        {
            end++;
        }

        return end > at &&
               int.TryParse(text.AsSpan(at, end - at), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : null;
    }

    private static IEnumerable<string> SplitLines(string? response) =>
        string.IsNullOrEmpty(response)
            ? []
            : response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
