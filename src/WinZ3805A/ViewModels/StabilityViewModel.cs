using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using WinZ3805A.Controls;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// One row of the stability table: an averaging time and what the series says about it.
/// </summary>
/// <param name="Point">The estimate, or null when this τ has no usable data.</param>
/// <param name="Tau">The averaging time, in seconds, even when there is no estimate.</param>
public sealed record StabilityRow(double Tau, AllanPoint? Point)
{
    /// <summary>The averaging time, formatted.</summary>
    /// <remarks>
    /// Seconds throughout rather than switching to minutes and hours further down the table. A
    /// stability curve is read by comparing rows, and a column that changes unit halfway is the
    /// thing §9.5.3 rule 6 is about — the reader has to convert before they can compare.
    /// </remarks>
    public string TauText => Tau >= 100
        ? Tau.ToString("N0", CultureInfo.CurrentCulture) + " s"
        : Tau.ToString("N1", CultureInfo.CurrentCulture) + " s";

    /// <summary>
    /// σ<sub>y</sub>(τ), or <c>—</c> where the series cannot support one.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Scientific notation with a fixed two-decimal mantissa. §9.5.3 rule 6 fixes the decimals per
    /// quantity and says that where a figure is too small to survive its precision the <i>unit</i>
    /// changes rather than the digit count — and σ<sub>y</sub> is <b>dimensionless</b>, so it has no
    /// unit to change. The exponent does that job instead, and the mantissa stays fixed, which is
    /// what the rule is protecting: two rows are comparable at a glance.
    /// </para>
    /// <para>
    /// U+2212 in the exponent, per rule 4. A hyphen there is optically too short beside lining
    /// figures, and a stability table is nothing but lining figures.
    /// </para>
    /// </remarks>
    public string DeviationText => Point is AllanPoint point
        ? point.Deviation.ToString("0.00e+00", CultureInfo.InvariantCulture).Replace("-", "−", StringComparison.Ordinal)
        : ReadoutFormatter.NoValue;

    /// <summary>How many second differences the estimate averaged, as text.</summary>
    public string PairsText => Point is AllanPoint point
        ? point.Pairs.ToString("N0", CultureInfo.CurrentCulture)
        : ReadoutFormatter.NoValue;

    /// <summary>
    /// Whether this row rests on too few differences to be worth comparing.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Confidence goes roughly as 1/√N, so at ten differences the figure carries something like a
    /// third of its own value in uncertainty. The row is still shown — hiding the long-τ end would
    /// misrepresent what the series covers — but it is marked, because the alternative is a reader
    /// comparing a well-founded number against a noisy one and seeing a trend that is not there.
    /// </para>
    /// <para>
    /// <b>It rarely fires, and that is the cap doing its job.</b>
    /// <see cref="AllanDeviation.AveragingFactors"/> stops at N/4, so the longest τ still averages
    /// about N/2 differences — a series has to be shorter than about twenty samples before any row
    /// qualifies. This is the backstop for that case rather than the main defence.
    /// </para>
    /// </remarks>
    public bool IsSparse => Point is AllanPoint point && point.Pairs < 10;
}

/// <summary>
/// P2-3's Allan deviation over the persisted time-interval series (#63).
/// </summary>
/// <remarks>
/// <para>
/// <b>The gap-aware estimator, always.</b> `trend.db` follows the poll schedule rather than a
/// clock, and this session alone put two multi-minute holes in it. Feeding that to an estimator
/// that assumes a uniform τ₀ does not degrade gracefully: a run either side of a gap is treated as
/// adjacent, so the second difference across it is a fiction, and the resulting σ is wrong by
/// whatever the gap was — silently, and in the direction of looking worse.
/// </para>
/// <para>
/// <b>τ₀ is measured, not assumed.</b> <see cref="AllanDeviation.NominalInterval"/> takes the median
/// step, which survives the gaps a mean would be dragged by. §7.3's poll cadence is a target rather
/// than a guarantee, so reading it from the data is the only honest starting point.
/// </para>
/// <para>
/// This is presentation over an existing computation: the estimator and its fourteen tests came
/// with the earlier half of #63.
/// </para>
/// </remarks>
public sealed class StabilityViewModel : INotifyPropertyChanged
{
    private readonly TrendStore _store;
    private readonly TimeProvider _time;

    private TimeSpan _window = TimeSpan.FromHours(24);
    private int _sampleCount;
    private double? _tau0;

    /// <summary>Creates the view model over a trend store.</summary>
    public StabilityViewModel(TrendStore store, TimeProvider timeProvider)
    {
        _store = store;
        _time = timeProvider;
        Rows = [];
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The stability curve, shortest τ first.</summary>
    public ObservableCollection<StabilityRow> Rows { get; }

    /// <summary>How far back the series is read.</summary>
    public TimeSpan Window
    {
        get => _window;
        set
        {
            _window = value;
            Raise(nameof(Window));
        }
    }

    /// <summary>The sampling interval the series was found to have, in seconds.</summary>
    public double? NominalIntervalSeconds => _tau0;

    /// <summary>Whether there is a curve to show.</summary>
    public bool HasCurve => Rows.Count > 0;

    /// <summary>
    /// What the table rests on, or why it is empty.
    /// </summary>
    /// <remarks>
    /// §9.11: an empty state says what will appear there. "No data" alone reads as a failure to
    /// read it, when the truth is usually that the receiver has not been logged for long enough
    /// yet — which resolves itself and needs no action.
    /// </remarks>
    public string Summary
    {
        get
        {
            if (_sampleCount == 0)
            {
                return "No time-interval samples have been logged in this window yet. The trend "
                    + "store fills as the receiver is polled.";
            }

            if (_tau0 is not double tau0)
            {
                return $"{_sampleCount:N0} samples, but their spacing is too irregular to establish "
                    + "a sampling interval. Allan deviation needs one.";
            }

            return Rows.Count == 0
                ? $"{_sampleCount:N0} samples at about {tau0:N1} s, which is too few to average over "
                  + "any averaging time. Four samples are the minimum for the shortest τ."
                : $"{_sampleCount:N0} samples at about {tau0:N1} s.";
        }
    }

    /// <summary>
    /// Recomputes the curve from the persisted series.
    /// </summary>
    /// <remarks>
    /// The time interval is stored in nanoseconds and the estimator works in seconds, because σ is
    /// dimensionless and the τ that divides it is in seconds — mixing the two scales the answer by
    /// 10⁹ and still produces a plausible-looking number.
    /// </remarks>
    public void Refresh()
    {
        DateTimeOffset now = _time.GetUtcNow();
        long from = now.Subtract(Window).UtcTicks;

        IReadOnlyList<TrendSample> series = _store.ReadSeries(from, now.UtcTicks, r => r.TimeIntervalNanoseconds);
        _sampleCount = series.Count;

        double[] phase = new double[series.Count];
        double[] seconds = new double[series.Count];
        for (int i = 0; i < series.Count; i++)
        {
            phase[i] = series[i].Value * 1e-9;
            seconds[i] = (double)series[i].Ticks / TimeSpan.TicksPerSecond;
        }

        _tau0 = AllanDeviation.NominalInterval(seconds);

        Rows.Clear();
        if (_tau0 is double tau0)
        {
            foreach (int m in AllanDeviation.AveragingFactors(series.Count))
            {
                AllanPoint? point = AllanDeviation.Estimate(phase, seconds, tau0, m);

                // A τ with no estimate is dropped rather than shown empty. Unlike a field the
                // receiver declined to answer, this is not a hole in the data — it is a τ this
                // series cannot speak to at all, and a row of dashes would imply otherwise.
                if (point is not null)
                {
                    Rows.Add(new StabilityRow(m * tau0, point));
                }
            }
        }

        Raise(nameof(NominalIntervalSeconds));
        Raise(nameof(HasCurve));
        Raise(nameof(Summary));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
