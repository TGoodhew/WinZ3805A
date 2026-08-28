namespace WinZ3805A.Services;

/// <summary>
/// The second discriminator on whether a fast sweep is a reading at all (#237, extending #209).
/// </summary>
/// <remarks>
/// <para>
/// #209 established the rule and the reason for it: a sweep whose sync state is not a state this
/// receiver reports is somebody else's reply, and storing it puts values in a durable seven-day
/// series that the instrument cannot produce. It also considered a range check and deliberately did
/// not use one — the sweep that prompted it carried an EFC of <b>+2 %</b>, which is inside the
/// oscillator's control range and indistinguishable from a real reading by magnitude alone.
/// </para>
/// <para>
/// <b>That argument is about EFC, and it does not carry to the 1 PPS time interval.</b> This is a
/// phase offset measured against a 1 Hz signal, so it is bounded by ±0.5 s by definition — half a
/// second of offset is the next pulse. There is a real limit here where there is none for EFC, and
/// the same six-day capture makes the practical range far tighter still: 19,456 of 20,133 samples
/// sit inside ±100 ns and the largest legitimate one all week is under 1 µs.
/// </para>
/// <para>
/// <b>The gap it closes.</b> <c>PollingService</c> reads the sync state on its own before the loop
/// that reads everything else, precisely because §7.3's ordering makes that possible. So a framing
/// slip that begins *inside* that loop leaves the sync state correct and every later answer shifted
/// — and #209's discriminator, which asks only about the sync state, passes it. That is not
/// hypothetical: it is the only shape the slip can take once the first read has already succeeded.
/// </para>
/// <para>
/// The bound is deliberately the physical one rather than the observed one. A tighter limit would
/// reject real readings from a receiver in a state this application has not yet seen — a cold start,
/// a bad antenna, a unit that is genuinely far out — and those are exactly the readings a diagnostic
/// tool must not quietly discard. ±0.5 s rejects only what cannot exist.
/// </para>
/// </remarks>
public static class ReadingPlausibility
{
    /// <summary>The largest 1 PPS time interval that is physically meaningful, in nanoseconds.</summary>
    /// <remarks>
    /// Half a second. The measurement is the offset of the receiver's pulse from GPS, and at more
    /// than half a second the nearer pulse is the next one — so a larger reading is not a big offset,
    /// it is not an offset.
    /// </remarks>
    public const double TimeIntervalBoundNanoseconds = 5e8;

    /// <summary>Whether a 1 PPS time interval could have come from the instrument.</summary>
    /// <remarks>
    /// A missing reading is plausible: §11.1 makes an unparseable field null, and the receiver
    /// legitimately declines this query in some states — <c>PollingService</c> counts those as
    /// skips. Absence is not evidence of a slip.
    /// </remarks>
    /// <param name="nanoseconds">The parsed reading, or null if there was not one.</param>
    public static bool IsPossibleTimeInterval(double? nanoseconds) =>
        nanoseconds is not double value
        || (double.IsFinite(value) && Math.Abs(value) <= TimeIntervalBoundNanoseconds);
}
