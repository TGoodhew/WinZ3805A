using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// Reading the §9 tokens out of <c>Themes/Colors.xaml</c>.
/// </summary>
/// <remarks>
/// <para>
/// These used to be parity tests: three C# copies of the palette, each held against the XAML by an
/// assertion. That caught drift but did not prevent it, and a colour was still written down twice.
/// Now there is one source and these check that reading it works — including the two forms that are
/// easy to get wrong, indirection and system colours.
/// </para>
/// <para>
/// The expected values below are §9.4.3's own table. That makes this a conformance test against the
/// specification rather than a comparison of two copies, which is a strictly better thing for it to
/// be: it now fails if someone changes the dictionary away from §9, not merely if two copies
/// disagree with each other.
/// </para>
/// </remarks>
public sealed class ThemePaletteTests
{
    /// <summary>The dictionary is found and parsed at all.</summary>
    /// <remarks>
    /// First because everything else here would pass vacuously if the embedded resource were
    /// missing — <see cref="ThemePalette.Colour"/> answers null for an absent token and for an
    /// absent file alike, so an unembedded dictionary would look like a palette of nulls.
    /// </remarks>
    [Fact]
    public void TheEmbeddedDictionaryIsFound()
    {
        Assert.Contains(ThemePalette.Light, ThemePalette.Themes);
        Assert.Contains(ThemePalette.Dark, ThemePalette.Themes);
        Assert.Contains(ThemePalette.HighContrast, ThemePalette.Themes);
    }

    /// <summary>§9.4.3's severity table, read from the file that implements it.</summary>
    [Theory]
    [InlineData(ThemePalette.Light, "WzSuccessBrush", 0x0F, 0x7B, 0x3C)]
    [InlineData(ThemePalette.Light, "WzCautionBrush", 0x8A, 0x53, 0x00)]
    [InlineData(ThemePalette.Light, "WzCriticalBrush", 0xB2, 0x2B, 0x2B)]
    [InlineData(ThemePalette.Light, "WzNeutralBrush", 0x61, 0x61, 0x61)]
    [InlineData(ThemePalette.Dark, "WzSuccessBrush", 0x4C, 0xC3, 0x8A)]
    [InlineData(ThemePalette.Dark, "WzCautionBrush", 0xF2, 0xB1, 0x55)]
    [InlineData(ThemePalette.Dark, "WzCriticalBrush", 0xFF, 0x6B, 0x6B)]
    [InlineData(ThemePalette.Dark, "WzNeutralBrush", 0x9A, 0x9A, 0x9A)]
    public void TheSeverityColoursAreTheOnesTheSpecificationNames(
        string theme, string key, byte r, byte g, byte b) =>
        Assert.Equal(new Rgb(r, g, b), ThemePalette.Colour(theme, key));

    /// <summary>§9.4.1's accent ramp, likewise.</summary>
    [Theory]
    [InlineData("WzAccentDark3", 0x05, 0x2F, 0x33)]
    [InlineData("WzAccentDark2", 0x08, 0x47, 0x4D)]
    [InlineData("WzAccentDark1", 0x0B, 0x6C, 0x74)]
    [InlineData("WzAccentBase", 0x0E, 0x7C, 0x86)]
    [InlineData("WzAccentLight1", 0x18, 0x9A, 0xA6)]
    [InlineData("WzAccentLight2", 0x3F, 0xB8, 0xC4)]
    [InlineData("WzAccentLight3", 0x7F, 0xD4, 0xDC)]
    public void TheAccentRampIsTheOneTheSpecificationNames(string key, byte r, byte g, byte b) =>
        Assert.Equal(new Rgb(r, g, b), ThemePalette.Colour(ThemePalette.Light, key));

    /// <summary>
    /// A <c>{StaticResource}</c> is followed, and followed <i>within the asking theme</i>.
    /// </summary>
    /// <remarks>
    /// <c>WzInfoBrush</c> is defined as <c>{StaticResource WzAccentDark1}</c> in Light and
    /// <c>{StaticResource WzAccentLight2}</c> in Dark. A resolver that followed references against
    /// one fixed theme would return the same colour for both and look entirely plausible doing it —
    /// which is the whole reason the raw values are kept per theme and resolved on demand.
    /// </remarks>
    [Fact]
    public void AStaticResourceReferenceIsFollowedWithinItsOwnTheme()
    {
        Assert.Equal(
            ThemePalette.Colour(ThemePalette.Light, "WzAccentDark1"),
            ThemePalette.Colour(ThemePalette.Light, "WzInfoBrush"));

        Assert.Equal(
            ThemePalette.Colour(ThemePalette.Dark, "WzAccentLight2"),
            ThemePalette.Colour(ThemePalette.Dark, "WzInfoBrush"));

        Assert.NotEqual(
            ThemePalette.Colour(ThemePalette.Light, "WzInfoBrush"),
            ThemePalette.Colour(ThemePalette.Dark, "WzInfoBrush"));
    }

    /// <summary>
    /// A <c>{ThemeResource}</c> answers null, because the file does not know that colour.
    /// </summary>
    /// <remarks>
    /// HighContrast sends every severity to <c>SystemColorWindowTextColor</c>, which is the user's
    /// setting rather than this application's. Answering null is what makes callers ask the system,
    /// and a resolver that instead returned black would silently override an accessibility choice.
    /// </remarks>
    [Theory]
    [InlineData("WzCriticalBrush")]
    [InlineData("WzCautionBrush")]
    [InlineData("WzSuccessBrush")]
    public void ASystemColourInHighContrastIsNotGuessedAt(string key) =>
        Assert.Null(ThemePalette.Colour(ThemePalette.HighContrast, key));

    /// <summary>An unknown key is null rather than an exception.</summary>
    [Theory]
    [InlineData(ThemePalette.Light, "WzNoSuchBrush")]
    [InlineData("NoSuchTheme", "WzCriticalBrush")]
    public void AnUnknownTokenIsNull(string theme, string key) =>
        Assert.Null(ThemePalette.Colour(theme, key));

    /// <summary>Every severity maps to a token that resolves in both real themes.</summary>
    /// <remarks>
    /// The mapping and the dictionary are separate files and could disagree — a severity added
    /// without a matching brush would leave the tray drawing it in the system text colour with no
    /// indication anything was wrong.
    /// </remarks>
    [Theory]
    [InlineData(Severity.Neutral)]
    [InlineData(Severity.Success)]
    [InlineData(Severity.Caution)]
    [InlineData(Severity.Critical)]
    [InlineData(Severity.Info)]
    public void EverySeverityResolvesToAColourInBothThemes(Severity severity)
    {
        string key = ThemePalette.BrushKey(severity);

        Assert.NotNull(ThemePalette.Colour(ThemePalette.Light, key));
        Assert.NotNull(ThemePalette.Colour(ThemePalette.Dark, key));
    }
}
