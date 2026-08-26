using System.Globalization;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The two sentences §10.7.1's drift card puts under its severity pill.
/// </summary>
/// <remarks>
/// <para>
/// Split out of <c>TimingPage</c> so the wording can be held against a known series. The fit itself
/// was always testable — <see cref="EfcDrift"/> is linked into the test project — but the sentences
/// that report it were built inline in a view, and they are where #182 went wrong: the arithmetic
/// was right to five decimal places and the card said <c>0.00 %</c>.
/// </para>
/// <para>
/// <b>Everything here is in ppm of control range, not per cent</b> (#182). See
/// <see cref="Ppm(double)"/> for why the unit changed rather than the precision.
/// </para>
/// </remarks>
public static class DriftAdvisory
{
    /// <summary>Parts per million of EFC control range, which is what one per cent is 10 000 of.</summary>
    /// <remarks>
    /// <para>
    /// §10.7.1 refuses to report a daily amplitude below a day of data because <i>"reporting a daily
    /// amplitude of zero would be a measurement"</i>. Past a day the card did exactly that: a real
    /// 0.00034 % swing and 0.0032 % of scatter both printed as <c>0.00 %</c>, which a user cannot
    /// tell from a broken readout. A double-oven oscillator holding 0.05 % of range across two days
    /// is the <i>good</i> case, and the card could not say so.
    /// </para>
    /// <para>
    /// <b>The unit changed rather than the precision.</b> §9.11 item 6 requires fixed decimal places
    /// per quantity and puts percentages at 1 dp, and the reason it gives — a column that changes
    /// precision row to row is unreadable — does not stop being true for a card. So the same figures
    /// are counted in a smaller unit instead: 0.00034 % is 3.4 ppm and 0.0032 % is 32 ppm, both
    /// perfectly readable at one decimal place. EFC is already a percentage <i>of control range</i>,
    /// so ppm here is that same range and needs no second reference.
    /// </para>
    /// </remarks>
    public static double Ppm(double percent) => percent * 10_000;

    /// <summary>The headline: the secular slope, and where it leads if anywhere.</summary>
    /// <param name="drift">The fit.</param>
    /// <param name="today">
    /// Local now, for dating the projection. Passed in rather than read, because §6.4 keeps clocks
    /// injectable and a sentence containing a date is exactly the kind that has to be pinned.
    /// </param>
    public static string Numbers(EfcDriftResult drift, DateTimeOffset today)
    {
        ArgumentNullException.ThrowIfNull(drift);

        string projection = drift.DaysToRail is double days
            ? $"reaches ±100 % in about {days.ToString("N0", CultureInfo.CurrentCulture)} day(s), around "
              + today.AddDays(days).ToString("d MMM yyyy", CultureInfo.CurrentCulture)
            : drift.DiurnalSeparable
                ? "no projection: the trend is flat or heading away from both rails"
                // "this range" would be wrong: the user may have the 7-day range selected and
                // simply not have 7 days of history yet. What is short is the data.
                : "no projection: under a day of data here, too short to tell drift from the "
                  + "room warming up";

        return $"Drift {Signed(Ppm(drift.SlopePercentPerDay))} ppm/day — {projection}.";
    }

    /// <summary>
    /// The evidence line: what was fitted, how noisy it was, and whether a daily term could be told
    /// apart from the trend.
    /// </summary>
    /// <remarks>
    /// The residual and the sample count are here because a slope without a sense of scatter is a
    /// number pretending to be a measurement (#137).
    /// </remarks>
    public static string Evidence(EfcDriftResult drift)
    {
        ArgumentNullException.ThrowIfNull(drift);

        return $"From {drift.SampleCount.ToString("N0", CultureInfo.CurrentCulture)} settled reading(s) "
            + $"spanning {DescribeSpan(drift.WindowSpan)}"
            + (drift.ExcludedForSettling > 0
                ? $", {drift.ExcludedForSettling.ToString("N0", CultureInfo.CurrentCulture)} excluded as post-power-up settling"
                : string.Empty)
            + $". Unexplained scatter {Fixed(Ppm(drift.ResidualPercent))} ppm of range. "
            + (drift.DiurnalSeparable
                ? $"Daily swing about ±{Fixed(Ppm(drift.DiurnalAmplitudePercent))} ppm, inferred from the "
                  + "reading's own 24-hour periodicity — this receiver reports no temperature, so "
                  + "nothing here is correlated against one."
                : "A daily swing cannot be separated from the trend in under a day of data, so "
                  + "none is reported — the fit here is a plain line.");
    }

    /// <summary>What the card says instead when the fit has too little to work with.</summary>
    public static string NotEnough(EfcDriftResult drift)
    {
        ArgumentNullException.ThrowIfNull(drift);

        return $"{drift.SampleCount.ToString("N0", CultureInfo.CurrentCulture)} usable reading(s) in "
            + $"this range; the fit needs at least {EfcDrift.MinimumSamples}.";
    }

    /// <summary>Says how long a fitted window is, in the largest unit that stays readable.</summary>
    /// <remarks>
    /// <b>Rounded down, never to nearest</b> (#184). This figure sits in the same sentence as a
    /// verdict about whether the window was long enough, and rounding up can carry it across the
    /// very threshold the verdict is about: 23.98 hours printed as "24.0 hours" beside "under a day
    /// of data", which reads as a contradiction rather than as a rounding. An evidence line may
    /// understate what it had; it may not overstate it.
    /// </remarks>
    public static string DescribeSpan(TimeSpan span) => span switch
    {
        { TotalDays: >= 2 } => $"{Down(span.TotalDays).ToString("0.0", CultureInfo.CurrentCulture)} days",
        { TotalHours: >= 2 } => $"{Down(span.TotalHours).ToString("0.0", CultureInfo.CurrentCulture)} hours",
        { TotalMinutes: >= 2 } => $"{Math.Floor(span.TotalMinutes).ToString("N0", CultureInfo.CurrentCulture)} minutes",
        _ => $"{Math.Floor(span.TotalSeconds).ToString("N0", CultureInfo.CurrentCulture)} seconds",
    };

    /// <summary>Truncates to one decimal place.</summary>
    /// <remarks>
    /// The epsilon is not decoration. <c>TimeSpan.FromHours(47.4).TotalHours</c> is
    /// 47.399999999999999 in binary floating point, so a plain truncation of ten times it yields
    /// 47.3 — a tenth of an hour lost to a representation, in the one direction this method exists
    /// to control. A nudge far smaller than a tenth restores the intended value without ever
    /// carrying a figure up past a real boundary.
    /// </remarks>
    private static double Down(double value) => Math.Truncate((value * 10) + 1e-9) / 10;

    /// <summary>One decimal place, always, and U+2212 for a negative (§9.5.3, §9.11 item 6).</summary>
    private static string Signed(double value) =>
        value.ToString("+0.0;−0.0;0.0", CultureInfo.CurrentCulture);

    /// <inheritdoc cref="Signed(double)" />
    private static string Fixed(double value) =>
        Math.Abs(value).ToString("0.0", CultureInfo.CurrentCulture);
}
