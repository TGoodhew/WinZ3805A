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
/// <b>These values are copied from <c>Themes/Colors.xaml</c> and must not drift from it.</b> Headless
/// test code cannot resolve a <c>ThemeResource</c>, so they are restated here — and a test reads the
/// XAML and asserts the two agree, which is the only thing that keeps a copy honest.
/// </para>
/// </remarks>
public static class AccentGuard
{
    /// <summary>The colours §9.4.3 reserves for severity, across both themes.</summary>
    public static IReadOnlyList<SemanticColour> Semantics { get; } =
    [
        new("caution", "light", 0x8A, 0x53, 0x00),
        new("critical", "light", 0xB2, 0x2B, 0x2B),
        new("caution", "dark", 0xF2, 0xB1, 0x55),
        new("critical", "dark", 0xFF, 0x6B, 0x6B),
    ];

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
