using WinZ3805A.Device.Models;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// What a two-hour survey should leave behind in the log (P0-12, #12).
/// </summary>
/// <remarks>
/// §10.6 puts survey progress on the Position page and nowhere else, so a survey run with that page
/// closed left no trace at all. #185's own figures make a stall likely rather than hypothetical —
/// the receiver held four or more satellites for six per cent of a two-day window — and whether the
/// antenna move fixed that is exactly what the log of the first survey afterwards answers.
/// </remarks>
public class SurveyWatchTests
{
    private static SurveyNote Feed(SurveyWatch watch, params double?[] readings)
    {
        SurveyNote last = SurveyNote.None;

        foreach (double? reading in readings)
        {
            last = watch.Observe(reading, SurveySuspendedReason.None);
        }

        return last;
    }

    // -------------------------------------------------------------------------------------
    // The shape of a run
    // -------------------------------------------------------------------------------------

    [Fact]
    public void ASurveyBeginningIsWorthALine() =>
        Assert.Equal(SurveyNote.Started, new SurveyWatch().Observe(0, SurveySuspendedReason.None));

    /// <summary>A receiver that is not surveying is not news, however long it goes on not being.</summary>
    [Fact]
    public void NotSurveyingIsNeverNews()
    {
        SurveyWatch watch = new();

        Assert.Equal(SurveyNote.None, Feed(watch, null, null, null));
    }

    /// <remarks>
    /// The one that matters for a two-hour run polled every second: recording the state would write
    /// some seven thousand identical lines and bury the four that mean anything.
    /// </remarks>
    [Fact]
    public void StandingStillIsNotNews()
    {
        SurveyWatch watch = new();
        watch.Observe(12, SurveySuspendedReason.None);

        Assert.Equal(SurveyNote.None, Feed(watch, 12, 12, 12, 13, 14, 15));
    }

    [Fact]
    public void EachTenthOfTheWayIsWorthALine()
    {
        SurveyWatch watch = new();
        watch.Observe(0, SurveySuspendedReason.None);

        Assert.Equal(SurveyNote.Progressed, Feed(watch, 5, 10));
        Assert.Equal(SurveyNote.None, Feed(watch, 11, 19));
        Assert.Equal(SurveyNote.Progressed, Feed(watch, 20));
    }

    /// <remarks>
    /// Snapped to the step rather than to the reading. A survey that jumps 8 % to 21 % in one poll
    /// would otherwise shift every later milestone by one per cent, and the log would drift out of
    /// step with the round numbers a reader is scanning for.
    /// </remarks>
    [Fact]
    public void AJumpDoesNotShiftEveryLaterMilestone()
    {
        SurveyWatch watch = new();
        watch.Observe(8, SurveySuspendedReason.None);

        Assert.Equal(SurveyNote.Progressed, Feed(watch, 21));
        Assert.Equal(SurveyNote.None, Feed(watch, 29));
        Assert.Equal(SurveyNote.Progressed, Feed(watch, 30));
    }

    /// <summary>A survey that was running and no longer is has finished.</summary>
    [Fact]
    public void TheEndOfASurveyIsWorthALine()
    {
        SurveyWatch watch = new();
        watch.Observe(97, SurveySuspendedReason.None);

        Assert.Equal(SurveyNote.Finished, watch.Observe(null, SurveySuspendedReason.None));

        // And once said, not said again on every poll for the rest of the day.
        Assert.Equal(SurveyNote.None, Feed(watch, null, null));
    }

    // -------------------------------------------------------------------------------------
    // Stalls, which are what tomorrow is likely to produce
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// Reported at once, with no grace period, which is the opposite of <c>LockWatch</c> and
    /// deliberate: that class waits because an unnecessary notification trains a user to ignore the
    /// necessary ones. This is a log, and a thirty-second stall at the ninety-minute mark is
    /// precisely what somebody will want to find afterwards.
    /// </remarks>
    [Theory]
    [InlineData(SurveySuspendedReason.TooFewSatellites)]
    [InlineData(SurveySuspendedReason.PoorGeometry)]
    [InlineData(SurveySuspendedReason.NoTrackData)]
    [InlineData(SurveySuspendedReason.Other)]
    public void AStallIsReportedImmediately(SurveySuspendedReason reason)
    {
        SurveyWatch watch = new();
        watch.Observe(40, SurveySuspendedReason.None);

        Assert.Equal(SurveyNote.Suspended, watch.Observe(40, reason));
        Assert.Equal(reason, watch.Reason);
    }

    /// <summary>The same reason on the next poll is not news.</summary>
    [Fact]
    public void AStallIsNotRepeatedWhileItLasts()
    {
        SurveyWatch watch = new();
        watch.Observe(40, SurveySuspendedReason.None);
        watch.Observe(40, SurveySuspendedReason.TooFewSatellites);

        for (int i = 0; i < 500; i++)
        {
            Assert.Equal(SurveyNote.None, watch.Observe(40, SurveySuspendedReason.TooFewSatellites));
        }
    }

    /// <summary>A stall that becomes a different stall is a different line.</summary>
    /// <remarks>
    /// §11.3 decodes these to enum values precisely so the application can tell them apart, and
    /// "too few satellites" becoming "poor geometry" is a real change in what is wrong.
    /// </remarks>
    [Fact]
    public void AChangedReasonIsANewLine()
    {
        SurveyWatch watch = new();
        watch.Observe(40, SurveySuspendedReason.None);
        watch.Observe(40, SurveySuspendedReason.TooFewSatellites);

        Assert.Equal(SurveyNote.Suspended, watch.Observe(40, SurveySuspendedReason.PoorGeometry));
    }

    [Fact]
    public void PickingUpAgainIsWorthALine()
    {
        SurveyWatch watch = new();
        watch.Observe(40, SurveySuspendedReason.None);
        watch.Observe(40, SurveySuspendedReason.TooFewSatellites);

        Assert.Equal(SurveyNote.Resumed, watch.Observe(41, SurveySuspendedReason.None));
        Assert.Equal(SurveyNote.None, watch.Observe(42, SurveySuspendedReason.None));
    }

    /// <remarks>
    /// A stall outranks a milestone. The survey has stopped, so how far it got is the less
    /// interesting half of the sentence, and one line per poll is the budget.
    /// </remarks>
    [Fact]
    public void AStallOutranksAMilestone()
    {
        SurveyWatch watch = new();
        watch.Observe(10, SurveySuspendedReason.None);

        Assert.Equal(SurveyNote.Suspended, watch.Observe(50, SurveySuspendedReason.TooFewSatellites));
    }

    // -------------------------------------------------------------------------------------
    // A whole run, which is the thing this is actually for
    // -------------------------------------------------------------------------------------

    /// <remarks>
    /// Two hours at one poll a second, stalling twice on satellite count the way #185 expects. The
    /// assertion is not on the exact figure but on the order of magnitude: a log a person will read
    /// at a bench, not a transcript.
    /// </remarks>
    [Fact]
    public void ATwoHourRunLeavesALogSomeoneWouldRead()
    {
        SurveyWatch watch = new();
        List<SurveyNote> notes = [];

        const int polls = 2 * 60 * 60;

        for (int i = 0; i <= polls; i++)
        {
            double percent = 100.0 * i / polls;

            // Two stalls: eight minutes around a third of the way, four around three-quarters.
            SurveySuspendedReason reason =
                (i > 2400 && i < 2880) || (i > 5400 && i < 5640)
                    ? SurveySuspendedReason.TooFewSatellites
                    : SurveySuspendedReason.None;

            SurveyNote note = watch.Observe(percent, reason);
            if (note != SurveyNote.None)
            {
                notes.Add(note);
            }
        }

        notes.Add(watch.Observe(null, SurveySuspendedReason.None));

        Assert.Equal(SurveyNote.Started, notes[0]);
        Assert.Equal(SurveyNote.Finished, notes[^1]);
        Assert.Equal(2, notes.Count(n => n == SurveyNote.Suspended));
        Assert.Equal(2, notes.Count(n => n == SurveyNote.Resumed));

        // Ten milestones at most, and the whole run fits on a screen.
        Assert.InRange(notes.Count, 10, 20);
    }
}
