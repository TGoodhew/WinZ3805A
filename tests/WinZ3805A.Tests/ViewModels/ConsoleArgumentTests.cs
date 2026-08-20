using WinZ3805A.Device.Commands;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §10.11's parameter entry: typed, range-validated, and never passed through.
/// </summary>
/// <remarks>
/// The last of those is the one worth testing hardest. §8.1 says no code path builds a command from
/// arbitrary user input, and a console whose whole job is to take user input keeps that true by
/// parsing every value and re-rendering it — so what reaches the wire is a string this application
/// wrote, not one the user typed.
/// </remarks>
public sealed class ConsoleArgumentTests
{
    private static ParameterSpec Integer(double? min = null, double? max = null) =>
        new("Mask", ParameterKind.Integer, Minimum: min, Maximum: max);

    // ------------------------------------------------------------------------------- no parameter

    [Fact]
    public void ACommandWithNoParameterNeedsNoValue()
    {
        ConsoleArgument.Result result = ConsoleArgument.For(null, "ignored");

        Assert.True(result.IsValid);
        Assert.Null(result.Text);
    }

    [Fact]
    public void ARequiredParameterLeftEmptyIsRefused()
    {
        ConsoleArgument.Result result = ConsoleArgument.For(Integer(), "  ");

        Assert.False(result.IsValid);
        Assert.Contains("Mask", result.Error!, StringComparison.Ordinal);
    }

    [Fact]
    public void AnOptionalParameterMayBeOmitted()
    {
        ParameterSpec optional = new("Entry", ParameterKind.Integer, IsOptional: true);

        ConsoleArgument.Result result = ConsoleArgument.For(optional, string.Empty);

        Assert.True(result.IsValid);
        Assert.Null(result.Text);
    }

    // ------------------------------------------------------------------------------------ numbers

    [Fact]
    public void AWholeNumberInRangeIsAccepted() =>
        Assert.Equal("10", ConsoleArgument.For(Integer(0, 89), "10").Text);

    [Fact]
    public void SurroundingSpaceDoesNotSurviveTheTrip() =>
        Assert.Equal("10", ConsoleArgument.For(Integer(0, 89), "  10  ").Text);

    [Theory]
    [InlineData("-1")]
    [InlineData("90")]
    public void AValueOutsideTheRangeIsRefusedWithTheRange(string value)
    {
        ConsoleArgument.Result result = ConsoleArgument.For(Integer(0, 89), value);

        Assert.False(result.IsValid);
        Assert.Equal("Enter a value between 0 and 89.", result.Error);
    }

    [Fact]
    public void ARangeErrorCarriesTheUnitWhenThereIsOne()
    {
        ParameterSpec delay = new("Delay", ParameterKind.Integer, Unit: "ns", Minimum: 0, Maximum: 999999);

        Assert.Equal(
            "Enter a value between 0 and 999999 ns.",
            ConsoleArgument.For(delay, "1000000").Error);
    }

    [Fact]
    public void AFractionalValueIsRefusedForAWholeNumberParameter() =>
        Assert.Contains(
            "whole number",
            ConsoleArgument.For(Integer(0, 89), "10.5").Error!,
            StringComparison.Ordinal);

    [Fact]
    public void ADecimalParameterKeepsItsFraction()
    {
        ParameterSpec spec = new("Threshold", ParameterKind.Decimal, Minimum: 0, Maximum: 10);

        Assert.Equal("1.25", ConsoleArgument.For(spec, "1.25").Text);
    }

    [Theory]
    [InlineData("ten")]
    [InlineData("NaN")]
    [InlineData("")]
    [InlineData("1,2")]
    public void SomethingThatIsNotANumberIsRefused(string value) =>
        Assert.False(ConsoleArgument.For(Integer(0, 89), value).IsValid);

    [Theory]
    [InlineData("Infinity")]
    [InlineData("-Infinity")]
    public void AnInfinityIsRefusedRatherThanClamped(string value) =>
        Assert.False(ConsoleArgument.For(Integer(), value).IsValid);

    /// <summary>
    /// A numeric parameter carrying Choices is a fixed set of legal values, not a range — the baud
    /// rates are the case. 4800 sits between two of them and is not one of them.
    /// </summary>
    [Fact]
    public void ANumericParameterWithAFixedSetRefusesAValueBetweenTwoOfThem()
    {
        ParameterSpec baud = new("Baud", ParameterKind.Integer, Choices: ["1200", "2400", "9600", "19200"]);

        Assert.False(ConsoleArgument.For(baud, "4800").IsValid);
        Assert.Equal("9600", ConsoleArgument.For(baud, "9600").Text);
    }

    // ----------------------------------------------------------------------------------- keywords

    [Fact]
    public void AKeywordIsMatchedAgainstTheCatalogsOwnList()
    {
        ParameterSpec spec = new("Parity", ParameterKind.Keyword, Choices: ["NONE", "EVEN", "ODD"]);

        Assert.Equal("EVEN", ConsoleArgument.For(spec, "EVEN").Text);
        Assert.False(ConsoleArgument.For(spec, "MARK").IsValid);
    }

    /// <summary>
    /// Matched case-insensitively, sent in the catalog's spelling. The user's own text never
    /// reaches the wire even when it names a legal value.
    /// </summary>
    [Fact]
    public void TheCatalogsSpellingIsWhatIsSent()
    {
        ParameterSpec spec = new("Parity", ParameterKind.Keyword, Choices: ["NONE", "EVEN", "ODD"]);

        Assert.Equal("EVEN", ConsoleArgument.For(spec, "even").Text);
    }

    // ---------------------------------------------------------------------------------- PRN lists

    [Fact]
    public void APrnListIsParsedAndReRendered()
    {
        ParameterSpec spec = new("Satellites", ParameterKind.PrnList);

        Assert.Equal("3,17,28", ConsoleArgument.For(spec, " 3, 17 ,28 ").Text);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("33")]
    [InlineData("-4")]
    public void APrnOutsideOneToThirtyTwoIsRefused(string value) =>
        Assert.False(ConsoleArgument.For(new ParameterSpec("Satellites", ParameterKind.PrnList), value).IsValid);

    /// <summary>
    /// The injection case, and the reason a PRN list is parsed rather than passed through. A
    /// semicolon is SCPI's command separator: a value carrying one, if it reached the wire, would
    /// append a second command to a message the catalog authorised only the first half of.
    /// </summary>
    [Theory]
    [InlineData("3;*RST")]
    [InlineData("3 ; 17")]
    [InlineData("3,17;:SYST:PRESet")]
    [InlineData("3\n17")]
    public void AValueCarryingACommandSeparatorCannotSurvive(string value)
    {
        ConsoleArgument.Result result = ConsoleArgument.For(
            new ParameterSpec("Satellites", ParameterKind.PrnList), value);

        Assert.False(result.IsValid);
        Assert.Null(result.Text);
    }

    /// <summary>
    /// And the general statement, over every kind the console will format: whatever comes out
    /// contains only characters the grammar of that kind allows. Nothing is escaped or stripped —
    /// a value that would need escaping simply fails to parse.
    /// </summary>
    [Theory]
    [InlineData(ParameterKind.Integer)]
    [InlineData(ParameterKind.Decimal)]
    [InlineData(ParameterKind.PrnList)]
    public void NothingFormattedEverContainsASeparator(ParameterKind kind)
    {
        ParameterSpec spec = new("Value", kind);

        foreach (string attempt in new[] { "1;2", "1 2", "1:2", "*RST", "1\r\n2", "1|2" })
        {
            string? text = ConsoleArgument.For(spec, attempt).Text;

            Assert.True(
                text is null || text.All(character => char.IsAsciiDigit(character) || character is '.' or ',' or '-'),
                $"\"{attempt}\" formatted as \"{text}\".");
        }
    }
}
