namespace WinZ3805A.Controls;

/// <summary>
/// One point on a stability curve: the averaging time, the deviation, and how well founded it is.
/// </summary>
/// <param name="Tau">The averaging time τ, in seconds.</param>
/// <param name="Deviation">σ<sub>y</sub>(τ), dimensionless.</param>
/// <param name="Pairs">
/// How many second differences the estimate averaged. Confidence goes roughly as 1/√N, so this is
/// part of the reading rather than a footnote to it.
/// </param>
public readonly record struct AllanPoint(double Tau, double Deviation, int Pairs);

/// <summary>
/// §13's Allan deviation over the logged 1 PPS time-interval series (P2-3, #63).
/// </summary>
/// <remarks>
/// <para>
/// The standard stability measure for this class of instrument, and the first thing a time-nut
/// reaches for after the trace itself. It answers a question the chart cannot: whether the loop is
/// noisier at one averaging time than another, which is what separates a receiver hunting on a
/// second-by-second basis from one drifting slowly.
/// </para>
/// <para>
/// <b>Overlapping, not plain.</b> Both are correct estimators; the overlapping form uses every
/// available second difference at each tau rather than only the disjoint ones, so its confidence at
/// large tau is far better on a series of the length this application collects. On a 47-hour capture
/// the difference at long tau is between an estimate and a rumour.
/// </para>
/// <para>
/// <b>Feed it the raw series, never the decimated one.</b> #63 says so and it is worth repeating
/// where the code is: <see cref="TrendDecimation"/> keeps the minimum and maximum of each column,
/// which is right for drawing a shape and wrong for a statistic — the extremes of a bucket are not a
/// sample of it, and a second difference taken across them measures the decimation rather than the
/// oscillator.
/// </para>
/// <para>
/// <b>Phase in, deviation out.</b> The receiver reports 1 PPS time interval, which is phase: the
/// offset of its pulse from GPS. Allan deviation is a frequency statistic, and the second difference
/// below is what converts one to the other, so nothing here should be handed a frequency series.
/// </para>
/// </remarks>
public static class AllanDeviation
{
    /// <summary>
    /// Overlapping Allan deviation at one averaging time.
    /// </summary>
    /// <remarks>
    /// <para>
    /// σ<sub>y</sub>(τ) = √( 1 / (2(N−2m)τ²) · Σ (x<sub>i+2m</sub> − 2x<sub>i+m</sub> + x<sub>i</sub>)² ),
    /// with τ = m·τ₀ and the sum taken over every i from 0 to N−2m−1.
    /// </para>
    /// <para>
    /// Returns <see langword="null"/> rather than a number when the series cannot support the
    /// question — fewer than three samples, or an averaging factor so large that no second
    /// difference fits. §11.1's habit: a missing answer is null and renders as an em dash, and an
    /// Allan deviation computed from one difference would be a number with no meaning, which is
    /// worse than nothing.
    /// </para>
    /// <para>
    /// <b>Only for a series that is genuinely uniform.</b> Anything read back from the trend store
    /// is not — use the overload taking sample times, which will not silently treat a gap as a
    /// sample interval.
    /// </para>
    /// </remarks>
    /// <param name="phase">Time-interval samples in seconds, uniformly spaced, oldest first.</param>
    /// <param name="tau0">The sampling interval in seconds.</param>
    /// <param name="averagingFactor">m, so that τ = m·τ₀. Must be at least 1.</param>
    public static double? Overlapping(IReadOnlyList<double> phase, double tau0, int averagingFactor)
    {
        if (phase is null || averagingFactor < 1 || !double.IsFinite(tau0) || tau0 <= 0)
        {
            return null;
        }

        int n = phase.Count;
        int m = averagingFactor;
        int terms = n - (2 * m);

        if (terms < 1)
        {
            return null;
        }

        double sum = 0;
        int used = 0;

        for (int i = 0; i < terms; i++)
        {
            double second = phase[i + (2 * m)] - (2 * phase[i + m]) + phase[i];
            if (!double.IsFinite(second))
            {
                continue;
            }

            sum += second * second;
            used++;
        }

        if (used < 1)
        {
            return null;
        }

        double tau = m * tau0;
        return Math.Sqrt(sum / (2.0 * used * tau * tau));
    }

    /// <summary>
    /// Overlapping Allan deviation over a series whose samples are not evenly spaced.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Use this one for anything that came out of the trend store.</b> The uniform overload is
    /// correct only for a series that really is uniform, and the logged series is not: the store
    /// writes a row per poll, and the poll cadence moves with the connection state and with what the
    /// user is doing on screen. A six-day capture taken while this application was being worked hard
    /// held 9,423 intervals shorter than the nominal ten seconds — many of them 0.2 s — alongside 31
    /// gaps longer than ten minutes, two of them over half a day. Its longest genuinely uniform
    /// stretch was 3.2 hours.
    /// </para>
    /// <para>
    /// Handing that to the uniform overload does not fail, which is the problem. It returns a
    /// confident number describing the poll schedule and the intervals when the receiver was
    /// unplugged, labelled as the stability of the oscillator. Making that unavailable is the whole
    /// reason this overload exists.
    /// </para>
    /// <para>
    /// The series is split into maximal runs whose consecutive spacing is within
    /// <paramref name="tolerance"/> of τ₀, and second differences accumulate across every run. Each
    /// term is therefore a real second difference at lag m·τ₀; a gap contributes nothing rather than
    /// contributing a wrong term, and a run too short to hold one difference drops out entirely.
    /// </para>
    /// </remarks>
    /// <param name="phase">Time-interval samples in seconds, oldest first.</param>
    /// <param name="seconds">When each sample was taken, in seconds, same length and order as <paramref name="phase"/>.</param>
    /// <param name="tau0">The intended sampling interval; see <see cref="NominalInterval"/>.</param>
    /// <param name="averagingFactor">m, so that τ = m·τ₀. Must be at least 1.</param>
    /// <param name="tolerance">How far a step may stray from τ₀ and still count, as a fraction of τ₀.</param>
    public static double? Overlapping(
        IReadOnlyList<double> phase,
        IReadOnlyList<double> seconds,
        double tau0,
        int averagingFactor,
        double tolerance = 0.25)
    {
        if (phase is null || seconds is null || phase.Count != seconds.Count
            || averagingFactor < 1
            || !double.IsFinite(tau0) || tau0 <= 0
            || !double.IsFinite(tolerance) || tolerance < 0)
        {
            return null;
        }

        double slack = tau0 * tolerance;
        double sum = 0;
        int used = 0;
        int runStart = 0;

        for (int i = 1; i <= phase.Count; i++)
        {
            if (i < phase.Count && StepFits(seconds, i, tau0, slack))
            {
                continue;
            }

            Accumulate(phase, runStart, i, averagingFactor, ref sum, ref used);
            runStart = i;
        }

        if (used < 1)
        {
            return null;
        }

        double tau = averagingFactor * tau0;
        return Math.Sqrt(sum / (2.0 * used * tau * tau));
    }

    /// <summary>
    /// The same estimate, with the number of second differences that produced it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>The count is not a diagnostic, it is part of the reading.</b> The confidence of an
    /// overlapping estimate goes roughly as 1/√N, so σ(τ) from four differences and σ(τ) from four
    /// thousand are different claims wearing the same number of digits. A stability figure quoted
    /// without it invites a reader to compare two values that are not comparable — which is exactly
    /// what the longest τ on a short series looks like.
    /// </para>
    /// <para>
    /// Shares its arithmetic with <see cref="Overlapping(IReadOnlyList{double}, IReadOnlyList{double}, double, int, double)"/>
    /// rather than restating it, so the two cannot drift into disagreeing about the same series.
    /// </para>
    /// </remarks>
    public static AllanPoint? Estimate(
        IReadOnlyList<double> phase,
        IReadOnlyList<double> seconds,
        double tau0,
        int averagingFactor,
        double tolerance = 0.25)
    {
        if (Overlapping(phase, seconds, tau0, averagingFactor, tolerance) is not double deviation)
        {
            return null;
        }

        // Recounted rather than threaded out of the estimator, because the estimator's shape is
        // what the fourteen tests around it pin. This walk is the same one and is O(n).
        double slack = tau0 * tolerance;
        double ignored = 0;
        int used = 0;
        int runStart = 0;

        for (int i = 1; i <= phase.Count; i++)
        {
            if (i < phase.Count && StepFits(seconds, i, tau0, slack))
            {
                continue;
            }

            Accumulate(phase, runStart, i, averagingFactor, ref ignored, ref used);
            runStart = i;
        }

        return new AllanPoint(averagingFactor * tau0, deviation, used);
    }

    /// <summary>
    /// The sampling interval a series appears to have been logged at, or null if it has none.
    /// </summary>
    /// <remarks>
    /// The median step, which is the robust choice: a mean is dragged upward by a single overnight
    /// gap, and this series has several. It is a starting point for τ₀ rather than a guarantee — on
    /// a capture that spans a change of poll rate there is no single right answer, and the run
    /// splitting in the gap-aware overload is what keeps the result honest either way.
    /// </remarks>
    /// <param name="seconds">Sample times in seconds, oldest first.</param>
    public static double? NominalInterval(IReadOnlyList<double> seconds)
    {
        if (seconds is null || seconds.Count < 2)
        {
            return null;
        }

        List<double> steps = [];

        for (int i = 1; i < seconds.Count; i++)
        {
            double step = seconds[i] - seconds[i - 1];
            if (double.IsFinite(step) && step > 0)
            {
                steps.Add(step);
            }
        }

        if (steps.Count == 0)
        {
            return null;
        }

        steps.Sort();
        return steps[steps.Count / 2];
    }

    /// <summary>Whether the step ending at <paramref name="i"/> is close enough to τ₀ to count.</summary>
    private static bool StepFits(IReadOnlyList<double> seconds, int i, double tau0, double slack)
    {
        double step = seconds[i] - seconds[i - 1];
        return double.IsFinite(step) && Math.Abs(step - tau0) <= slack;
    }

    /// <summary>Adds every second difference that fits inside one uniformly spaced run.</summary>
    private static void Accumulate(
        IReadOnlyList<double> phase, int start, int end, int m, ref double sum, ref int used)
    {
        for (int i = start; i + (2 * m) < end; i++)
        {
            double second = phase[i + (2 * m)] - (2 * phase[i + m]) + phase[i];
            if (!double.IsFinite(second))
            {
                continue;
            }

            sum += second * second;
            used++;
        }
    }

    /// <summary>
    /// The averaging factors worth reporting for a series of the given length.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Octave-spaced — 1, 2, 4, 8, … — which is the convention for an Allan plot and the reason it
    /// is drawn on log axes. Linear tau spacing wastes most of its points at the short end where
    /// the estimate is already good.
    /// </para>
    /// <para>
    /// <b>Stops at N/4 rather than at N/2.</b> A second difference fits whenever 2m &lt; N, but at
    /// m = N/2 exactly one difference contributes and the estimate is dominated by wherever the
    /// series happened to start. N/4 leaves at least half the series overlapping at the longest tau
    /// reported, which is the usual rule and the difference between a curve that means something at
    /// its right-hand end and one that only looks like it does.
    /// </para>
    /// </remarks>
    /// <param name="sampleCount">How many samples the series holds.</param>
    public static IReadOnlyList<int> AveragingFactors(int sampleCount)
    {
        List<int> factors = [];

        for (int m = 1; m * 4 <= sampleCount; m *= 2)
        {
            factors.Add(m);
        }

        return factors;
    }
}
