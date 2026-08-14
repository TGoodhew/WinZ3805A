using System.ComponentModel;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.7 Timing &amp; Antenna page.
/// </summary>
/// <remarks>
/// The calculator computes; it does not apply. <c>:GPS:REF:ADEL</c> is a §8.3 write that can put a
/// locked receiver into holdover, so it needs the confirmation dialogs of §15 step 10. Computing
/// the number is still the useful half — it is the part a user cannot do in their head, and P0-11
/// is about the arithmetic.
/// </remarks>
public sealed class TimingViewModel : INotifyPropertyChanged
{
    private readonly ReceiverStateStore _store;

    private ConnectionStatus _connection = ConnectionStatus.Disconnected;
    private AntennaCable _cable = AntennaCable.Lmr400;
    private double _lengthMetres = 20;
    private double _velocityFactor = 0.85;
    private bool _useVelocityFactor;

    /// <summary>Creates a view model over the shared store.</summary>
    public TimingViewModel(ReceiverStateStore store)
    {
        ArgumentNullException.ThrowIfNull(store);

        _store = store;
        _store.PropertyChanged += (_, _) => RaiseAll();
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Where the session stands.</summary>
    public ConnectionStatus Connection
    {
        get => _connection;
        set
        {
            if (_connection != value)
            {
                _connection = value;
                RaiseAll();
            }
        }
    }

    private ReceiverStatus? Status =>
        Connection == ConnectionStatus.Connected ? _store.Status : null;

    // ---- What the receiver is using -----------------------------------------------------

    /// <summary>The antenna delay the receiver is currently subtracting.</summary>
    public double? CurrentDelayNanoseconds => Status?.AntennaDelayNanoseconds;

    /// <summary>That delay as it is shown.</summary>
    public string CurrentDelayText => CurrentDelayNanoseconds is double delay
        ? $"{ReadoutFormatter.Format(delay, decimalPlaces: 0)}{ReadoutFormatter.HairSpace}ns"
        : ReadoutFormatter.NoValue;

    // ---- The calculator -------------------------------------------------------------------

    /// <summary>The cable presets §10.7 offers.</summary>
    public static IReadOnlyList<AntennaCable> Cables => AntennaCable.Presets;

    /// <summary>The chosen preset.</summary>
    public AntennaCable Cable
    {
        get => _cable;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!ReferenceEquals(_cable, value))
            {
                _cable = value;
                RaiseAll();
            }
        }
    }

    /// <summary>Whether the custom velocity factor is in use rather than a preset.</summary>
    public bool UseVelocityFactor
    {
        get => _useVelocityFactor;
        set
        {
            if (_useVelocityFactor != value)
            {
                _useVelocityFactor = value;
                RaiseAll();
            }
        }
    }

    /// <summary>The custom velocity factor.</summary>
    public double VelocityFactor
    {
        get => _velocityFactor;
        set
        {
            if (!_velocityFactor.Equals(value))
            {
                _velocityFactor = value;
                RaiseAll();
            }
        }
    }

    /// <summary>Cable length in metres.</summary>
    public double LengthMetres
    {
        get => _lengthMetres;
        set
        {
            if (!_lengthMetres.Equals(value))
            {
                _lengthMetres = value;
                RaiseAll();
            }
        }
    }

    /// <summary>The cable the calculation is actually using.</summary>
    public AntennaCable? EffectiveCable => UseVelocityFactor
        ? AntennaCable.FromVelocityFactor(VelocityFactor)
        : Cable;

    /// <summary>The computed delay, or <see langword="null"/> if the inputs do not make one.</summary>
    public double? ComputedDelayNanoseconds => EffectiveCable?.DelayFor(LengthMetres);

    /// <summary>The computed delay as it is shown.</summary>
    public string ComputedDelayText => ComputedDelayNanoseconds is double delay
        ? $"{ReadoutFormatter.Format(delay, decimalPlaces: 1)}{ReadoutFormatter.HairSpace}ns"
        : ReadoutFormatter.NoValue;

    /// <summary>Where the figure behind the calculation came from.</summary>
    public string CableSourceText => EffectiveCable is AntennaCable cable
        ? $"{ReadoutFormatter.Format(cable.DelayNanosecondsPerMetre, 2)} ns/m — {cable.Source}"
        : "Enter a velocity factor between 0 and 1.";

    /// <summary>Whether the receiver would accept the computed delay.</summary>
    /// <remarks>
    /// §10.7 gives the field 0 – 999 999 ns, and §10.6's rule applies here too: reject client-side
    /// rather than letting the device answer with an error the user cannot act on.
    /// </remarks>
    public bool IsComputedDelayAcceptable => AntennaCable.IsAcceptableDelay(ComputedDelayNanoseconds);

    /// <summary>
    /// How far the computed delay is from what the receiver is using.
    /// </summary>
    /// <remarks>
    /// The reason a user opens this page. The receiver subtracts whatever it was told, so a
    /// difference here is a systematic offset of exactly this size sitting on the 1 PPS output,
    /// and nothing downstream will flag it.
    /// </remarks>
    public double? DifferenceNanoseconds =>
        ComputedDelayNanoseconds is double computed && CurrentDelayNanoseconds is double current
            ? computed - current
            : null;

    /// <summary>That difference in words.</summary>
    public string DifferenceText => DifferenceNanoseconds is double difference
        ? $"{ReadoutFormatter.Format(difference, decimalPlaces: 1)}{ReadoutFormatter.HairSpace}ns from what the receiver is using"
        : string.Empty;

    /// <summary>
    /// Whether the difference is worth pointing out.
    /// </summary>
    /// <remarks>
    /// One nanosecond is 30 cm of cable and is inside anyone's measurement of a cable run. Below
    /// that the two agree for every practical purpose, and a caution over 0.4 ns would be noise.
    /// </remarks>
    public bool IsDifferenceSignificant =>
        DifferenceNanoseconds is double difference && Math.Abs(difference) >= 1.0;

    /// <summary>The severity of that difference.</summary>
    public Severity DifferenceSeverity => IsDifferenceSignificant ? Severity.Caution : Severity.Success;

    // ---- 1 PPS time interval ---------------------------------------------------------------

    /// <summary>The current 1 PPS time interval against GPS.</summary>
    public double? TimeIntervalNanoseconds =>
        Connection == ConnectionStatus.Connected ? _store.OnePpsTiNanoseconds : null;

    /// <summary>The 60-sample window the medallion draws.</summary>
    public IReadOnlyList<double?> TimeIntervalSamples => _store.RecentTimeInterval;

    /// <summary>
    /// The standard deviation of the samples held in memory.
    /// </summary>
    /// <remarks>
    /// §10.7's wireframe says "σ (1 h)". Nothing keeps an hour yet — trend persistence is P1-2 and
    /// the ring buffer is the §12 60-sample window — so this is the deviation of what there is, and
    /// <see cref="DeviationWindow"/> says so rather than letting a reader assume an hour.
    /// </remarks>
    public double? TimeIntervalDeviation
    {
        get
        {
            double[] samples = [.. TimeIntervalSamples.OfType<double>()];

            // Two points define a line; a deviation from fewer than three is arithmetic without
            // meaning.
            if (samples.Length < 3)
            {
                return null;
            }

            double mean = samples.Average();
            double sumOfSquares = samples.Sum(sample => (sample - mean) * (sample - mean));

            // Sample standard deviation: these are a sample of the receiver's behaviour, not the
            // whole population of it.
            return Math.Sqrt(sumOfSquares / (samples.Length - 1));
        }
    }

    /// <summary>How many samples that deviation is over.</summary>
    public int DeviationSampleCount => TimeIntervalSamples.Count(sample => sample is not null);

    /// <summary>The window the deviation covers, named honestly.</summary>
    public string DeviationWindow => DeviationSampleCount > 0
        ? $"last {DeviationSampleCount} s"
        : "no samples yet";

    /// <summary>How old the fast-tier readings are.</summary>
    public TimeSpan? Age => _store.AgeOf(_store.LastFastPoll);

    /// <summary>That age in words (§9.11).</summary>
    public string AgeDescription => Staleness.Describe(Age);

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}
