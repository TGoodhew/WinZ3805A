using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// §9.10.2's arrow-key cursor, over a constellation that changes underneath it.
/// </summary>
/// <remarks>
/// The other half of the sky plot that can be wrong silently, and it was. The cursor used to be an
/// index into a list rebuilt and re-sorted on every reading, so a satellite acquired at a lower PRN
/// shifted the ring onto a different satellite with nothing said, and Enter selected one the user
/// had never been on. The cases below are the ones a bench cannot produce on demand: they need a
/// satellite to appear, or disappear, at a chosen moment.
/// </remarks>
public class SkyPlotCursorTests
{
    /// <summary>A constellation with room either side of PRN 12 for one to appear.</summary>
    private static readonly int[] Plotted = [5, 9, 14, 20, 31];

    // -------------------------------------------------------------------------------------
    // Ordinary movement
    // -------------------------------------------------------------------------------------

    [Theory]
    [InlineData(5, 9)]
    [InlineData(9, 14)]
    [InlineData(14, 20)]
    [InlineData(20, 31)]
    public void ForwardMovesToTheNextPrn(int from, int expected) =>
        Assert.Equal(expected, SkyPlotCursor.Step(Plotted, from, 1));

    [Theory]
    [InlineData(31, 20)]
    [InlineData(20, 14)]
    [InlineData(14, 9)]
    [InlineData(9, 5)]
    public void BackMovesToThePreviousPrn(int from, int expected) =>
        Assert.Equal(expected, SkyPlotCursor.Step(Plotted, from, -1));

    /// <summary>The order is a ring, so the ends join.</summary>
    [Fact]
    public void TheOrderWrapsAtBothEnds()
    {
        Assert.Equal(5, SkyPlotCursor.Step(Plotted, 31, 1));
        Assert.Equal(31, SkyPlotCursor.Step(Plotted, 5, -1));
    }

    /// <summary>Before the arrows have been pressed: forward to the first, back to the last.</summary>
    [Fact]
    public void AnUnsetCursorEntersAtTheEndItIsMovingFrom()
    {
        Assert.Equal(5, SkyPlotCursor.Step(Plotted, null, 1));
        Assert.Equal(31, SkyPlotCursor.Step(Plotted, null, -1));
    }

    // -------------------------------------------------------------------------------------
    // The defect: the constellation changes under the cursor
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// The exact failure, with the old behaviour asserted beside the new one so the fix cannot be
    /// quietly undone. The user is on PRN 20, index 3 of five. PRN 7 is acquired; index 3 now names
    /// PRN 14, so an index-keyed cursor has moved the ring onto a different satellite and said
    /// nothing. Keyed on the PRN it is still on 20, which has simply become index 4.
    /// </remarks>
    [Fact]
    public void AcquiringALowerPrnDoesNotMoveTheCursor()
    {
        int[] acquired = [5, 7, 9, 14, 20, 31];

        int wasAt = SkyPlotCursor.IndexOf(Plotted, 20);
        Assert.Equal(3, wasAt);
        Assert.Equal(14, acquired[wasAt]);          // what the old cursor would now be on

        Assert.Equal(4, SkyPlotCursor.IndexOf(acquired, 20));
        Assert.Equal(31, SkyPlotCursor.Step(acquired, 20, 1));
        Assert.Equal(14, SkyPlotCursor.Step(acquired, 20, -1));
    }

    /// <remarks>
    /// The same in the other direction, and it was worse: losing the two lowest PRNs put index 3
    /// past the end of a three-satellite list, so the old code clamped it to the last one. The user
    /// was on PRN 20 and the ring jumped to PRN 31.
    /// </remarks>
    [Fact]
    public void LosingALowerPrnDoesNotMoveTheCursor()
    {
        int[] lost = [14, 20, 31];

        int wasAt = SkyPlotCursor.IndexOf(Plotted, 20);
        Assert.True(wasAt >= lost.Length);
        Assert.Equal(31, lost[^1]);                 // where the old clamp would have landed

        Assert.Equal(1, SkyPlotCursor.IndexOf(lost, 20));
        Assert.Equal(31, SkyPlotCursor.Step(lost, 20, 1));
        Assert.Equal(14, SkyPlotCursor.Step(lost, 20, -1));
    }

    /// <remarks>
    /// The satellite the user was on has gone. The cursor keeps its PRN — the control draws no ring
    /// for it and Enter does nothing — and the next arrow resumes from the gap it left rather than
    /// from the start of the order. PRN 12 sorts between 9 and 14, so forward reaches 14 and back
    /// reaches 9.
    /// </remarks>
    [Fact]
    public void AMissingPrnResumesFromTheGapItLeft()
    {
        Assert.Equal(14, SkyPlotCursor.Step(Plotted, 12, 1));
        Assert.Equal(9, SkyPlotCursor.Step(Plotted, 12, -1));
    }

    /// <summary>A missing PRN below every plotted one behaves as an unset cursor does.</summary>
    [Fact]
    public void AMissingPrnBelowThemAllWrapsLikeAnUnsetCursor()
    {
        Assert.Equal(5, SkyPlotCursor.Step(Plotted, 1, 1));
        Assert.Equal(31, SkyPlotCursor.Step(Plotted, 1, -1));
    }

    /// <summary>And one above them all wraps the other way.</summary>
    [Fact]
    public void AMissingPrnAboveThemAllWrapsForward()
    {
        Assert.Equal(5, SkyPlotCursor.Step(Plotted, 32, 1));
        Assert.Equal(31, SkyPlotCursor.Step(Plotted, 32, -1));
    }

    /// <remarks>
    /// A satellite at the mask edge drops out and returns within a reading or two. Because the
    /// cursor kept the PRN rather than being reset or clamped, the ring comes back onto the same
    /// satellite and the user's place was never lost.
    /// </remarks>
    [Fact]
    public void APrnThatFlickersOutAndBackKeepsThePlace()
    {
        int[] without = [5, 9, 20, 31];

        // Gone: no exact position, so the cursor is not on a plotted satellite.
        Assert.Equal(-1, SkyPlotCursor.IndexOf(without, 14));

        // Back: the same PRN is found again, and stepping from it behaves exactly as before.
        Assert.Equal(2, SkyPlotCursor.IndexOf(Plotted, 14));
        Assert.Equal(20, SkyPlotCursor.Step(Plotted, 14, 1));
    }

    // -------------------------------------------------------------------------------------
    // Degenerate constellations
    // -------------------------------------------------------------------------------------

    /// <summary>Nothing plotted means nowhere to be, whatever the cursor was on.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void AnEmptyPlotLeavesTheCursorNowhere(int delta)
    {
        Assert.Null(SkyPlotCursor.Step([], 20, delta));
        Assert.Null(SkyPlotCursor.Step([], null, delta));
    }

    /// <summary>One satellite is a ring of one: every arrow press stays on it.</summary>
    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void ASinglePlottedSatelliteAbsorbsEveryStep(int delta)
    {
        Assert.Equal(20, SkyPlotCursor.Step([20], 20, delta));
        Assert.Equal(20, SkyPlotCursor.Step([20], null, delta));
        Assert.Equal(20, SkyPlotCursor.Step([20], 7, delta));
    }

    /// <summary>
    /// A step larger than the constellation still lands somewhere real.
    /// </summary>
    /// <remarks>
    /// Only ±1 is sent today, but the wrap is written as arithmetic rather than as two branches and
    /// arithmetic that is only ever exercised at one value is arithmetic nobody has checked.
    /// </remarks>
    [Fact]
    public void AStepLargerThanThePlotStillWraps()
    {
        Assert.Equal(9, SkyPlotCursor.Step(Plotted, 5, 6));
        Assert.Equal(31, SkyPlotCursor.Step(Plotted, 5, -6));
        Assert.Equal(20, SkyPlotCursor.Step(Plotted, 12, 7));
    }

    /// <summary>A step of nothing is not a move.</summary>
    [Fact]
    public void AZeroStepStaysPut() => Assert.Equal(14, SkyPlotCursor.Step(Plotted, 14, 0));

    [Fact]
    public void TheOrderIsRequiredAndSaysSo() =>
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = SkyPlotCursor.Step(null!, 20, 1);
        });
}
