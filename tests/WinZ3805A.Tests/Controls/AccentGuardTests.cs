using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// §9.4.2's accent guard, and the measurements that decided how it works.
/// </summary>
/// <remarks>
/// The numbers quoted throughout are the measured ΔE₀₀ values, not estimates. They are written down
/// because the guard's whole design turns on them, and because the first version of these tests
/// asserted a collision that does not exist.
/// </remarks>
public sealed class AccentGuardTests
{
    // ------------------------------------------------- the palette comes from the dictionary

    /// <summary>
    /// All four semantic colours were found, and they are the ones the dictionary defines.
    /// </summary>
    /// <remarks>
    /// <see cref="AccentGuard.Semantics"/> skips a token it cannot resolve rather than defaulting
    /// it, which is the right behaviour and also a silent one: a dictionary that stopped defining
    /// <c>WzCautionBrush</c> would leave the guard quietly measuring against three colours instead
    /// of four, and every collision test below would still pass. This is where that is caught.
    /// </remarks>
    [Fact]
    public void EverySemanticColourWasReadFromTheDictionary()
    {
        Assert.Equal(4, AccentGuard.Semantics.Count);

        foreach (SemanticColour semantic in AccentGuard.Semantics)
        {
            string theme = semantic.Theme == "dark" ? ThemePalette.Dark : ThemePalette.Light;
            string key = semantic.Name == "caution" ? "WzCautionBrush" : "WzCriticalBrush";

            Assert.Equal(
                ThemePalette.Colour(theme, key),
                new Rgb(semantic.R, semantic.G, semantic.B));
        }
    }

    /// <summary>Both severities, both themes — four colours and no more.</summary>
    /// <remarks>
    /// HighContrast is deliberately absent: it routes both brushes to
    /// <c>SystemColorWindowTextColor</c>, so there is no fixed colour to measure and §9.4 hands the
    /// palette to the system anyway.
    /// </remarks>
    [Fact]
    public void TheGuardCoversBothSeveritiesInBothThemes()
    {
        Assert.Equal(4, AccentGuard.Semantics.Count);
        Assert.Equal(
            ["caution/dark", "caution/light", "critical/dark", "critical/light"],
            AccentGuard.Semantics.Select(s => $"{s.Name}/{s.Theme}").OrderBy(s => s, StringComparer.Ordinal));
    }

    // ------------------------------------------------------------------ the finding, made a test

    /// <summary>
    /// Windows' "Gold" accent is clear of light-theme caution and collides with the dark one.
    /// </summary>
    /// <remarks>
    /// This is the case that justifies checking every theme rather than the one on screen. A guard
    /// that consulted only the active palette would clear this accent on a light desktop and then,
    /// when the desktop switched at sunset, leave the user with a navigation highlight they could
    /// not tell from a caution pill — with no second prompt, because the question had been asked
    /// and answered hours earlier.
    /// </remarks>
    [Fact]
    public void WindowsGoldCollidesOnlyInTheDarkTheme()
    {
        (byte R, byte G, byte B) gold = (0xFF, 0x8C, 0x00);

        Assert.True(ColourDifference.Between(gold, (0x8A, 0x53, 0x00)) > 28, "light caution");
        Assert.True(ColourDifference.Between(gold, (0xF2, 0xB1, 0x55)) < 12, "dark caution");

        AccentCollision? found = AccentGuard.Check(gold.R, gold.G, gold.B);

        Assert.NotNull(found);
        Assert.Equal("caution", found!.Value.Colour.Name);
        Assert.Equal("dark", found.Value.Colour.Theme);
    }

    /// <summary>
    /// When an accent is near more than one semantic colour, the nearest is the one reported.
    /// </summary>
    /// <remarks>
    /// Windows' "Brick red" sits 14.8 from light-theme critical and 8.9 from the dark one. Either
    /// would be a true warning; naming the closer one makes the message match what the user will
    /// most obviously see.
    /// </remarks>
    [Fact]
    public void TheNearestCollisionIsTheOneReported()
    {
        AccentCollision? found = AccentGuard.Check(0xE7, 0x48, 0x56);

        Assert.NotNull(found);
        Assert.Equal("critical", found!.Value.Colour.Name);
        Assert.Equal("dark", found.Value.Colour.Theme);
        Assert.Equal(8.9, found.Value.Difference, 1);
    }

    /// <summary>
    /// The accents Windows ships that must produce no warning at all.
    /// </summary>
    /// <remarks>
    /// A guard that fires on the default blue would be turned off by everyone within a day, and
    /// would then not be there for the red accent it exists for. The measured margins are wide —
    /// the nearest of these is 34.6 — so this is not a close-run thing.
    /// </remarks>
    [Theory]
    [InlineData(0x00, 0x78, 0xD4, "Windows default blue")]
    [InlineData(0x74, 0x4D, 0xA9, "Windows purple")]
    [InlineData(0x10, 0x89, 0x3E, "Windows green")]
    [InlineData(0x0E, 0x7C, 0x86, "the brand accent itself")]
    public void AnAccentClearOfEverySemanticColourIsNotWarnedAbout(byte r, byte g, byte b, string which)
    {
        AccentCollision? found = AccentGuard.Check(r, g, b);

        Assert.True(
            found is null,
            $"{which} was warned about: {found?.Difference:F1} from "
            + $"{found?.Colour.Theme} {found?.Colour.Name}");
    }

    /// <summary>The accents that must be caught, warm across the range from amber to red.</summary>
    [Theory]
    [InlineData(0xFF, 0x8C, 0x00, "Windows 'Gold'")]
    [InlineData(0xE7, 0x48, 0x56, "Windows 'Brick red'")]
    [InlineData(0xCA, 0x50, 0x10, "a burnt orange accent")]
    [InlineData(0xB2, 0x30, 0x30, "all but WzCritical outright")]
    public void AWarmAccentIsAlwaysCaught(byte r, byte g, byte b, string which) =>
        Assert.True(AccentGuard.Check(r, g, b) is not null, $"{which} went unwarned");

    // ------------------------------------------------------------------------------ the wording

    /// <summary>A clear accent produces no message, because there is nothing to say.</summary>
    [Fact]
    public void AClearAccentHasNothingToDescribe() =>
        Assert.Null(AccentGuard.Describe(AccentGuard.Check(0x00, 0x78, 0xD4)));

    /// <summary>
    /// The warning names the severity and the theme, and never relies on colour to make its point.
    /// </summary>
    /// <remarks>
    /// A11Y-12 in prose form: the user being warned may be the one who cannot see the collision,
    /// and the user on a light desktop cannot see a dark-theme collision at all. Both need the
    /// words to carry the whole message.
    /// </remarks>
    [Fact]
    public void TheWarningNamesTheSeverityAndTheThemeInWords()
    {
        string? message = AccentGuard.Describe(AccentGuard.Check(0xFF, 0x8C, 0x00));

        Assert.NotNull(message);
        Assert.Contains("caution", message, StringComparison.Ordinal);
        Assert.Contains("dark theme", message, StringComparison.Ordinal);

        // §9.11: no apology, and the second person throughout.
        Assert.DoesNotContain("sorry", message, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Your", message, StringComparison.Ordinal);
    }
}
