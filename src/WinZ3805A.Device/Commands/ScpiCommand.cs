using System.Text;

namespace WinZ3805A.Device.Commands;

/// <summary>
/// One command the application is permitted to send, with everything needed to present it, take a
/// parameter for it, and decide what ceremony it requires (§8.1).
/// </summary>
/// <remarks>
/// <para>
/// The shape follows §8.1, with two members appended. §8.1's record predates §8.3's "stronger
/// variant" rule and §8.5's opt-in queries, neither of which it has anywhere to record, so
/// <see cref="RequiresAcknowledgement"/> and <see cref="IsExperimental"/> are added at the end
/// where they are source-compatible with the declared shape. Both are noted against the
/// specification rather than resolved silently.
/// </para>
/// <para>
/// Instances exist only inside <see cref="CommandCatalog"/>. Nothing else constructs one, because
/// a command that did not come from the catalog is exactly what §8.1 exists to prevent.
/// </para>
/// </remarks>
/// <param name="Mnemonic">
/// The command in its long form, as the manual spells it — <c>:GPS:SAT:TRAC:EMANgle</c>. Mixed
/// case is meaningful: the capitals are the short form (see <see cref="ToShortForm"/>).
/// </param>
/// <param name="ShortForm">
/// The abbreviated form the receiver also accepts — <c>:GPS:SAT:TRAC:EMAN</c>. Derived from
/// <paramref name="Mnemonic"/> rather than typed twice.
/// </param>
/// <param name="Tier">What ceremony this command needs before it runs.</param>
/// <param name="IsQuery">True when the command ends in <c>?</c> and answers with a value.</param>
/// <param name="DisplayName">A short human label, for a button or a list row.</param>
/// <param name="Description">One sentence on what it does, for a tooltip or a details pane.</param>
/// <param name="Parameters">The values it takes, in order. Empty for most.</param>
/// <param name="ResponseFormat">The shape of what it answers with.</param>
/// <param name="ConfirmationText">
/// For <see cref="SafetyTier.Confirm"/>, the §8.3 consequence text shown in the dialog, verbatim.
/// Null for <see cref="SafetyTier.Safe"/>. Where a value appears in the sentence it is a
/// <c>{0}</c> placeholder, so the dialog states the number the user is actually about to send.
/// </param>
/// <param name="RequiresAcknowledgement">
/// True for the four commands §9.7.4 names as strong variants, where a checkbox gates the confirm
/// button. Not in §8.1's record; see the remarks.
/// </param>
/// <param name="IsExperimental">
/// True for the §8.5 queries, which are undocumented, read-only, off by default, and run only on
/// an explicit click. Not in §8.1's record; see the remarks.
/// </param>
public sealed record ScpiCommand(
    string Mnemonic,
    string ShortForm,
    SafetyTier Tier,
    bool IsQuery,
    string DisplayName,
    string Description,
    IReadOnlyList<ParameterSpec> Parameters,
    ResponseFormat ResponseFormat,
    string? ConfirmationText = null,
    bool RequiresAcknowledgement = false,
    bool IsExperimental = false)
{
    /// <summary>
    /// Abbreviates a mnemonic to its SCPI short form by dropping the lower-case letters of each
    /// node: <c>:SYNC:HOLDover:INITiate</c> becomes <c>:SYNC:HOLD:INIT</c>.
    /// </summary>
    /// <remarks>
    /// Derived rather than written out beside every entry. The long and short forms are not two
    /// facts — SCPI defines the second as a function of the capitalisation of the first — and a
    /// catalog of this size stored as two hand-typed columns would drift, silently, in whichever
    /// column nothing reads.
    /// </remarks>
    public static string ToShortForm(string mnemonic)
    {
        ArgumentNullException.ThrowIfNull(mnemonic);

        StringBuilder builder = new(mnemonic.Length);
        foreach (char character in mnemonic)
        {
            if (!char.IsLower(character))
            {
                builder.Append(character);
            }
        }

        return builder.ToString();
    }
}
