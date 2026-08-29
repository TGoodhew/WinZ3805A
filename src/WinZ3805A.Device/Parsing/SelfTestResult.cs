using System.Globalization;

using WinZ3805A.Device.Models;

namespace WinZ3805A.Device.Parsing;

/// <summary>
/// What the receiver said about a self-test, and how far that can honestly be read (#53).
/// </summary>
/// <param name="Subsystem">The subsystem the result belongs to, or null when it is unrecognised.</param>
/// <param name="Code">The receiver's code. Zero is a pass; anything else is undocumented.</param>
/// <param name="RawSubsystem">The keyword exactly as the receiver echoed it.</param>
public sealed record SelfTestResult(SelfTestSubsystem? Subsystem, int? Code, string? RawSubsystem)
{
    /// <summary>Whether the receiver reported a pass.</summary>
    /// <remarks>
    /// Only <c>0</c> is a pass, per the Z3801A guide's <c>*TST?</c> row: "0 = passed, non-zero is
    /// test specific code". Null when nothing parsed, which is <b>not</b> the same as a failure.
    /// </remarks>
    public bool? Passed => Code is int code ? code == 0 : null;

    /// <summary>
    /// Parses <c>:DIAG:TEST:RES?</c>'s answer, which is <c>&lt;code&gt;,&lt;subsystem&gt;</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Observed on the live receiver as <c>+0,ALL</c> and <c>+65536,GPS</c>. The leading sign is
    /// always present and <c>int.TryParse</c> with <see cref="NumberStyles.AllowLeadingSign"/>
    /// takes it.
    /// </para>
    /// <para>
    /// Never throws, per §11.1. An answer in a shape nobody has seen yields a result whose
    /// <see cref="Code"/> is null and whose <see cref="Passed"/> is therefore null — rendered as
    /// <c>—</c> rather than guessed at either way.
    /// </para>
    /// </remarks>
    public static SelfTestResult Parse(string? reply)
    {
        if (string.IsNullOrWhiteSpace(reply))
        {
            return new SelfTestResult(null, null, null);
        }

        string[] parts = reply.Trim().Split(',', StringSplitOptions.TrimEntries);

        int? code = parts.Length > 0
            && int.TryParse(parts[0], NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int parsed)
                ? parsed
                : null;

        string? keyword = parts.Length > 1 && !string.IsNullOrWhiteSpace(parts[1]) ? parts[1] : null;

        return new SelfTestResult(SelfTestSubsystem.ByKeyword(keyword), code, keyword);
    }

    /// <summary>
    /// Parses the reply to <c>:DIAG:TEST? &lt;keyword&gt;</c>, which is three integers.
    /// </summary>
    /// <param name="reply">The receiver's answer, observed as <c>+0,+0,+0</c>.</param>
    /// <param name="subsystem">The subsystem that was asked for — the reply does not name it.</param>
    /// <remarks>
    /// Only the first integer is read, because only the first is understood: it matches the code
    /// <c>:DIAG:TEST:RES?</c> reports afterwards. <b>What the other two mean is unknown</b>, and
    /// they are deliberately not surfaced — a number shown on a diagnostics page is read as
    /// meaningful, and these would be decoration.
    /// </remarks>
    public static SelfTestResult ParseRun(string? reply, SelfTestSubsystem subsystem)
    {
        SelfTestResult parsed = Parse(reply);

        return new SelfTestResult(subsystem, parsed.Code, subsystem.Keyword);
    }
}
