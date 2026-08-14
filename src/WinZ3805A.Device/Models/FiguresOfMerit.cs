namespace WinZ3805A.Device.Models;

/// <summary>
/// What the receiver's two figures of merit mean.
/// </summary>
/// <remarks>
/// <para>
/// From the <i>58503A/59551A Operating and Programming Guide</i>, Command Reference 5-23 and 5-24
/// (<c>:SYNChronization:FFOMerit?</c> and <c>:SYNChronization:TFOMerit?</c>). Recorded here rather
/// than looked up again: the guide is not redistributable and the tables are the whole reason a
/// bare "TFOM 3" is worth showing at all — the number alone tells a user nothing, and the range
/// behind it is the thing they came to find out.
/// </para>
/// <para>
/// <b>Lower is better for both.</b> That is the opposite of most instrument scales, which is why
/// §9.4.3 forbids conveying either by colour alone.
/// </para>
/// </remarks>
public static class FiguresOfMerit
{
    /// <summary>
    /// The 1 PPS output's time error for a given TFOM, or <see langword="null"/> if out of range.
    /// </summary>
    /// <remarks>
    /// Values 0, 1 and 2 are documented but "not presently used in the 58503A and 59551A products",
    /// which "display TFOM values ranging from 9 to 3". They are carried anyway: a receiver
    /// reporting one is not a parse failure, and the Z3805A's firmware is a sibling rather than the
    /// exact product the guide describes.
    /// </remarks>
    public static string? TimeError(int? tfom) => tfom switch
    {
        0 => "less than 1 ns",
        1 => "1 – 10 ns",
        2 => "10 – 100 ns",
        3 => "100 ns – 1 µs",
        4 => "1 – 10 µs",
        5 => "10 – 100 µs",
        6 => "100 µs – 1 ms",
        7 => "1 – 10 ms",
        8 => "10 – 100 ms",
        9 => "more than 100 ms",
        _ => null,
    };

    /// <summary>
    /// What a given FFOM says about the 10 MHz output, or <see langword="null"/> if out of range.
    /// </summary>
    /// <remarks>
    /// FFOM 2 and 3 are both "PLL unlocked" and are not interchangeable: 2 is holdover, where the
    /// output starts within specification and drifts out, and 3 is unlocked while <i>not</i> in
    /// holdover, which the guide answers with "do not use the output".
    /// </remarks>
    public static string? PllState(int? ffom) => ffom switch
    {
        0 => "PLL stabilized",
        1 => "PLL stabilizing",
        2 => "PLL unlocked, in holdover",
        3 => "PLL unlocked — do not use the output",
        _ => null,
    };

    /// <summary>
    /// The longer form of <see cref="PllState"/>, for a tooltip.
    /// </summary>
    public static string? PllDetail(int? ffom) => ffom switch
    {
        0 => "The 10 MHz output is within specification.",
        1 => "The phase-locked loop is still settling. The 10 MHz output is not yet within specification.",
        2 => "The phase-locked loop is unlocked and the receiver is in holdover. The 10 MHz output "
             + "starts within specification and drifts out as holdover continues.",
        3 => "The phase-locked loop is unlocked and the receiver is not in holdover. Do not use the "
             + "10 MHz output.",
        _ => null,
    };
}
