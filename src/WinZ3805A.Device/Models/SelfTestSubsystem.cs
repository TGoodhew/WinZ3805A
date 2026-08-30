namespace WinZ3805A.Device.Models;

/// <summary>
/// One subsystem <c>:DIAG:TEST?</c> will test, and the result of testing it (P1-5, #53).
/// </summary>
/// <param name="Keyword">
/// The keyword sent to the receiver — <c>DISP</c>, <c>GPS</c>, <c>ALL</c> and so on.
/// </param>
/// <param name="DisplayName">The §10.9 name, for the selector and the result row.</param>
public sealed record SelfTestSubsystem(string Keyword, string DisplayName)
{
    /// <summary>
    /// Every keyword the receiver accepts, in §10.9's order.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Probed, not guessed.</b> The Z3801A guide does not document <c>:DIAG:TEST?</c>'s
    /// parameter at all — there the command appears only in the error list for <c>-330</c>. The
    /// 58503A/59551A guide does, and disagrees with itself: its Quick Reference (4-9) lists
    /// twelve keywords and its Command Reference (5-54) eleven, omitting <c>IREFerence</c>, and
    /// §10.9's eleven names had no stated source. Each was sent to the live receiver on
    /// 28 Aug 2026 and all twelve were accepted, which is what turned this from a plausible list
    /// into a fact.
    /// </para>
    /// <para>
    /// The control that made the result mean something was an invalid keyword sent first:
    /// <c>:DIAG:TEST? ZZNOSUCH</c> returned <c>-224,"Illegal parameter value"</c> immediately and
    /// ran nothing. Without it, a keyword that was silently ignored would have looked exactly like
    /// one that worked.
    /// </para>
    /// <para>
    /// <see cref="All"/> is the receiver's own sweep rather than this application running the other
    /// eleven in turn. It is one command, took 12.4 s where eleven sequential runs would cost close
    /// to a minute of testing, and — the part that matters — it is what the hardware offers rather
    /// than something invented on top of it.
    /// </para>
    /// </remarks>
    public static IReadOnlyList<SelfTestSubsystem> Known { get; } =
    [
        new("ALL", "All subsystems"),
        new("DISP", "Display"),
        new("PROC", "Processor"),
        new("RAM", "RAM"),
        new("EEPR", "EEPROM"),
        new("UART", "UART"),
        new("QSPI", "QSPI"),
        new("FPGA", "FPGA"),
        new("INT", "Interpolator"),
        new("IREF", "Internal reference"),
        new("GPS", "GPS"),
        new("POW", "Power"),
    ];

    /// <summary>The sweep the receiver performs itself.</summary>
    public static SelfTestSubsystem All => Known[0];

    /// <summary>Finds a subsystem by the keyword the receiver reports, or null.</summary>
    /// <remarks>
    /// Used to match <c>:DIAG:TEST:RES?</c>'s answer back to a row. The comparison is
    /// case-insensitive because the receiver's own echo is not guaranteed to match the case sent.
    /// </remarks>
    public static SelfTestSubsystem? ByKeyword(string? keyword) =>
        string.IsNullOrWhiteSpace(keyword)
            ? null
            : Known.FirstOrDefault(s => string.Equals(s.Keyword, keyword.Trim(), StringComparison.OrdinalIgnoreCase));
}
