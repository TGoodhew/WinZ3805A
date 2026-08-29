using System.Text.RegularExpressions;

namespace WinZ3805A.Device.Commands;

/// <summary>
/// The §8.4 exclusion list, and the only place in this repository where it exists.
/// </summary>
/// <remarks>
/// <para>
/// §8.4 requires that these commands are absent from the application in every user-visible sense:
/// not in the catalog, not in a picker, an autocomplete, help text, or any log a user can read.
/// They are not catalog entries carrying a flag — they do not exist as data. This type holds
/// patterns, never commands, and has exactly one route out: the <c>IsBlocked</c> predicate, kept
/// for the §8.4 tests and for any future path that accepts typed text. (The Advanced Console this
/// was written for shipped as a picker with no free-text path — see the predicate's remarks.)
/// </para>
/// <para>
/// <b>The collection is deliberately not public.</b> §8.4 names it
/// <c>CommandCatalog.BlockedPatterns</c> and then requires in the same paragraph that it "must not
/// be enumerable through any public API that a view binds to". Those two cannot both be taken
/// literally, so the requirement wins over the name: the patterns are private, and the only way
/// out of this assembly is <see cref="CommandCatalog.IsBlocked"/>, which answers one bool about
/// one candidate. Nothing can bind to it, enumerate it, or render it into a list.
/// </para>
/// <para>
/// <c>build/Test-NoBlockedCommands.ps1</c> enforces that this file is the sole occurrence, the way
/// the hex-literal gate treats <c>Themes/Colors.xaml</c>.
/// </para>
/// </remarks>
internal static partial class BlockedCommands
{
    /// <summary>
    /// Every pattern is tested against a command's header — the text before any parameter — with
    /// the leading colon optional and case ignored, because a user typing into a console will not
    /// match the manual's capitalisation and must be stopped anyway.
    /// </summary>
    internal static IReadOnlyList<Regex> Patterns { get; } =
    [
        FirmwareTransferPattern(),
        FlashErasePattern(),
        LanguageNodePattern(),
        UndocumentedSetFormPattern(),
    ];

    /// <summary>
    /// Answers whether <paramref name="header"/> is excluded. Expects a command header with any
    /// parameter already removed.
    /// </summary>
    internal static bool Matches(string header)
    {
        foreach (Regex pattern in Patterns)
        {
            if (pattern.IsMatch(header))
            {
                return true;
            }
        }

        return false;
    }

    // -------------------------------------------------------------------------------------------
    // The named cases. Each blocks its query form as well as its set form: a query that cannot
    // change anything is still a node the user should never see named in an error message.
    // -------------------------------------------------------------------------------------------

    [GeneratedRegex(@"^:?DIAG(NOSTIC)?:DOWN(LOAD)?\??$", RegexOptions.IgnoreCase)]
    private static partial Regex FirmwareTransferPattern();

    [GeneratedRegex(@"^:?DIAG(NOSTIC)?:ERAS(E)?\??$", RegexOptions.IgnoreCase)]
    private static partial Regex FlashErasePattern();

    [GeneratedRegex(@"^:?SYST(EM)?:LANG(UAGE)?\??$", RegexOptions.IgnoreCase)]
    private static partial Regex LanguageNodePattern();

    /// <summary>
    /// The categorical case: any undocumented parser node <b>in set form</b>.
    /// </summary>
    /// <remarks>
    /// The leading negative lookahead is what makes this set-only. §8.5 enables the query form of a
    /// small subset as an opt-in read-only card, so a pattern that ignored the question mark would
    /// block a feature the specification asks for two sections later. A set form has no way to be
    /// safe: these nodes are undocumented, so what they write is unknown, and §8.4 gives them no
    /// override.
    /// </remarks>
    [GeneratedRegex(
        @"^(?![^\s]*\?)" +
        @":?(?:[A-Z0-9]+:)*" +
        @"(?:TCO(?:EFFICIENT)?|PSTARTUP|DOUT(?:PUT)?|REST(?:RICTED)?|SOUR(?:CE)?|IREF(?:ERENCE)?" +
        @"|EGRESPONSE|OUTP(?:UT)?:PINS:PIN[1-8])$",
        RegexOptions.IgnoreCase)]
    private static partial Regex UndocumentedSetFormPattern();
}
