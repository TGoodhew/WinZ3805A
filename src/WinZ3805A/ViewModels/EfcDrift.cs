namespace WinZ3805A.ViewModels;

/// <summary>One EFC reading, with the context the fit needs to know whether to trust it.</summary>
/// <param name="Ticks">UTC ticks.</param>
/// <param name="Percent">Relative oscillator control, per cent.</param>
/// <param name="IsPowerUp">Whether the receiver was in its power-up mode when this was taken.</param>
/// <param name="IsLocked">Whether it was locked to GPS.</param>
public readonly record struct EfcSample(long Ticks, double Percent, bool IsPowerUp, bool IsLocked);

/// <summary>Which of #137's three patterns the evidence is consistent with.</summary>
/// <remarks>
/// <b>Consistent with, never "is".</b> These name a pattern in the readings, not a diagnosis of the
/// hardware, and the interface must say so. §11.1's rule that the application reports what the
/// receiver said rather than substituting its own guess does not stop applying because the guess
/// has arithmetic behind it.
/// </remarks>
public enum DriftPattern
{
    /// <summary>Not enough data, or not enough of it usable, to say anything.</summary>
    Insufficient,

    /// <summary>Nothing in the readings suggests any of the three faults.</summary>
    NothingRemarkable,

    /// <summary>EFC near a rail with a good fix — consistent with an oscillator near end of range.</summary>
    OscillatorNearingRange,

    /// <summary>EFC mid-range with a degraded fix — consistent with a GPS or antenna path fault.</summary>
    GpsOrAntennaPath,

    /// <summary>EFC mid-range but erratic — consistent with a loop, DAC or reference fault.</summary>
    LoopOrReference,
}

/// <summary>What the fit found.</summary>
public sealed record EfcDriftResult
{
    /// <summary>How many samples the fit used.</summary>
    public required int SampleCount { get; init; }

    /// <summary>How many were dropped as post-power-up settling.</summary>
    public required int ExcludedForSettling { get; init; }

    /// <summary>Secular drift in per cent per day, with the diurnal term removed.</summary>
    public double SlopePercentPerDay { get; init; }

    /// <summary>Half the peak-to-peak of the fitted 24-hour component.</summary>
    public double DiurnalAmplitudePercent { get; init; }

    /// <summary>RMS of what the model does not explain.</summary>
    public double ResidualPercent { get; init; }

    /// <summary>The most recent usable reading.</summary>
    public double LatestPercent { get; init; }

    /// <summary>Days until the trend would reach a rail, or <see langword="null"/>.</summary>
    public double? DaysToRail { get; init; }

    /// <summary>Which pattern the evidence is consistent with.</summary>
    public DriftPattern Pattern { get; init; } = DriftPattern.Insufficient;

    /// <summary>How much time the fitted samples actually span.</summary>
    /// <remarks>
    /// Not the same thing as the range the user picked. Selecting seven days on a receiver that has
    /// been logging for one gives a seven-day range and a one-day window, and it is the window that
    /// governs what the fit can support.
    /// </remarks>
    public TimeSpan WindowSpan { get; init; }

    /// <summary>
    /// Whether the window was long enough to tell a daily cycle from a trend.
    /// </summary>
    /// <remarks>
    /// False for a window shorter than a day, where the 24-hour sine and cosine terms have almost
    /// nothing to distinguish them and the fit drops to a plain line. The slope is still reported —
    /// it is simply not yet known how much of it is the room warming up.
    /// </remarks>
    public bool DiurnalSeparable { get; init; }

    /// <summary>Whether there was enough usable data to fit at all.</summary>
    public bool IsUsable => Pattern != DriftPattern.Insufficient;
}

/// <summary>
/// #137's drift analysis: secular slope, diurnal component, and an advisory pattern.
/// </summary>
/// <remarks>
/// <para>
/// Free of UI types and linked into the tests, because every judgement here is one a screenshot
/// cannot check. A slope is a number that looks equally plausible whether or not the 24-hour term
/// was removed from it first.
/// </para>
/// <para>
/// <b>The diurnal component is fitted, not assumed away.</b> Ambient temperature cycles EFC on a
/// 24-hour period, and a straight line through a day of that reports the room rather than the
/// oscillator — the slope comes out as whatever phase of the cycle the window happened to start
/// and end on. So the model is a line <i>plus</i> a 24-hour sinusoid, solved together, and the
/// slope reported is the line's.
/// </para>
/// <para>
/// <b>There is no temperature reading to correlate against.</b> Verified against both the command
/// catalog and the 58503A reference: the only documented node is
/// <c>:DIAGnostic:ROSCillator:EFControl:RELative?</c>, and <c>:DIAG:ROSC:EFC:TCOefficient?</c> is a
/// temperature <i>coefficient</i> and §8.5 opt-in besides. The 24-hour term here is inferred from
/// EFC's own periodicity, and the interface has to say so — a user who reads "diurnal" will
/// otherwise assume something measured it.
/// </para>
/// </remarks>
public static class EfcDrift
{
    /// <summary>The rail EFC is projected towards, per cent.</summary>
    public const double RailPercent = 100;

    /// <summary>How long after a power-up the loop is still settling (§10.8 uses the same figure).</summary>
    public static readonly TimeSpan SettlingWindow = TimeSpan.FromHours(24);

    /// <summary>Below this many usable samples, no fit is attempted.</summary>
    public const int MinimumSamples = 30;

    /// <summary>Beyond this much of a rail, EFC counts as "near" one.</summary>
    public const double NearRailPercent = 80;

    /// <summary>Above this RMS residual, the series counts as erratic rather than merely noisy.</summary>
    public const double ErraticResidualPercent = 5;

    /// <summary>Below this fraction of samples locked, the fix counts as degraded.</summary>
    public const double DegradedLockFraction = 0.9;

    /// <summary>Fits a window of EFC readings.</summary>
    /// <param name="samples">Readings in ascending time order.</param>
    /// <param name="settling">
    /// How long after the last power-up to ignore. Defaults to <see cref="SettlingWindow"/>.
    /// </param>
    public static EfcDriftResult Analyse(IReadOnlyList<EfcSample> samples, TimeSpan? settling = null)
    {
        ArgumentNullException.ThrowIfNull(samples);

        TimeSpan skip = settling ?? SettlingWindow;

        // A cold start is a run of power-up samples; everything within `skip` of the last one is
        // the loop settling rather than the oscillator ageing, and including it bends the fit.
        long usableFrom = long.MinValue;
        foreach (EfcSample sample in samples)
        {
            if (sample.IsPowerUp)
            {
                usableFrom = sample.Ticks + skip.Ticks;
            }
        }

        List<EfcSample> usable = [];
        int excluded = 0;
        foreach (EfcSample sample in samples)
        {
            if (double.IsNaN(sample.Percent))
            {
                continue;
            }

            if (sample.Ticks < usableFrom || sample.IsPowerUp)
            {
                excluded++;
                continue;
            }

            usable.Add(sample);
        }

        if (usable.Count < MinimumSamples)
        {
            return new EfcDriftResult
            {
                SampleCount = usable.Count,
                ExcludedForSettling = excluded,
                Pattern = DriftPattern.Insufficient,
            };
        }

        (double slopePerDay, double diurnal, double residual, bool separable) = Fit(usable);

        double latest = usable[^1].Percent;
        double lockedFraction = usable.Count(sample => sample.IsLocked) / (double)usable.Count;

        return new EfcDriftResult
        {
            SampleCount = usable.Count,
            ExcludedForSettling = excluded,
            SlopePercentPerDay = slopePerDay,
            DiurnalAmplitudePercent = diurnal,
            DiurnalSeparable = separable,
            WindowSpan = TimeSpan.FromTicks(usable[^1].Ticks - usable[0].Ticks),
            ResidualPercent = residual,
            LatestPercent = latest,
            // No projection from a window shorter than a day. The slope is real arithmetic on
            // real samples, but below a day it cannot be told apart from the room warming up, and
            // "38 days to the rail" extrapolated from two minutes of it would be read as a
            // measurement rather than the guess it is.
            DaysToRail = separable ? DaysToRail(latest, slopePerDay) : null,
            Pattern = Classify(latest, slopePerDay, residual, lockedFraction),
        };
    }

    /// <summary>
    /// Least squares over a line plus a 24-hour sinusoid.
    /// </summary>
    /// <remarks>
    /// Four basis functions — 1, t, sin(2πt/24h), cos(2πt/24h) — solved as normal equations by
    /// Gaussian elimination. Four unknowns is small enough that the numerical objections to normal
    /// equations do not bite, and it keeps this file free of a matrix library.
    /// <para>
    /// Time is measured in days from the first sample, so the slope falls out of the solution in
    /// per cent per day rather than needing a conversion that could be wrong.
    /// </para>
    /// </remarks>
    private static (double SlopePerDay, double DiurnalAmplitude, double Residual, bool Separable) Fit(
        IReadOnlyList<EfcSample> samples)
    {
        long origin = samples[0].Ticks;
        const double ticksPerDay = TimeSpan.TicksPerDay;
        double omega = 2 * Math.PI; // one cycle per day, with t already in days

        // Below a full day the sine and cosine columns are nearly collinear with the constant and
        // the line, and asking four coefficients out of that gives a singular system. Dropping to
        // a plain line is the honest answer rather than a workaround: the daily cycle genuinely is
        // not identifiable yet, and the caller is told so through DiurnalSeparable.
        //
        // This mattered in practice. With the full basis over a two-minute window the solve went
        // singular, returned zeros, and the residual was then measured against a model of zero —
        // so a dead-flat −16.83 % series reported "unexplained scatter 16.83 %", the reading itself
        // wearing the label of its own error.
        double span = (samples[^1].Ticks - origin) / ticksPerDay;
        bool separable = span >= 1.0;
        int terms = separable ? 4 : 2;

        double[,] a = new double[terms, terms + 1];

        foreach (EfcSample sample in samples)
        {
            double t = (sample.Ticks - origin) / ticksPerDay;
            double[] basis = separable
                ? [1, t, Math.Sin(omega * t), Math.Cos(omega * t)]
                : [1, t];

            for (int i = 0; i < terms; i++)
            {
                for (int j = 0; j < terms; j++)
                {
                    a[i, j] += basis[i] * basis[j];
                }

                a[i, terms] += basis[i] * sample.Percent;
            }
        }

        if (Solve(a, terms) is not double[] c)
        {
            // Still singular — every sample at the same instant. Report a flat fit with the
            // scatter measured about the mean, which is true and asserts nothing false.
            double mean = samples.Average(sample => sample.Percent);
            double variance = samples.Average(sample => (sample.Percent - mean) * (sample.Percent - mean));
            return (0, 0, Math.Sqrt(variance), false);
        }

        double sumSquares = 0;
        foreach (EfcSample sample in samples)
        {
            double t = (sample.Ticks - origin) / ticksPerDay;
            double modelled = separable
                ? c[0] + (c[1] * t) + (c[2] * Math.Sin(omega * t)) + (c[3] * Math.Cos(omega * t))
                : c[0] + (c[1] * t);

            double error = sample.Percent - modelled;
            sumSquares += error * error;
        }

        double amplitude = separable ? Math.Sqrt((c[2] * c[2]) + (c[3] * c[3])) : 0;
        return (c[1], amplitude, Math.Sqrt(sumSquares / samples.Count), separable);
    }

    /// <summary>Gaussian elimination with partial pivoting on an augmented matrix.</summary>
    /// <remarks>
    /// Returns <see langword="null"/> on a singular system rather than zeros. Zeros are a
    /// valid-looking answer that is wrong in a specific and damaging way: the caller then measures
    /// its residual against a model of zero, so a perfectly steady reading is reported as scatter
    /// equal to its own magnitude. Null forces the caller to decide what to say instead.
    /// </remarks>
    private static double[]? Solve(double[,] a, int n)
    {
        for (int column = 0; column < n; column++)
        {
            int pivot = column;
            for (int row = column + 1; row < n; row++)
            {
                if (Math.Abs(a[row, column]) > Math.Abs(a[pivot, column]))
                {
                    pivot = row;
                }
            }

            if (Math.Abs(a[pivot, column]) < 1e-12)
            {
                return null;
            }

            if (pivot != column)
            {
                for (int k = 0; k <= n; k++)
                {
                    (a[column, k], a[pivot, k]) = (a[pivot, k], a[column, k]);
                }
            }

            for (int row = 0; row < n; row++)
            {
                if (row == column)
                {
                    continue;
                }

                double factor = a[row, column] / a[column, column];
                for (int k = column; k <= n; k++)
                {
                    a[row, k] -= factor * a[column, k];
                }
            }
        }

        double[] result = new double[n];
        for (int i = 0; i < n; i++)
        {
            result[i] = a[i, n] / a[i, i];
        }

        return result;
    }

    /// <summary>
    /// Days until the trend reaches whichever rail it is heading for.
    /// </summary>
    /// <remarks>
    /// Null when the slope is flat or heading away from both rails, which is the common and
    /// healthy case. A projection is only meaningful if the line actually arrives somewhere, and
    /// "12 000 days" is a way of saying "never" that invites being read as a measurement.
    /// </remarks>
    public static double? DaysToRail(double latestPercent, double slopePerDay)
    {
        if (Math.Abs(slopePerDay) < 0.001 || double.IsNaN(slopePerDay))
        {
            return null;
        }

        double rail = slopePerDay > 0 ? RailPercent : -RailPercent;
        double days = (rail - latestPercent) / slopePerDay;

        return days is > 0 and < 36500 ? days : null;
    }

    /// <summary>
    /// Which of #137's three patterns the evidence is consistent with.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The order matters. A receiver near a rail <i>and</i> without a fix is reported as the GPS
    /// case, because a bad fix explains a wandering EFC and the reverse is not true — an oscillator
    /// at end of range does not stop satellites being received. Reporting the more alarming of two
    /// explanations because it was checked first would be exactly the overstatement #137 forbids.
    /// </para>
    /// <para>
    /// Erratic is judged on the residual rather than on the slope: a loop hunting about its
    /// setpoint has no trend to speak of and a large unexplained scatter, which is what separates
    /// it from an oscillator walking steadily in one direction.
    /// </para>
    /// </remarks>
    public static DriftPattern Classify(
        double latestPercent,
        double slopePerDay,
        double residualPercent,
        double lockedFraction)
    {
        if (lockedFraction < DegradedLockFraction)
        {
            return DriftPattern.GpsOrAntennaPath;
        }

        if (Math.Abs(latestPercent) >= NearRailPercent)
        {
            return DriftPattern.OscillatorNearingRange;
        }

        if (residualPercent >= ErraticResidualPercent)
        {
            return DriftPattern.LoopOrReference;
        }

        return DriftPattern.NothingRemarkable;
    }

    /// <summary>The hedged sentence for a pattern, for the advisory card.</summary>
    /// <remarks>
    /// Every one of these says "consistent with" rather than naming a fault, and each names the
    /// evidence it rests on so a user can disagree with it. #137 requires the advisory not to
    /// overstate its confidence, and the wording is where that requirement actually lives.
    /// </remarks>
    public static string Describe(DriftPattern pattern) => pattern switch
    {
        DriftPattern.OscillatorNearingRange =>
            "The oscillator control is near the end of its range while the receiver is holding a "
            + "good fix. That is consistent with an oscillator approaching the end of its tuning "
            + "range, though it is not the only explanation.",
        DriftPattern.GpsOrAntennaPath =>
            "The oscillator control is mid-range but the receiver has been unlocked for much of "
            + "this window. That is consistent with a problem in the GPS signal path — antenna, "
            + "cable, connector or sky view — rather than with the oscillator. It does not "
            + "distinguish between them: a restricted view and a degraded signal path look the "
            + "same from here, and telling them apart needs the carrier-to-noise figures on the "
            + "Satellites page.",
        DriftPattern.LoopOrReference =>
            "The oscillator control is mid-range but scatters more than a settled loop should. "
            + "That is consistent with a control-loop, DAC or reference problem, and is worth "
            + "watching over a longer window before drawing any conclusion.",
        DriftPattern.NothingRemarkable =>
            "Nothing in this window suggests a fault. The oscillator control is mid-range and "
            + "steady.",
        _ =>
            "Not enough settled data in this window to say anything. The fit needs readings from "
            + "after the loop has settled, over a window long enough for a daily cycle to be told "
            + "apart from a trend.",
    };
}
