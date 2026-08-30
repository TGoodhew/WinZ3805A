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
/// The seven rungs §9.4.2 gives the accent, from darkest to lightest.
/// </summary>
/// <param name="Dark3">The darkest rung.</param>
/// <param name="Dark2">Darker.</param>
/// <param name="Dark1">Dark.</param>
/// <param name="Base">The accent itself.</param>
/// <param name="Light1">Light.</param>
/// <param name="Light2">Lighter.</param>
/// <param name="Light3">The lightest rung.</param>
/// <remarks>
/// Windows publishes a ramp of exactly this shape, which is why §9.4.2's has seven rungs rather
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
    /// <summary>
    /// The brand ramp, read from <c>Themes/Colors.xaml</c>, or null if it cannot be read.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Nullable because it is now a lookup rather than a literal, and a lookup can fail. The
    /// failure is benign and the type says so: when there is no brand ramp to apply, the right
    /// thing to do is apply nothing and leave the brushes with the colours the dictionary already
    /// gave them — which are these colours. A hard-coded fallback would be the second copy this
    /// change exists to remove.
    /// </para>
    /// <para>
    /// The rungs are read from the Light dictionary. Both themes define the ramp identically —
    /// only which rung the interaction brushes point at differs, which is
    /// <see cref="BrushAssignments"/>'s business rather than the ramp's.
    /// </para>
    /// </remarks>
    public static AccentRamp? Brand { get; } = Load();

    private static AccentRamp? Load()
    {
        Rgb?[] rungs =
        [
            ThemePalette.Colour(ThemePalette.Light, "WzAccentDark3"),
            ThemePalette.Colour(ThemePalette.Light, "WzAccentDark2"),
            ThemePalette.Colour(ThemePalette.Light, "WzAccentDark1"),
            ThemePalette.Colour(ThemePalette.Light, "WzAccentBase"),
            ThemePalette.Colour(ThemePalette.Light, "WzAccentLight1"),
            ThemePalette.Colour(ThemePalette.Light, "WzAccentLight2"),
            ThemePalette.Colour(ThemePalette.Light, "WzAccentLight3"),
        ];

        // All seven or none. A partial ramp would be worse than no substitution: half the accent
        // would come from the dictionary and half would be whatever the uninitialised rung held.
        return Array.TrueForAll(rungs, rung => rung is not null)
            ? new AccentRamp(
                rungs[0]!.Value, rungs[1]!.Value, rungs[2]!.Value, rungs[3]!.Value,
                rungs[4]!.Value, rungs[5]!.Value, rungs[6]!.Value)
            : null;
    }

    /// <summary>
    /// The <c>Wz*</c> brush keys this ramp sets, and what each becomes, for one theme.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The interaction brushes are not the same rung in both themes and cannot be: §9.4.2 puts the
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

        // §9.4.3 draws the informational severity from the accent, unlike caution and critical.
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
