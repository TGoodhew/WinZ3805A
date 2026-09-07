using System.Globalization;

namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>
/// What <c>DIAG:LOOP?</c> answered, read according to whichever vendor's shape it arrived in (#418).
/// </summary>
/// <remarks>
/// <para>
/// <b>The same query returns two entirely different formats, and this is where the vendor is
/// actually established.</b> Trimble answers with seven space-separated floats in fixed positions;
/// Symmetricom answers with labelled lines whose payload sits after an <c>=</c>. Reading one as the
/// other does not fail loudly — it produces confident nonsense — which is why the header line is
/// matched before any value is taken.
/// </para>
/// <para>
/// Adapted from Lady Heather's <c>decode_uccm_loop()</c> and <c>get_uccm_loop()</c>
/// (heathgps.cpp), MIT licensed, © 2008-2016 Mark S. Sims. <b>Unverified against hardware.</b>
/// </para>
/// <para>
/// Note that Heather does not actually poll <c>DIAG:LOOP?</c> in its live UCCM cycle — the call is
/// commented out in <c>poll_next_uccm()</c> — so its loop decoder is reached only by a different
/// path. Anything here is therefore less exercised in the reference than the rest of this driver,
/// and deserves more suspicion rather than less when hardware is available.
/// </para>
/// </remarks>
public sealed record UccmLoopReading
{
    /// <summary>
    /// The band outside which a frequency difference is discarded as spurious.
    /// </summary>
    /// <remarks>
    /// <b>Provenance matters more than the number here.</b> Heather discards anything outside
    /// ±2.00E-7 with the comment that a bogus <c>-2.79E-7</c> "shows up occasionally on Trimble".
    /// That is field knowledge obtainable only by owning the hardware for a long time, and it is
    /// the single most valuable thing in the reference implementation for us — without it a
    /// spurious reading reaches the trend store and §9.10.2's chart draws a full-height excursion
    /// for an event that never happened. An unexplained magic constant is worse than none, so the
    /// reason travels with the value.
    /// </remarks>
    public const double FrequencyDifferenceLimit = 2.00E-7;

    /// <summary>Which vendor's format the answer was in.</summary>
    public UccmVendor Vendor { get; private init; }

    /// <summary>
    /// Fractional frequency difference, or <see langword="null"/> when absent or outside the
    /// sanity band.
    /// </summary>
    public double? FrequencyDifference { get; private init; }

    /// <summary>Oscillator offset in parts per billion, derived from <see cref="FrequencyDifference"/>.</summary>
    public double? OscillatorOffsetPpb =>
        FrequencyDifference is double difference ? difference * 1.0E9 : null;

    /// <summary>
    /// Whether a reading was seen and rejected by the sanity band, as opposed to never seen.
    /// </summary>
    /// <remarks>
    /// The distinction is worth keeping: "the receiver said something impossible" belongs in the
    /// parse warnings where a field report can quote it, while "the receiver did not say" is
    /// ordinary and silent.
    /// </remarks>
    public bool FrequencyDifferenceRejected { get; private init; }

    /// <summary>
    /// The frequency correction, Trimble only, or <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// Heather infers a disciplining state from this — non-zero implies disciplining — and has no
    /// such inference for Symmetricom. See <see cref="Disciplining"/>.
    /// </remarks>
    public double? FrequencyCorrection { get; private init; }

    /// <summary>
    /// Whether the oscillator appears to be being disciplined, where that can be told at all.
    /// </summary>
    /// <remarks>
    /// <b>An inference, not a report, and it must be labelled as one wherever a user sees it.</b>
    /// Trimble has no disciplining field; Heather deduces the state from
    /// <see cref="FrequencyCorrection"/> being non-zero. Symmetricom's format carries no equivalent,
    /// so this is <see langword="null"/> there rather than false — §11.1's rule that an absent
    /// field is absent and never a fabricated value.
    /// </remarks>
    public bool? Disciplining { get; private init; }

    /// <summary>
    /// Oscillator temperature correction, Symmetricom only.
    /// </summary>
    /// <remarks>
    /// <b>Trimble modules do not report this at all</b>, so it stays <see langword="null"/> there
    /// and must render as an em dash rather than as zero. Heather makes the same distinction by
    /// enabling its temperature plot only for the Symmetricom variant.
    /// </remarks>
    public double? TemperatureCorrection { get; private init; }

    /// <summary>
    /// Reads a whole <c>DIAG:LOOP?</c> response, header included.
    /// </summary>
    /// <remarks>
    /// Never throws (§11.1). A response in no recognised shape yields a reading whose vendor is
    /// <see cref="UccmVendor.Unknown"/> and whose values are all absent.
    /// </remarks>
    public static UccmLoopReading Parse(string? response)
    {
        UccmVendor vendor = UccmVendor.Unknown;
        double? difference = null;
        double? correction = null;
        double? temperature = null;
        bool rejected = false;

        foreach (string raw in SplitLines(response))
        {
            string line = raw.Trim();
            if (line.Length == 0 || UccmTimeCode.IsTimeCodeLine(line))
            {
                // An unsolicited time code can land in the middle of this response; it is not
                // loop data and must not be read as a row of floats.
                continue;
            }

            // The header decides the format, and it must be seen before any value is taken.
            // Symmetricom is tested first: its rule is a run of dashes, which is unambiguous,
            // whereas "DAC" could in principle appear in a label.
            if (line.Contains("----", StringComparison.Ordinal))
            {
                vendor = UccmVendor.Symmetricom;
                continue;
            }

            if (line.Contains("DAC", StringComparison.OrdinalIgnoreCase))
            {
                vendor = UccmVendor.Trimble;
                continue;
            }

            if (vendor == UccmVendor.Symmetricom)
            {
                if (line.StartsWith("FREQ COR", StringComparison.OrdinalIgnoreCase))
                {
                    (difference, rejected) = Sanitise(AfterEquals(line));
                }
                else if (line.StartsWith("TEMP COR", StringComparison.OrdinalIgnoreCase))
                {
                    temperature = AfterEquals(line);
                }
            }
            else if (vendor == UccmVendor.Trimble)
            {
                double[] fields = Floats(line);
                if (fields.Length >= 7)
                {
                    // dac_link dac_avg dac_gps freq_corr link_off freq_diff freq_frac
                    correction = fields[3];
                    (difference, rejected) = Sanitise(fields[5]);
                }
            }
        }

        return new UccmLoopReading
        {
            Vendor = vendor,
            FrequencyDifference = difference,
            FrequencyDifferenceRejected = rejected,
            FrequencyCorrection = correction,
            TemperatureCorrection = temperature,
            Disciplining = vendor == UccmVendor.Trimble && correction is double value
                ? value != 0.0
                : null,
        };
    }

    /// <summary>Applies the sanity band, reporting whether a value was seen and thrown away.</summary>
    private static (double? Value, bool Rejected) Sanitise(double? candidate) =>
        candidate switch
        {
            null => (null, false),
            double value when double.IsNaN(value) || Math.Abs(value) > FrequencyDifferenceLimit => (null, true),
            double value => (value, false),
        };

    /// <summary>The number after the first <c>=</c>, or null.</summary>
    private static double? AfterEquals(string line)
    {
        int at = line.IndexOf('=');
        return at < 0 ? null : FirstFloat(line[(at + 1)..]);
    }

    private static double? FirstFloat(string text)
    {
        double[] found = Floats(text);
        return found.Length > 0 ? found[0] : null;
    }

    private static double[] Floats(string text)
    {
        string[] parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        List<double> values = new(parts.Length);
        foreach (string part in parts)
        {
            if (double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
            {
                values.Add(value);
            }
        }

        return [.. values];
    }

    private static IEnumerable<string> SplitLines(string? response) =>
        string.IsNullOrEmpty(response)
            ? []
            : response.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
}
