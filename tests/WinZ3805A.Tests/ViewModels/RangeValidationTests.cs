using WinZ3805A.Device.Commands;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §9.11's client-side range check — the reason the receiver is never sent a value it will refuse.
/// </summary>
public class RangeValidationTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(500)]
    [InlineData(999999)]
    public void AValueInsideTheRangeHasNothingToSay(double value) =>
        Assert.Null(RangeValidation.Describe(value, 0, 999999, "ns"));

    /// <summary>The bounds are inclusive: §10.7's field is labelled 0 – 999 999, not 1 – 999 998.</summary>
    [Theory]
    [InlineData(-0.001)]
    [InlineData(1000000)]
    public void AValueOutsideItDoesNot(double value) =>
        Assert.NotNull(RangeValidation.Describe(value, 0, 999999, "ns"));

    /// <summary>
    /// §9.11's own example sentence, verbatim. The separators are §9.5.2's — 999999 read off a
    /// screen is a different number from 999,999 at a glance.
    /// </summary>
    [Fact]
    public void ProducesTheSentenceSection911Quotes() =>
        Assert.Equal(
            "Enter a value between 0 and 999,999 ns.",
            RangeValidation.Describe(1e9, 0, 999999, "ns"));

    /// <summary>
    /// <b>NaN is what a <c>NumberBox</c> holds after unparseable text</b>, and it compares false
    /// against every bound — so a check that tested the bounds first would pass an unreadable
    /// field as valid and send whatever the box last held.
    /// </summary>
    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    [InlineData(null)]
    public void AFieldWithNoUsableNumberIsInvalid(double? value) =>
        Assert.NotNull(RangeValidation.Describe(value, 0, 999999, "ns"));

    /// <summary>A half-open range still says something useful about which half.</summary>
    [Fact]
    public void AOneSidedRangeNamesTheSideItHas()
    {
        Assert.Equal("Enter a value of at least 0 s.", RangeValidation.Describe(-1, 0, null, "s"));
        Assert.Equal("Enter a value of at most 90.", RangeValidation.Describe(91, null, 90));
    }

    /// <summary>With no bounds at all, only the absence of a number is worth reporting.</summary>
    [Fact]
    public void WithNoBoundsOnlyAnUnreadableFieldFails()
    {
        Assert.Null(RangeValidation.Describe(1e12, null, null));
        Assert.Equal("Enter a value.", RangeValidation.Describe(double.NaN, null, null));
    }

    /// <summary>Trailing zeros are noise in an instruction: "between 0 and 90", not "0.000000".</summary>
    [Fact]
    public void TheRangeIsWrittenWithoutTrailingZeros() =>
        Assert.Equal("Enter a value between 0 and 90 °.", RangeValidation.Describe(-1, 0, 90, "°"));

    // -------------------------------------------------------------------------------------
    // Against the catalog
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// The bounds come from the entry the command will actually be built from, so the field and the
    /// wire cannot disagree about what is acceptable.
    /// </summary>
    [Fact]
    public void ReadsTheBoundsOffTheCatalogParameter()
    {
        ParameterSpec mask = CommandCatalog.Find(":GPS:SAT:TRAC:EMANgle")!.Parameters[0];

        Assert.Null(RangeValidation.Describe(15, mask));
        Assert.Equal("Enter a value between 0 and 90 °.", RangeValidation.Describe(91, mask));
    }

    /// <summary>
    /// §10.7's field is in nanoseconds and so is §8.3's confirmation, while the receiver takes
    /// seconds. The parameter follows the two the user reads; the caller scales.
    /// </summary>
    [Fact]
    public void TheAntennaDelayParameterIsInTheUnitsTheUserSees()
    {
        ParameterSpec delay = CommandCatalog.Find(":GPS:REF:ADELay")!.Parameters[0];

        Assert.Equal("ns", delay.Unit);
        Assert.Equal(0, delay.Minimum);
        Assert.Equal(999999, delay.Maximum);
    }
}
