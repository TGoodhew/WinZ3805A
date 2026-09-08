using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// What may reach the elevation-mask slider, and what may come back off it.
/// </summary>
/// <remarks>
/// <para>
/// These pin a crash rather than a preference. The Satellites page carried
/// <c>Value="{x:Bind MaskBox.Value, Mode=TwoWay}"</c> between the mask slider and the mask
/// <c>NumberBox</c> — the obvious way to write "two controls, one value". An empty
/// <c>NumberBox</c> holds <see cref="double.NaN"/>, which is its own representation of "no value"
/// and which this page sets deliberately; <c>Slider</c> inherits <c>RangeBase</c>, whose
/// <c>Value</c> setter throws on NaN. The binding initialises during <c>Loading</c>, so opening the
/// page terminated the process for every receiver, every time.
/// </para>
/// <para>
/// <b>Nothing here could have caught that, and that is the point of extracting the arithmetic.</b>
/// The defect lived in a XAML attribute and could only be observed by running the application and
/// watching it die. What can be tested is the rule that replaced it, so that a future edit which
/// re-introduces a raw assignment has something to fail against.
/// </para>
/// </remarks>
public sealed class MaskSliderMathTests
{
    /// <summary>The case that crashed the application.</summary>
    [Fact]
    public void AnEmptyFieldParksTheSliderAtItsMinimumRatherThanHandingItNaN()
    {
        double value = MaskSliderMath.Coerce(double.NaN, 0, 90);

        Assert.False(double.IsNaN(value));
        Assert.Equal(0, value);
    }

    /// <remarks>
    /// <c>RangeBase</c> rejects infinity with the same exception as NaN, and a field that can hold
    /// one can hold the other.
    /// </remarks>
    [Theory]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InfinityIsRefusedTheSameWay(double infinite)
    {
        double value = MaskSliderMath.Coerce(infinite, 5, 90);

        Assert.False(double.IsInfinity(value));
        Assert.Equal(5, value);
    }

    [Theory]
    [InlineData(10, 10)]
    [InlineData(-5, 0)]
    [InlineData(120, 90)]
    [InlineData(0, 0)]
    [InlineData(90, 90)]
    public void AKnownMaskArrivesUnchangedUnlessItIsOffTheTrack(double given, double expected) =>
        Assert.Equal(expected, MaskSliderMath.Coerce(given, 0, 90));

    /// <remarks>
    /// A reversed range would make <c>Math.Clamp</c> throw, trading one crash on this page for
    /// another. The bounds come from a driver's catalog entry, so they are data rather than a
    /// constant anyone has checked.
    /// </remarks>
    [Fact]
    public void AReversedRangeIsSurvivedRatherThanThrown()
    {
        double value = MaskSliderMath.Coerce(45, 90, 0);

        Assert.Equal(45, value);
        Assert.Equal(0, MaskSliderMath.Coerce(double.NaN, 90, 0));
    }

    // -------------------------------------------------------------------------------------
    // Writing back
    // -------------------------------------------------------------------------------------

    /// <summary>
    /// A slider parked against an empty field must not invent a mask of zero.
    /// </summary>
    /// <remarks>
    /// This is #320's lesson applied to a second control: a hard-coded 10 read as the receiver's
    /// own mask and was not one, and "a default that is right by luck is a default nobody checks".
    /// A slider has no way to display "nothing", so its resting position is not a value the user
    /// chose, and writing it back would turn "the receiver has not said" into "0 degrees".
    /// </remarks>
    [Fact]
    public void AParkedSliderAgainstAnEmptyFieldIsNotWrittenBack() =>
        Assert.False(MaskSliderMath.ShouldWriteBack(sliderValue: 0, fieldValue: double.NaN, minimum: 0));

    /// <summary>But moving it off the parked position is a choice, and does count.</summary>
    [Fact]
    public void MovingTheSliderAwayFromTheMinimumFillsAnEmptyField() =>
        Assert.True(MaskSliderMath.ShouldWriteBack(sliderValue: 15, fieldValue: double.NaN, minimum: 0));

    /// <summary>A slider that already agrees with the field has nothing to say.</summary>
    /// <remarks>
    /// Without this the two controls echo: the field writes the slider, the slider writes the field
    /// back, and each write marks the mask as user-edited — which would stop the receiver's own
    /// value from ever seeding the editor again.
    /// </remarks>
    [Fact]
    public void ASliderThatMatchesTheFieldIsNotWrittenBack() =>
        Assert.False(MaskSliderMath.ShouldWriteBack(sliderValue: 20, fieldValue: 20, minimum: 0));

    [Fact]
    public void AMovedSliderIsWrittenBack() =>
        Assert.True(MaskSliderMath.ShouldWriteBack(sliderValue: 25, fieldValue: 20, minimum: 0));

    /// <summary>
    /// A minimum that is not zero parks there instead, and the same refusal applies.
    /// </summary>
    /// <remarks>
    /// The bounds come from the driver's catalog entry rather than from XAML, so a family whose
    /// mask starts at 5 degrees is an ordinary case rather than a hypothetical one.
    /// </remarks>
    [Fact]
    public void TheParkedPositionFollowsTheMinimumRatherThanBeingZero()
    {
        Assert.False(MaskSliderMath.ShouldWriteBack(sliderValue: 5, fieldValue: double.NaN, minimum: 5));
        Assert.True(MaskSliderMath.ShouldWriteBack(sliderValue: 0, fieldValue: double.NaN, minimum: 5));
    }
}
