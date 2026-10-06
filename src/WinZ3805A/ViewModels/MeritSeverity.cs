using WinZ3805A.Controls;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The severity each figure of merit's pill renders with, on the main window and the Overview alike.
/// </summary>
/// <remarks>
/// <para>
/// <b>Two scales, two rules (#719, decided by Tony 5 Oct 2026).</b> Both pills once ran one set of
/// thresholds, 0-3 success, 4-6 caution, 7 and up critical. Those were TFOM's ranges, and FFOM only
/// runs 0-3, so every FFOM was green - including 3, whose own caption says do not use the output.
/// The thresholds were copied into the main window and the Overview separately; they live here so the
/// two cannot drift.
/// </para>
/// <para>
/// <b>FFOM is a state, so it is judged.</b> It reports the phase-locked loop that steers the 10 MHz
/// output (Operating and Programming Guide, p. 5-30): 0 stabilized and within specification, 1
/// stabilizing, 2 unlocked in holdover and drifting, 3 unlocked and not in holdover.
/// </para>
/// <para>
/// <b>TFOM is an amount, so it is not.</b> It is the time error of the 1 PPS output, and on this
/// family 3 - 100 ns to 1 µs - is the best it reaches: "will display TFOM values ranging from 9 to 3"
/// (p. 5-32). A colour judging it says nothing the caption does not, so the pill is neutral and the
/// caption carries the range.
/// </para>
/// </remarks>
public static class MeritSeverity
{
    /// <summary>TFOM's pill: neutral whatever the value; its caption gives the time error.</summary>
    public static Severity OfTfom(int? tfom) => Severity.Neutral;

    /// <summary>FFOM's pill, from the state of the loop it reports.</summary>
    public static Severity OfFfom(int? ffom) => ffom switch
    {
        0 => Severity.Success,
        1 or 2 => Severity.Caution,
        3 => Severity.Critical,
        _ => Severity.Neutral,
    };
}
