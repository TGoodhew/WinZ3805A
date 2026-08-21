namespace WinZ3805A.Controls;

/// <summary>One semantic colour the accent must not be mistaken for.</summary>
/// <param name="Name">What it means, for the warning's wording.</param>
/// <param name="Theme">Which theme it belongs to, for the same reason.</param>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct SemanticColour(string Name, string Theme, byte R, byte G, byte B);

/// <summary>The nearest semantic colour to an accent, and how near.</summary>
/// <param name="Colour">Which one.</param>
/// <param name="Difference">Its CIEDE2000 distance from the accent.</param>
public readonly record struct AccentCollision(SemanticColour Colour, double Difference);

/// <summary>
/// §9.4.2's guard: a system accent must not look like an alarm.
/// </summary>
/// <remarks>
/// <para>
/// <b>Every semantic colour in every theme, not just the one showing.</b> That is the whole
/// finding, and it is not what the specification's wording suggests on a first read. Measured
/// against this palette, Windows' own "Gold" accent sits <b>28.8</b> from light-theme caution — well
/// clear — and <b>11.8</b> from dark-theme caution, which is a collision. Checking only the current
/// theme would clear that accent at noon and leave the user, at sunset, with a navigation highlight
/// they cannot tell from a caution pill.
/// </para>
/// <para>
/// A theme can change without the application being restarted or the accent being reconsidered, so
/// a guard that ran once against one palette would be answering a question the user had not asked.
/// Four comparisons cost nothing and the answer then holds whatever the desktop does.
/// </para>
/// <para>
/// <b>The colours come from <c>Themes/Colors.xaml</c> itself</b>, through
/// <see cref="ThemePalette"/>. They used to be restated here with a test holding the copy against
/// the XAML, which caught drift rather than preventing it; now there is only one place a semantic
/// colour is written down, which is what §9.13 asks for.
/// </para>
/// </remarks>
public static class AccentGuard
{
    /// <summary>The colours §9.4.3 reserves for severity, across both themes.</summary>
    /// <remarks>
    /// <para>
    /// Read from the token dictionary. HighContrast is deliberately not among them: there both
    /// severities resolve to <c>SystemColorWindowTextColor</c>, so there is no fixed colour to
    /// measure against — and §9.4 hands the palette to the system in that theme anyway, which
    /// makes the accent question moot rather than unanswered.
    /// </para>
    /// <para>
    /// A token that fails to resolve is skipped rather than defaulted, so the guard never measures
    /// against a colour the application does not actually use. <c>AccentGuardTests</c> asserts all
    /// four are present, which is where a dictionary that stopped defining one would be caught.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SemanticColour> Semantics { get; } = Load();

    private static IReadOnlyList<SemanticColour> Load()
    {
        List<SemanticColour> semantics = [];

        (string Name, Severity Severity)[] severities =
            [("caution", Severity.Caution), ("critical", Severity.Critical)];

        // The label is lower case because it is user-facing copy, not the dictionary key.
        (string Key, string Label)[] themes =
            [(ThemePalette.Light, "light"), (ThemePalette.Dark, "dark")];

        foreach ((string name, Severity severity) in severities)
        {
            foreach ((string theme, string label) in themes)
            {
                if (ThemePalette.Colour(theme, ThemePalette.BrushKey(severity)) is Rgb colour)
                {
                    semantics.Add(new SemanticColour(name, label, colour.R, colour.G, colour.B));
                }
            }
        }

        return semantics;
    }

    /// <summary>
    /// The nearest semantic colour to an accent if it is too near, or null if the accent is clear.
    /// </summary>
    /// <remarks>
    /// Returns the <i>nearest</i> rather than the first found, so the warning names the collision a
    /// user is most likely to notice rather than whichever happened to be checked first.
    /// </remarks>
    public static AccentCollision? Check(byte red, byte green, byte blue)
    {
        AccentCollision? worst = null;

        foreach (SemanticColour semantic in Semantics)
        {
            double difference = ColourDifference.Between(
                (red, green, blue),
                (semantic.R, semantic.G, semantic.B));

            if (difference < ColourDifference.CollisionThreshold
                && (worst is null || difference < worst.Value.Difference))
            {
                worst = new AccentCollision(semantic, difference);
            }
        }

        return worst;
    }

    /// <summary>
    /// The sentence the one-time <c>TeachingTip</c> shows, or null when there is nothing to say.
    /// </summary>
    /// <remarks>
    /// §9.11's copy rules: what happened, what it means, and what to do — second person, no apology.
    /// It names the theme, because a user looking at a light desktop being told about a dark-theme
    /// collision needs to know why they cannot see the problem they are being warned about.
    /// </remarks>
    public static string? Describe(AccentCollision? collision)
    {
        if (collision is not AccentCollision found)
        {
            return null;
        }

        string where = found.Colour.Theme == "dark"
            ? $"in the dark theme, where {found.Colour.Name} is a lighter shade"
            : "in the light theme";

        return $"Your Windows accent is close to the colour this app uses for {found.Colour.Name} "
            + $"{where}. Selected items and {found.Colour.Name} warnings will be hard to tell apart. "
            + "The app's own accent does not have this problem.";
    }
}
