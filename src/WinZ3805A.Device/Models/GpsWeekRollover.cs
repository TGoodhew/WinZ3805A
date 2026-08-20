namespace WinZ3805A.Device.Models;

/// <summary>
/// §7.4's 1024-week rollover: the period, and what applying it to an instant means.
/// </summary>
/// <remarks>
/// <para>
/// A separate type because the correction has two callers with nothing else in common. The parser
/// applies it to the status screen's own date, comparing against the host clock to decide whether a
/// rollover has happened at all; the log export applies the epoch count the parser arrived at to
/// timestamps the receiver printed years earlier, where there is no host clock to compare against.
/// </para>
/// <para>
/// The alternative was 1024 weeks written down twice, which is exactly the kind of duplication that
/// stays correct until one of the two is touched.
/// </para>
/// </remarks>
public static class GpsWeekRollover
{
    /// <summary>One GPS epoch: 1024 weeks, after which an unpatched receiver's date wraps (§7.4).</summary>
    public static readonly TimeSpan Epoch = TimeSpan.FromDays(7168);

    /// <summary>How far from an exact multiple of <see cref="Epoch"/> still counts as a rollover (§7.4).</summary>
    public static readonly TimeSpan Tolerance = TimeSpan.FromDays(7);

    /// <summary>Advances an instant by a number of epochs, or returns null if there is nothing to advance.</summary>
    /// <param name="value">The instant the receiver reported.</param>
    /// <param name="epochs">
    /// How many epochs behind the receiver is, from <see cref="ReceiverStatus.WeekRolloverEpochs"/>.
    /// Zero returns <see langword="null"/> rather than the input: no correction applies, and
    /// returning the value unchanged would imply one was computed and came to nothing.
    /// </param>
    public static DateTime? Correct(DateTime? value, int epochs) =>
        value is DateTime instant && epochs > 0 ? instant + (Epoch * epochs) : null;
}
