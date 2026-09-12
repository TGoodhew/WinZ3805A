using WinZ3805A.Device.Commands;

namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>
/// The UCCM family's allowlist — every command the application may send to one (§8.1, #416).
/// </summary>
/// <remarks>
/// <para>
/// <b>Reads only, and that is a decision rather than an omission.</b> Lady Heather sends several
/// writes to these modules — output enable and disable, the 1 PPS / 2 PPS selector, an EFC data
/// setter and a pull-in range setter. Every one of them needs its own §8 tier ruling, its own
/// §8.3 consequence sentence and its own §9.11 success text, and at least one is plainly
/// consequential: Heather's own comment on the EFC setter is that "this causes a recovery state".
/// </para>
/// <para>
/// <b>Deciding those tiers without hardware would be guessing at consequences.</b> §8.1's catalog
/// is an allowlist, so a command that is not here cannot be sent at all — which makes "leave them
/// out until a receiver can be watched doing it" the safe default rather than a gap. Adding them is
/// a deliberate later change with a tier decision per command, not a fill-in-the-blanks exercise.
/// </para>
/// <para>
/// Mnemonics and their groupings are from Heather's <c>poll_next_uccm()</c> and
/// <c>decode_uccm_msg()</c> (heathgps.cpp), MIT licensed, © 2008-2016 Mark S. Sims. Note the
/// leading colons on the three UCCM-P queries: they are written that way in the reference and are
/// reproduced verbatim rather than normalised, because a mnemonic is a wire fact and we have no
/// hardware to establish that the other spelling is accepted.
/// </para>
/// </remarks>
public static class UccmCommands
{
    /// <summary>The status query, whose reply <see cref="UccmStatusParser"/> reads.</summary>
    public const string Status = "SYST:STAT?";

    /// <summary>The 1 PPS time interval against GPS, in seconds.</summary>
    public const string TimeInterval = "SYNC:TINT?";

    /// <summary>Oscillator control voltage as a relative figure.</summary>
    public const string EfcRelative = "DIAG:ROSC:EFC:REL?";

    /// <summary>The disciplining loop, whose reply shape establishes the vendor (#418).</summary>
    public const string Loop = "DIAG:LOOP?";

    /// <summary>Front-panel GPS lock LED, which doubles as a coarse lock state.</summary>
    public const string LockLed = "LED:GPSL?";

    /// <summary>Holdover duration. UCCM-P only.</summary>
    public const string HoldoverDuration = ":ROSC:HOLD:DUR?";

    /// <summary>Position survey state. UCCM-P only.</summary>
    public const string SurveyState = ":GPS:POS:SURV:STAT?";

    /// <summary>Position survey progress. UCCM-P only.</summary>
    public const string SurveyProgress = ":GPS:POS:SURV:PROG?";

    // ---- Measured present on a Trimble UCCM-P, 13 Sep 2026 (#416) -------------------------------
    //
    // Every one of these is in Heather's poll cycle and was absent from this catalog because nobody
    // had asked the module whether it had them. All eight answered with a value; the census that
    // established it also asked a node known to exist and one known not to, and both behaved, so a
    // blanket "this firmware answers anything" is ruled out.

    /// <summary>Latitude, longitude and height as one composite reply.</summary>
    public const string Position = "GPS:POS?";

    /// <summary>Which satellites are being ignored, or <c>None</c>.</summary>
    public const string IgnoredSatellites = "GPS:SAT:TRAC:IGN?";

    /// <summary>The elevation mask, in degrees.</summary>
    public const string ElevationMask = "GPS:SAT:TRAC:EMAN?";

    /// <summary>The antenna cable delay the receiver is subtracting, in seconds.</summary>
    public const string AntennaDelay = "GPS:REF:ADEL?";

    /// <summary>Which pulse the timing output carries — <c>PP1S</c> or <c>PP2S</c>.</summary>
    public const string PulseSelect = "OUTP:TP:SEL?";

    /// <summary>Whether the outputs are active.</summary>
    public const string OutputState = "OUTP:STAT?";

    /// <summary>
    /// Oscillator control as the raw DAC word, which this firmware answers in hexadecimal.
    /// </summary>
    /// <remarks>
    /// <b>Not the same reading as <see cref="EfcRelative"/>, and not the same units.</b> That one
    /// answers a percentage; this answered <c>0x99387</c> — a 20-bit word written as hex text, which
    /// no other reply in this family does. Anything consuming it has to parse it as hex and know
    /// the full scale, neither of which is established, so it is catalogued and not yet read.
    /// </remarks>
    public const string EfcData = "DIAG:ROSC:EFC:DATA?";

    /// <summary>
    /// The oscillator pull-in range.
    /// </summary>
    /// <remarks>
    /// <b>The spelling is not a mistake.</b> Alone in this catalog it carries no subsystem and no
    /// leading colon: the module answers <c>PULLINRANGE?</c> and its reply is the prose
    /// <c>Pull-in Range:[30 ppb]</c> rather than a bare value. Reproduced exactly as it went on the
    /// wire, for the reason the three UCCM-P queries keep their leading colons.
    /// </remarks>
    public const string PullInRange = "PULLINRANGE?";

    /// <summary>Every command, for a plain UCCM and a UCCM-P alike.</summary>
    /// <remarks>
    /// One catalog rather than one per variant. A UCCM-P answers three queries a plain UCCM does
    /// not, but §8.1's allowlist governs what may be <i>sent</i>, and the poll plan governs what is
    /// actually asked — so the variant shapes the plan below, not this list. A plain UCCM asked one
    /// of the three answers with an undefined-header error, which
    /// <see cref="UccmReply.Classify"/> reads as an error rather than as a value.
    /// </remarks>
    public static IReadOnlyList<ScpiCommand> All { get; } =
    [
        Query(Status, "Status", "The whole status reply: figures of merit, the satellite table, and the time code.", ResponseFormat.MultiLine),
        Query(TimeInterval, "1 PPS time interval", "The offset between the receiver's 1 PPS and GPS, in seconds.", ResponseFormat.Decimal),
        Query(EfcRelative, "Oscillator control", "Electronic frequency control as a relative figure.", ResponseFormat.Decimal),
        Query(Loop, "Disciplining loop", "The loop's own figures. Its format differs by manufacturer, and reading it is how the manufacturer is established.", ResponseFormat.MultiLine),
        Query(LockLed, "Lock indicator", "The front-panel GPS lock lamp, which reports a coarse lock state.", ResponseFormat.Text),
        Query(HoldoverDuration, "Holdover duration", "How long the receiver has been in holdover. UCCM-P only.", ResponseFormat.Decimal),
        Query(SurveyState, "Survey state", "Whether a position survey is running. UCCM-P only.", ResponseFormat.Text),
        Query(SurveyProgress, "Survey progress", "How far a position survey has run, as a percentage. UCCM-P only.", ResponseFormat.Decimal),
        Query("*IDN?", "Identity", "Manufacturer, model, serial number and firmware revision.", ResponseFormat.Text),

        // Measured present, 13 Sep 2026. Catalogued so the Advanced Console can offer them and so
        // Capture-Uccm.ps1 — which reads this list since #482 — asks them on the next sitting.
        // None is in a poll plan: what a receiver ANSWERS and what the application ASKS every
        // second are different decisions, and the second one needs a reason beyond availability.
        Query(Position, "Position", "Latitude, longitude and height, as one composite reply.", ResponseFormat.Text),
        Query(IgnoredSatellites, "Ignored satellites", "Which satellites the receiver is excluding, or None.", ResponseFormat.Text),
        Query(ElevationMask, "Elevation mask", "The elevation below which satellites are not used, in degrees.", ResponseFormat.Text),
        Query(AntennaDelay, "Antenna delay", "The cable delay the receiver is subtracting, in seconds.", ResponseFormat.Decimal),
        Query(PulseSelect, "Pulse select", "Whether the timing output carries one pulse per second or two.", ResponseFormat.Keyword),
        Query(OutputState, "Output state", "Whether the receiver's outputs are active.", ResponseFormat.Keyword),
        Query(EfcData, "Oscillator control word", "Oscillator control as the raw DAC word, which this family answers in hexadecimal.", ResponseFormat.Text),
        Query(PullInRange, "Pull-in range", "How far the oscillator can be steered before the loop gives up.", ResponseFormat.Text),
    ];

    /// <summary>The three queries only a UCCM-P answers.</summary>
    public static IReadOnlyList<string> UccmPOnly { get; } =
        [HoldoverDuration, SurveyState, SurveyProgress];

    /// <summary>Finds a command by mnemonic, case-insensitively.</summary>
    public static ScpiCommand? Find(string? mnemonic)
    {
        string? wanted = mnemonic?.Trim();
        return string.IsNullOrEmpty(wanted)
            ? null
            : All.FirstOrDefault(c => string.Equals(c.Mnemonic, wanted, StringComparison.OrdinalIgnoreCase));
    }

    private static ScpiCommand Query(
        string mnemonic,
        string displayName,
        string description,
        ResponseFormat format) => new(
            Mnemonic: mnemonic,
            ShortForm: mnemonic,
            Tier: SafetyTier.Safe,
            IsQuery: true,
            DisplayName: displayName,
            Description: description,
            Parameters: [],
            ResponseFormat: format);
}
