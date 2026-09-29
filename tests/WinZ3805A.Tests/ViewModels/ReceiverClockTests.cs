using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Models;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// #560 — the main window's clock ticks every second instead of every ten.
/// </summary>
/// <remarks>
/// The clock is the screen's date, the one-second sweep's time of day, and the PC's clock between
/// readings. These pin each part, the midnight case where the date and the time come from readings
/// ten seconds apart, and the rule that an overdue clock stops rather than inventing seconds.
/// </remarks>
public sealed class ReceiverClockTests
{
    private static readonly DateTimeOffset Pc = new(2026, 9, 29, 19, 40, 20, TimeSpan.Zero);

    /// <summary>The screen as the bench Z3805A prints it, rollover-corrected, on UTC.</summary>
    private static readonly DateTimeOffset Screen = new(2026, 9, 29, 19, 40, 10, TimeSpan.Zero);

    private static DateTimeOffset? At(
        TimeSpan sincePcStart,
        TimeSpan? timeOfDay = null,
        TimeSpan? timeReadAfterStart = null,
        DateTimeOffset? screen = null) =>
        ReceiverClock.Now(
            screen ?? Screen,
            Pc,
            timeOfDay,
            timeReadAfterStart is TimeSpan after ? Pc + after : null,
            Pc + sincePcStart);

    [Fact]
    public void WithNoScreenThereIsNothingToShow() =>
        Assert.Null(ReceiverClock.Now(null, null, new TimeSpan(19, 40, 16), Pc, Pc));

    /// <summary>The screen alone still gives a clock, and it now ticks between screens.</summary>
    [Fact]
    public void TheScreenAloneCountsForward()
    {
        Assert.Equal(Screen, At(TimeSpan.Zero));
        Assert.Equal(Screen.AddSeconds(3), At(TimeSpan.FromSeconds(3)));
        Assert.Equal(Screen.AddSeconds(9), At(TimeSpan.FromSeconds(9)));
    }

    /// <summary>
    /// <b>The case #560 is for.</b> Each sweep's time of day re-anchors the clock on the screen's
    /// date, and the clock counts on from it until the next.
    /// </summary>
    [Fact]
    public void TheSweepsTimeOfDayReanchorsTheClock()
    {
        TimeSpan read = new(19, 40, 14);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 19, 40, 14, TimeSpan.Zero),
            At(TimeSpan.FromSeconds(4), read, TimeSpan.FromSeconds(4)));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 19, 40, 15, TimeSpan.Zero),
            At(TimeSpan.FromSeconds(5), read, TimeSpan.FromSeconds(4)));
    }

    /// <summary>
    /// Across midnight the screen still says yesterday while the sweep says just after 00:00. The
    /// day turns with the time, not ten seconds later with the next screen.
    /// </summary>
    [Fact]
    public void AtMidnightTheDateTurnsWithTheTime()
    {
        DateTimeOffset beforeMidnight = new(2026, 9, 29, 23, 59, 55, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 30, 0, 0, 2, TimeSpan.Zero),
            At(TimeSpan.FromSeconds(7), new TimeSpan(0, 0, 2), TimeSpan.FromSeconds(7), beforeMidnight));
    }

    /// <summary>
    /// <b>Seen on the bench, 29 Sep 2026.</b> The screen's time is stamped as the receiver starts
    /// sending it, and the screen took 2.7 s to arrive. Anchoring on it because it arrived last put
    /// the clock back from :24 to :22. The fresh one-line time of day from the sweep before it is
    /// the better anchor, and the clock keeps going forward.
    /// </summary>
    [Fact]
    public void AScreenArrivingDoesNotPutTheClockBack()
    {
        DateTimeOffset screenStampedAtStart = new(2026, 9, 29, 19, 40, 22, TimeSpan.Zero);
        DateTimeOffset screenArrived = Pc + TimeSpan.FromSeconds(2.7);

        DateTimeOffset? shown = ReceiverClock.Now(
            screenStampedAtStart, screenArrived, new TimeSpan(19, 40, 21), Pc, screenArrived);

        Assert.Equal(new DateTimeOffset(2026, 9, 29, 19, 40, 23, TimeSpan.Zero), Truncated(shown));
    }

    /// <summary>
    /// A time of day the receiver has stopped answering is not preferred over a screen that still
    /// comes: once it is overdue and the screen is newer, the screen's time is the better anchor.
    /// </summary>
    [Fact]
    public void AnOverdueTimeOfDayGivesWayToANewerScreen() =>
        Assert.Equal(
            Screen.AddSeconds(1),
            ReceiverClock.Now(Screen, Pc, new TimeSpan(19, 39, 50), Pc - TimeSpan.FromSeconds(20), Pc.AddSeconds(1)));

    /// <summary>
    /// A screen stamped just after midnight, with the sweep before it still just before: the time of
    /// day belongs to the day before the screen's date.
    /// </summary>
    [Fact]
    public void AScreenJustAfterMidnightKeepsTheSweepsDay()
    {
        DateTimeOffset afterMidnight = new(2026, 9, 30, 0, 0, 1, TimeSpan.Zero);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 23, 59, 59, TimeSpan.Zero),
            ReceiverClock.Now(afterMidnight, Pc + TimeSpan.FromSeconds(3), new TimeSpan(23, 59, 58), Pc + TimeSpan.FromSeconds(2), Pc + TimeSpan.FromSeconds(3)));
    }

    private static DateTimeOffset? Truncated(DateTimeOffset? value) =>
        value is DateTimeOffset v ? new DateTimeOffset(v.Ticks - (v.Ticks % TimeSpan.TicksPerSecond), v.Offset) : null;

    /// <summary>
    /// <b>Overdue, it stops.</b> Past fifteen seconds with nothing heard the clock shows the last
    /// time it was given; ticking on would hide that the receiver has gone quiet (§9.11).
    /// </summary>
    [Fact]
    public void AnOverdueClockStopsRatherThanInventingTime()
    {
        TimeSpan read = new(19, 40, 14);

        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 19, 40, 28, TimeSpan.Zero),
            At(TimeSpan.FromSeconds(18), read, TimeSpan.FromSeconds(4)));

        Assert.Equal(
            new DateTimeOffset(2026, 9, 29, 19, 40, 14, TimeSpan.Zero),
            At(TimeSpan.FromSeconds(19), read, TimeSpan.FromSeconds(4)));
    }

    /// <summary>A PC clock that steps back does not run the receiver's time backwards.</summary>
    [Fact]
    public void APcClockThatStepsBackHoldsTheAnchor() =>
        Assert.Equal(Screen, At(TimeSpan.FromSeconds(-5)));

    /// <summary>The screen's offset is kept, so a device-local screen stays device-local.</summary>
    [Fact]
    public void TheScreensOffsetIsKept()
    {
        DateTimeOffset local = new(2026, 9, 29, 12, 40, 10, TimeSpan.FromHours(-7));

        DateTimeOffset? shown = At(TimeSpan.FromSeconds(2), new TimeSpan(12, 40, 11), TimeSpan.FromSeconds(1), local);

        Assert.Equal(TimeSpan.FromHours(-7), shown?.Offset);
        Assert.Equal(new DateTimeOffset(2026, 9, 29, 12, 40, 12, TimeSpan.FromHours(-7)), shown);
    }

    /// <summary>
    /// End to end, as the main window reads it: the clock moves every second between screens, and
    /// a sweep's time of day takes over from the screen's.
    /// </summary>
    [Fact]
    public void TheMainWindowsClockTicksBetweenScreens()
    {
        FakeTimeProvider clock = new(Pc);
        ReceiverStateStore store = new(clock);
        store.UpdateFull(new ReceiverStatus { TimeScale = TimeScale.Utc, DeviceDateTime = Screen, CapturedAt = Pc }, FastFields.All);

        MainViewModel model = new(store, clock, new SmartClockDriver(clock)) { DisplayZone = TimeZoneInfo.Utc };

        Assert.Equal("19:40:10", Shown(model));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("19:40:11", Shown(model));

        // The sweep says 19:40:13: the receiver's word replaces the count from the screen.
        store.UpdateFast(" LOCK", 3, 0, -5.4, -16.7, 7, FastFields.All, timeOfDay: new TimeSpan(19, 40, 13));
        Assert.Equal("19:40:13", Shown(model));

        clock.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal("19:40:14", Shown(model));
    }

    private static string? Shown(MainViewModel model) =>
        model.ShownTime?.Value.ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
}
