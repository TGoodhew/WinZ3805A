using System.Collections.Frozen;

namespace WinZ3805A.Device.Commands;

/// <summary>
/// Every SCPI command the application is permitted to send (§8.1).
/// </summary>
/// <remarks>
/// <para>
/// <b>This is an allowlist.</b> Every string the application emits originates here, and there is
/// no code path that builds one from arbitrary user input. The commands §8.4 excludes are not in
/// this catalog at all — not as entries carrying a flag, not as data of any kind. That is what
/// makes them unreachable rather than merely discouraged, which is goal G4.
/// </para>
/// <para>
/// Built once at type initialisation into <see cref="FrozenDictionary{TKey,TValue}"/> and
/// <see cref="FrozenSet{T}"/> per §6.4. The point is not only lookup speed on a collection read
/// constantly and written never: freezing makes the allowlist's immutability structural rather
/// than a convention someone can quietly break.
/// </para>
/// <para>
/// Mnemonics are spelled exactly as §8.2 and §8.3 spell them. Where those sections distinguish two
/// commands by a fixed keyword argument — adopting a surveyed position against restoring the last
/// held one — the argument is part of the mnemonic, because §8.3 gives each its own consequence
/// text and they are two different things to confirm.
/// </para>
/// </remarks>
public static class CommandCatalog
{
    /// <summary>Every catalogued command: tier S, tier C, and the §8.5 opt-in queries.</summary>
    public static IReadOnlyList<ScpiCommand> All { get; }

    /// <summary>The tier S commands — safe to run on click (§8.2).</summary>
    public static IReadOnlyList<ScpiCommand> Safe { get; }

    /// <summary>The tier C commands — each needs its §8.3 confirmation first.</summary>
    public static IReadOnlyList<ScpiCommand> Confirm { get; }

    /// <summary>
    /// The §8.5 undocumented read-only queries: off by default, shown only when the user opts in,
    /// and run only on an explicit click — never on a poll timer.
    /// </summary>
    public static IReadOnlyList<ScpiCommand> Experimental { get; }

    /// <summary>Indexed by long form and short form alike, both upper-cased.</summary>
    private static readonly FrozenDictionary<string, ScpiCommand> ByName;

    static CommandCatalog()
    {
        List<ScpiCommand> all = [.. BuildSafe(), .. BuildConfirm(), .. BuildExperimental()];

        All = all.AsReadOnly();
        Safe = all.Where(c => c.Tier == SafetyTier.Safe && !c.IsExperimental).ToList().AsReadOnly();
        Confirm = all.Where(c => c.Tier == SafetyTier.Confirm).ToList().AsReadOnly();
        Experimental = all.Where(c => c.IsExperimental).ToList().AsReadOnly();

        Dictionary<string, ScpiCommand> index = new(StringComparer.OrdinalIgnoreCase);
        foreach (ScpiCommand command in all)
        {
            index[command.Mnemonic] = command;
            index[command.ShortForm] = command;
        }

        ByName = index.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Finds a catalogued command by either of its forms, or returns <see langword="null"/>.
    /// </summary>
    /// <remarks>
    /// A null answer means "not in the allowlist", which covers a typo, an unsupported model's
    /// command, and an excluded one alike. Callers must not infer which: reporting that a
    /// particular unknown string was excluded would name it, and §8.4 forbids that. Use
    /// <see cref="IsBlocked"/> only where the Advanced Console needs to log the attempt.
    /// </remarks>
    public static ScpiCommand? Find(string? mnemonic) =>
        mnemonic is not null && ByName.TryGetValue(Header(mnemonic), out ScpiCommand? command) ? command : null;

    /// <summary>True when <paramref name="mnemonic"/> names a catalogued command.</summary>
    public static bool Contains(string? mnemonic) => Find(mnemonic) is not null;

    /// <summary>
    /// True when a user-typed string is one the §8.4 exclusions cover.
    /// </summary>
    /// <remarks>
    /// The single caller is the Advanced Console's validator, which rejects the string and logs
    /// that an excluded command was attempted — without echoing what it was. This is the only
    /// route out of the assembly for the exclusion list, and it answers one bool about one
    /// candidate precisely so that nothing can enumerate, bind to, or display the list itself.
    /// </remarks>
    public static bool IsBlocked(string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) && BlockedCommands.Matches(Header(candidate));

    /// <summary>
    /// Strips a parameter off a candidate, leaving the command header — except where the mnemonic
    /// legitimately carries a fixed keyword, which the catalog's own entries do.
    /// </summary>
    private static string Header(string candidate)
    {
        string trimmed = candidate.Trim();
        return ByName is not null && ByName.ContainsKey(trimmed)
            ? trimmed
            : trimmed.Split(' ', StringSplitOptions.RemoveEmptyEntries) is [string first, ..] ? first : trimmed;
    }

    // ===========================================================================================
    // Tier S — §8.2
    // ===========================================================================================

    private static List<ScpiCommand> BuildSafe()
    {
        List<ScpiCommand> commands =
        [
            // ---- IEEE 488.2 common commands ----------------------------------------------
            Query("*IDN?", "Identify", "Reads the manufacturer, model, serial number, and firmware revision.", ResponseFormat.Text),
            Action("*CLS", "Clear status", "Clears the event registers and the error queue."),
            Query("*ESE?", "Event enable mask", "Reads the standard event status enable mask.", ResponseFormat.Integer),
            Query("*ESR?", "Event status", "Reads and clears the standard event status register.", ResponseFormat.Integer),
            Query("*SRE?", "Service request mask", "Reads the service request enable mask.", ResponseFormat.Integer),
            Query("*STB?", "Status byte", "Reads the status byte summary register.", ResponseFormat.Integer),

            // ---- System ------------------------------------------------------------------
            Query(":SYST:STAT?", "Full status screen", "Reads the complete status screen, the only source for the satellite table.", ResponseFormat.StatusScreen),
            Query(":SYST:STAT:LENG?", "Status screen length", "Reads how many lines the status screen occupies.", ResponseFormat.Integer),
            Query(":SYST:ERR?", "Next error", "Reads and removes the oldest entry from the error queue.", ResponseFormat.ValueList),
            Query(":SYST:DATE?", "Receiver date", "Reads the receiver's date as year, month, and day.", ResponseFormat.IntegerList),
            Query(":SYST:TIME?", "Receiver time", "Reads the receiver's time as hours, minutes, and seconds.", ResponseFormat.IntegerList),
            Query(":SYST:COMM?", "Serial settings", "Reads the current serial port configuration.", ResponseFormat.ValueList),

            // ---- Synchronization ---------------------------------------------------------
            Query(":SYNC:STAT?", "Sync state", "Reads the SmartClock disciplining state.", ResponseFormat.Keyword),
            Query(":SYNC:FFOM?", "Frequency figure of merit", "Reads the frequency figure of merit; lower is better.", ResponseFormat.Integer),
            Query(":SYNC:TFOM?", "Time figure of merit", "Reads the time figure of merit; lower is better.", ResponseFormat.Integer),
            Query(":SYNC:TINT?", "1 PPS time interval", "Reads the measured offset between the receiver's 1 PPS and GPS.", ResponseFormat.Decimal),
            Query(":SYNC:HOLD:DUR?", "Holdover duration", "Reads how long the receiver has been in holdover.", ResponseFormat.ValueList),
            Query(":SYNC:HOLD:DUR:THR?", "Holdover threshold", "Reads the holdover duration threshold.", ResponseFormat.Decimal),
            Query(":SYNC:HOLD:DUR:THR:EXC?", "Threshold exceeded", "Reads whether holdover has run past its threshold.", ResponseFormat.Boolean),
            Query(":SYNC:HOLD:TUNC:PRED?", "Predicted uncertainty", "Reads the predicted time uncertainty for the coming holdover interval.", ResponseFormat.Decimal),
            Query(":SYNC:HOLD:TUNC:PRES?", "Present uncertainty", "Reads the time uncertainty accumulated so far in holdover.", ResponseFormat.Decimal),
            Query(":SYNC:HOLD:WAIT?", "Holdover wait state", "Reads whether the receiver is waiting before entering holdover.", ResponseFormat.Boolean),

            // The two §8.2 actions. Classed Safe because they move the unit toward lock, which is
            // the state the user wants, and neither can damage anything.
            Action(":SYNC:HOLD:REC:INIT", "Recover from holdover", "Starts recovery from holdover and returns the receiver toward lock."),
            Action(":SYNC:HOLD:REC:LIM:IGN", "Ignore recovery limit", "Continues recovery past the usual limit instead of stopping."),

            // ---- GPS reference and position ----------------------------------------------
            Query(":GPS:REF:VAL?", "Reference valid", "Reads whether the GPS reference is currently valid.", ResponseFormat.Boolean),
            Query(":GPS:REF:ADEL?", "Antenna delay", "Reads the configured antenna cable delay.", ResponseFormat.Decimal),
            Query(":GPS:POS?", "Position", "Reads the position the receiver is using for its timing solution.", ResponseFormat.ValueList),
            Query(":GPS:POS:ACT?", "Actual position", "Reads the position currently computed from satellites.", ResponseFormat.ValueList),
            Query(":GPS:POS:HOLD:LAST?", "Last held position", "Reads the last position the receiver held.", ResponseFormat.ValueList),
            Query(":GPS:POS:HOLD:STAT?", "Position hold state", "Reads whether a fixed position is being held.", ResponseFormat.Keyword),
            Query(":GPS:POS:SURV:PROG?", "Survey progress", "Reads how far a position survey has progressed.", ResponseFormat.Integer),
            Query(":GPS:POS:SURV:STAT?", "Survey state", "Reads whether a position survey is running.", ResponseFormat.Keyword),
            Query(":GPS:POS:SURV:STAT:POW?", "Survey at power-up", "Reads whether a survey starts automatically at power-up.", ResponseFormat.Boolean),

            // ---- Satellites ---------------------------------------------------------------
            Query(":GPS:SAT:TRAC?", "Tracked satellites", "Reads the PRNs of the satellites being tracked.", ResponseFormat.ValueList),
            Query(":GPS:SAT:TRAC:COUN?", "Tracked count", "Reads how many satellites are being tracked.", ResponseFormat.Integer),
            Query(":GPS:SAT:TRAC:EMAN?", "Elevation mask", "Reads the elevation below which satellites are ignored.", ResponseFormat.Decimal),
            Query(":GPS:SAT:TRAC:IGN?", "Excluded satellites", "Reads the PRNs excluded from tracking.", ResponseFormat.ValueList),
            Query(":GPS:SAT:TRAC:IGN:COUN?", "Excluded count", "Reads how many satellites are excluded from tracking.", ResponseFormat.Integer),
            Query(":GPS:SAT:TRAC:IGN:STAT?", "Is satellite excluded", "Reads whether one satellite is excluded from tracking.", ResponseFormat.Boolean, Prn()),
            Query(":GPS:SAT:TRAC:INCL?", "Included satellites", "Reads the PRNs on the tracking inclusion list.", ResponseFormat.ValueList),
            Query(":GPS:SAT:TRAC:INCL:COUN?", "Included count", "Reads how many satellites are on the inclusion list.", ResponseFormat.Integer),
            Query(":GPS:SAT:TRAC:INCL:STAT?", "Is satellite included", "Reads whether one satellite is on the inclusion list.", ResponseFormat.Boolean, Prn()),
            Query(":GPS:SAT:VIS:PRED?", "Predicted satellites", "Reads the PRNs the receiver expects to be visible.", ResponseFormat.ValueList),
            Query(":GPS:SAT:VIS:PRED:COUN?", "Predicted count", "Reads how many satellites are expected to be visible.", ResponseFormat.Integer),

            // ---- Precision time ------------------------------------------------------------
            Query(":PTIM:TCOD?", "Time code", "Reads the current time code output.", ResponseFormat.Text),
            Query(":PTIM:DATE?", "Date", "Reads the date on the selected time scale.", ResponseFormat.IntegerList),
            Query(":PTIM:TIME?", "Time", "Reads the time on the selected time scale.", ResponseFormat.IntegerList),
            Query(":PTIM:TIME:STR?", "Time as text", "Reads the time already formatted as a string.", ResponseFormat.Text),
            Query(":PTIM:TZON?", "Time zone", "Reads the configured time zone offset.", ResponseFormat.IntegerList),
            Query(":PTIM:LEAP:ACC?", "Leap second accumulated", "Reads the accumulated difference between GPS time and UTC.", ResponseFormat.Integer),
            Query(":PTIM:LEAP:DATE?", "Leap second date", "Reads the date of the announced leap second. Answers only while one is announced; rejected with E-230 otherwise.", ResponseFormat.IntegerList),
            Query(":PTIM:LEAP:DUR?", "Leap second direction", "Reads whether the announced leap second is added or removed. Answers only while one is announced; rejected with E-230 otherwise.", ResponseFormat.Integer),
            Query(":PTIM:LEAP:STAT?", "Leap second pending", "Reads whether a leap second is currently announced.", ResponseFormat.Keyword),

            // ---- Front panel indicators -----------------------------------------------------
            Query(":LED:ALAR?", "Alarm lamp", "Reads the state of the alarm indicator.", ResponseFormat.Keyword),
            Query(":LED:GPSL?", "GPS lock lamp", "Reads the state of the GPS lock indicator.", ResponseFormat.Keyword),
            Query(":LED:HOLD?", "Holdover lamp", "Reads the state of the holdover indicator.", ResponseFormat.Keyword),

            // ---- Diagnostics -----------------------------------------------------------------
            Query(":DIAG:ROSC:EFC:REL?", "Oscillator control voltage", "Reads the oscillator's electronic frequency control as a relative value.", ResponseFormat.Decimal),
            Query(":DIAG:LIF:COUN?", "Power-on hours", "Reads the receiver's accumulated running time.", ResponseFormat.Integer),
            Query(":DIAG:QUER:RESP?", "Query response test", "Reads a fixed response, used to prove the link is alive.", ResponseFormat.Text),
            Query(":DIAG:LOG:COUN?", "Log entry count", "Reads how many entries the diagnostic log holds.", ResponseFormat.Integer),
            Query(":DIAG:LOG:READ?", "Read log entry", "Reads one diagnostic log entry, or the whole log when no entry is given.", ResponseFormat.MultiLine,
                new ParameterSpec("Entry", ParameterKind.Integer, Minimum: 0, IsOptional: true)),
            Query(":DIAG:LOG:READ:ALL?", "Read whole log", "Reads every entry in the diagnostic log.", ResponseFormat.MultiLine),
            Query(":DIAG:TEST:RES?", "Self-test result", "Reads the result of the last self-test.", ResponseFormat.ValueList),
        ];

        // The status registers are a regular grid — five register groups, each with the same five
        // readable fields — so they are built rather than typed out twenty-five times. Anything
        // hand-written at that size drifts in whichever corner nothing reads.
        foreach ((string node, string label) in StatusRegisters)
        {
            foreach ((string field, string fieldLabel, string fieldDescription, ResponseFormat format) in ReadableRegisterFields)
            {
                commands.Add(Query(
                    $":STAT:{node}:{field}?",
                    $"{label} — {fieldLabel}",
                    $"Reads the {label.ToLowerInvariant()} register's {fieldDescription}.",
                    format));
            }
        }

        return commands;
    }

    // ===========================================================================================
    // Tier C — §8.3. Confirmation text is reproduced from that table verbatim.
    // ===========================================================================================

    private static List<ScpiCommand> BuildConfirm()
    {
        List<ScpiCommand> commands =
        [
            NeedsConfirmation(":SYST:PRESet", "Reset to factory defaults",
                "Restores every receiver setting to its factory value.",
                "Reset all receiver settings to factory defaults? Antenna delay, position, elevation mask, and satellite selections will be lost. Serial port settings are not affected.",
                "Reset the receiver to factory defaults.",
                acknowledge: true),

            NeedsConfirmation(":SYST:COMM:SER1:PRESet", "Restore serial defaults",
                "Returns the serial port to its factory configuration.",
                "Restore serial port to factory defaults (9600-8-N-1)? The connection will drop and reconnect.",
                "Restored the serial port to 9600-8-N-1."),

            NeedsConfirmation(":GPS:REF:ADELay", "Set antenna delay",
                "Sets the compensation for the delay through the antenna cable.",
                "Set antenna delay to {0} ns? Changing this while locked can push the receiver into holdover.",
                "Set the antenna delay to {0} ns.",
                // Nanoseconds, not the seconds the receiver takes on the wire. §8.3's own
                // confirmation text is written in ns ("Set antenna delay to {0} ns?") and §10.7's
                // field is labelled 0 - 999 999 ns, so the parameter describes what the user
                // enters and the caller scales it. See ParameterSpec.Unit.
                parameters: [new ParameterSpec("Delay", ParameterKind.Decimal, Unit: "ns", Minimum: 0, Maximum: 999999)]),

            NeedsConfirmation(":GPS:POSition", "Set fixed position",
                "Sets the antenna position the receiver uses for all timing solutions.",
                "Set fixed antenna position? This cancels any survey in progress and the receiver will use these coordinates for all timing solutions. An incorrect position degrades timing accuracy.",
                "Set the fixed antenna position.",
                parameters: [new ParameterSpec("Position", ParameterKind.Coordinates)]),

            NeedsConfirmation(":GPS:POSition LAST", "Restore last position",
                "Cancels a survey and returns to the previously held position.",
                "Cancel survey and restore the last held position?",
                "Restored the last held position."),

            NeedsConfirmation(":GPS:POSition SURVey", "Adopt surveyed position",
                "Ends the survey and holds the average position it computed.",
                "Stop surveying and adopt the computed average position?",
                "Adopted the surveyed position."),

            NeedsConfirmation(":GPS:POSition:SURVey:STATe ONCE", "Start position survey",
                "Starts a single position survey.",
                "Start a position survey? This takes approximately two hours with four or more satellites tracked.",
                "Started the position survey. This usually takes about two hours."),

            NeedsConfirmation(":GPS:POS:SURV:STAT:POWerup", "Survey at power-up",
                "Sets whether a survey starts automatically when the receiver powers up.",
                "Change power-up behaviour?",
                "Changed the power-up survey behaviour to {0}.",
                parameters: [Switch("State")]),

            NeedsConfirmation(":GPS:SAT:TRAC:EMANgle", "Set elevation mask",
                "Sets the elevation below which satellites are ignored.",
                "Set elevation mask to {0}°? Values above 15° during survey may prevent position determination; above 40° severely limits availability.",
                "Set the elevation mask to {0}°.",
                parameters: [new ParameterSpec("Angle", ParameterKind.Decimal, Unit: "°", Minimum: 0, Maximum: 90)]),

            NeedsConfirmation(":GPS:SAT:TRAC:IGNore", "Exclude satellites",
                "Excludes the selected satellites from tracking.",
                "Exclude the selected satellites from tracking?",
                "Excluded satellites {0} from tracking.",
                parameters: [new ParameterSpec("Satellites", ParameterKind.PrnList)]),

            NeedsConfirmation(":GPS:SAT:TRAC:IGNore ALL", "Exclude all satellites",
                "Excludes every satellite from tracking.",
                "Exclude all satellites? The receiver will lose lock and enter holdover.",
                "Excluded every satellite from tracking.",
                acknowledge: true),

            NeedsConfirmation(":GPS:SAT:TRAC:IGNore NONE", "Exclude no satellites",
                "Clears the exclusion list so no satellite is excluded.",
                "Exclude the selected satellites from tracking?",
                "Cleared the exclusion list."),

            NeedsConfirmation(":GPS:SAT:TRAC:INCLude", "Include satellites",
                "Sets which satellites are on the tracking inclusion list.",
                "Update the tracking inclusion list?",
                "Set the inclusion list to satellites {0}.",
                parameters: [new ParameterSpec("Satellites", ParameterKind.PrnList)]),

            NeedsConfirmation(":GPS:SAT:TRAC:INCLude ALL", "Include all satellites",
                "Puts every satellite on the tracking inclusion list.",
                "Update the tracking inclusion list?",
                "Put every satellite on the inclusion list."),

            NeedsConfirmation(":GPS:SAT:TRAC:INCLude NONE", "Include no satellites",
                "Empties the tracking inclusion list.",
                "Update the tracking inclusion list?",
                "Emptied the inclusion list.",
                acknowledge: true),

            NeedsConfirmation(":SYNC:HOLDover:INITiate", "Force holdover",
                "Stops disciplining to GPS until recovery is started explicitly.",
                "Force manual holdover? The receiver will stop disciplining to GPS until you explicitly recover. Do not do this within the first 24 hours after power-up — it corrupts SmartClock oscillator learning.",
                "Forced holdover. The receiver will not discipline to GPS until you recover it.",
                acknowledge: true),

            NeedsConfirmation(":SYNC:HOLD:DUR:THReshold", "Set holdover threshold",
                "Sets how long holdover may run before it is reported as exceeded.",
                "Set holdover threshold?",
                "Set the holdover threshold to {0} s.",
                parameters: [new ParameterSpec("Threshold", ParameterKind.Decimal, Unit: "s", Minimum: 0)]),

            NeedsConfirmation(":SYNC:IMMediate", "Force resynchronisation",
                "Resynchronises immediately rather than steering gradually.",
                "Force immediate resynchronisation? This causes a step change in the 1 PPS output.",
                "Forced an immediate resynchronisation."),

            NeedsConfirmation(":PTIM:TZONe", "Set time zone",
                "Sets the offset applied to every reported time.",
                "Change time zone offset? All reported times change, including the timecode output.",
                "Set the time zone offset the receiver reports in to {0}.",
                parameters:
                [
                    new ParameterSpec("Hours", ParameterKind.Integer, Unit: "h", Minimum: -12, Maximum: 13),
                    new ParameterSpec("Minutes", ParameterKind.Integer, Unit: "min", Minimum: -59, Maximum: 59),
                ]),

            NeedsConfirmation(":DIAG:LOG:CLEar", "Clear diagnostic log",
                "Deletes every entry in the diagnostic log.",
                "Clear the diagnostic log? This cannot be undone.",
                "Cleared the diagnostic log."),

            NeedsConfirmation(":STAT:PRESet:ALARm", "Reset alarm masks",
                "Returns the alarm masks to their default values.",
                "Reset alarm masks to defaults?",
                "Reset the alarm masks to their defaults."),

            NeedsConfirmation(":STAT:QUES:COND:USER", "Set user status bit",
                "Sets or clears the user-defined bit in the questionable condition register.",
                "Change user-defined questionable status bit?",
                "Set the user-defined questionable status bit to {0}.",
                parameters: [new ParameterSpec("Action", ParameterKind.Keyword, Choices: ["SET", "CLEar"])]),

            NeedsConfirmation(":STAT:QUES:EVEN:USER", "Set user event bit",
                "Chooses which transition sets the user-defined questionable event bit.",
                "Change user-defined questionable status bit?",
                "Set the user-defined questionable event bit to trigger on {0}.",
                parameters: [new ParameterSpec("Transition", ParameterKind.Keyword, Choices: ["PTR", "NTR"])]),

            NeedsConfirmation("*ESE", "Set event enable mask",
                "Sets the standard event status enable mask.",
                "Change event/service-request enable mask?",
                "Set the event enable mask to {0}.",
                parameters: [Mask()]),

            NeedsConfirmation("*SRE", "Set service request mask",
                "Sets the service request enable mask.",
                "Change event/service-request enable mask?",
                "Set the service request enable mask to {0}.",
                parameters: [Mask()]),

            NeedsConfirmation("*TST?", "Run self-test",
                "Runs the receiver's built-in self-test.",
                "Run receiver self-test? This takes up to 30 seconds and may briefly interrupt normal operation.",
                "Ran the receiver self-test.",
                isQuery: true, format: ResponseFormat.Integer),

            NeedsConfirmation(":DIAG:TEST?", "Run subsystem diagnostic",
                "Runs the diagnostic for one subsystem.",
                "Run {0} diagnostic? This may briefly interrupt normal operation.",
                "Ran the {0} diagnostic.",
                parameters: [new ParameterSpec("Subsystem", ParameterKind.Keyword)],
                isQuery: true, format: ResponseFormat.ValueList),
        ];

        // The serial-line setters all carry one consequence, because they all have the same one:
        // the link drops under the application and may not come back.
        const string SerialConfirmation =
            "Change serial port settings? The connection will drop and the app will attempt to reconnect with the new settings. " +
            "These persist through power cycling — if reconnection fails you will need to try each setting manually.";

        foreach ((string node, string label, string description, ParameterSpec parameter) in SerialSettings)
        {
            commands.Add(NeedsConfirmation(
                $":SYST:COMM:SER1:{node}",
                label,
                description,
                SerialConfirmation,
                $"Changed the serial port {label.ToLowerInvariant()}. Reconnecting.",
                parameters: [parameter]));
        }

        // Acquisition aids, which share a consequence and differ only in what they seed.
        foreach ((string node, string label, string description, ParameterKind kind) in AcquisitionAids)
        {
            commands.Add(NeedsConfirmation(
                $":GPS:INIT:{node}",
                label,
                description,
                "Send initial acquisition aid? Only valid before the first satellite is tracked; the receiver will return error −221 otherwise.",
                $"Sent the {label.ToLowerInvariant()} acquisition aid.",
                parameters: [new ParameterSpec(label, kind)]));
        }

        // The writable half of the status-register grid built in BuildSafe.
        foreach ((string node, string label) in StatusRegisters)
        {
            foreach ((string field, string fieldLabel) in WritableRegisterFields)
            {
                commands.Add(NeedsConfirmation(
                    $":STAT:{node}:{field}",
                    $"{label} — set {fieldLabel}",
                    $"Sets the {label.ToLowerInvariant()} register's {fieldLabel} mask.",
                    "Change status register mask?",
                    $"Set the {label.ToLowerInvariant()} register's {fieldLabel} mask to {{0}}.",
                    parameters: [Mask()]));
            }
        }

        return commands;
    }

    // ===========================================================================================
    // §8.5 — undocumented, read-only, opt-in
    // ===========================================================================================

    private static List<ScpiCommand> BuildExperimental() =>
    [
        OptInQuery(":DIAG:ROSC:EFC:ABSolute?", "Oscillator control, absolute", "Reads the oscillator's electronic frequency control as an absolute value."),
        OptInQuery(":DIAG:ROSC:EFC:TCOefficient?", "Oscillator temperature coefficient", "Reads the oscillator's temperature coefficient."),
        OptInQuery(":SYST:STAT:SLOG?", "Status log", "Reads an undocumented status log."),
        OptInQuery(":DIAG:STACk?", "Stack", "Reads firmware stack information."),
        OptInQuery(":DIAG:PROCess?", "Processes", "Reads firmware process information."),
        OptInQuery(":DIAG:MEMory?", "Memory", "Reads firmware memory information."),
    ];

    // ===========================================================================================
    // Shared data and factories
    // ===========================================================================================

    /// <summary>The five status register groups §8.2 and §8.3 both address.</summary>
    private static readonly (string Node, string Label)[] StatusRegisters =
    [
        ("OPER", "Operation"),
        ("OPER:HARD", "Hardware"),
        ("OPER:HOLD", "Holdover"),
        ("OPER:POW", "Power"),
        ("QUES", "Questionable"),
    ];

    private static readonly (string Field, string Label, string Description, ResponseFormat Format)[] ReadableRegisterFields =
    [
        ("COND", "condition", "present condition", ResponseFormat.Integer),
        ("EVEN", "events", "latched events, clearing them", ResponseFormat.Integer),
        ("ENAB", "enable mask", "enable mask", ResponseFormat.Integer),
        ("NTR", "negative transitions", "negative transition mask", ResponseFormat.Integer),
        ("PTR", "positive transitions", "positive transition mask", ResponseFormat.Integer),
    ];

    private static readonly (string Field, string Label)[] WritableRegisterFields =
    [
        ("ENABle", "enable"),
        ("NTRansition", "negative transition"),
        ("PTRansition", "positive transition"),
    ];

    private static readonly (string Node, string Label, string Description, ParameterSpec Parameter)[] SerialSettings =
    [
        ("BAUD", "Set baud rate", "Sets the serial line speed.",
            new ParameterSpec("Baud", ParameterKind.Integer, Choices: ["1200", "2400", "9600", "19200"])),
        ("BITS", "Set data bits", "Sets the number of data bits per character.",
            new ParameterSpec("Data bits", ParameterKind.Integer, Minimum: 7, Maximum: 8)),
        ("PARity", "Set parity", "Sets the parity scheme.",
            new ParameterSpec("Parity", ParameterKind.Keyword, Choices: ["NONE", "EVEN", "ODD"])),
        ("SBITs", "Set stop bits", "Sets the number of stop bits.",
            new ParameterSpec("Stop bits", ParameterKind.Integer, Minimum: 1, Maximum: 2)),
        ("PACE", "Set flow control", "Sets the flow control scheme.",
            new ParameterSpec("Pacing", ParameterKind.Keyword, Choices: ["NONE", "XON"])),
        ("FDUPlex", "Set echo", "Sets whether the receiver echoes what it is sent.",
            Switch("Echo")),
    ];

    private static readonly (string Node, string Label, string Description, ParameterKind Kind)[] AcquisitionAids =
    [
        ("DATE", "Initial date", "Seeds the receiver's date to speed first acquisition.", ParameterKind.DateParts),
        ("TIME", "Initial time", "Seeds the receiver's time to speed first acquisition.", ParameterKind.TimeParts),
        ("POSition", "Initial position", "Seeds an approximate position to speed first acquisition.", ParameterKind.Coordinates),
    ];

    private static ParameterSpec Prn() =>
        new("PRN", ParameterKind.Integer, Minimum: 1, Maximum: 32);

    private static ParameterSpec Mask() =>
        new("Mask", ParameterKind.Integer, Minimum: 0, Maximum: 65535);

    private static ParameterSpec Switch(string name) =>
        new(name, ParameterKind.Keyword, Choices: ["ON", "OFF"]);

    private static ScpiCommand Query(
        string mnemonic,
        string displayName,
        string description,
        ResponseFormat format,
        params ParameterSpec[] parameters) =>
        new(mnemonic, ScpiCommand.ToShortForm(mnemonic), SafetyTier.Safe, IsQuery: true,
            displayName, description, parameters, format);

    private static ScpiCommand Action(string mnemonic, string displayName, string description) =>
        new(mnemonic, ScpiCommand.ToShortForm(mnemonic), SafetyTier.Safe, IsQuery: false,
            displayName, description, [], ResponseFormat.None);

    private static ScpiCommand OptInQuery(string mnemonic, string displayName, string description) =>
        new(mnemonic, ScpiCommand.ToShortForm(mnemonic), SafetyTier.Safe, IsQuery: true,
            displayName, description, [], ResponseFormat.Text, IsExperimental: true);

    private static ScpiCommand NeedsConfirmation(
        string mnemonic,
        string displayName,
        string description,
        string confirmationText,
        string successText,
        IReadOnlyList<ParameterSpec>? parameters = null,
        bool acknowledge = false,
        bool isQuery = false,
        ResponseFormat format = ResponseFormat.None) =>
        new(mnemonic, ScpiCommand.ToShortForm(mnemonic), SafetyTier.Confirm, isQuery,
            displayName, description, parameters ?? [], format, confirmationText, successText, acknowledge);
}
