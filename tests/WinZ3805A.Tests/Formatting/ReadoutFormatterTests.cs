using System.Globalization;
using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Formatting;

/// <summary>
/// The §9.5.3 numeric rules (P0-20).
/// </summary>
/// <remarks>
/// These are the rules whose violation is invisible in a screenshot and obvious across a bench,
/// which is exactly why they are asserted here rather than left to review. Culture is pinned on
/// every test that touches a separator so the suite does not pass or fail by machine.
/// </remarks>
public class ReadoutFormatterTests
{
    private static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    private static readonly CultureInfo German = new("de-DE");

    // -------------------------------------------------------------------------------------
    // Rule 4 — U+2212 MINUS SIGN, not a hyphen
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The whole point of rule 4. HYPHEN-MINUS is optically too short and sits too high beside
    /// lining figures, and the two characters are indistinguishable in a diff, so this asserts the
    /// code point rather than the appearance.
    /// </summary>
    [Fact]
    public void ANegativeValueUsesTheMinusSignAndNeverAHyphen()
    {
        string formatted = ReadoutFormatter.Format(-33.1, 1, Invariant);

        Assert.StartsWith("−", formatted, StringComparison.Ordinal);
        Assert.DoesNotContain('-', formatted);
        Assert.Equal("−33.1", formatted);
    }

    [Fact]
    public void TheMinusSignConstantIsTheRightCodePoint()
    {
        Assert.Equal("−", ReadoutFormatter.MinusSign);
        Assert.Equal(" ", ReadoutFormatter.HairSpace);
    }

    [Fact]
    public void APositiveValueCarriesNoSign()
    {
        Assert.Equal("9.8", ReadoutFormatter.Format(9.8, 1, Invariant));
    }

    // -------------------------------------------------------------------------------------
    // Rule 6 — fixed decimal places per quantity
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0.0, 1, "0.0")]
    [InlineData(5.0, 1, "5.0")]
    [InlineData(-5.0, 1, "−5.0")]
    [InlineData(33.14159, 1, "33.1")]
    [InlineData(33.14159, 3, "33.142")]
    [InlineData(42.0, 0, "42")]
    [InlineData(-0.04, 1, "−0.0")]
    public void DecimalPlacesAreFixedRatherThanFollowingTheValue(double value, int places, string expected)
    {
        Assert.Equal(expected, ReadoutFormatter.Format(value, places, Invariant));
    }

    /// <summary>
    /// A quantity keeps its precision across values. A column that shows 1 dp on one row and 3 on
    /// the next cannot be scanned down, which is the failure rule 6 exists to prevent.
    /// </summary>
    [Fact]
    public void EveryValueOfOneQuantityFormatsToTheSameDecimalCount()
    {
        double[] values = [-999.94, -33.1, -9.8, 0, 0.04, 7, 128.5];

        string[] formatted = [.. values.Select(v => ReadoutFormatter.Format(v, 1, Invariant))];

        Assert.All(formatted, f => Assert.Equal(1, f.Length - f.IndexOf('.') - 1));
    }

    // -------------------------------------------------------------------------------------
    // Missing values (§11.1)
    // -------------------------------------------------------------------------------------

    [Fact]
    public void AMissingValueRendersAsAnEmDash()
    {
        Assert.Equal("—", ReadoutFormatter.Format(null, 1, Invariant));
        Assert.Equal("—", ReadoutFormatter.NoValue);
    }

    /// <summary>
    /// NaN and infinity reach here only from arithmetic on a partly-parsed screen. Rendering
    /// "NaN" in a readout would look like a device fault rather than a missing field.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void ValuesThatAreNotNumbersRenderAsMissingRatherThanAsWords(double value)
    {
        Assert.Equal("—", ReadoutFormatter.Format(value, 1, Invariant));
    }

    // -------------------------------------------------------------------------------------
    // Locale
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The decimal separator follows the user's locale; only the negative sign is overridden,
    /// because U+2212 is a typographic rule rather than a locale convention.
    /// </summary>
    [Fact]
    public void TheDecimalSeparatorFollowsTheCultureButTheMinusSignDoesNot()
    {
        string formatted = ReadoutFormatter.Format(-33.1, 1, German);

        Assert.Equal("−33,1", formatted);
        Assert.DoesNotContain('-', formatted);
    }

    // -------------------------------------------------------------------------------------
    // Rule 2 — reserved width
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(3, 1, true, "−000.0")]
    [InlineData(3, 1, false, "000.0")]
    [InlineData(2, 0, true, "−00")]
    [InlineData(1, 2, false, "0.00")]
    public void TheReserveStringIsTheWidestTheQuantityCanRender(
        int digits, int places, bool negative, string expected)
    {
        Assert.Equal(expected, ReadoutFormatter.WidestString(digits, places, negative, Invariant));
    }

    /// <summary>
    /// The property that actually matters: no value the quantity can take renders longer than the
    /// string reserved for it. With tabular figures every digit shares one advance, so equal length
    /// means equal width — which is what stops the field resizing as the value changes.
    /// </summary>
    [Fact]
    public void NoValueInRangeRendersWiderThanTheReservedWidth()
    {
        const int Digits = 3;
        const int Places = 1;
        string reserve = ReadoutFormatter.WidestString(Digits, Places, allowNegative: true, Invariant);

        double[] values = [0, 0.04, -0.04, 9.8, -9.8, 33.1, -33.1, 999.9, -999.9, 128.55];

        foreach (double value in values)
        {
            string formatted = ReadoutFormatter.Format(value, Places, Invariant);
            Assert.True(
                formatted.Length <= reserve.Length,
                $"'{formatted}' ({formatted.Length}) exceeds the reserved '{reserve}' ({reserve.Length}).");
        }
    }

    /// <summary>
    /// P0-20's stated acceptance criterion, in the part a unit test can carry: stepping between
    /// values of different digit counts must not change the rendered width. The remaining half —
    /// that the glyphs themselves do not shift — is a tabular-figures property and is checked by
    /// running the app.
    /// </summary>
    [Fact]
    public void SteppingFromMinus331ToMinus98DoesNotChangeTheReservedWidth()
    {
        string reserve = ReadoutFormatter.WidestString(3, 1, allowNegative: true, Invariant);

        string wide = ReadoutFormatter.Format(-33.1, 1, Invariant);
        string narrow = ReadoutFormatter.Format(-9.8, 1, Invariant);

        Assert.NotEqual(wide.Length, narrow.Length);
        Assert.True(wide.Length <= reserve.Length);
        Assert.True(narrow.Length <= reserve.Length);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(-1, 1)]
    public void AReserveNarrowerThanOneDigitIsRejected(int digits, int places)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReadoutFormatter.WidestString(digits, places, true, Invariant));
    }

    [Fact]
    public void ANegativeDecimalCountIsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => ReadoutFormatter.Format(1.0, -1, Invariant));
    }

    // -------------------------------------------------------------------------------------
    // Spoken form
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A screen reader gets one phrase. The hair space is for the eye and is deliberately not in
    /// the spoken form, where an unusual whitespace character is either spelled out or swallowed.
    /// </summary>
    [Fact]
    public void TheSpokenFormIsOnePhraseWithAnOrdinarySpace()
    {
        string spoken = ReadoutFormatter.ToSpokenText("Time interval", "−33.1", "ns");

        Assert.Equal("Time interval: −33.1 ns", spoken);
        Assert.DoesNotContain(' ', spoken);
    }

    [Fact]
    public void TheSpokenFormOmitsPartsThatAreNotThere()
    {
        Assert.Equal("−33.1", ReadoutFormatter.ToSpokenText(null, "−33.1", null));
        Assert.Equal("Satellites: 6", ReadoutFormatter.ToSpokenText("Satellites", "6", null));
        Assert.Equal("6 dB", ReadoutFormatter.ToSpokenText(string.Empty, "6", "dB"));
    }
}
