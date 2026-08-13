using WinZ3805A.Device.Parsing;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// The §7.3 fast tier's single-value answers.
/// </summary>
/// <remarks>
/// Every case here uses a response shape actually observed on the reference unit, recorded in
/// <c>Fixtures/README.md</c> — including the leading space, which is the single most likely thing
/// to break a naive parse and is invisible in a bug report.
/// </remarks>
public class ScalarParsersTests
{
    /// <summary>
    /// Responses arrive as <c>_+3</c>, not <c>+3</c>. Trimming in one place is what stops that
    /// framing artefact reaching six separate call sites.
    /// </summary>
    [Theory]
    [InlineData(" +3", 3)]
    [InlineData("+3", 3)]
    [InlineData("  +0", 0)]
    [InlineData(" -12", -12)]
    [InlineData(" +1\r\n", 1)]
    public void AnIntegerParsesThroughItsLeadingSpaceAndExplicitSign(string response, int expected)
    {
        Assert.Equal(expected, ScalarParsers.ParseInteger(response));
    }

    [Theory]
    [InlineData(" -5.4E-009", -5.4e-9)]
    [InlineData(" -1.68245E+001", -16.8245)]
    [InlineData(" +7.70000E-008", 7.7e-8)]
    [InlineData(" 0", 0d)]
    public void ARealParsesInTheReceiversScientificNotation(string response, double expected)
    {
        double? actual = ScalarParsers.ParseDecimal(response);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, 12);
    }

    /// <summary>
    /// The receiver answers the time interval in seconds; everything that displays it works in
    /// nanoseconds. Converting once here keeps the factor of a billion out of the view models.
    /// </summary>
    [Fact]
    public void TheTimeIntervalConvertsFromSecondsToNanoseconds()
    {
        double? nanoseconds = ScalarParsers.ParseSecondsAsNanoseconds(" -5.4E-009");

        Assert.NotNull(nanoseconds);
        Assert.Equal(-5.4, nanoseconds.Value, 9);
    }

    [Fact]
    public void AKeywordIsUpperCasedSoComparisonsDoNotHaveToCare()
    {
        Assert.Equal("LOCK", ScalarParsers.ParseKeyword(" LOCK"));
        Assert.Equal("LOCK", ScalarParsers.ParseKeyword("lock"));
    }

    /// <summary>
    /// <c>:SYNC:HOLD:DUR?</c> answers <c>+6.00000E+002,0</c> — a value and a flag. Only the first
    /// field is the duration.
    /// </summary>
    [Fact]
    public void TheFirstFieldOfAListIsTakenWithoutTheRest()
    {
        double? seconds = ScalarParsers.ParseFirstOfList(" +6.00000E+002,0");

        Assert.NotNull(seconds);
        Assert.Equal(600d, seconds.Value, 6);
    }

    [Theory]
    [InlineData(" 0", false)]
    [InlineData(" 1", true)]
    [InlineData(" +1", true)]
    public void ABooleanIsSpeltAsZeroOrOne(string response, bool expected)
    {
        Assert.Equal(expected, ScalarParsers.ParseBoolean(response));
    }

    /// <summary>
    /// Nothing here throws, on the same principle as the screen parser: a poll that threw would
    /// take down the loop that produced it, and one odd reply an hour would then look like a dead
    /// application.
    /// </summary>
    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("E-113")]
    [InlineData("not a number")]
    [InlineData("\0ÿ")]
    public void AnUnparseableAnswerBecomesNullRatherThanAnException(string? response)
    {
        Assert.Null(ScalarParsers.ParseInteger(response));
        Assert.Null(ScalarParsers.ParseDecimal(response));
        Assert.Null(ScalarParsers.ParseSecondsAsNanoseconds(response));
        Assert.Null(ScalarParsers.ParseFirstOfList(response));
        Assert.Null(ScalarParsers.ParseBoolean(response));
    }

    /// <summary>
    /// The receiver is not localised. Parsing its output against a comma-decimal culture would
    /// silently produce the wrong number rather than failing, which is the worst kind of bug for a
    /// timing instrument.
    /// </summary>
    [Fact]
    public void ParsingDoesNotFollowTheCurrentCulture()
    {
        System.Globalization.CultureInfo original = Thread.CurrentThread.CurrentCulture;
        try
        {
            Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("de-DE");

            double? value = ScalarParsers.ParseDecimal(" -5.4E-009");

            Assert.NotNull(value);
            Assert.Equal(-5.4e-9, value.Value, 15);
        }
        finally
        {
            Thread.CurrentThread.CurrentCulture = original;
        }
    }
}
