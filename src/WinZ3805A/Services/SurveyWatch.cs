using WinZ3805A.Device.Models;

namespace WinZ3805A.Services;

/// <summary>What, if anything, about the survey is worth writing down (§10.6, P0-12).</summary>
public enum SurveyNote
{
    /// <summary>Nothing new. The answer on almost every poll.</summary>
    None = 0,

    /// <summary>A survey has begun.</summary>
    Started,

    /// <summary>
    /// A survey was already under way when this watch first looked, so its progress so far is not
    /// something this session witnessed.
    /// </summary>
    AlreadyRunning,

    /// <summary>It has passed another tenth of the way.</summary>
    Progressed,

    /// <summary>The receiver has stopped accumulating, and said why (§11.3).</summary>
    Suspended,

    /// <summary>It is accumulating again after a suspension.</summary>
    Resumed,

    /// <summary>It has finished.</summary>
    Finished,
}

/// <summary>
/// Turns a stream of survey readings into the handful of lines a log should carry (P0-12, #12).
/// </summary>
/// <remarks>
/// <para>
/// A survey runs for about two hours and its progress arrives with the full status screen — §7.3's
/// 10 s tier — so recording the state would produce some seven hundred near-identical lines and
/// bury the four that matter. This records
/// <b>transitions</b>: it began, it passed another tenth, it stalled and why, it picked up again,
/// it finished.
/// </para>
/// <para>
/// <b>A suspension is reported immediately, with no grace period</b>, which is the opposite of
/// <see cref="LockWatch"/> and deliberately so. That class waits before speaking because a
/// notification the user did not need trains them to ignore the ones they do. This is a log, not an
/// interruption, and the thing being logged is a deliberate two-hour operation somebody is waiting
/// on — a thirty-second stall at the ninety-minute mark is exactly what they will want to find
/// afterwards, not noise to be filtered out. Repetition is still suppressed: the same reason on the
/// next poll is not news.
/// </para>
/// <para>
/// <b>Why this exists at all.</b> §10.6 shows survey progress on the Position page and nowhere else,
/// so a survey run with that page closed left no trace. #185's own figures say a stall is likely
/// rather than hypothetical — the receiver held four or more satellites for six per cent of a
/// two-day window — and whether the antenna move fixed that is precisely what the log of the first
/// survey afterwards answers.
/// </para>
/// <para>
/// Pure, and deliberately: every decision is a function of the previous reading and this one, so
/// the two-hour run it is written for can be replayed in a millisecond.
/// </para>
/// </remarks>
public sealed class SurveyWatch
{
    /// <summary>How far the survey must move before another line is worth writing.</summary>
    /// <remarks>
    /// Ten steps across two hours is one line roughly every twelve minutes — enough to see the rate
    /// and to spot a survey that has quietly stopped advancing, few enough to read at a glance.
    /// </remarks>
    public const double ProgressStep = 10;

    private double? _percent;
    private SurveySuspendedReason _reason = SurveySuspendedReason.None;
    private double _lastReported = double.NegativeInfinity;
    private bool _running;
    private bool _observed;

    /// <summary>The reason last reported, so a caller can name it without re-reading the status.</summary>
    public SurveySuspendedReason Reason => _reason;

    /// <summary>The percentage last seen, for the same purpose.</summary>
    public double? Percent => _percent;

    /// <summary>Forgets everything, as though no survey had been seen.</summary>
    public void Reset()
    {
        _percent = null;
        _reason = SurveySuspendedReason.None;
        _lastReported = double.NegativeInfinity;
        _running = false;
        _observed = false;
    }

    /// <summary>Folds one reading in and says what it changed.</summary>
    /// <param name="percent">
    /// How far along, or <see langword="null"/> when no survey is running — which is both the state
    /// before one starts and the state after one ends.
    /// </param>
    /// <param name="reason">Why the receiver has stopped accumulating, if it has (§11.3).</param>
    public SurveyNote Observe(double? percent, SurveySuspendedReason reason)
    {
        SurveyNote note = Decide(percent, reason);

        _percent = percent;
        _reason = reason;
        _observed = true;

        return note;
    }

    private SurveyNote Decide(double? percent, SurveySuspendedReason reason)
    {
        if (percent is not double now)
        {
            // A survey that was running and is no longer is one that finished. A percentage that
            // was never there is simply the ordinary state of a receiver that is not surveying.
            bool finished = _running;
            _running = false;
            _lastReported = double.NegativeInfinity;
            return finished ? SurveyNote.Finished : SurveyNote.None;
        }

        if (!_running)
        {
            // "Started" is a claim about a transition, so it is only honest when this watch saw one:
            // either it has a previous reading to compare against, or it caught the survey at zero.
            // A first-ever reading partway through means the survey was already running, and the
            // progress it shows belongs to some earlier session.
            //
            // Not hypothetical. This class is a singleton created at startup, so every restart makes
            // a fresh one, and a restart during the 27 Aug survey wrote "Position survey started at
            // 15.5 %" - a sentence that reads as a survey restarting and losing its first quarter.
            // Whether a two-hour run restarted is exactly the question someone opens this log to
            // answer, and it was the one thing the log got wrong.
            bool witnessed = _observed || now <= 0;

            _running = true;
            _lastReported = now;
            return witnessed ? SurveyNote.Started : SurveyNote.AlreadyRunning;
        }

        // A stall and its reason outrank a progress step: the survey has stopped, so how far it got
        // is the less interesting half of the sentence.
        if (reason != _reason)
        {
            return reason == SurveySuspendedReason.None ? SurveyNote.Resumed : SurveyNote.Suspended;
        }

        if (now >= _lastReported + ProgressStep)
        {
            // Snapped to the step rather than set to the reading, so a survey that jumps from 8 %
            // to 21 % does not shift every later milestone by one per cent.
            _lastReported = Math.Floor(now / ProgressStep) * ProgressStep;
            return SurveyNote.Progressed;
        }

        return SurveyNote.None;
    }
}
