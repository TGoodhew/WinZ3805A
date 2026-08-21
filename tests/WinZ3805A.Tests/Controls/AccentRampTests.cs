using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The rung-for-rung substitution, and the boundary §9.4.3 depends on.
/// </summary>
public sealed class AccentRampTests
{
    /// <summary>
    /// <b>The accent can never reach a semantic colour.</b>
    /// </summary>
    /// <remarks>
    /// This is the test that makes §9.4.3 true rather than intended. Severity is colour + shape +
    /// text, and the colour half is only meaningful while "critical" is a fixed red rather than
    /// whatever the user set in Windows. Adding <c>WzCriticalBrush</c> to the ramp's assignments
    /// would be a one-line change that looked tidy — the pill would then match the accent — and it
    /// would quietly delete the guarantee. This fails the build instead.
    /// </remarks>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoSemanticBrushIsEverAssignedFromTheAccent(bool isLightTheme)
    {
        IEnumerable<string> assigned = Brand.BrushAssignments(isLightTheme)
            .Select(a => a.Key);

        foreach (string forbidden in AccentRamp.NeverDerivedFromAccent)
        {
            Assert.DoesNotContain(forbidden, assigned, StringComparer.Ordinal);
        }
    }

    /// <summary>Every rung is substituted, or the ramp would be half one accent and half another.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AllSevenRungsAreAssigned(bool isLightTheme)
    {
        IEnumerable<string> assigned = Brand.BrushAssignments(isLightTheme)
            .Select(a => a.Key);

        foreach (string rung in (string[])
            ["Dark3", "Dark2", "Dark1", "Base", "Light1", "Light2", "Light3"])
        {
            Assert.Contains($"WzAccent{rung}Brush", assigned, StringComparer.Ordinal);
        }
    }

    /// <summary>No key is assigned twice, which would make the later one silently win.</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void NoBrushIsAssignedTwice(bool isLightTheme)
    {
        List<string> assigned = [.. Brand.BrushAssignments(isLightTheme).Select(a => a.Key)];

        Assert.Equal(assigned.Count, assigned.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>
    /// The fill is a dark rung against a light ground and a light rung against a dark one.
    /// </summary>
    /// <remarks>
    /// Getting this backwards produces a button that is nearly invisible against the card behind
    /// it — legible in a screenshot of one theme and not the other, which is exactly the kind of
    /// defect that survives review.
    /// </remarks>
    [Fact]
    public void TheInteractionBrushesFollowTheThemeRatherThanOneFixedRung()
    {
        AccentRamp ramp = Brand;

        Assert.Equal(ramp.Dark1, Find(ramp.BrushAssignments(true), "WzAccentFillBrush"));
        Assert.Equal(ramp.Light2, Find(ramp.BrushAssignments(false), "WzAccentFillBrush"));

        static Rgb Find(IReadOnlyList<KeyValuePair<string, Rgb>> assignments, string key) =>
            assignments.First(a => a.Key == key).Value;
    }

    /// <summary>
    /// The brand ramp was read from the dictionary rather than coming back null.
    /// </summary>
    /// <remarks>
    /// <see cref="AccentRamp.Brand"/> is nullable because it is a lookup now, and every other test
    /// in this file reaches through <see cref="Brand"/>, which would fail loudly on null anyway.
    /// This says so directly, and pins the base rung against §9.4.1 so that a dictionary edited
    /// away from the specification is caught here rather than noticed on screen.
    /// </remarks>
    [Fact]
    public void TheBrandRampIsReadFromTheDictionary()
    {
        Assert.NotNull(AccentRamp.Brand);
        Assert.Equal(new Rgb(0x0E, 0x7C, 0x86), Brand.Base);
        Assert.Equal(ThemePalette.Colour(ThemePalette.Light, "WzAccentBase"), Brand.Base);
    }

    /// <summary>The ramp, unwrapped, for the tests that only care about its shape.</summary>
    private static AccentRamp Brand =>
        AccentRamp.Brand ?? throw new InvalidOperationException(
            "Themes/Colors.xaml was not embedded, so no test here means anything.");
}
