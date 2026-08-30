using System.Collections.Frozen;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// The transaction timeout classes — §7.2's three and the three measured since (the auto-detect
/// probe, the diagnostic log, #256's position commit) — and the mapping from a command to its class.
/// </summary>
/// <remarks>
/// The figures are wire time plus device latency, not guesses: the status screen is ~1900 bytes,
/// which is about two seconds at 9600 baud before the receiver has done any work, and self-test
/// genuinely takes tens of seconds. A single 3 s timeout for everything would make the full poll
/// fail permanently on the slowest link the app supports.
/// </remarks>
public static class TransactionTimeouts
{
    /// <summary>3000 ms — every scalar query and every setter.</summary>
    public static TimeSpan Default { get; } = TimeSpan.FromMilliseconds(3000);

    /// <summary>15000 ms — the full status screen, the one multi-line block in the polling schedule (§7.3).</summary>
    public static TimeSpan StatusScreen { get; } = TimeSpan.FromMilliseconds(15000);

    /// <summary>30000 ms — self-test and the diagnostic test, which exercise hardware before answering.</summary>
    /// <remarks>
    /// Confirmed against the live Z3805A on 28 Aug 2026 (#53), and it is tighter than it looks.
    /// Individual subsystems returned in 2.4–5.4 s and <c>ALL</c> in 12.4 s, but <c>GPS</c> reached
    /// <b>24.0 s</b> — six seconds of headroom. A 15 s class, which would look generous beside every
    /// other figure here, would fail a healthy receiver on the one subsystem most worth testing.
    /// </remarks>
    public static TimeSpan SelfTest { get; } = TimeSpan.FromMilliseconds(30000);

    /// <summary>
    /// 60000 ms — reading the whole diagnostic log, which is far larger than anything else on the wire.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, after the 3 s default timed out against the reference unit.</b> The log holds up
    /// to 222 entries (58503A guide, <c>:DIAG:LOG:COUNt?</c>) and the unit on the bench was full at
    /// exactly that. At roughly 70 bytes an entry that is about 15 kB, or <b>16 seconds</b> at 9600
    /// baud — five times the whole status screen, and the reason this needs a class of its own that
    /// §7.2's three do not provide.
    /// </para>
    /// <para>
    /// 60 s gives that nearly four times over, which covers longer messages — the guide allows 255
    /// characters each — without waiting minutes on a link that has genuinely died. It does not
    /// cover the worst case at 1200 baud, where a full log of maximum-length entries would take
    /// most of nine minutes; that is the same baud-rate assumption already recorded against §7.3,
    /// and no timeout is the right answer to it.
    /// </para>
    /// </remarks>
    public static TimeSpan DiagnosticLog { get; } = TimeSpan.FromMilliseconds(60000);

    /// <summary>
    /// 2000 ms — one transaction of the connect sequence during auto-detect, §10.12's figure. The
    /// synchronise listen, the <c>*CLS</c> it sends (twice when the first is not answered) and the
    /// identity probe each spend one, so a combination at the wrong baud rate costs about 8 s, and
    /// the walk — every registered driver's settings, ten as shipped — well over a minute.
    /// </summary>
    public static TimeSpan AutoDetectProbe { get; } = TimeSpan.FromMilliseconds(2000);

    /// <summary>
    /// 30000 ms — the commands that commit a fixed position, which is real work rather than a write.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Measured, after the 3 s default reported a working command as a failure</b> (#256) — the
    /// same way <see cref="DiagnosticLog"/> was found. Pressing <i>Cancel survey</i> on the bench
    /// unit showed "Couldn't restore last position. The receiver did not answer within 3 seconds",
    /// while the receiver had in fact done it: the survey ended and the held position came back to
    /// the digit. Sending <c>:GPS:POSition LAST</c> directly and timing it gave <b>9.67 s</b> to a
    /// clean prompt.
    /// </para>
    /// <para>
    /// These are not slow because of wire time — the reply is a prompt and nothing else. They are
    /// slow because the receiver tears down an accumulating survey and reloads a stored position
    /// before answering, which is why the whole class is the position-commit commands rather than
    /// the one that was measured.
    /// </para>
    /// <para>
    /// 30 s is three times the measurement, on the same reasoning as <see cref="DiagnosticLog"/>'s
    /// margin: one sample on one unit in one state is enough to prove 3 s wrong and nowhere near
    /// enough to characterise the distribution. The cost of being generous is small — these are
    /// tier C commands the user has just confirmed in a dialog, so waiting is expected, and a false
    /// failure on a command that changed the instrument is far worse than a slow success.
    /// </para>
    /// </remarks>
    public static TimeSpan PositionCommit { get; } = TimeSpan.FromMilliseconds(30000);

    private static readonly FrozenDictionary<string, TimeSpan> s_byCommand = BuildLookup();

    /// <summary>
    /// The timeout class for <paramref name="command"/>, defaulting to <see cref="Default"/>.
    /// </summary>
    /// <remarks>
    /// Matching is exact against every legal SCPI spelling rather than by prefix, because
    /// <c>:SYST:STAT:LENG?</c> is a cheap scalar that shares its first two nodes with the expensive
    /// screen — a prefix match would give it fifteen seconds to hang the poller in.
    /// </remarks>
    public static TimeSpan For(string command)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(command);
        return s_byCommand.TryGetValue(Normalise(command), out TimeSpan timeout) ? timeout : Default;
    }

    /// <summary>Upper-cases, trims, and drops the optional leading colon so all spellings collapse onto one key.</summary>
    private static string Normalise(string command) => command.Trim().ToUpperInvariant().TrimStart(':');

    private static FrozenDictionary<string, TimeSpan> BuildLookup()
    {
        Dictionary<string, TimeSpan> lookup = new(StringComparer.Ordinal);

        AddSpellings(lookup, StatusScreen, ["SYST", "SYSTEM"], ["STAT", "STATUS"]);
        AddSpellings(lookup, SelfTest, ["DIAG", "DIAGNOSTIC"], ["TEST"]);

        // Only the whole-log read. :DIAG:LOG:READ? returns one entry and is a scalar by any measure,
        // so it keeps the default rather than being given a minute to hang in.
        AddSpellings(lookup, DiagnosticLog, ["DIAG", "DIAGNOSTIC"], ["LOG"], ["READ"], ["ALL"]);

        // *TST? is IEEE 488.2 common syntax: no node structure and no long form.
        lookup["*TST?"] = SelfTest;

        // The position-commit commands (#256). Setters, not queries, so they are registered without
        // the trailing "?" that AddSpellings adds - and with their keyword argument, because the
        // lookup is keyed on ScpiCommand.Mnemonic and the catalog spells these as one string each:
        // ":GPS:POSition LAST" and ":GPS:POSition SURVey" are distinct commands, not one command
        // with a parameter.
        //
        // The bare ":GPS:POSition" - the manual setter - is included on reasoning rather than
        // measurement. It commits a position by the same route and would tear down a running survey
        // the same way; it has never been run against hardware, so leaving it at 3 s would mean
        // rediscovering #256 the next time somebody types coordinates in.
        //
        // ":GPS:POSition:SURVey:STATe ONCE" is deliberately NOT here. It answers promptly - observed
        // four times returning -300 well inside the default - and starting an accumulation is not
        // the same work as ending one.
        AddCommandSpellings(lookup, PositionCommit, [["GPS"], ["POS", "POSITION"]], []);
        AddCommandSpellings(lookup, PositionCommit, [["GPS"], ["POS", "POSITION"]], ["LAST"]);
        AddCommandSpellings(lookup, PositionCommit, [["GPS"], ["POS", "POSITION"]], ["SURV", "SURVEY"]);

        return lookup.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Registers a command that is not a query, across node spellings and argument spellings.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="AddSpellings"/> because that one appends the <c>?</c> that makes a
    /// header a query, and these are setters. The argument is part of the key rather than stripped:
    /// the catalog gives each keyword form its own entry with its own §8.3 consequence text, so
    /// <c>:GPS:POSition LAST</c> and <c>:GPS:POSition SURVey</c> are two commands that happen to
    /// share a header, and only the header is shared.
    /// </remarks>
    /// <param name="lookup">The table being built.</param>
    /// <param name="timeout">The class to give every spelling.</param>
    /// <param name="nodes">Each node of the header, with its legal spellings.</param>
    /// <param name="arguments">Spellings of the keyword argument, or empty for none.</param>
    private static void AddCommandSpellings(
        Dictionary<string, TimeSpan> lookup,
        TimeSpan timeout,
        string[][] nodes,
        string[] arguments)
    {
        foreach (string header in Spellings(nodes))
        {
            if (arguments.Length == 0)
            {
                lookup[header] = timeout;
                continue;
            }

            foreach (string argument in arguments)
            {
                lookup[$"{header} {argument}"] = timeout;
            }
        }
    }

    /// <summary>Every combination of the given node spellings, joined with colons.</summary>
    private static List<string> Spellings(string[][] nodes)
    {
        List<string> spellings = [string.Empty];

        foreach (string[] alternatives in nodes)
        {
            List<string> extended = new(spellings.Count * alternatives.Length);
            foreach (string prefix in spellings)
            {
                foreach (string alternative in alternatives)
                {
                    extended.Add(prefix.Length == 0 ? alternative : $"{prefix}:{alternative}");
                }
            }

            spellings = extended;
        }

        return spellings;
    }

    /// <summary>
    /// Adds every combination of short and long node spellings, because SCPI lets a caller mix them
    /// freely — <c>:SYSTem:STAT?</c> is as legal as <c>:SYST:STATus?</c>.
    /// </summary>
    private static void AddSpellings(Dictionary<string, TimeSpan> lookup, TimeSpan timeout, params string[][] nodes)
    {
        foreach (string spelling in Spellings(nodes))
        {
            lookup[$"{spelling}?"] = timeout;
        }
    }
}
