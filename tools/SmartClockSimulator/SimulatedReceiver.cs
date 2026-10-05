using System.Globalization;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>
/// The receiver behind the wire: its disciplining state, its sky, its settings and its faults.
/// </summary>
/// <remarks>
/// <para>
/// <b>The states, their order and what each one answers come from the bench unit; their durations
/// mostly do not.</b> The captures under <c>tests/WinZ3805A.Tests/Fixtures/</c> and the state runs of
/// 2 Oct 2026 (<c>comparisons/states-2026-10-02.md</c>) took a Z3805A through power-up, lock, a forced
/// holdover, a pulled antenna, recovery and two power cycles, and recorded what every read-only query
/// answered in each. How long each state lasts depends on the oscillator, the sky and the antenna, so
/// the durations are <see cref="Timing"/> settings with defaults close to what was seen, and
/// <see cref="Speed"/> runs the timeline faster.
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

    /// <summary>
    /// The receiver's factory initial position, shown before a first fix: 34° 44′ N 135° 21′ E, height
    /// zero. Seen on the second power cycle of 2 Oct 2026.
    /// </summary>
    public static readonly PositionPanel FactoryInitialPosition = new()
    {
        Latitude = 34 + (44 / 60.0),
        Longitude = 135 + (21 / 60.0),
        Height = 0,
        Initial = true,
    };

    private readonly TimeProvider _clock;
    private readonly Random _noise;
    private readonly List<Orbit> _sky;

    private DateTimeOffset _lastRealTime;
    private TimeSpan _simulatedElapsed;
    private TimeSpan _phaseStarted;
    private Phase _phase = Phase.Acquiring;
    private Phase _coastingFrom = Phase.Locked;
    private TimeSpan? _holdoverStarted;
    private TimeSpan _lastHoldover;
    private bool _manualHoldover;
    private double _timeInterval;
    private double _efc = -16.7426;
    private TimeSpan? _surveyStarted;
    private bool _surveyArmed;
    private bool _cold;
    private bool _antennaConnected = true;
    private bool _poweredOn = true;
    // A new simulator is a receiver already in power-up when the link opened, not one whose power has
    // just returned, so it has neither the lost first command nor the slow first screens. PowerOn does.
    private bool _firstCommandPending;
    private int _screensSincePowerUp = 2;
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
        _surveyArmed = SurveyAtPowerUp;
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

    /// <summary>Whether the receiver has power. While it has none it says nothing at all.</summary>
    public bool PoweredOn => _poweredOn;

    /// <summary>The six health items, any of which can be made to fail.</summary>
    public HealthPanel Health { get; set; } = new();

    /// <summary>The antenna cable delay, in seconds as the receiver stores it. 60 ns on the bench unit.</summary>
    public double AntennaDelaySeconds { get; set; } = 60e-9;

    /// <summary>The elevation mask in degrees.</summary>
    public int ElevationMaskDegrees { get; set; } = 10;

    /// <summary>The holdover threshold in seconds.</summary>
    public double HoldThresholdSeconds { get; set; } = 1e-6;

    /// <summary>The holdover duration limit in seconds, the one holdover setting a command changes.</summary>
    public double HoldDurationThresholdSeconds { get; set; } = 86400;

    /// <summary>The time zone offset the receiver applies to local time.</summary>
    public (int Hours, int Minutes) TimeZone { get; set; }

    /// <summary>The held position: where the bench unit was holding on 2 Oct 2026.</summary>
    public PositionPanel HeldPosition { get; set; } = new()
    {
        Latitude = 47 + (31 / 60.0) + (18.546 / 3600),
        Longitude = -(122 + (12 / 60.0) + (22.128 / 3600)),
        Height = 38.00,
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
    public int LifetimeHours { get; set; } = 37_015;

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
    /// <remarks>
    /// <b>Two words for holdover, and the difference is the cause.</b> A forced holdover answers
    /// <c>HOLD</c>. One the receiver fell into because GPS went away answers <c>WAIT</c>, waiting for
    /// GPS to come back, while its screen says <c>Holdover: GPS 1PPS invalid</c> (2 Oct 2026). Through
    /// the minute after the antenna goes, before holdover starts, it still answers <c>LOCK</c>.
    /// </remarks>
    public string SyncState
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Stabilizing or Phase.Locked or Phase.Coasting => "LOCK",
                Phase.Recovery => "REC",
                Phase.Holdover or Phase.SignalBack => _manualHoldover ? "HOLD" : "WAIT",
                _ => "POW",
            };
        }
    }

    /// <summary>What <c>:SYNC:HOLD:WAIT?</c> says holdover is waiting for: <c>GPS</c>, or <c>NONE</c>.</summary>
    public string WaitingFor
    {
        get
        {
            Advance();
            return _phase is Phase.Holdover or Phase.SignalBack && !_manualHoldover ? "GPS" : "NONE";
        }
    }

    /// <summary>Whether the 1 PPS can be measured against GPS, which is when <c>:SYNC:TINT?</c> answers.</summary>
    /// <remarks>
    /// Locked, recovering, and in a forced holdover with the antenna still connected, where GPS is
    /// still there to measure against. After the antenna goes it answers for about 18 seconds more,
    /// then not (2 Oct 2026).
    /// </remarks>
    public bool HasTimeInterval
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Stabilizing or Phase.Locked or Phase.Recovery => true,
                Phase.Coasting => _simulatedElapsed - _phaseStarted < Timing.TimeIntervalAfterLoss,
                Phase.Holdover or Phase.SignalBack => _manualHoldover && AntennaConnected,
                _ => false,
            };
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

    /// <summary>The time figure of merit: 9 in power-up, 4 locked while surveying, otherwise 3.</summary>
    public int Tfom
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Acquiring or Phase.FineFrequency => 9,
                Phase.Stabilizing or Phase.Locked when _surveyStarted is not null => 4,
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
            Phase phase = _phase == Phase.Coasting ? _coastingFrom : _phase;
            return phase switch
            {
                Phase.Locked => 0,
                Phase.Stabilizing => 1,
                Phase.Holdover or Phase.SignalBack or Phase.Recovery => 2,
                _ => 3,
            };
        }
    }

    /// <summary>Whether a survey is running or about to, as <c>:GPS:POS:SURV:STAT?</c> reports it.</summary>
    public bool Surveying
    {
        get
        {
            Advance();
            return _surveyStarted is not null || _surveyArmed;
        }
    }

    /// <summary>The survey's progress in percent, or null when holding a position.</summary>
    /// <remarks>Zero before the first fix of a cold start, when the survey is armed but suspended.</remarks>
    public double? SurveyPercent
    {
        get
        {
            if (_surveyStarted is TimeSpan started)
            {
                double percent = 0.3 + ((_simulatedElapsed - started) / Timing.Survey * 99.7);
                return Math.Min(100, Math.Round(percent, 1));
            }

            return _surveyArmed && _cold && _phase == Phase.Acquiring ? 0 : null;
        }
    }

    /// <summary>Whether the GPS reference is valid, as <c>:GPS:REF:VAL?</c> reports it.</summary>
    public bool ReferenceValid
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Acquiring => false,
                Phase.Holdover => _manualHoldover && AntennaConnected,
                _ => true,
            };
        }
    }

    /// <summary>Whether the receiver is anywhere in power-up, when most of its time queries are refused.</summary>
    public bool InPowerUp
    {
        get
        {
            Advance();
            return _phase is Phase.Acquiring or Phase.FineFrequency;
        }
    }

    /// <summary>Whether the receiver is still acquiring, before it has a position.</summary>
    public bool Acquiring
    {
        get
        {
            Advance();
            return _phase == Phase.Acquiring;
        }
    }

    /// <summary>The first seconds after power-up, while the GPS engine itself is still starting.</summary>
    public bool Booting
    {
        get
        {
            Advance();
            return _phase == Phase.Acquiring && _simulatedElapsed - _phaseStarted < Timing.Boot;
        }
    }

    /// <summary><c>:STAT:OPER:COND?</c> as the bench unit answered it in each state (2 Oct 2026).</summary>
    public int OperationCondition
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Acquiring when _simulatedElapsed - _phaseStarted < Timing.Boot => 64,
                Phase.Acquiring => 65,
                Phase.FineFrequency => 81,
                Phase.Stabilizing or Phase.Locked or Phase.Coasting => _surveyStarted is null ? 90 : 83,
                Phase.Holdover when !_manualHoldover => 72,
                _ => 88,
            };
        }
    }

    /// <summary><c>:STAT:OPER:HOLD:COND?</c>: 1 forced, 2 waiting for GPS, 4 once GPS is back, else 0.</summary>
    public int HoldoverCondition
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Holdover or Phase.SignalBack when _manualHoldover => 1,
                Phase.Holdover => 2,
                Phase.SignalBack or Phase.Recovery => 4,
                _ => 0,
            };
        }
    }

    /// <summary><c>:STAT:OPER:POW:COND?</c>: 2 while booting, 3 through power-up, 7 after.</summary>
    public int PowerCondition
    {
        get
        {
            Advance();
            return _phase switch
            {
                Phase.Acquiring when _simulatedElapsed - _phaseStarted < Timing.Boot => 2,
                Phase.Acquiring or Phase.FineFrequency => 3,
                _ => 7,
            };
        }
    }

    /// <summary>The GPS Lock lamp: lit while locked, including the minute after the antenna goes.</summary>
    public bool GpsLockLamp => Mode == ClockMode.Locked;

    /// <summary>The Holdover lamp: lit from the start of holdover until lock, through recovery.</summary>
    public bool HoldoverLamp
    {
        get
        {
            Advance();
            return _holdoverStarted is not null;
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

    /// <summary>Whether the receiver is in holdover now, forced or not.</summary>
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

    /// <summary>Takes the power away. Nothing is answered until <see cref="PowerOn"/>.</summary>
    public void PowerOff()
    {
        Advance();
        _poweredOn = false;
    }

    /// <summary>Restores power: the receiver starts again from power-up.</summary>
    /// <param name="cold">
    /// Start as the second power cycle of 2 Oct 2026 did, with no position: the factory initial one
    /// shown, a satellite tracked before the almanac places it, the survey suspended, and a long
    /// acquisition. Otherwise as the first did, which found its satellites in about forty seconds.
    /// </param>
    public void PowerOn(bool cold = false)
    {
        Advance();
        _poweredOn = true;
        _holdoverStarted = null;
        _manualHoldover = false;
        _surveyStarted = null;
        _surveyArmed = SurveyAtPowerUp;
        _cold = cold;
        _firstCommandPending = true;
        _screensSincePowerUp = 0;
        Enter(Phase.Acquiring);
    }

    /// <summary>
    /// Back to power-up without losing power, as the subsystem self-tests did (LOCK to POW, #53): no
    /// lost first command and no slow first screen, which belong to a power cycle.
    /// </summary>
    public void RestartAfterSelfTest()
    {
        PowerOn();
        _firstCommandPending = false;
        _screensSincePowerUp = 2;
    }

    /// <summary>Power off and straight back on, as a power cycle does.</summary>
    public void PowerCycle(bool cold = false)
    {
        PowerOff();
        PowerOn(cold);
    }

    /// <summary>
    /// Whether this is the first command since power came back, which the bench unit lost: it answered
    /// a bare <c>scpi &gt; </c> with no data and no error. Asking clears it.
    /// </summary>
    public bool TakeFirstCommand()
    {
        bool first = _firstCommandPending;
        _firstCommandPending = false;
        return first;
    }

    /// <summary>
    /// How long the receiver takes to start a status screen. The first after a power-up took longer
    /// than fifteen seconds and the second 7.3 (2 Oct 2026); after that about 1.2.
    /// </summary>
    public TimeSpan NextScreenLatency() => _screensSincePowerUp++ switch
    {
        0 => Timing.FirstScreen,
        1 => Timing.SecondScreen,
        _ => TimeSpan.FromMilliseconds(1200),
    };

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
        _poweredOn = true;
        _holdoverStarted = null;
        _manualHoldover = false;
        _surveyStarted = null;
        _surveyArmed = false;
        _cold = false;
        _firstCommandPending = false;
        _screensSincePowerUp = 2;
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
            _timeInterval = -14.0;
            Enter(Phase.Recovery);
        }
    }

    /// <summary>Starts a survey, as <c>:GPS:POS:SURV:STAT ONCE</c> does.</summary>
    public void StartSurvey()
    {
        Advance();
        _surveyStarted = _simulatedElapsed;
    }

    /// <summary>Ends a survey and holds its estimate as it stands, as <c>:GPS:POS SURV</c> does.</summary>
    /// <remarks>
    /// The estimate is what the screen shows while surveying: the held position with the survey's
    /// height. Adopting early leaves that partial estimate held; cancelling (<see cref="CancelSurvey"/>)
    /// does not. A guess from the manual and docs/manual-qa.md section 6 - the bench unit's survey has
    /// never been adopted or cancelled (README).
    /// </remarks>
    public void AdoptSurvey()
    {
        Advance();
        if (_surveyStarted is not null && SurveyPercent is not null)
        {
            HeldPosition = HeldPosition with { Height = HeldPosition.Height - SurveyHeightOffset };
        }

        _surveyStarted = null;
        _surveyArmed = false;
    }

    /// <summary>
    /// Ends a survey and goes back to the position held before it, as <c>:GPS:POS LAST</c> does: the
    /// Position page's Cancel survey. The partial estimate is discarded.
    /// </summary>
    public void CancelSurvey()
    {
        Advance();
        _surveyStarted = null;
        _surveyArmed = false;
    }

    /// <summary>How far, in metres, the survey's estimate sits below the held height while it runs.</summary>
    internal const double SurveyHeightOffset = 15.7;

    /// <summary>The screen <c>:SYST:STAT?</c> prints right now.</summary>
    public ScreenSnapshot Snapshot()
    {
        Advance();

        bool acquiring = _phase == Phase.Acquiring;
        (IReadOnlyList<TrackedSatellite> tracked, IReadOnlyList<UntrackedSatellite> notTracked) = Sky();
        bool positioned = !(acquiring && _cold);
        bool gpsValid = tracked.Count > 0 && !acquiring;
        DateTime reported = ReportedUtc;

        // A warm start showed "Invalid: not tracking", ANT DLY 0 ns and ELEV MASK 0 deg until it had
        // satellites (Aug and Oct 2026); a cold one showed its real settings and "inacc position".
        bool warmAcquiring = acquiring && !_cold;

        return new ScreenSnapshot
        {
            Outputs = _phase switch
            {
                Phase.Acquiring or Phase.FineFrequency => OutputsSummary.Invalid,
                Phase.Locked => OutputsSummary.Valid,
                Phase.Coasting when _coastingFrom == Phase.Locked => OutputsSummary.Valid,
                _ => OutputsSummary.ValidReducedAccuracy,
            },
            Mode = ModeOf(_phase),
            ModeDetail = _phase switch
            {
                Phase.Acquiring => "GPS acquisition",
                Phase.FineFrequency or Phase.Recovery => "fine freq adj",
                Phase.Stabilizing => "stabilizing frequency",
                Phase.Coasting when _coastingFrom == Phase.Stabilizing => "stabilizing frequency",
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
            TimeScale = acquiring ? "GPS" : "UTC",
            Time = acquiring ? reported.AddSeconds(LeapSeconds) : reported,
            TimeProvisional = _phase is Phase.Acquiring or Phase.FineFrequency,
            ClockAdvisory = gpsValid ? "Synchronized to UTC"
                : acquiring && _cold && tracked.Count > 0 ? "Invalid: inacc position"
                : "Invalid: not tracking",
            AntennaDelayNanoseconds = warmAcquiring ? 0 : (int)Math.Round(AntennaDelaySeconds * 1e9),
            ElevationMaskDegrees = warmAcquiring ? 0 : ElevationMaskDegrees,
            Position = !positioned
                ? FactoryInitialPosition with
                {
                    SurveyPercent = SurveyPercent,
                    SurveySuspended = SurveyPercent is not null && tracked.Count < 4 ? "track <4 sats" : null,
                }
                : _surveyStarted is not null && SurveyPercent is double percent
                    ? HeldPosition with { SurveyPercent = percent, Height = HeldPosition.Height - SurveyHeightOffset }
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
        if (real <= TimeSpan.Zero || !_poweredOn)
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
                case Phase.Stabilizing or Phase.Locked:
                    // Not holdover yet: for about a minute it goes on saying LOCK with nothing tracked.
                    _coastingFrom = _phase;
                    Enter(Phase.Coasting);
                    return;
                case Phase.Recovery:
                    StartHoldover();
                    return;
                case Phase.SignalBack:
                    Enter(Phase.Holdover);
                    return;
            }
        }

        switch (_phase)
        {
            case Phase.Acquiring when AntennaConnected && inPhase >= (_cold ? Timing.ColdAcquisition : Timing.Acquisition):
                _timeInterval = 160;
                Enter(Phase.FineFrequency);
                if (_surveyArmed)
                {
                    _surveyStarted = _simulatedElapsed;
                    _surveyArmed = false;
                }

                break;

            case Phase.FineFrequency:
                _timeInterval = Decay(_timeInterval, 1.2, seconds);
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

            case Phase.Coasting when AntennaConnected:
                Enter(_coastingFrom);
                break;

            case Phase.Coasting when inPhase >= Timing.CoastBeforeHoldover:
                StartHoldover();
                break;

            case Phase.Holdover when AntennaConnected && !_manualHoldover:
                Enter(Phase.SignalBack);
                break;

            case Phase.SignalBack when !_manualHoldover && inPhase >= Timing.HoldoverRelease:
                _timeInterval = -12.5;
                Enter(Phase.Recovery);
                break;

            case Phase.Recovery:
                _timeInterval = Wander(_timeInterval, 20, seconds);
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
    /// 2.0 to 6.8 within the first hours, and 0.8 after weeks of running, holdover or not.
    /// </summary>
    /// <remarks>
    /// The simulator keeps the two ends: 432.0 while stabilizing straight after a power-up, which both
    /// power cycles of 2 Oct 2026 showed, and 0.8 otherwise, holdover included, as it read all day.
    /// </remarks>
    private double Predict() => _phase == Phase.Stabilizing && _surveyStarted is not null ? 432.0 : 0.8;

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
        bool acquiring = _phase == Phase.Acquiring;
        bool booting = acquiring && _simulatedElapsed - _phaseStarted < Timing.Boot;
        bool canTrack = AntennaConnected && _phase is not Phase.Holdover && _phase is not Phase.Coasting &&
            (!acquiring || (_cold && !booting));

        List<TrackedSatellite> tracked = [];
        List<UntrackedSatellite> notTracked = [];
        foreach (Orbit orbit in _sky)
        {
            (int el, int az) = orbit.At(t);
            if (el <= 0)
            {
                continue;
            }

            // A cold start tracks a few before it has a position, the first one before the almanac
            // places it: "  4  -- ---   --" on the bench unit.
            bool coldLimit = acquiring && _cold && tracked.Count >= 3;
            if (canTrack && !coldLimit && el >= ElevationMaskDegrees + 3 && !Ignored.Contains(orbit.Prn))
            {
                int cn = Math.Clamp(30 + (el / 7) + _noise.Next(-1, 2), 26, 55);
                tracked.Add(acquiring && tracked.Count == 0
                    ? new TrackedSatellite(orbit.Prn, null, null, null)
                    : new TrackedSatellite(orbit.Prn, el, az, cn));
            }
            else
            {
                // While a warm start acquires, it stars the satellites it is trying for; the bench
                // unit starred about half of them.
                bool attempting = acquiring && !_cold && AntennaConnected && orbit.Prn % 2 == 0;
                notTracked.Add(new UntrackedSatellite(orbit.Prn, el, az, attempting));
            }
        }

        // Twelve rows is all the screen has for each table.
        return ([.. tracked.Take(12)], [.. notTracked.Take(12)]);
    }

    private static ClockMode ModeOf(Phase phase) => phase switch
    {
        Phase.Stabilizing or Phase.Locked or Phase.Coasting => ClockMode.Locked,
        Phase.Recovery => ClockMode.Recovery,
        Phase.Holdover or Phase.SignalBack => ClockMode.Holdover,
        _ => ClockMode.PowerUp,
    };

    /// <inheritdoc/>
    public override string ToString() => !_poweredOn
        ? "powered off"
        : string.Create(CultureInfo.InvariantCulture, $"{_phase} ({SyncState}), antenna {(AntennaConnected ? "connected" : "disconnected")}, {Tracked.Count} tracked");

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

        /// <summary>Still saying locked, with the antenna gone and nothing tracked, before holdover.</summary>
        Coasting,

        /// <summary>Holdover: forced, or with no GPS.</summary>
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
            int[] prns = [1, 2, 3, 4, 6, 7, 9, 11, 16, 17, 21, 26, 27, 30, 31];
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
/// The defaults are close to what the bench unit did on 2 Oct 2026 where it was measured, and chosen
/// to keep a run watchable where it was not.
/// </remarks>
public sealed record ReceiverTiming
{
    /// <summary>A warm start's GPS acquisition. About 40 s on the first power cycle of 2 Oct 2026.</summary>
    public TimeSpan Acquisition { get; init; } = TimeSpan.FromSeconds(40);

    /// <summary>A cold start's GPS acquisition, with no position. About 6 minutes on the second.</summary>
    public TimeSpan ColdAcquisition { get; init; } = TimeSpan.FromMinutes(6);

    /// <summary>The first seconds of power-up, while the GPS engine is starting and refuses some queries.</summary>
    public TimeSpan Boot { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Power-up: fine freq adj. About 1 to 2 minutes on the bench unit.</summary>
    public TimeSpan FineFrequency { get; init; } = TimeSpan.FromSeconds(110);

    /// <summary>Locked to GPS: stabilizing frequency. Much longer on hardware; shortened to be watchable.</summary>
    public TimeSpan Stabilizing { get; init; } = TimeSpan.FromSeconds(180);

    /// <summary>How long it goes on saying LOCK after the antenna goes. About 54 s on the bench unit.</summary>
    public TimeSpan CoastBeforeHoldover { get; init; } = TimeSpan.FromSeconds(54);

    /// <summary>How long the time interval goes on answering after the antenna goes. About 18 s.</summary>
    public TimeSpan TimeIntervalAfterLoss { get; init; } = TimeSpan.FromSeconds(18);

    /// <summary>Holdover with the signal back, before recovery starts. About 54 s on the bench unit.</summary>
    public TimeSpan HoldoverRelease { get; init; } = TimeSpan.FromSeconds(54);

    /// <summary>Recovery: fine freq adj. 20 s to a minute on the bench unit.</summary>
    public TimeSpan Recovery { get; init; } = TimeSpan.FromSeconds(55);

    /// <summary>A position survey from start to finish. The manual says about two hours.</summary>
    public TimeSpan Survey { get; init; } = TimeSpan.FromHours(2);

    /// <summary>How long the first status screen after power-up takes to start: over 15 s on the bench unit.</summary>
    public TimeSpan FirstScreen { get; init; } = TimeSpan.FromSeconds(16.5);

    /// <summary>How long the second status screen after power-up takes to start: 7.3 s end to end.</summary>
    public TimeSpan SecondScreen { get; init; } = TimeSpan.FromSeconds(5.3);
}
