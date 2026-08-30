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

    // ---- Engineering units -----------------------------------------------------------------

    /// <summary>
    /// A duration in seconds picks the unit that suits its magnitude, stepping in thousands.
    /// </summary>
    /// <remarks>
    /// The receiver reports every interval in seconds, and 2.7E-006 beside 1.0E-006 does not read
    /// as one being nearly three times the other. Only ns, µs, ms and s are produced, so a growing
    /// value never passes through a unit a reader has to stop and think about.
    /// </remarks>
    [Theory]
    [InlineData(2.7e-6, "2.7", "µs")]
    [InlineData(1.0e-6, "1.0", "µs")]
    [InlineData(3.31e-8, "33.1", "ns")]
    [InlineData(9.99e-10, "1.0", "ns")]
    [InlineData(4.5e-3, "4.5", "ms")]
    [InlineData(12.34, "12.3", "s")]  // Not a midpoint: half-way rounding is unspecified and is the plain formatter's business, not this one's.
    [InlineData(0.0, "0.0", "ns")]
    public void SecondsPicksTheEngineeringUnit(double seconds, string value, string unit) =>
        Assert.Equal((value, unit), ReadoutFormatter.Seconds(seconds));

    /// <remarks>
    /// A threshold is a setting rather than a measurement, so it shows the decimals it was set
    /// with — "1 µs" would hide the difference between what was asked for and what took effect.
    /// </remarks>
    [Fact]
    public void SecondsHonoursTheRequestedPrecision() =>
        Assert.Equal(("1.000", "µs"), ReadoutFormatter.Seconds(1.0e-6, decimalPlaces: 3));

    /// <remarks>U+2212, not a hyphen — the same §9.5.3 rule 4 the plain formatter follows.</remarks>
    [Fact]
    public void ANegativeIntervalUsesTheMinusSign()
    {
        (string value, string unit) = ReadoutFormatter.Seconds(-3.31e-8);

        Assert.StartsWith(ReadoutFormatter.MinusSign, value, StringComparison.Ordinal);
        Assert.Equal("ns", unit);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    public void AnUnreadableIntervalHasNoUnit(double? seconds) =>
        Assert.Equal((ReadoutFormatter.NoValue, string.Empty), ReadoutFormatter.Seconds(seconds));

    // -------------------------------------------------------------------------------------
    // §9.7.4's copy layer: the typesetting undone, for a value leaving the application
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The one that matters. A readout shows U+2212 MINUS SIGN because a hyphen reads badly beside
    /// lining figures; a spreadsheet handed U+2212 gets text rather than a number.
    /// </summary>
    [Fact]
    public void CopyingANegativeGivesAnAsciiHyphen()
    {
        string copied = Assert.IsType<string>(
            ReadoutFormatter.ToMachineText(ReadoutFormatter.Format(-2.9, 1)));

        Assert.Equal("-2.9", copied);
        Assert.Equal('-', copied[0]);
        Assert.DoesNotContain(ReadoutFormatter.MinusSign, copied, StringComparison.Ordinal);
    }

    /// <remarks>
    /// U+200A separates a value from its unit on screen and is invisible in a paste — which is the
    /// problem, not the excuse: a cell holding "33.1<em>&#x200a;</em>ns" is text, and it looks
    /// exactly like one holding a number.
    /// </remarks>
    [Fact]
    public void CopyingDropsTheHairSpace() =>
        Assert.Equal("33.1ns", ReadoutFormatter.ToMachineText($"33.1{ReadoutFormatter.HairSpace}ns"));

    /// <remarks>
    /// §11.1 makes the em dash mean <i>the device did not report one</i>. Pasting it would put a
    /// character that looks like data where the absence of data was the fact, so the menu item is
    /// disabled instead.
    /// </remarks>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("—")]
    public void NothingToCopyIsNull(string? text) => Assert.Null(ReadoutFormatter.ToMachineText(text));

    /// <remarks>
    /// Ordinary text passes through unchanged: the copy layer is not a sanitiser, and a device
    /// string that happens to contain a hyphen or a word must arrive as the receiver wrote it.
    /// </remarks>
    [Theory]
    [InlineData("Locked to GPS", "Locked to GPS")]
    [InlineData("10:15:38 Pacific Daylight Time", "10:15:38 Pacific Daylight Time")]
    [InlineData(" 3697A ", "3697A")]
    public void OrdinaryTextIsUnchangedApartFromTrimming(string text, string expected) =>
        Assert.Equal(expected, ReadoutFormatter.ToMachineText(text));
}
