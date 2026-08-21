namespace WinZ3805A.Controls;

/// <summary>An sRGB triple, which is as much colour as this layer needs to know about.</summary>
/// <param name="R">Red.</param>
/// <param name="G">Green.</param>
/// <param name="B">Blue.</param>
public readonly record struct Rgb(byte R, byte G, byte B)
{
    /// <summary>The colour as <c>#RRGGBB</c>, for logging and test failure messages.</summary>
    public override string ToString() => $"#{R:X2}{G:X2}{B:X2}";
}

/// <summary>
/// The seven rungs §9.4.1 gives the accent, from darkest to lightest.
/// </summary>
/// <param name="Dark3">The darkest rung.</param>
/// <param name="Dark2">Darker.</param>
/// <param name="Dark1">Dark.</param>
/// <param name="Base">The accent itself.</param>
/// <param name="Light1">Light.</param>
/// <param name="Light2">Lighter.</param>
/// <param name="Light3">The lightest rung.</param>
/// <remarks>
/// Windows publishes a ramp of exactly this shape, which is why §9.4.1's has seven rungs rather
/// than a number chosen for its own sake — substituting one for the other is then a rung-for-rung
/// swap with nothing to interpolate and nothing to guess.
/// </remarks>
public readonly record struct AccentRamp(
    Rgb Dark3,
    Rgb Dark2,
    Rgb Dark1,
    Rgb Base,
    Rgb Light1,
    Rgb Light2,
    Rgb Light3)
{
    /// <summary>The brand ramp from <c>Themes/Colors.xaml</c>, which is the default.</summary>
    public static AccentRamp Brand { get; } = new(
        new Rgb(0x05, 0x2F, 0x33),
        new Rgb(0x08, 0x47, 0x4D),
        new Rgb(0x0B, 0x6C, 0x74),
        new Rgb(0x0E, 0x7C, 0x86),
        new Rgb(0x18, 0x9A, 0xA6),
        new Rgb(0x3F, 0xB8, 0xC4),
        new Rgb(0x7F, 0xD4, 0xDC));

    /// <summary>
    /// The <c>Wz*</c> brush keys this ramp sets, and what each becomes, for one theme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interaction brushes are not the same rung in both themes and cannot be: §9.4.1 puts the
    /// fill on <c>Dark1</c> against a light ground and <c>Light2</c> against a dark one, because a
    /// fill has to be darker than what is behind it in one case and lighter in the other. A single
    /// mapping would leave one theme with a button that barely separates from the card under it.
    /// </para>
    /// <para>
    /// <c>WzAccentForegroundBrush</c> is deliberately absent. It is the text drawn <i>on</i> the
    /// accent — white in light, black in dark — and it is a contrast decision about the fill rather
    /// than a rung of the ramp. Substituting a system accent must not change it, or a pale system
    /// accent would get white text on it and become unreadable.
    /// </para>
    /// </remarks>
    /// <param name="isLightTheme">Which theme's interaction mapping to produce.</param>
    /// <returns>Brush resource key to colour.</returns>
    public IReadOnlyList<KeyValuePair<string, Rgb>> BrushAssignments(bool isLightTheme) =>
    [
        new("WzAccentDark3Brush", Dark3),
        new("WzAccentDark2Brush", Dark2),
        new("WzAccentDark1Brush", Dark1),
        new("WzAccentBaseBrush", Base),
        new("WzAccentLight1Brush", Light1),
        new("WzAccentLight2Brush", Light2),
        new("WzAccentLight3Brush", Light3),

        new("WzAccentFillBrush", isLightTheme ? Dark1 : Light2),
        new("WzAccentFillHoverBrush", isLightTheme ? Dark2 : Light1),
        new("WzAccentFillPressedBrush", isLightTheme ? Dark3 : Light3),

        // §9.4.1 draws the informational severity from the accent, unlike caution and critical.
        // It follows the accent precisely because "informational" is not an alarm - it is the one
        // severity that may safely look like the rest of the application.
        new("WzInfoBrush", isLightTheme ? Dark1 : Light2),
    ];

    /// <summary>
    /// The keys that must never be reassigned, whatever the accent is.
    /// </summary>
    /// <remarks>
    /// §9.4.3 makes severity mean something, and it can only mean it if the accent cannot reach it.
    /// This list is the enforcement: a test asserts no assignment above names any of these, so
    /// adding a semantic key to the ramp mapping fails the build rather than quietly making
    /// "critical" whatever colour the user picked in Windows.
    /// </remarks>
    public static IReadOnlyList<string> NeverDerivedFromAccent { get; } =
    [
        "WzCriticalBrush",
        "WzCautionBrush",
        "WzSuccessBrush",
        "WzAccentForegroundBrush",
    ];
}
