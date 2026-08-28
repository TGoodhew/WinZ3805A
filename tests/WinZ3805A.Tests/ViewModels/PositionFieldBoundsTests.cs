using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>§10.6's entry ranges, which P0-12 is verified by (#12).</summary>
/// <remarks>
/// <para>
/// <see cref="RangeValidation"/> already covered the mechanism, and covered it well. What nothing
/// reached was <b>which range each field is given</b> — seven literals in a XAML code-behind
/// constructor. The mechanism being right is not the same as the fields being right, and a latitude
/// quietly accepting 0–80 would have failed no test.
/// </para>
/// <para>
/// So these assert the numbers against the specification sentence rather than against the code:
/// "lat degrees 0–90, lon degrees 0–180, minutes 0–59, seconds 0–59.999 (0.001 resolution), height
/// −1000.00 to +18000.00 m (0.01 resolution)."
/// </para>
/// </remarks>
public class PositionFieldBoundsTests
{
    /// <summary>Latitude stops at the pole.</summary>
    /// <remarks>
    /// 90 rather than 180: the hemisphere is a separate field carrying N or S, so the degrees box
    /// spans one quadrant. Accepting 180 here would let a user enter a latitude that does not exist
    /// and have the receiver reject it — which §10.6 asks the form to prevent, in as many words:
    /// "reject client-side rather than letting the device error".
    /// </remarks>
    [Fact]
    public void LatitudeDegreesStopAtThePole()
    {
        Assert.Equal(0, PositionFieldBounds.LatitudeDegrees.Minimum);
        Assert.Equal(90, PositionFieldBounds.LatitudeDegrees.Maximum);
    }

    /// <summary>Longitude stops at the antimeridian.</summary>
    /// <remarks>
    /// 180, twice latitude, for the same reason in reverse: the hemisphere field carries E or W, so
    /// the degrees box spans half the globe rather than a quadrant. The two being different is the
    /// detail a single shared "degrees" bound would have lost.
    /// </remarks>
    [Fact]
    public void LongitudeDegreesStopAtTheAntimeridian()
    {
        Assert.Equal(0, PositionFieldBounds.LongitudeDegrees.Minimum);
        Assert.Equal(180, PositionFieldBounds.LongitudeDegrees.Maximum);
    }

    /// <summary>Minutes are sexagesimal, so they stop at 59 rather than 60.</summary>
    [Theory]
    [MemberData(nameof(MinuteFields))]
    public void MinutesStopAtFiftyNine(PositionFieldBound bound)
    {
        Assert.Equal(0, bound.Minimum);
        Assert.Equal(59, bound.Maximum);
    }

    /// <summary>Seconds stop just short of 60, at the resolution §10.6 gives the field.</summary>
    /// <remarks>
    /// 59.999 rather than 60: a whole 60 seconds is the next minute, and the third decimal is the
    /// 0.001 resolution the specification names — so the largest value the field can hold is the
    /// largest one that is still this minute.
    /// </remarks>
    [Theory]
    [MemberData(nameof(SecondFields))]
    public void SecondsStopJustShortOfTheNextMinute(PositionFieldBound bound)
    {
        Assert.Equal(0, bound.Minimum);
        Assert.Equal(59.999, bound.Maximum);
    }

    /// <summary>Height spans below sea level to well above any surveyed antenna.</summary>
    [Fact]
    public void HeightSpansBelowSeaLevelToWellAbove()
    {
        Assert.Equal(-1000, PositionFieldBounds.Height.Minimum);
        Assert.Equal(18000, PositionFieldBounds.Height.Maximum);
    }

    /// <summary>Every field has a usable range and a unit to show beside it.</summary>
    /// <remarks>
    /// A table rather than seven repetitions, so a field added later is checked for the properties
    /// all of them need without anybody remembering to write the test.
    /// </remarks>
    [Fact]
    public void EveryFieldIsUsable()
    {
        Assert.Equal(7, PositionFieldBounds.All.Count);

        foreach (PositionFieldBound bound in PositionFieldBounds.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(bound.Field));
            Assert.False(string.IsNullOrWhiteSpace(bound.Unit));
            Assert.True(bound.Maximum > bound.Minimum, $"{bound.Field} has an empty range");
        }
    }

    /// <summary>The bounds are the ones the validator enforces, end to end.</summary>
    /// <remarks>
    /// Runs each field's own limits through <see cref="RangeValidation"/> — the code the page
    /// actually calls — so this is not merely asserting that a record holds the numbers it was given.
    /// Both ends are accepted and both neighbours rejected, which is the accept/reject boundary
    /// §10.6's "reject client-side" turns on.
    /// </remarks>
    [Fact]
    public void EachBoundIsEnforcedAtItsEdges()
    {
        foreach (PositionFieldBound bound in PositionFieldBounds.All)
        {
            Assert.Null(RangeValidation.Describe(bound.Minimum, bound.Minimum, bound.Maximum, bound.Unit));
            Assert.Null(RangeValidation.Describe(bound.Maximum, bound.Minimum, bound.Maximum, bound.Unit));

            Assert.NotNull(RangeValidation.Describe(bound.Minimum - 0.001, bound.Minimum, bound.Maximum, bound.Unit));
            Assert.NotNull(RangeValidation.Describe(bound.Maximum + 0.001, bound.Minimum, bound.Maximum, bound.Unit));
        }
    }

    public static TheoryData<PositionFieldBound> MinuteFields() =>
        new(PositionFieldBounds.LatitudeMinutes, PositionFieldBounds.LongitudeMinutes);

    public static TheoryData<PositionFieldBound> SecondFields() =>
        new(PositionFieldBounds.LatitudeSeconds, PositionFieldBounds.LongitudeSeconds);
}
