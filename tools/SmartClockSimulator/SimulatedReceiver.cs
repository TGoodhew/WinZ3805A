using System.Globalization;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>
/// The receiver behind the wire: its disciplining state, its sky, its settings and its faults.
/// </summary>
/// <remarks>
/// <para>
/// <b>The states and their order come from the bench unit; their durations do not.</b> The captures
/// under <c>tests/WinZ3805A.Tests/Fixtures/</c> show a Z3805A going through power-up (GPS acquisition,
/// then fine frequency adjustment), locked and stabilizing, locked, holdover with the antenna pulled,
/// holdover with the signal back, and recovery. They show the order and what each state prints. How
/// long each lasts depends on the oscillator, the sky and the antenna, so the durations are
/// <see cref="Timing"/> settings with defaults that make a run watchable, and <see cref="Speed"/> runs
/// the whole timeline faster still.
/// </para>
/// <para>
/// Everything is computed from elapsed time on the injected <see cref="TimeProvider"/>, so a test pins
/// it with a fake clock and a host runs it on the real one. Nothing here touches a port.
/// </para>
/// </remarks>
public sealed class SimulatedReceiver
{
    /// <summary>One GPS week rollover: the bench unit reports dates 1024 weeks behind.</summary>
    public static readonly TimeSpan Epoch = TimeSpan.FromDays(7168);

    /// <summary>GPS time leads UTC by this many seconds (since 2017).</summary>
    public const int LeapSeconds = 18;

    private readonly TimeProvider _clock;
    private readonly Random _noise;
    private readonly List<Orbit> _sky;

    private DateTimeOffset _lastRealTime;
    private TimeSpan _simulatedElapsed;
    private TimeSpan _phaseStarted;
    private Phase _phase = Phase.Acquiring;
    private TimeSpan? _holdoverStarted;
    private TimeSpan _lastHoldover;
    private bool _manualHoldover;
    private double _timeInterval;
    private double _efc = -16.8528;
    private TimeSpan? _surveyStarted;
    private bool _antennaConnected = true;
    private double _speed = 1.0;

    /// <summary>Starts a receiver that has just been powered up.</summary>
    /// <param name="clock">Supplies the real time the simulated time advances from.</param>
    /// <param name="seed">Seeds the noise, so a test sees the same readings every run.</param>
    public SimulatedReceiver(TimeProvider clock, int seed = 3625)
    {
        ArgumentNullException.ThrowIfNull(clock);
        _clock = clock;
        _noise = new Random(seed);
        _lastRealTime = clock.GetUtcNow();
        _sky = Orbit.Constellation(seed);
    }

    /// <summary>What <c>*IDN?</c> answers: the bench unit's own identity.</summary>
    public string Identity { get; set; } = "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A";

    /// <summary>How many times faster than real time the states progress and the sky turns.</summary>
    /// <remarks>The clock the receiver reports stays real; only the timeline speeds up.</remarks>
    public double Speed
    {
        get => _speed;
        set
        {
            Advance();
            _speed = value;
        }
    }

    /// <summary>How long each timed state lasts, in simulated time.</summary>
    public ReceiverTiming Timing { get; set; } = new();

    /// <summary>How many 1024-week epochs behind the reported date is. One on the bench unit.</summary>
    public int RolloverEpochs { get; set; } = 1;

    /// <summary>Whether the antenna is connected. Pull it to cause holdover.</summary>
    /// <remarks>
    /// Time is caught up to now before the change and the receiver reacts at once, so a pull and a
    /// reconnect with nobody asking in between are still two events, not none.
    /// </remarks>
    public bool AntennaConnected
    {
        get => _antennaConnected;
        set
        {
            Advance();
            _antennaConnected = value;
            Step(TimeSpan.Zero);
        }
    }

    /// <summary>The six health items, any of which can be made to fail.</summary>
    public HealthPanel Health { get; set; } = new();

    /// <summary>The antenna cable delay, in seconds as the receiver stores it.</summary>
    public double AntennaDelaySeconds { get; set; } = 77e-9;

    /// <summary>The elevation mask in degrees.</summary>
    public int ElevationMaskDegrees { get; set; } = 10;

    /// <summary>The holdover threshold in seconds.</summary>
    public double HoldThresholdSeconds { get; set; } = 1e-6;

    /// <summary>The holdover duration limit in seconds, the one holdover setting a command changes.</summary>
    public double HoldDurationThresholdSeconds { get; set; } = 86400;

    /// <summary>The time zone offset the receiver applies to local time.</summary>
    public (int Hours, int Minutes) TimeZone { get; set; }

    /// <summary>The held position.</summary>
    public PositionPanel HeldPosition { get; set; } = new()
    {
        Latitude = 47 + (31 / 60.0) + (18.582 / 3600),
        Longitude = -(122 + (12 / 60.0) + (22.092 / 3600)),
        Height = 25.20,
    };

    /// <summary>Whether a survey starts by itself at power-up. On, as the bench unit is set.</summary>
    public bool SurveyAtPowerUp { get; set; } = true;

    /// <summary>Satellites excluded from tracking.</summary>
    public SortedSet<int> Ignored { get; } = [];

    /// <summary>Whether the front-panel Active lamp is lit.</summary>
    public bool ActiveLamp { get; set; }

    /// <summary>Whether the front-panel Enabled lamp is lit.</summary>
    public bool EnabledLamp { get; set; }

    /// <summary>Hours of running time, as <c>:DIAG:LIF:COUN?</c> reports them.</summary>
    public int LifetimeHours { get; set; } = 37_014;

    /// <summary>Raised when the SmartClock mode changes, which is what the diagnostic log records.</summary>
    public event EventHandler<ModeChange>? ModeChanged;

    /// <summary>The mode the receiver is in now.</summary>
    public ClockMode Mode
    {
        get
        {
            Advance();
            return ModeOf(_phase);
        }
    }

    /// <summary>The disciplining state as <c>:SYNC:STAT?</c> spells it.</summary>
    public string SyncState => Mode switch
    {
        ClockMode.Locked => "LOCK",
        ClockMode.Recovery => "REC",
        ClockMode.Holdover => "HOLD",
        _ => "POW",
    };

    /// <summary>Whether the 1 PPS is locked to GPS, which is when <c>:SYNC:TINT?</c> has an answer.</summary>
    public bool HasTimeInterval
    {
        get
        {
            Advance();
            return _phase is Phase.Stabilizing or Phase.Locked or Phase.Recovery;
        }
    }

    /// <summary>The 1 PPS time interval in seconds.</summary>
    public double TimeIntervalSeconds
    {
        get
        {
            Advance();
            return _timeInterval * 1e-9;
        }
    }

    /// <summary>The oscillator control as a percentage of its range.</summary>
    public double EfcPercent
    {
        get
        {
            Advance();
            return _efc;
        }
    }

    /// <summary>The time figure of merit.</summary>
    public int Tfom
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Acquiring or Phase.FineFrequency => 9,
                Phase.Stabilizing when SurveyPercent is not null => 4,
                _ => 3,
            };
        }
    }

    /// <summary>The frequency figure of merit.</summary>
    public int Ffom
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Locked => 0,
                Phase.Stabilizing => 1,
                Phase.Holdover or Phase.SignalBack or Phase.Recovery => 2,
                _ => 3,
            };
        }
    }

    /// <summary>The survey's progress in percent, or null when holding a position.</summary>
    public double? SurveyPercent
    {
        get
        {
            if (_surveyStarted is not TimeSpan started)
            {
                return null;
            }

            double percent = 0.3 + ((_simulatedElapsed - started) / Timing.Survey * 99.7);
            return Math.Min(100, Math.Round(percent, 1));
        }
    }

    /// <summary>How long the present holdover has lasted, including the recovery after it.</summary>
    public TimeSpan? HoldoverDuration
    {
        get
        {
            Advance();
            return _holdoverStarted is TimeSpan started ? _simulatedElapsed - started : null;
        }
    }

    /// <summary>The last holdover's duration, or the present one's while it runs.</summary>
    public TimeSpan LastHoldover => HoldoverDuration ?? _lastHoldover;

    /// <summary>Whether the receiver is in holdover now.</summary>
    public bool InHoldover => Mode == ClockMode.Holdover;

    /// <summary>The time the receiver believes it is, in UTC, before the rollover is applied.</summary>
    public DateTimeOffset UtcNow => _clock.GetUtcNow();

    /// <summary>The date and time the receiver reports: UTC moved back <see cref="RolloverEpochs"/> epochs.</summary>
    public DateTime ReportedUtc => (UtcNow - (Epoch * RolloverEpochs)).UtcDateTime;

    /// <summary>The satellites the receiver is tracking now.</summary>
    public IReadOnlyList<TrackedSatellite> Tracked
    {
        get
        {
            Advance();
            return Sky().Tracked;
        }
    }

    /// <summary>The satellites predicted visible but not tracked.</summary>
    public IReadOnlyList<UntrackedSatellite> NotTracked
    {
        get
        {
            Advance();
            return Sky().NotTracked;
        }
    }

    /// <summary>Starts again from power-up, as a power cycle does.</summary>
    public void PowerCycle()
    {
        Advance();
        _holdoverStarted = null;
        _manualHoldover = false;
        _surveyStarted = null;
        Enter(Phase.Acquiring);
    }

    /// <summary>
    /// Jumps to a settled lock with a held position, as a receiver that has been running for a day is.
    /// </summary>
    /// <remarks>
    /// For runs that care about what comes after lock — a QA pass, a holdover test — and should not
    /// wait out a power-up first.
    /// </remarks>
    public void StartLocked()
    {
        Advance();
        _holdoverStarted = null;
        _manualHoldover = false;
        _surveyStarted = null;
        _simulatedElapsed += TimeSpan.FromDays(1);
        _timeInterval = 1.0;
        Enter(Phase.Locked);
    }

    /// <summary>Forces holdover, as <c>:SYNC:HOLD:INIT</c> does. Stays until <see cref="Recover"/>.</summary>
    public void ForceHoldover()
    {
        Advance();
        if (_phase is Phase.Acquiring or Phase.FineFrequency)
        {
            return;
        }

        _manualHoldover = true;
        StartHoldover();
    }

    /// <summary>Starts recovery from holdover, as <c>:SYNC:HOLD:REC:INIT</c> does.</summary>
    public void Recover()
    {
        Advance();
        _manualHoldover = false;
        if (_phase is Phase.Holdover or Phase.SignalBack && AntennaConnected)
        {
            Enter(Phase.Recovery);
        }
    }

    /// <summary>Starts a survey, as <c>:GPS:POS:SURV:STAT ONCE</c> does.</summary>
    public void StartSurvey()
    {
        Advance();
        _surveyStarted = _simulatedElapsed;
    }

    /// <summary>Ends a survey and holds its average, as <c>:GPS:POS SURV</c> does.</summary>
    public void AdoptSurvey()
    {
        Advance();
        _surveyStarted = null;
    }

    /// <summary>The screen <c>:SYST:STAT?</c> prints right now.</summary>
    public ScreenSnapshot Snapshot()
    {
        Advance();

        bool acquiring = _phase == Phase.Acquiring;
        (IReadOnlyList<TrackedSatellite> tracked, IReadOnlyList<UntrackedSatellite> notTracked) = Sky();
        ClockMode mode = ModeOf(_phase);
        bool gpsValid = tracked.Count > 0;
        DateTime reported = ReportedUtc;
        bool provisional = _phase is Phase.Acquiring or Phase.FineFrequency;
        bool gpsScale = acquiring;

        return new ScreenSnapshot
        {
            Outputs = _phase switch
            {
                Phase.Acquiring or Phase.FineFrequency => OutputsSummary.Invalid,
                Phase.Locked => OutputsSummary.Valid,
                _ => OutputsSummary.ValidReducedAccuracy,
            },
            Mode = mode,
            ModeDetail = _phase switch
            {
                Phase.Acquiring => "GPS acquisition",
                Phase.FineFrequency or Phase.Recovery => "fine freq adj",
                Phase.Stabilizing => "stabilizing frequency",
                Phase.Holdover or Phase.SignalBack => _manualHoldover ? "manually initiated" : "GPS 1PPS invalid",
                _ => null,
            },
            ModeTimeIntervalNanoseconds = _phase is Phase.FineFrequency or Phase.Recovery ? Math.Round(_timeInterval, 1) : null,
            Tfom = Tfom,
            Ffom = Ffom,
            TimeIntervalNanoseconds = HasTimeInterval ? Math.Round(_timeInterval, 1) : null,
            HoldThresholdMicroseconds = HoldThresholdSeconds * 1e6,
            PredictMicroseconds = acquiring || _phase == Phase.FineFrequency ? null : Math.Round(Predict(), 1),
            PresentMicroseconds = _holdoverStarted is null ? null : Math.Round(Present(), 1),
            HoldoverDuration = _holdoverStarted is TimeSpan started ? _simulatedElapsed - started : null,
            GpsOnePpsValid = gpsValid,
            Tracked = tracked,
            NotTracked = notTracked,
            TimeScale = gpsScale ? "GPS" : "UTC",
            Time = gpsScale ? reported.AddSeconds(LeapSeconds) : reported,
            TimeProvisional = provisional,
            ClockAdvisory = gpsValid ? "Synchronized to UTC" : "Invalid: not tracking",

            // During GPS acquisition the bench unit printed ANT DLY 0 ns and ELEV MASK 0 deg, then
            // its real settings from the next screen on: the settings are not shown until the GPS
            // engine is up. Seen once (power-up-gps-acquisition.txt), reproduced as seen.
            AntennaDelayNanoseconds = acquiring ? 0 : (int)Math.Round(AntennaDelaySeconds * 1e9),
            ElevationMaskDegrees = acquiring ? 0 : ElevationMaskDegrees,
            Position = SurveyPercent is double percent
                ? HeldPosition with { SurveyPercent = percent, Height = HeldPosition.Height + 10.6 }
                : HeldPosition,
            Health = Health,
        };
    }

    /// <summary>Moves simulated time on to now and lets the state machine act on it.</summary>
    private void Advance()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        TimeSpan real = now - _lastRealTime;
        _lastRealTime = now;
        if (real <= TimeSpan.Zero)
        {
            return;
        }

        // Step in pieces of at most a simulated second, so the noise and the transitions see the
        // same path whether a caller asks every second or once an hour.
        TimeSpan remaining = real * Speed;
        while (remaining > TimeSpan.Zero)
        {
            TimeSpan step = remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1);
            remaining -= step;
            _simulatedElapsed += step;
            Step(step);
        }
    }

    private void Step(TimeSpan step)
    {
        TimeSpan inPhase = _simulatedElapsed - _phaseStarted;
        double seconds = step.TotalSeconds;
        _efc += (_noise.NextDouble() - 0.5) * 0.0002 * seconds;

        if (!AntennaConnected)
        {
            switch (_phase)
            {
                case Phase.FineFrequency:
                    Enter(Phase.Acquiring);
                    return;
                case Phase.Stabilizing or Phase.Locked or Phase.Recovery:
                    StartHoldover();
                    return;
                case Phase.SignalBack:
                    Enter(Phase.Holdover);
                    return;
            }
        }

        switch (_phase)
        {
            case Phase.Acquiring when AntennaConnected && inPhase >= Timing.Acquisition:
                _timeInterval = 108.5;
                Enter(Phase.FineFrequency);
                if (SurveyAtPowerUp)
                {
                    _surveyStarted = _simulatedElapsed;
                }

                break;

            case Phase.FineFrequency:
                _timeInterval = Decay(_timeInterval, 2.0, seconds);
                if (inPhase >= Timing.FineFrequency)
                {
                    Enter(Phase.Stabilizing);
                }

                break;

            case Phase.Stabilizing:
                _timeInterval = Wander(_timeInterval, 25, seconds);
                if (inPhase >= Timing.Stabilizing)
                {
                    Enter(Phase.Locked);
                }

                break;

            case Phase.Locked:
                // Settled, the bench unit read +2.0 ns on one query and +300 ps on the screen a
                // second later (2 Oct 2026); the captures from August show up to 50 ns.
                _timeInterval = Wander(_timeInterval, 3, seconds);
                break;

            case Phase.Holdover when AntennaConnected:
                Enter(Phase.SignalBack);
                break;

            case Phase.SignalBack when !_manualHoldover && inPhase >= Timing.HoldoverRelease:
                _timeInterval = -17.0;
                Enter(Phase.Recovery);
                break;

            case Phase.Recovery:
                _timeInterval = Decay(_timeInterval, 1.0, seconds);
                if (inPhase >= Timing.Recovery)
                {
                    _lastHoldover = _simulatedElapsed - (_holdoverStarted ?? _simulatedElapsed);
                    _holdoverStarted = null;
                    Enter(Phase.Stabilizing);
                }

                break;
        }

        if (_surveyStarted is not null && SurveyPercent >= 100)
        {
            _surveyStarted = null;
        }
    }

    private void StartHoldover()
    {
        _holdoverStarted ??= _simulatedElapsed;
        Enter(Phase.Holdover);
    }

    private void Enter(Phase phase)
    {
        ClockMode from = ModeOf(_phase);
        _phase = phase;
        _phaseStarted = _simulatedElapsed;

        ClockMode to = ModeOf(phase);
        if (from != to || phase == Phase.Acquiring)
        {
            ModeChanged?.Invoke(this, new ModeChange(from, to, _manualHoldover));
        }
    }

    /// <summary>A random walk held within a band, so a locked TI wanders but stays plausible.</summary>
    private double Wander(double value, double band, double seconds)
    {
        double next = value + ((_noise.NextDouble() - 0.5) * 4 * Math.Sqrt(seconds));
        return Math.Clamp(next, -band, band);
    }

    /// <summary>Settles towards zero by <paramref name="rate"/> nanoseconds a second, with a little noise.</summary>
    private double Decay(double value, double rate, double seconds)
    {
        double step = Math.Min(Math.Abs(value), rate * seconds);
        return value - (Math.Sign(value) * step) + ((_noise.NextDouble() - 0.5) * 0.4);
    }

    /// <summary>
    /// The predicted 24-hour holdover uncertainty, in microseconds: large just after power-up,
    /// falling as the oscillator is learned. The captures show 432.0 two minutes after power-up,
    /// 2.0 to 6.8 within the first hours, and the comparison 0.8 after weeks of running.
    /// </summary>
    private double Predict()
    {
        double learned = Math.Max(0, (_simulatedElapsed - Timing.Acquisition - Timing.FineFrequency).TotalSeconds);
        double settling = (432.0 * Math.Exp(-learned / 300.0)) + (2.0 * Math.Exp(-learned / 21_600.0));
        return 0.8 + settling + (_holdoverStarted is null ? 0 : 3.0);
    }

    /// <summary>The present holdover uncertainty in microseconds, growing with time in holdover.</summary>
    private double Present()
    {
        double hours = (_simulatedElapsed - (_holdoverStarted ?? _simulatedElapsed)).TotalHours;
        return 1.0 + (0.4 * hours * hours);
    }

    /// <summary>Which satellites are tracked and which are only predicted, given the state.</summary>
    private (IReadOnlyList<TrackedSatellite> Tracked, IReadOnlyList<UntrackedSatellite> NotTracked) Sky()
    {
        double t = _simulatedElapsed.TotalSeconds;
        bool canTrack = AntennaConnected && _phase is not Phase.Acquiring && _phase is not Phase.Holdover;

        List<TrackedSatellite> tracked = [];
        List<UntrackedSatellite> notTracked = [];
        foreach (Orbit orbit in _sky)
        {
            (int el, int az) = orbit.At(t);
            if (el <= 0)
            {
                continue;
            }

            if (canTrack && el >= ElevationMaskDegrees + 3 && !Ignored.Contains(orbit.Prn))
            {
                int cn = Math.Clamp(30 + (el / 7) + _noise.Next(-1, 2), 26, 55);
                tracked.Add(new TrackedSatellite(orbit.Prn, el, az, cn));
            }
            else
            {
                // While acquiring, the receiver marks the satellites it is trying for; the bench unit
                // starred about half of them.
                bool attempting = _phase == Phase.Acquiring && AntennaConnected && orbit.Prn % 2 == 0;
                notTracked.Add(new UntrackedSatellite(orbit.Prn, el, az, attempting));
            }
        }

        // Twelve rows is all the screen has for each table.
        return ([.. tracked.Take(12)], [.. notTracked.Take(12)]);
    }

    private static ClockMode ModeOf(Phase phase) => phase switch
    {
        Phase.Stabilizing or Phase.Locked => ClockMode.Locked,
        Phase.Recovery => ClockMode.Recovery,
        Phase.Holdover or Phase.SignalBack => ClockMode.Holdover,
        _ => ClockMode.PowerUp,
    };

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{_phase} ({SyncState}), antenna {(AntennaConnected ? "connected" : "disconnected")}, {Tracked.Count} tracked");

    /// <summary>The steps the bench unit was seen to pass through.</summary>
    private enum Phase
    {
        /// <summary>Power-up: GPS acquisition.</summary>
        Acquiring = 0,

        /// <summary>Power-up: fine freq adj.</summary>
        FineFrequency,

        /// <summary>Locked to GPS: stabilizing frequency.</summary>
        Stabilizing,

        /// <summary>Locked to GPS.</summary>
        Locked,

        /// <summary>Holdover with no GPS.</summary>
        Holdover,

        /// <summary>Holdover with the GPS back and the mode not yet changed.</summary>
        SignalBack,

        /// <summary>Recovery: fine freq adj.</summary>
        Recovery,
    }

    /// <summary>A satellite's pass across the sky, simple enough to compute and plausible to look at.</summary>
    private sealed record Orbit(int Prn, double Phase, double PeakElevation, double Azimuth0)
    {
        /// <summary>Half a sidereal day: a GPS satellite repeats its ground track twice a day.</summary>
        private const double Period = 43_082;

        public (int Elevation, int Azimuth) At(double seconds)
        {
            double angle = Phase + (2 * Math.PI * seconds / Period);
            int elevation = (int)Math.Round(PeakElevation * Math.Sin(angle));
            int azimuth = (int)Math.Round(((Azimuth0 + (seconds / Period * 360)) % 360 + 360) % 360);
            return (elevation, azimuth);
        }

        public static List<Orbit> Constellation(int seed)
        {
            Random random = new(seed);
            int[] prns = [1, 2, 5, 7, 8, 10, 14, 15, 17, 18, 19, 20, 22, 23, 24, 27, 30, 32];
            return [.. prns.Select(prn => new Orbit(
                prn,
                random.NextDouble() * 2 * Math.PI,
                40 + (random.NextDouble() * 48),
                random.NextDouble() * 360))];
        }
    }
}

/// <summary>A change of SmartClock mode.</summary>
/// <param name="From">The mode before.</param>
/// <param name="To">The mode after.</param>
/// <param name="Manual">Whether a holdover was forced by command rather than by losing GPS.</param>
public sealed record ModeChange(ClockMode From, ClockMode To, bool Manual);

/// <summary>How long each timed state lasts, in simulated time.</summary>
/// <remarks>
/// Defaults are chosen so a full power-up runs in a few minutes at normal speed. The bench unit took
/// 22 seconds from acquisition to fine adjustment on a warm restart; a cold start or a long
/// stabilization is much slower, and a run that needs realism sets these.
/// </remarks>
public sealed record ReceiverTiming
{
    /// <summary>Power-up: GPS acquisition, once the antenna is connected.</summary>
    public TimeSpan Acquisition { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Power-up: fine freq adj.</summary>
    public TimeSpan FineFrequency { get; init; } = TimeSpan.FromSeconds(90);

    /// <summary>Locked to GPS: stabilizing frequency.</summary>
    public TimeSpan Stabilizing { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>Holdover with the signal back, before recovery starts.</summary>
    public TimeSpan HoldoverRelease { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Recovery: fine freq adj.</summary>
    public TimeSpan Recovery { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>A position survey from start to finish. The manual says about two hours.</summary>
    public TimeSpan Survey { get; init; } = TimeSpan.FromHours(2);
}
