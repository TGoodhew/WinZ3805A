using System.Globalization;

namespace WinZ3805A.Simulation.SmartClock;

/// <summary>
/// The receiver's side of the RS-232 conversation: one command line in, the bytes it answers with out.
/// </summary>
/// <remarks>
/// <para>
/// <b>Written against <c>docs/requirements.md</c> §7.2, which was itself corrected against the bench
/// unit (#78).</b> The rules that matter most, because a client that gets them wrong cannot connect:
/// </para>
/// <list type="bullet">
/// <item><description>No echo by default. The manual says the receiver echoes; the bench unit does not.
/// <see cref="Echo"/> turns it on, as <c>:SYST:COMM:SER1:FDUP ON</c> does.</description></item>
/// <item><description>The prompt is <c>scpi &gt; </c>, or <c>E-nnn&gt; </c> naming the <i>newest</i> queued
/// error while the queue is not empty — whatever the last command did. <c>:SYST:ERR?</c> reads the
/// <i>oldest</i>.</description></item>
/// <item><description>A refused command answers with the prompt and nothing else.</description></item>
/// <item><description>Opening the port announces the identity unasked, and the first command after it
/// is lost to a framing error, <c>-362</c>.</description></item>
/// </list>
/// <para>
/// <b>It answers only what it knows, and it knows only documented commands.</b> Its table is built
/// from the catalogue in §8.2, §8.3 and the manual, never from a list of anything excluded: a command
/// it has no entry for gets <c>-113, "Undefined header"</c>, which is what the receiver says to
/// anything it does not recognise. The §8.5 undocumented queries get the same, because that is what
/// the bench unit answered (§8.5).
/// </para>
/// <para>
/// Formats are the bench unit's where it has shown one — the README beside this file lists which —
/// and the manual's, or a labelled guess, where it has not.
/// </para>
/// </remarks>
public sealed class ScpiEngine
{
    /// <summary>What the receiver says for each error it can queue (SCPI-99 wording).</summary>
    private static readonly Dictionary<int, string> ErrorText = new()
    {
        [-100] = "Command error",
        [-108] = "Parameter not allowed",
        [-109] = "Missing parameter",
        [-113] = "Undefined header",
        [-221] = "Settings conflict",
        [-222] = "Data out of range",
        [-224] = "Illegal parameter value",
        [-230] = "Data corrupt or stale",
        [-300] = "Device-specific error",
        [-350] = "Queue overflow",
        [-362] = "Framing error in program message",
    };

    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;

    private const string GpsIdentity =
        "\"--\",\"SFTW P/N # 4850266\",\"SOFTWARE VER # 005\",\"--\",\"--\",\"MODEL # FURUNO GT-80\",\"--\",\"--\",\"--\",\"--\"";

    private static readonly string[] Subsystems =
        ["ALL", "DISPlay", "PROCessor", "RAM", "EEPROM", "UART", "QSPI", "FPGA", "INTerpolator", "IREFerence", "GPS", "POWer"];

    private readonly SimulatedReceiver _receiver;
    private readonly TimeProvider _clock;
    private readonly List<(string Pattern, Func<Command, Result> Handler)> _table = [];
    private readonly Queue<int> _errors = new();
    private int _newestError;
    private bool _glitchPending;
    private string _lastTest = "+0,ALL";
    private Result? _lastBody;

    // Masks set over the wire. The condition registers that move with the state come from the
    // receiver instead (OperationCondition and its siblings).
    private readonly Dictionary<string, int> _registers = new(StringComparer.OrdinalIgnoreCase);

    private int _eventEnable;

    // The bench unit's service request mask, 2 Oct 2026. Whether it is the factory value is not known.
    private int _serviceEnable = 136;

    /// <summary>Creates the engine for one receiver.</summary>
    public ScpiEngine(SimulatedReceiver receiver, TimeProvider clock)
    {
        ArgumentNullException.ThrowIfNull(receiver);
        ArgumentNullException.ThrowIfNull(clock);
        _receiver = receiver;
        _clock = clock;
        Log = new DiagnosticLog(clock);
        receiver.ModeChanged += OnModeChanged;
        Build();
    }

    /// <summary>Whether the receiver echoes each command before answering. Off on the bench unit.</summary>
    public bool Echo { get; set; }

    /// <summary>
    /// Whether a value reply starts with a space, as §7.2 recorded (<c>:SYNC:TFOM?</c> answering
    /// <c>␣+3</c>).
    /// </summary>
    /// <remarks>
    /// Off, because the bench unit does not do it: compared on 2 Oct 2026, none of 70 replies began
    /// with a space. Kept as a setting so a client can still be shown one; the application trims
    /// either way, so nothing in it could tell the two apart.
    /// </remarks>
    public bool LeadingSpace { get; set; }

    /// <summary>How many errors the queue holds before the newest is replaced by -350.</summary>
    /// <remarks>Never measured on the bench unit; five were read back as five. 30 is a guess.</remarks>
    public int ErrorQueueCapacity { get; set; } = 30;

    /// <summary>The diagnostic log <c>:DIAG:LOG:READ?</c> reads.</summary>
    public DiagnosticLog Log { get; }

    /// <summary>The errors waiting in the queue, oldest first.</summary>
    public IReadOnlyCollection<int> QueuedErrors => _errors;

    /// <summary>The prompt as it stands: <c>scpi &gt; </c> or <c>E-nnn&gt; </c>.</summary>
    public string Prompt => _errors.Count == 0
        ? "scpi > "
        : string.Create(Invariant, $"E{_newestError}> ");

    /// <summary>The port has just been opened and DTR asserted.</summary>
    /// <returns>The banner: the identity and a prompt, sent unasked a moment later.</returns>
    /// <remarks>
    /// The banner's exact bytes were never captured; §7.2 says it is "its identity string and a
    /// prompt". The first command after it is lost to the DTR glitch.
    /// </remarks>
    public Reply Connected()
    {
        _glitchPending = true;
        return new Reply(_receiver.Identity + "\r\n" + Prompt, TimeSpan.FromMilliseconds(600));
    }

    /// <summary>Answers one command line, already stripped of its terminator.</summary>
    /// <returns>What goes back on the wire, or null for a blank line, which the receiver ignores.</returns>
    public Reply? Receive(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        string text = line.Trim();
        if (text.Length == 0 || !_receiver.PoweredOn)
        {
            // A receiver with no power hears nothing and says nothing.
            return null;
        }

        string echo = Echo ? line + "\r\n" : string.Empty;

        if (_receiver.TakeFirstCommand())
        {
            // The first command after power returned was lost on the bench unit: a bare prompt, no
            // data, no error (2 Oct 2026). Not the -362 §7.2 records for opening the port.
            return new Reply(echo + Prompt, TimeSpan.FromMilliseconds(300));
        }

        if (_glitchPending)
        {
            // §7.2: the DTR assertion reached the receiver as a character, so the first command is
            // a framing error and is dropped unexecuted.
            _glitchPending = false;
            Queue(-362);
            return new Reply(echo + Prompt, TimeSpan.FromMilliseconds(20));
        }

        Result result = Execute(text);
        if (result.Error is int error)
        {
            Queue(error);
        }

        string body = result.Body is null ? string.Empty : Shape(result) + "\r\n";
        return new Reply(echo + body + Prompt, result.Delay);
    }

    /// <summary>Puts an error in the queue, replacing the newest with -350 when it is full.</summary>
    public void Queue(int error)
    {
        if (_errors.Count >= ErrorQueueCapacity)
        {
            // SCPI-99: on overflow the most recent entry becomes -350 and the rest are kept.
            List<int> kept = [.. _errors];
            kept[^1] = -350;
            _errors.Clear();
            kept.ForEach(_errors.Enqueue);
            _newestError = -350;
            return;
        }

        _errors.Enqueue(error);
        _newestError = error;
    }

    private string Shape(Result result) =>
        result.IsValue && LeadingSpace ? " " + result.Body : result.Body!;

    private void OnModeChanged(object? sender, ModeChange change)
    {
        // The two messages the bench unit's log has ever shown (DiagnosticLogParserTests).
        if (change.To is ClockMode.Locked && change.From is not ClockMode.Locked)
        {
            Log.Add("GPS lock started");
        }
        else if (change.To is ClockMode.Holdover)
        {
            Log.Add(change.Manual ? "Holdover started, manually initiated" : "Holdover started, not tracking GPS");
        }
    }

    private Result Execute(string text)
    {
        int space = text.IndexOf(' ', StringComparison.Ordinal);
        string header = space < 0 ? text : text[..space];
        string arguments = space < 0 ? string.Empty : text[(space + 1)..].Trim();
        Command command = new(header, arguments);

        foreach ((string pattern, Func<Command, Result> handler) in _table)
        {
            if (Matches(pattern, header))
            {
                Result result = RefusedNow(pattern) ? Result.Fail(-230) : handler(command);
                if (result.Body is not null && !Matches(":DIAGnostic:QUERy:RESPonse?", header))
                {
                    _lastBody = result;
                }

                return result;
            }
        }

        return Result.Fail(-113);
    }

    /// <summary>
    /// The queries the bench unit refused with <c>-230</c> in a state, though it answers them in others
    /// (2 Oct 2026).
    /// </summary>
    /// <remarks>
    /// Through power-up it has no time to give, so every time and date query is refused until lock, as
    /// are the leap count and the predicted uncertainty. Until it has a position it has none to give.
    /// And for the first half minute, while its GPS engine is still starting, it cannot say what that
    /// engine is, what it expects to see or how many it tracks.
    /// </remarks>
    private bool RefusedNow(string pattern)
    {
        string[] untilLock =
        [
            ":SYSTem:DATE?", ":SYSTem:TIME?", ":PTIMe:DATE?", ":PTIMe:TIME?", ":PTIMe:TIME:STRing?",
            ":PTIMe:LEAPsecond:ACCumulated?", ":SYNChronization:HOLDover:TUNCertainty:PREDicted?",
        ];
        string[] untilPositioned = [":GPS:POSition?", ":GPS:POSition:ACTual?"];
        string[] whileBooting =
        [
            ":DIAGnostic:IDENtify:GPS?", ":GPS:SATellite:VISibility:PREDicted?",
            ":GPS:SATellite:VISibility:PREDicted:COUNt?", ":GPS:SATellite:TRACking:COUNt?",
        ];

        return (untilLock.Contains(pattern) && _receiver.InPowerUp)
            || (untilPositioned.Contains(pattern) && _receiver.Acquiring)
            || (whileBooting.Contains(pattern) && _receiver.Booting);
    }

    /// <summary>
    /// Whether a header matches a pattern in the manual's notation: upper-case letters required,
    /// lower-case optional, so <c>:SYNChronization:STATe?</c> accepts <c>:SYNC:STAT?</c> and the long form.
    /// </summary>
    internal static bool Matches(string pattern, string header)
    {
        string[] want = pattern.TrimStart(':').Split(':');
        string[] got = header.TrimStart(':').Split(':');
        if (want.Length != got.Length)
        {
            return false;
        }

        for (int i = 0; i < want.Length; i++)
        {
            string node = want[i];
            string candidate = got[i];
            bool query = node.EndsWith('?');
            if (query != candidate.EndsWith('?'))
            {
                return false;
            }

            node = node.TrimEnd('?');
            candidate = candidate.TrimEnd('?');
            string shortForm = new([.. node.Where(c => !char.IsLower(c))]);
            if (!candidate.Equals(shortForm, StringComparison.OrdinalIgnoreCase) &&
                !candidate.Equals(node, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }
        }

        return true;
    }

    // ===========================================================================================
    // The table
    // ===========================================================================================

    private void On(string pattern, Func<Command, Result> handler) => _table.Add((pattern, handler));

    private void Build()
    {
        SimulatedReceiver r = _receiver;

        // ---- IEEE 488.2 ---------------------------------------------------------------------
        On("*IDN?", _ => Result.Text(r.Identity));
        On("*CLS", _ =>
        {
            _errors.Clear();
            return Result.None(TimeSpan.FromMilliseconds(15));
        });
        On("*ESE?", _ => Result.Value(Int(_eventEnable)));
        On("*ESE", c => Mask(c, v => _eventEnable = v));
        On("*SRE?", _ => Result.Value(Int(_serviceEnable)));
        On("*SRE", c => Mask(c, v => _serviceEnable = v));
        On("*ESR?", _ => Result.Value(Int(0)));
        On("*STB?", _ => Result.Value(Int(_errors.Count > 0 ? 4 : 0)));
        On("*TST?", _ =>
        {
            // Never run on the bench unit. The subsystem tests took the receiver from LOCK to POW
            // (§8.3, #53), and *TST? runs them all, so it does the same.
            r.RestartAfterSelfTest();
            return Result.Value(Int(0), TimeSpan.FromSeconds(12));
        });

        // ---- System -------------------------------------------------------------------------
        On(":SYSTem:STATus?", _ => Result.Text(StatusScreenWriter.Write(r.Snapshot()).TrimEnd('\r', '\n'), r.NextScreenLatency()));
        On(":SYSTem:STATus:LENGth?", _ => Result.Value(Int(23)));
        On(":SYSTem:ERRor?", _ => Result.Value(NextError()));
        On(":SYSTem:DATE?", _ => Result.Value(Date(r.ReportedUtc)));
        On(":SYSTem:TIME?", _ => Result.Value(Time(r.ReportedUtc)));

        // The bench unit names its port rather than describing it (2 Oct 2026).
        On(":SYSTem:COMMunicate?", _ => Result.Value("SER1"));
        On(":SYSTem:PRESet", _ =>
        {
            r.AntennaDelaySeconds = 0;
            r.ElevationMaskDegrees = 10;
            r.Ignored.Clear();
            return Result.None(TimeSpan.FromSeconds(1));
        });
        On(":SYSTem:COMMunicate:SERial1:PRESet", _ =>
        {
            Echo = false;
            return Result.None();
        });
        On(":SYSTem:COMMunicate:SERial1:FDUPlex", c => Switch(c, on => Echo = on));
        foreach (string node in new[] { "BAUD", "BITS", "PARity", "SBITs", "PACE" })
        {
            // Accepted and forgotten: the simulated line keeps the settings it was opened at.
            On($":SYSTem:COMMunicate:SERial1:{node}", c => c.HasArguments ? Result.None() : Result.Fail(-109));
        }

        // ---- Synchronization ----------------------------------------------------------------
        On(":SYNChronization:STATe?", _ => Result.Value(r.SyncState));
        On(":SYNChronization:TFOMerit?", _ => Result.Value(Int(r.Tfom)));
        On(":SYNChronization:FFOMerit?", _ => Result.Value(Int(r.Ffom)));
        On(":SYNChronization:TINTerval?", _ => r.HasTimeInterval ? Result.Value(Short(r.TimeIntervalSeconds)) : Result.Fail(-230));
        On(":SYNChronization:HOLDover:DURation?", _ => Result.Value(Real(r.LastHoldover.TotalSeconds) + "," + (r.InHoldover ? "1" : "0")));
        // "+86400" on the bench unit: an integer, unlike the holdover duration beside it.
        On(":SYNChronization:HOLDover:DURation:THReshold?", _ => Result.Value(
            r.HoldDurationThresholdSeconds == Math.Floor(r.HoldDurationThresholdSeconds)
                ? Int((long)r.HoldDurationThresholdSeconds)
                : Real(r.HoldDurationThresholdSeconds)));
        On(":SYNChronization:HOLDover:DURation:THReshold", c => Number(c, 0, double.MaxValue, v => r.HoldDurationThresholdSeconds = v));
        On(":SYNChronization:HOLDover:DURation:THReshold:EXCeeded?", _ => Result.Value(Bool(r.LastHoldover.TotalSeconds > r.HoldDurationThresholdSeconds)));
        // "+0.8E-006,0" on the bench unit: microseconds to one decimal over a fixed exponent, then a flag.
        On(":SYNChronization:HOLDover:TUNCertainty:PREDicted?", _ => Result.Value(Micro(r.Snapshot().PredictMicroseconds ?? 0) + "," + (r.InHoldover ? "1" : "0")));

        // -221 outside holdover on the bench unit, not the -230 the 58503A guide gives.
        On(":SYNChronization:HOLDover:TUNCertainty:PRESent?", _ => r.Snapshot().PresentMicroseconds is double p && r.InHoldover ? Result.Value(Micro(p)) : Result.Fail(-221));

        // "GPS" through a holdover caused by losing GPS, "NONE" otherwise, a forced one included.
        On(":SYNChronization:HOLDover:WAITing?", _ => Result.Value(r.WaitingFor));
        On(":SYNChronization:HOLDover:INITiate", _ =>
        {
            r.ForceHoldover();
            return Result.None();
        });
        On(":SYNChronization:HOLDover:RECovery:INITiate", _ =>
        {
            r.Recover();
            return Result.None();
        });
        On(":SYNChronization:HOLDover:RECovery:LIMit:IGNore", _ => Result.None());
        On(":SYNChronization:IMMediate", _ => Result.None());

        // ---- GPS reference and position -----------------------------------------------------
        On(":GPS:REFerence:VALid?", _ => Result.Value(Bool(r.ReferenceValid)));
        On(":GPS:REFerence:ADELay?", _ => Result.Value(Real(r.AntennaDelaySeconds)));
        On(":GPS:REFerence:ADELay", c => Number(c, 0, 999_999e-9, v => r.AntennaDelaySeconds = v));
        On(":GPS:POSition?", _ => Result.Value(Position(r.HeldPosition)));
        On(":GPS:POSition:ACTual?", _ => Result.Value(Position(r.HeldPosition)));
        On(":GPS:POSition:HOLD:LAST?", _ => Result.Value(Position(r.HeldPosition)));
        On(":GPS:POSition:HOLD:STATe?", _ => Result.Value(Bool(!r.Surveying)));

        // With no survey running the bench unit refuses this with -221 rather than answer a number.
        // "+1.8" while one runs, to a decimal, and "ONCE" for its state rather than a 1 (2 Oct 2026).
        On(":GPS:POSition:SURVey:PROGress?", _ => r.Surveying
            ? Result.Value((r.SurveyPercent ?? 0).ToString("+0.0;-0.0", Invariant))
            : Result.Fail(-221));
        On(":GPS:POSition:SURVey:STATe?", _ => Result.Value(r.Surveying ? "ONCE" : "0"));
        On(":GPS:POSition:SURVey:STATe:POWerup?", _ => Result.Value(Bool(r.SurveyAtPowerUp)));
        On(":GPS:POSition:SURVey:STATe:POWerup", c => Switch(c, on => r.SurveyAtPowerUp = on));
        On(":GPS:POSition:SURVey:STATe", c =>
        {
            if (!c.Arguments.Equals("ONCE", StringComparison.OrdinalIgnoreCase))
            {
                return Result.Fail(-224);
            }

            // #229: a receiver holding a position refuses with -300, and promptly. Power-up is the
            // way into a survey.
            return r.SurveyPercent is null ? Result.Fail(-300) : Result.None();
        });
        On(":GPS:POSition", SetPosition);

        // ---- Satellites ---------------------------------------------------------------------
        On(":GPS:SATellite:TRACking?", _ => Result.Value(PrnList(r.Tracked.Select(s => s.Prn))));
        On(":GPS:SATellite:TRACking:COUNt?", _ => Result.Value(Int(r.Tracked.Count)));
        On(":GPS:SATellite:TRACking:EMANgle?", _ => Result.Value(Int(r.ElevationMaskDegrees)));
        On(":GPS:SATellite:TRACking:EMANgle", c => Number(c, 0, 90, v => r.ElevationMaskDegrees = (int)Math.Round(v)));
        On(":GPS:SATellite:TRACking:IGNore?", _ => Result.Value(PrnList(r.Ignored)));
        On(":GPS:SATellite:TRACking:IGNore:COUNt?", _ => Result.Value(Int(r.Ignored.Count)));
        On(":GPS:SATellite:TRACking:IGNore:STATe?", c => Prn(c, prn => Result.Value(Bool(r.Ignored.Contains(prn)))));
        On(":GPS:SATellite:TRACking:IGNore", c => Selection(c, prns =>
        {
            r.Ignored.Clear();
            r.Ignored.UnionWith(prns);
        }));

        // On the first line. SatelliteTrackingParser records a blank line before it on 20 Aug 2026; the
        // comparison on 2 Oct 2026 saw none, so that line was most likely the console's.
        On(":GPS:SATellite:TRACking:INCLude?", _ => Result.Value(PrnList(Included())));
        On(":GPS:SATellite:TRACking:INCLude:COUNt?", _ => Result.Value(Int(Included().Count)));
        On(":GPS:SATellite:TRACking:INCLude:STATe?", c => Prn(c, prn => Result.Value(Bool(!r.Ignored.Contains(prn)))));
        On(":GPS:SATellite:TRACking:INCLude", c => Selection(c, prns =>
        {
            r.Ignored.Clear();
            r.Ignored.UnionWith(Enumerable.Range(1, 32).Except(prns));
        }));
        On(":GPS:SATellite:VISibility:PREDicted?", _ => Result.Value(PrnList(Visible())));
        On(":GPS:SATellite:VISibility:PREDicted:COUNt?", _ => Result.Value(Int(Visible().Count())));
        foreach (string aid in new[] { "DATE", "TIME", "POSition" })
        {
            On($":GPS:INITial:{aid}", c => !c.HasArguments ? Result.Fail(-109) : r.Tracked.Count > 0 ? Result.Fail(-221) : Result.None());
        }

        // ---- Precision time -----------------------------------------------------------------
        On(":PTIMe:TCODe?", _ => TimeCode());
        On(":PTIMe:TCODe:FORMat?", _ => Result.Value("F2"));
        On(":PTIMe:DATE?", _ => Result.Value(Date(Local())));
        On(":PTIMe:TIME?", _ => Result.Value(Time(Local()), TimeSpan.FromMilliseconds(40)));
        On(":PTIMe:TIME:STRing?", _ => Result.Value("\"" + Local().ToString("HH:mm:ss", Invariant) + "\""));
        On(":PTIMe:TZONe?", _ => Result.Value(Int(r.TimeZone.Hours) + "," + Int(r.TimeZone.Minutes)));
        On(":PTIMe:TZONe", c =>
        {
            string[] parts = c.Arguments.Split(',', StringSplitOptions.TrimEntries);
            if (parts.Length != 2 ||
                !int.TryParse(parts[0], NumberStyles.AllowLeadingSign, Invariant, out int hours) ||
                !int.TryParse(parts[1], NumberStyles.AllowLeadingSign, Invariant, out int minutes))
            {
                return Result.Fail(c.HasArguments ? -224 : -109);
            }

            r.TimeZone = (hours, minutes);
            return Result.None();
        });
        On(":PTIMe:LEAPsecond:ACCumulated?", _ => Result.Value(Int(SimulatedReceiver.LeapSeconds)));
        On(":PTIMe:LEAPsecond:STATe?", _ => Result.Value("0"));

        // Nothing announced: no answer, E-230 (measured 20 Aug 2026, §10.14). The receiver has no leap second to describe.
        On(":PTIMe:LEAPsecond:DATE?", _ => Result.Fail(-230));
        On(":PTIMe:LEAPsecond:DURation?", _ => Result.Fail(-230));

        // ---- Front panel ----------------------------------------------------------------------
        On(":LED:ALARm?", _ => Result.Value(Bool(!r.Health.AllOk)));
        On(":LED:GPSLock?", _ => Result.Value(Bool(r.GpsLockLamp)));
        On(":LED:HOLDover?", _ => Result.Value(Bool(r.HoldoverLamp)));
        On(":LED:ACTive?", _ => Result.Value(Bool(r.ActiveLamp)));
        On(":LED:ENABled?", _ => Result.Value(Bool(r.EnabledLamp)));

        // A lamp write waits for the receiver's 1 Hz tick: 999 ms measured over ten runs (#440).
        On(":LED:ACTive", c => Switch(c, on => r.ActiveLamp = on, TimeSpan.FromMilliseconds(900)));
        On(":LED:ENABled", c => Switch(c, on => r.EnabledLamp = on, TimeSpan.FromMilliseconds(900)));

        // ---- Diagnostics ----------------------------------------------------------------------
        On(":DIAGnostic:ROSCillator:EFControl:RELative?", _ => Result.Value(Real(r.EfcPercent)));
        On(":DIAGnostic:LIFetime:COUNt?", _ => Result.Value(Int(r.LifetimeHours)));
        On(":DIAGnostic:IDENtify:GPS?", _ => Result.Text(GpsIdentity));

        // Repeats whatever the previous command answered: the GPS engine's identity after
        // :DIAG:IDEN:GPS?, the running hours after :DIAG:LIF:COUN? (2 Oct 2026).
        On(":DIAGnostic:QUERy:RESPonse?", _ => _lastBody ?? Result.Fail(-230));
        On(":DIAGnostic:LOG:COUNt?", _ => Result.Value(Int(Log.Count)));
        On(":DIAGnostic:LOG:READ?", c =>
        {
            if (!c.HasArguments)
            {
                return Result.Text(Log.ReadAll(), Log.ReadTime);
            }

            return int.TryParse(c.Arguments, NumberStyles.Integer, Invariant, out int n) && Log.Read(n) is string entry
                ? Result.Text(entry)
                : Result.Fail(-222);
        });
        On(":DIAGnostic:LOG:READ:ALL?", _ => Result.Text(Log.ReadAll(), Log.ReadTime));
        On(":DIAGnostic:LOG:CLEar", _ =>
        {
            Log.Clear();
            return Result.None();
        });
        On(":DIAGnostic:TEST:RESult?", _ => Result.Value(_lastTest));
        On(":DIAGnostic:TEST?", c =>
        {
            string? subsystem = Subsystems.FirstOrDefault(s => Matches(s, c.Arguments.Trim()));
            if (subsystem is null)
            {
                // "ZZNOSUCH" answered -224 at once and ran nothing (#404).
                return Result.Fail(c.HasArguments ? -224 : -109);
            }

            string name = new([.. subsystem.Where(ch => !char.IsLower(ch))]);
            _lastTest = "+0," + name;

            // Every subsystem test took the receiver out of lock (§8.3, #53). Durations measured:
            // ALL 12.4 s, GPS 11.6 s, the rest 2.4 to 5.4 s.
            r.RestartAfterSelfTest();
            TimeSpan took = name switch
            {
                "ALL" => TimeSpan.FromSeconds(12.4),
                "GPS" => TimeSpan.FromSeconds(11.6),
                _ => TimeSpan.FromSeconds(3.5),
            };
            return Result.Value("+0,+0,+0", took);
        });

        // ---- Status registers -------------------------------------------------------------------
        foreach (string group in new[] { "OPERation", "OPERation:HARDware", "OPERation:HOLDover", "OPERation:POWerup", "QUEStionable" })
        {
            string key = group;
            On($":STATus:{group}:CONDition?", _ => Result.Value(Int(key switch
            {
                // The three that moved with the state on 2 Oct 2026; the others read 0 throughout.
                "OPERation" => r.OperationCondition,
                "OPERation:HOLDover" => r.HoldoverCondition,
                "OPERation:POWerup" => r.PowerCondition,
                _ => Register(key, "COND"),
            })));
            On($":STATus:{group}:EVENt?", _ => Result.Value(Int(Register(key, "EVEN"))));
            foreach (string field in new[] { "ENABle", "NTRansition", "PTRansition" })
            {
                string name = field;
                On($":STATus:{group}:{field}?", _ => Result.Value(Int(Register(key, name))));
                On($":STATus:{group}:{field}", c => Mask(c, v => _registers[key + ":" + name] = v));
            }
        }

        On(":STATus:PRESet:ALARm", _ => Result.None());
        On(":STATus:QUEStionable:CONDition:USER", c => Choice(c, "SET", "CLEar"));
        On(":STATus:QUEStionable:EVENt:USER", c => Choice(c, "PTR", "NTR"));
    }

    // ===========================================================================================
    // Handlers that are more than a line
    // ===========================================================================================

    private Result SetPosition(Command command)
    {
        // All three commit or tear down a position and took 9.67 s to a clean prompt (#256).
        TimeSpan took = TimeSpan.FromSeconds(9.67);
        string arguments = command.Arguments;

        if (arguments.Equals("LAST", StringComparison.OrdinalIgnoreCase))
        {
            _receiver.AdoptSurvey();
            return Result.None(took);
        }

        if (Matches("SURVey", arguments))
        {
            _receiver.AdoptSurvey();
            return Result.None(took);
        }

        string[] parts = arguments.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length != 9)
        {
            return Result.Fail(arguments.Length == 0 ? -109 : -224);
        }

        if (!TryAngle(parts[0], parts[1], parts[2], parts[3], "N", "S", 90, out double latitude) ||
            !TryAngle(parts[4], parts[5], parts[6], parts[7], "E", "W", 180, out double longitude) ||
            !double.TryParse(parts[8], NumberStyles.Float, Invariant, out double height))
        {
            return Result.Fail(-224);
        }

        _receiver.HeldPosition = _receiver.HeldPosition with { Latitude = latitude, Longitude = longitude, Height = height };
        _receiver.AdoptSurvey();
        return Result.None(took);
    }

    private static bool TryAngle(string hemisphere, string d, string m, string s, string positive, string negative, int limit, out double degrees)
    {
        degrees = 0;
        bool north = hemisphere.Equals(positive, StringComparison.OrdinalIgnoreCase);
        if (!north && !hemisphere.Equals(negative, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!int.TryParse(d, NumberStyles.AllowLeadingSign, Invariant, out int whole) ||
            !int.TryParse(m, NumberStyles.AllowLeadingSign, Invariant, out int minutes) ||
            !double.TryParse(s, NumberStyles.Float, Invariant, out double seconds) ||
            whole < 0 || whole > limit || minutes is < 0 or > 59 || seconds is < 0 or >= 60)
        {
            return false;
        }

        degrees = (whole + (minutes / 60.0) + (seconds / 3600)) * (north ? 1 : -1);
        return true;
    }

    /// <summary>
    /// <c>:PTIM:TCOD?</c>: answered not on demand but on the receiver's tick, about 509 ms before the
    /// 1 PPS it names, in format T2 with its checksum (#37).
    /// </summary>
    private Result TimeCode()
    {
        DateTimeOffset now = _clock.GetUtcNow();
        DateTimeOffset nextSecond = now.AddTicks(TimeSpan.TicksPerSecond - (now.Ticks % TimeSpan.TicksPerSecond));
        DateTimeOffset emit = nextSecond.AddMilliseconds(-509);
        if (emit <= now)
        {
            emit = emit.AddSeconds(1);
            nextSecond = nextSecond.AddSeconds(1);
        }

        DateTime named = (nextSecond - (SimulatedReceiver.Epoch * _receiver.RolloverEpochs)).UtcDateTime;
        bool valid = _receiver.Tracked.Count > 0;
        string body = string.Create(Invariant, $"T2{named:yyyyMMddHHmmss}{_receiver.Tfom}{_receiver.Ffom}00{(valid ? 0 : 1)}");
        int sum = body.Sum(c => c) % 256;
        return Result.Text(body + sum.ToString("X2", Invariant), emit - now);
    }

    private string NextError()
    {
        if (_errors.Count == 0)
        {
            return "+0,\"No error\"";
        }

        int error = _errors.Dequeue();
        return Int(error) + ",\"" + ErrorText.GetValueOrDefault(error, "Unknown error") + "\"";
    }

    private int Register(string group, string field) => _registers.GetValueOrDefault(group + ":" + field);

    private DateTime Local() =>
        _receiver.ReportedUtc.AddHours(_receiver.TimeZone.Hours).AddMinutes(_receiver.TimeZone.Minutes);

    private SortedSet<int> Included() => [.. Enumerable.Range(1, 32).Except(_receiver.Ignored)];

    private IEnumerable<int> Visible() =>
        _receiver.Tracked.Select(s => s.Prn).Concat(_receiver.NotTracked.Select(s => s.Prn)).Order();

    // ===========================================================================================
    // Arguments
    // ===========================================================================================

    private static Result Mask(Command command, Action<int> set)
    {
        if (!command.HasArguments)
        {
            return Result.Fail(-109);
        }

        if (!int.TryParse(command.Arguments, NumberStyles.AllowLeadingSign, Invariant, out int value) || value is < 0 or > 65535)
        {
            return Result.Fail(-224);
        }

        set(value);
        return Result.None();
    }

    private static Result Number(Command command, double minimum, double maximum, Action<double> set)
    {
        if (!command.HasArguments)
        {
            return Result.Fail(-109);
        }

        if (!double.TryParse(command.Arguments, NumberStyles.Float, Invariant, out double value))
        {
            return Result.Fail(-224);
        }

        if (value < minimum || value > maximum)
        {
            return Result.Fail(-222);
        }

        set(value);
        return Result.None();
    }

    /// <summary>ON, OFF, 1 or 0 — all four accepted on the lamps (ActivityLamp).</summary>
    private static Result Switch(Command command, Action<bool> set, TimeSpan delay = default)
    {
        string value = command.Arguments.ToUpperInvariant();
        if (value.Length == 0)
        {
            return Result.Fail(-109);
        }

        bool? on = value switch
        {
            "ON" or "1" => true,
            "OFF" or "0" => false,
            _ => null,
        };
        if (on is not bool state)
        {
            return Result.Fail(-224);
        }

        set(state);
        return Result.None(delay);
    }

    private static Result Choice(Command command, params string[] choices) =>
        !command.HasArguments ? Result.Fail(-109)
        : choices.Any(choice => Matches(choice, command.Arguments)) ? Result.None()
        : Result.Fail(-224);

    private static Result Prn(Command command, Func<int, Result> answer) =>
        !command.HasArguments ? Result.Fail(-109)
        : int.TryParse(command.Arguments, NumberStyles.AllowLeadingSign, Invariant, out int prn) && prn is >= 1 and <= 32
            ? answer(prn)
            : Result.Fail(-224);

    private static Result Selection(Command command, Action<IEnumerable<int>> set)
    {
        string value = command.Arguments.Trim();
        if (value.Length == 0)
        {
            return Result.Fail(-109);
        }

        if (value.Equals("ALL", StringComparison.OrdinalIgnoreCase))
        {
            set(Enumerable.Range(1, 32));
            return Result.None();
        }

        if (value.Equals("NONE", StringComparison.OrdinalIgnoreCase))
        {
            set([]);
            return Result.None();
        }

        List<int> prns = [];
        foreach (string part in value.Split(',', StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.AllowLeadingSign, Invariant, out int prn) || prn is < 1 or > 32)
            {
                return Result.Fail(-224);
            }

            prns.Add(prn);
        }

        set(prns);
        return Result.None();
    }

    // ===========================================================================================
    // Formats
    // ===========================================================================================

    /// <summary><c>+3</c>, <c>-113</c>: integers carry their sign (<c>:SYNC:TFOM?</c> → <c>+3</c>).</summary>
    public static string Int(long value) => (value < 0 ? "-" : "+") + Math.Abs(value).ToString(Invariant);

    /// <summary><c>+6.00000E+002</c>: five decimals and a three-digit exponent (<c>:SYNC:HOLD:DUR?</c>).</summary>
    public static string Real(double value)
    {
        string text = value.ToString("0.00000E+000", Invariant);
        return value < 0 ? text : "+" + text;
    }

    /// <summary>
    /// <c>-5.4E-009</c>, <c>-3.56E-008</c>: the time interval at its 0.1 ns resolution, with a mantissa
    /// as long as that needs and no longer.
    /// </summary>
    public static string Short(double value)
    {
        value = Math.Round(value * 1e10) / 1e10;
        string text = value.ToString("0.0##E+000", Invariant);
        return value < 0 ? text : "+" + text;
    }

    /// <summary><c>+0.8E-006</c>: microseconds to one decimal over a fixed exponent, as the uncertainties are.</summary>
    public static string Micro(double microseconds) =>
        (microseconds < 0 ? "-" : "+") + Math.Abs(microseconds).ToString("0.0", Invariant) + "E-006";

    /// <summary>Booleans unsigned: the lamps, the leap state and the survey and hold states all answered <c>0</c> or <c>1</c>.</summary>
    public static string Bool(bool value) => value ? "1" : "0";

    /// <summary><c>+2006,+12,+27</c>: unpadded and signed.</summary>
    private static string Date(DateTime date) => Int(date.Year) + "," + Int(date.Month) + "," + Int(date.Day);

    /// <summary><c>+14,+45,+1</c>: unpadded and signed.</summary>
    private static string Time(DateTime time) => Int(time.Hour) + "," + Int(time.Minute) + "," + Int(time.Second);

    /// <summary>An empty list is <c>+0</c>; otherwise signed PRNs joined by commas.</summary>
    private static string PrnList(IEnumerable<int> prns)
    {
        string joined = string.Join(",", prns.Select(p => Int(p)));
        return joined.Length == 0 ? Int(0) : joined;
    }

    /// <summary>
    /// A position as the manual's nine parts. The bench unit has never been asked for one, so this is
    /// the manual's shape and a guess at the number formats.
    /// </summary>
    private static string Position(PositionPanel position)
    {
        static string Part(double degrees, string positive, string negative)
        {
            double abs = Math.Abs(degrees);
            int whole = (int)abs;
            int minutes = (int)((abs - whole) * 60);
            double seconds = (abs - whole - (minutes / 60.0)) * 3600;
            return (degrees < 0 ? negative : positive) + "," + Int(whole) + "," + Int(minutes) + "," + Real(seconds);
        }

        return Part(position.Latitude, "N", "S") + "," + Part(position.Longitude, "E", "W") + "," + Real(position.Height);
    }

    /// <summary>One parsed command line.</summary>
    private sealed record Command(string Header, string Arguments)
    {
        public bool HasArguments => Arguments.Length > 0;
    }

    /// <summary>What a handler decided.</summary>
    private sealed record Result(string? Body, int? Error, TimeSpan Delay, bool IsValue)
    {
        private static readonly TimeSpan Typical = TimeSpan.FromMilliseconds(30);

        public static Result Value(string body, TimeSpan delay = default) =>
            new(body, null, delay == default ? Typical : delay, IsValue: true);

        public static Result Text(string body, TimeSpan delay = default) =>
            new(body, null, delay == default ? Typical : delay, IsValue: false);

        public static Result None(TimeSpan delay = default) =>
            new(null, null, delay == default ? Typical : delay, IsValue: false);

        public static Result Fail(int error) => new(null, error, Typical, IsValue: false);
    }
}

/// <summary>Bytes for the wire, and how long the receiver takes before it starts sending them.</summary>
/// <param name="Text">The reply, prompt included. Latin-1 on the wire.</param>
/// <param name="Delay">The receiver's own latency, before any wire time at the line rate.</param>
public sealed record Reply(string Text, TimeSpan Delay);

/// <summary>
/// The receiver's diagnostic log, in the two forms the bench unit prints it (2 Oct 2026).
/// </summary>
/// <remarks>
/// <para>
/// <c>:DIAG:LOG:READ:ALL?</c> answers a status line, a blank line, one unquoted entry a line
/// (<c>Log NNN:YYYYMMDD.HH:MM:SS:  message</c>, two spaces after the time), and two blank lines.
/// <c>:DIAG:LOG:READ? n</c> answers one entry <b>quoted, with one space</b>, which is the manual's
/// form. Entries are numbered by position, oldest first, so a full log that overwrites still runs
/// from 001 to 222. Stamps are on the receiver's rolled-over date.
/// </para>
/// <para>
/// Three messages have been seen: <c>GPS lock started</c>, <c>Holdover started, not tracking
/// GPS</c> and <c>Holdover started, temporary</c>. What causes the third is not known, so the
/// simulator never writes it of its own accord.
/// </para>
/// </remarks>
public sealed class DiagnosticLog(TimeProvider clock)
{
    /// <summary>The bench unit's log holds 222 entries.</summary>
    public const int Capacity = 222;

    private readonly List<(DateTime At, string Message)> _entries = [];
    private bool _overwriting;

    /// <summary>How many entries the log holds.</summary>
    public int Count => _entries.Count;

    /// <summary>The receiver's own latency before a full read; the 14 s it takes is wire time.</summary>
    public TimeSpan ReadTime => TimeSpan.FromMilliseconds(200);

    /// <summary>Adds an entry stamped with the receiver's own (rolled-over) clock.</summary>
    public void Add(string message, int rolloverEpochs = 1) =>
        Add(message, (clock.GetUtcNow() - (SimulatedReceiver.Epoch * rolloverEpochs)).UtcDateTime);

    /// <summary>Adds an entry with a given stamp, as a log written before the simulator started has.</summary>
    public void Add(string message, DateTime at)
    {
        _entries.Add((at, message));
        if (_entries.Count > Capacity)
        {
            _entries.RemoveAt(0);
            _overwriting = true;
        }
    }

    /// <summary>Removes every entry.</summary>
    public void Clear()
    {
        _entries.Clear();
        _overwriting = false;
    }

    /// <summary>
    /// Fills the log as the bench unit's is: full and overwriting, lock and holdover alternating,
    /// the newest entry at <paramref name="end"/>.
    /// </summary>
    public void FillLikeTheBenchUnit(DateTime end)
    {
        Clear();
        for (int i = Capacity; i >= 0; i--)
        {
            Add(i % 2 == 0 ? "GPS lock started" : "Holdover started, not tracking GPS", end.AddMinutes(-150 * i));
        }
    }

    /// <summary>One entry by its number, quoted, or null.</summary>
    public string? Read(int number) => number >= 1 && number <= _entries.Count
        ? string.Create(CultureInfo.InvariantCulture, $"\"Log {number:000}:{_entries[number - 1].At:yyyyMMdd.HH:mm:ss}: {_entries[number - 1].Message}\"")
        : null;

    /// <summary>The whole log as <c>:DIAG:LOG:READ:ALL?</c> prints it.</summary>
    public string ReadAll()
    {
        // "Log status: 222 entries (overwriting)" is the only status line seen; the wording for a log
        // that is not full is the obvious guess.
        string status = string.Create(CultureInfo.InvariantCulture, $"Log status: {_entries.Count} entries{(_overwriting ? " (overwriting)" : string.Empty)}");
        IEnumerable<string> lines = _entries.Select((e, i) => string.Create(
            CultureInfo.InvariantCulture, $"Log {i + 1:000}:{e.At:yyyyMMdd.HH:mm:ss}:  {e.Message}"));
        return status + "\r\n\r\n" + string.Join("\r\n", lines) + "\r\n\r\n";
    }
}
