using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// CIEDE2000, and §9.4.2's rule that a system accent must not look like an alarm.
/// </summary>
/// <remarks>
/// The reference pairs come from Sharma, Wu and Dalal's test data for the CIEDE2000 formula, which
/// exists precisely because the formula is easy to implement plausibly and wrongly. The rotation
/// term for blues and the near-neutral cases are where a hand-checked implementation goes astray,
/// so those are the pairs chosen rather than a comfortable spread.
/// </remarks>
public sealed class ColourDifferenceTests
{
    // ------------------------------------------------------------------ the published test data

    [Theory]
    // Near-neutral: three pairs that must all come out at exactly 1, and do so only if the
    // chroma correction and the hue arithmetic are both right.
    [InlineData(50.0000, -1.3802, -84.2814, 50.0000, 0.0000, -82.7485, 1.0000)]
    [InlineData(50.0000, -1.1848, -84.8006, 50.0000, 0.0000, -82.7485, 1.0000)]
    [InlineData(50.0000, -0.9009, -85.5211, 50.0000, 0.0000, -82.7485, 1.0000)]
    // The blue region, where the rotation term earns its place.
    [InlineData(50.0000, 2.6772, -79.7751, 50.0000, 0.0000, -82.7485, 2.0425)]
    [InlineData(50.0000, 3.1571, -77.2803, 50.0000, 0.0000, -82.7485, 2.8615)]
    [InlineData(50.0000, 2.8361, -74.0200, 50.0000, 0.0000, -82.7485, 3.4412)]
    // Opposite signs across the neutral axis.
    [InlineData(50.0000, 2.4900, -0.0010, 50.0000, -2.4900, 0.0009, 7.1792)]
    // A large difference, which pins the weighting functions rather than the corrections.
    [InlineData(50.0000, 2.5000, 0.0000, 73.0000, 25.0000, -18.0000, 27.1492)]
    // Very dark, where the lightness weighting is furthest from 1.
    [InlineData(2.0776, 0.0795, -1.1350, 0.9033, -0.0636, -0.5514, 0.9082)]
    public void MatchesThePublishedReferenceValues(
        double l1, double a1, double b1,
        double l2, double a2, double b2,
        double expected)
    {
        double actual = ColourDifference.Between(new LabColour(l1, a1, b1), new LabColour(l2, a2, b2));

        Assert.Equal(expected, actual, 4);
    }

    // ------------------------------------------------------------------------------ properties

    /// <summary>
    /// A colour is zero from itself. Trivial to state and the first thing a sign error breaks.
    /// </summary>
    [Theory]
    [InlineData(14, 124, 134)]
    [InlineData(178, 43, 43)]
    [InlineData(0, 0, 0)]
    [InlineData(255, 255, 255)]
    public void AColourIsIdenticalToItself(byte r, byte g, byte b) =>
        Assert.Equal(0, ColourDifference.Between((r, g, b), (r, g, b)), 6);

    /// <summary>
    /// CIEDE2000 is symmetric, unlike some of the formulae it replaced. Worth asserting because
    /// the hue-difference branch is the one place an implementation can accidentally not be.
    /// </summary>
    [Fact]
    public void TheOrderOfTheArgumentsDoesNotMatter()
    {
        (byte, byte, byte) accent = (14, 124, 134);
        (byte, byte, byte) critical = (178, 43, 43);

        Assert.Equal(
            ColourDifference.Between(accent, critical),
            ColourDifference.Between(critical, accent),
            10);
    }

    /// <summary>Black and white are as far apart as sRGB goes, and the number should say so.</summary>
    [Fact]
    public void BlackAndWhiteAreVeryFarApart() =>
        Assert.True(ColourDifference.Between((0, 0, 0), (255, 255, 255)) > 95);

    // -------------------------------------------------------------- §9.4.2's actual question

    /// <summary>
    /// The brand accent against both semantic colours, in both themes. This is the test that says
    /// the default configuration is safe — and it had better pass, because §9.4.2 chose the brand
    /// accent for hue separation from exactly these.
    /// </summary>
    [Theory]
    // Light theme: WzCautionBrush #8A5300, WzCriticalBrush #B22B2B.
    [InlineData(0x8A, 0x53, 0x00)]
    [InlineData(0xB2, 0x2B, 0x2B)]
    // Dark theme: #F2B155, #FF6B6B.
    [InlineData(0xF2, 0xB1, 0x55)]
    [InlineData(0xFF, 0x6B, 0x6B)]
    public void TheBrandAccentCollidesWithNoSemanticColour(byte r, byte g, byte b)
    {
        // WzAccentBase, #0E7C86.
        (byte, byte, byte) accent = (0x0E, 0x7C, 0x86);

        Assert.False(
            ColourDifference.Collides(accent, (r, g, b)),
            $"the brand accent is only {ColourDifference.Between(accent, (r, g, b)):F1} from this semantic colour");
    }

    /// <summary>
    /// The case §9.4.2 exists for, in the issue's own words: "a user whose Windows accent is red".
    /// Windows ships several, and they must all be caught.
    /// </summary>
    [Theory]
    [InlineData(0xE7, 0x48, 0x56, "Windows 'Brick red'")]
    [InlineData(0xC4, 0x30, 0x3E, "a darker red accent")]
    [InlineData(0xB2, 0x30, 0x30, "close to WzCritical outright")]
    public void ARedSystemAccentIsCaughtAgainstCritical(byte r, byte g, byte b, string which)
    {
        Assert.True(
            ColourDifference.Collides((r, g, b), (0xB2, 0x2B, 0x2B)),
            $"{which} was {ColourDifference.Between((r, g, b), (0xB2, 0x2B, 0x2B)):F1} from critical and went unwarned");
    }

    /// <summary>
    /// And an amber accent against caution — but against the caution of the <i>right theme</i>.
    /// </summary>
    /// <remarks>
    /// This test originally asserted Windows' "Gold" collided with light-theme caution and failed,
    /// because it does not: <c>#FF8C00</c> is a bright orange and light-theme caution
    /// <c>#8A5300</c> is nearly brown, 28.8 apart. It collides with <i>dark</i>-theme caution
    /// <c>#F2B155</c>, at 11.8. The measurement, not the expectation, was right — see
    /// <see cref="AccentGuardTests"/>, which is why the guard checks both themes.
    /// </remarks>
    [Theory]
    [InlineData(0xFF, 0x8C, 0x00, 0xF2, 0xB1, 0x55, "Windows 'Gold' against dark caution")]
    [InlineData(0xCA, 0x50, 0x10, 0x8A, 0x53, 0x00, "a burnt orange accent against light caution")]
    public void AnAmberSystemAccentIsCaughtAgainstCaution(
        byte r, byte g, byte b,
        byte cr, byte cg, byte cb,
        string which) =>
        Assert.True(
            ColourDifference.Collides((r, g, b), (cr, cg, cb)),
            $"{which} was {ColourDifference.Between((r, g, b), (cr, cg, cb)):F1} apart and went unwarned");

    /// <summary>
    /// Windows' own default blue is the commonest accent there is, and it must NOT be warned
    /// about — a guard that cried wolf on the default would be switched off by everyone.
    /// </summary>
    [Theory]
    [InlineData(0x00, 0x78, 0xD4, "Windows default blue")]
    [InlineData(0x74, 0x4D, 0xA9, "Windows purple")]
    [InlineData(0x10, 0x89, 0x3E, "Windows green")]
    public void AnAccentThatIsNotWarmIsLeftAlone(byte r, byte g, byte b, string which)
    {
        Assert.False(ColourDifference.Collides((r, g, b), (0xB2, 0x2B, 0x2B)), $"{which} vs critical");
        Assert.False(ColourDifference.Collides((r, g, b), (0x8A, 0x53, 0x00)), $"{which} vs caution");
    }

    [Fact]
    public void TheThresholdIsTheOneTheSpecificationNames() =>
        Assert.Equal(20, ColourDifference.CollisionThreshold);
}
