using System.Globalization;

namespace WinZ3805A.Device.Transport;

/// <summary>
/// One entry read from the receiver's error queue by <c>:SYST:ERR?</c>.
/// </summary>
/// <remarks>
/// <para>
/// §7.2 requires <c>:SYST:ERR?</c> after every tier C command and nothing else, and §9.11's copy
/// rules require the number <i>and</i> its plain-language meaning to reach the user. SCPI supplies
/// both in the one response — <c>-222,"Data out of range"</c> — so this is a split, not a lookup
/// table. That matters: a table of meanings written from the manual would be a second opinion about
/// what the receiver just said, and would disagree with it on the firmware-specific codes.
/// </para>
/// <para>
/// Distinct from <see cref="Transaction.PromptStatus"/>, which is the <c>E-nnn&gt;</c> token the
/// prompt itself carries. That token says only <i>that</i> the last command was rejected; this says
/// which error and in what words.
/// </para>
/// </remarks>
/// <param name="Code">
/// The error number. Negative for the SCPI standard set, positive for device-specific errors, and
/// <c>0</c> for the queue's "no error" reply.
/// </param>
/// <param name="Message">The receiver's own description, with the surrounding quotes removed.</param>
public sealed record ScpiError(int Code, string Message)
{
    /// <summary>True when the receiver reported an actual error rather than an empty queue.</summary>
    public bool IsError => Code != 0;

    /// <summary>
    /// The error as one sentence, number first, for §9.11's "surface the number and its meaning".
    /// </summary>
    public string Describe() => $"The receiver returned error {Code.ToString(CultureInfo.InvariantCulture)}, {Message}.";

    /// <summary>
    /// Splits an error-queue response into its number and message, or returns
    /// <see langword="null"/> if it is not one.
    /// </summary>
    /// <remarks>
    /// Never throws, per §11.1. A response this cannot decompose is reported by the caller as the
    /// raw text it was, which is more useful than a fabricated code — and quieter than an exception
    /// on the confirmation path, where the command has already run and the user is owed an answer
    /// about it rather than a crash.
    /// </remarks>
    public static ScpiError? TryParse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        ReadOnlySpan<char> text = response.AsSpan().Trim();

        int comma = text.IndexOf(',');
        if (comma < 0)
        {
            return null;
        }

        if (!int.TryParse(text[..comma].Trim(), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out int code))
        {
            return null;
        }

        // The message is quoted, but a receiver that has dropped a quote should still be readable
        // rather than rejected — the number is the part the user acts on.
        ReadOnlySpan<char> message = text[(comma + 1)..].Trim().Trim('"').Trim();

        return new ScpiError(code, message.IsEmpty ? "no description given" : message.ToString());
    }
}
