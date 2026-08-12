using System.Collections.Frozen;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// The three transaction timeout classes of §7.2, and the mapping from a command to its class.
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
    public static TimeSpan SelfTest { get; } = TimeSpan.FromMilliseconds(30000);

    /// <summary>2000 ms — one identity probe during auto-detect (§10.12), where eight of these must fit inside 20 s.</summary>
    public static TimeSpan AutoDetectProbe { get; } = TimeSpan.FromMilliseconds(2000);

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

        // *TST? is IEEE 488.2 common syntax: no node structure and no long form.
        lookup["*TST?"] = SelfTest;

        return lookup.ToFrozenDictionary(StringComparer.Ordinal);
    }

    /// <summary>
    /// Adds every combination of short and long node spellings, because SCPI lets a caller mix them
    /// freely — <c>:SYSTem:STAT?</c> is as legal as <c>:SYST:STATus?</c>.
    /// </summary>
    private static void AddSpellings(Dictionary<string, TimeSpan> lookup, TimeSpan timeout, params string[][] nodes)
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

        foreach (string spelling in spellings)
        {
            lookup[$"{spelling}?"] = timeout;
        }
    }
}
