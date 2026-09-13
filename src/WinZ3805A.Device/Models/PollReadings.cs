namespace WinZ3805A.Device.Models;

/// <summary>
/// What a vendor poll answered about the receiver's clock (#508).
/// </summary>
/// <remarks>
/// <para>
/// A model rather than a driver detail, and it lives here for the same reason
/// <see cref="TalkerBanner"/> does: the store remembers it, and the store has no business knowing
/// which family's sentence produced it. Today only the NMEA driver fills it, from u-blox's
/// <c>$PUBX,04</c>.
/// </para>
/// <para>
/// Every member is nullable: a reply can be well formed and still leave a field empty, and §11.1's
/// rule is that an unreadable field is absent rather than guessed.
/// </para>
/// </remarks>
public sealed record PollReadings
{
    /// <summary>
    /// GPS − UTC in whole seconds, <b>only when decoded from the satellites</b>.
    /// </summary>
    /// <remarks>
    /// <b>Null when the module reported its firmware default</b>, which is a constant it was built
    /// with rather than a reading of what GPS is broadcasting now. u-blox marks the difference with
    /// a <c>D</c> suffix, and the distinction is the whole reason this field is worth having.
    /// </remarks>
    public int? LeapSeconds { get; init; }

    /// <summary>The receiver's clock offset in nanoseconds.</summary>
    public double? ClockBiasNanoseconds { get; init; }

    /// <summary>How fast that offset is changing, in nanoseconds per second.</summary>
    public double? ClockDriftNanosecondsPerSecond { get; init; }

    /// <summary>Time-pulse quantisation error in nanoseconds — an uncertainty figure.</summary>
    public double? TimePulseGranularityNanoseconds { get; init; }
}
