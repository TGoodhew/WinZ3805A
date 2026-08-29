namespace WinZ3805A.Device.Models;

/// <summary>
/// Which SmartClock-family receiver is on the other end of the port (§8.6, P2-4).
/// </summary>
/// <remarks>
/// The names are the model fields these units put in <c>*IDN?</c>. <see cref="Unknown"/> is not a
/// failure state — the family is wider than this list, and an unrecognised model gets the
/// conservative profile rather than an error.
/// </remarks>
public enum ReceiverModel
{
    /// <summary>Not recognised, or not yet read. Treated conservatively.</summary>
    Unknown = 0,

    /// <summary>The Z3805A this application was written against.</summary>
    Z3805A,

    /// <summary>The Z3801A. Differs most visibly in its serial defaults.</summary>
    Z3801A,

    /// <summary>The Z3816A.</summary>
    Z3816A,

    /// <summary>The 58503A and 58503B, whose programming guide is this family's reference.</summary>
    Hp58503,

    /// <summary>The 59551A, which has hardware none of the others do.</summary>
    Hp59551,
}

/// <summary>
/// A parsed <c>*IDN?</c> response.
/// </summary>
/// <param name="Manufacturer">Field 1, e.g. <c>SYMMETRICOM</c>.</param>
/// <param name="Model">Field 2 verbatim, e.g. <c>Z3805A</c>.</param>
/// <param name="SerialNumber">Field 3, e.g. <c>3625A02931</c>.</param>
/// <param name="FirmwareRevision">Field 4, e.g. <c>1.01.03-A</c>.</param>
/// <param name="Receiver">Which model the second field names, or <see cref="ReceiverModel.Unknown"/>.</param>
public sealed record DeviceIdentity(
    string Manufacturer,
    string Model,
    string SerialNumber,
    string FirmwareRevision,
    ReceiverModel Receiver)
{
    /// <summary>
    /// Parses the four comma-separated fields IEEE 488.2 defines for <c>*IDN?</c>.
    /// </summary>
    /// <param name="response">The receiver's answer, prompt and framing already removed.</param>
    /// <returns>The parsed identity, or <see langword="null"/> when it is not four fields.</returns>
    /// <remarks>
    /// <para>
    /// Confirmed against the live receiver, which answers
    /// <c>SYMMETRICOM,Z3805A,3625A02931,1.01.03-A</c>. The four-field shape is the standard's rather
    /// than this unit's, which is why parsing it is safe for models nobody here has seen.
    /// </para>
    /// <para>
    /// <b>Never throws</b>, on §11.1's rule. A response in an unexpected shape yields
    /// <see langword="null"/>, and the caller keeps the raw string — which is what
    /// <c>DeviceSessionService</c> already displays, so nothing is lost by failing to parse.
    /// </para>
    /// </remarks>
    public static DeviceIdentity? Parse(string? response)
    {
        if (string.IsNullOrWhiteSpace(response))
        {
            return null;
        }

        string[] fields = response.Trim().Split(',');
        if (fields.Length != 4)
        {
            return null;
        }

        string model = fields[1].Trim();

        return new DeviceIdentity(
            fields[0].Trim(),
            model,
            fields[2].Trim(),
            fields[3].Trim(),
            ModelFor(model));
    }

    /// <summary>Maps the model field onto the family.</summary>
    /// <remarks>
    /// <para>
    /// Prefix matching, case-insensitively, because the suffix carries a variant the profile does
    /// not care about — a <c>58503B</c> takes the same profile as a <c>58503A</c>, and §11.1 already
    /// treats them as one class for the signal-strength scale.
    /// </para>
    /// <para>
    /// <b>Only the Z3805A spelling has been seen.</b> The others are the model numbers the manuals
    /// and §8.6 use; no <c>*IDN?</c> example is published for any of them, so these are the best
    /// available evidence rather than confirmed strings. An unrecognised model falls to
    /// <see cref="ReceiverModel.Unknown"/> and its conservative profile, which is why guessing wrong
    /// here degrades rather than breaks.
    /// </para>
    /// </remarks>
    private static ReceiverModel ModelFor(string model)
    {
        if (model.StartsWith("Z3805", StringComparison.OrdinalIgnoreCase)) { return ReceiverModel.Z3805A; }
        if (model.StartsWith("Z3801", StringComparison.OrdinalIgnoreCase)) { return ReceiverModel.Z3801A; }
        if (model.StartsWith("Z3816", StringComparison.OrdinalIgnoreCase)) { return ReceiverModel.Z3816A; }
        if (model.Contains("58503", StringComparison.OrdinalIgnoreCase)) { return ReceiverModel.Hp58503; }
        if (model.Contains("59551", StringComparison.OrdinalIgnoreCase)) { return ReceiverModel.Hp59551; }

        return ReceiverModel.Unknown;
    }
}
