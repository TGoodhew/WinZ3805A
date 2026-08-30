namespace WinZ3805A.Device.Models;

/// <summary>
/// What a given receiver model has, so divergence is a table rather than a scatter of conditionals
/// (§8.6, P2-4, #64).
/// </summary>
/// <param name="Model">Which model this describes.</param>
/// <param name="HasSecondSerialPort">
/// Whether a <c>PORT 2</c> exists, which is what <c>:SYST:COMM:SER2:*</c> addresses.
/// </param>
/// <param name="HasProgrammablePulseOutput">Whether <c>:PULSe:*</c> exists.</param>
/// <param name="HasTimestampMemory">Whether <c>:SENSe:DATA:*</c> and <c>:SENSe:TSTamp*</c> exist.</param>
/// <param name="HasPpsEdgeControl">Whether <c>:PTIMe:PPS:EDGE</c> exists.</param>
public sealed record ModelProfile(
    ReceiverModel Model,
    bool HasSecondSerialPort,
    bool HasProgrammablePulseOutput,
    bool HasTimestampMemory,
    bool HasPpsEdgeControl)
{
    /// <summary>
    /// The profile for each model, from §8.6 and the manuals.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §8.6 lists <c>:PULSe:*</c>, <c>:SENSe:TSTamp&lt;n&gt;:*</c>, <c>:SENSe:DATA:*</c>,
    /// <c>:FORMat:DATA</c>, <c>:PTIM:PPS:EDGE</c> and <c>:SYST:COMM:SER2:*</c> as
    /// <b>59551A-only hardware features</b>, and the 58503A guide confirms PORT 2 as
    /// "(59551A Only)". Every row below follows from that one list.
    /// </para>
    /// <para>
    /// <b>Only the SER2 cell of the Z3805A row is measured.</b> <c>:SYST:COMM:SER2:BAUD?</c>
    /// answers <c>-113,"Undefined header"</c> on the live unit and it has one serial connector
    /// (#62), which is why that cell rests on evidence rather than on the specification's own
    /// table. The row's other three cells follow from §16.1 — its bench probes (the PPS edge
    /// answers the same error; the pulse subsystem is only half accepted) and its connector
    /// inspection (one BNC output, no Time Tag inputs) — rather than from the table alone.
    /// </para>
    /// </remarks>
    private static readonly Dictionary<ReceiverModel, ModelProfile> Profiles = new()
    {
        [ReceiverModel.Z3805A] = new(ReceiverModel.Z3805A, false, false, false, false),
        [ReceiverModel.Z3801A] = new(ReceiverModel.Z3801A, false, false, false, false),
        [ReceiverModel.Z3816A] = new(ReceiverModel.Z3816A, false, false, false, false),
        [ReceiverModel.Hp58503] = new(ReceiverModel.Hp58503, false, false, false, false),
        [ReceiverModel.Hp59551] = new(ReceiverModel.Hp59551, true, true, true, true),
    };

    /// <summary>
    /// The profile applied when the model is not recognised.
    /// </summary>
    /// <remarks>
    /// <b>Everything optional is off.</b> An unknown receiver gets the smallest surface, so the
    /// failure mode of not recognising a model is a feature that is missing rather than a command
    /// sent to hardware that may not have it. §8.5's rule is the same one: absent unless shown to be
    /// present.
    /// </remarks>
    public static ModelProfile Conservative { get; } =
        new(ReceiverModel.Unknown, false, false, false, false);

    /// <summary>The profile for a model, or <see cref="Conservative"/> when it is unrecognised.</summary>
    public static ModelProfile For(ReceiverModel model) =>
        Profiles.TryGetValue(model, out ModelProfile? profile) ? profile : Conservative;

    /// <summary>The profile for a parsed identity, or <see cref="Conservative"/> when there is none.</summary>
    public static ModelProfile For(DeviceIdentity? identity) =>
        identity is null ? Conservative : For(identity.Receiver);

    /// <summary>
    /// Whether this model can be sent a command, by its SCPI header.
    /// </summary>
    /// <remarks>
    /// <para>
    /// §8.6 says these are "hidden entirely" on a model that lacks them. Today that holds
    /// <b>vacuously</b> — none of them is in <c>CommandCatalog</c>, so there is nothing to hide, and
    /// §16.1 records why each stays out (#154's inventory, closed 29 Aug 2026). This exists so that
    /// adding one later cannot quietly offer it on hardware without the feature.
    /// </para>
    /// <para>
    /// Node-prefix matching, because SCPI abbreviations are legal and the catalog spells some
    /// headers short: <c>:PTIM:PPS:EDGE</c> and <c>:PTIMe:PPS:EDGE</c> are the same command.
    /// </para>
    /// </remarks>
    public bool Supports(string? mnemonic)
    {
        if (string.IsNullOrWhiteSpace(mnemonic))
        {
            return false;
        }

        string header = mnemonic.Trim();

        if (StartsWithNode(header, ":PULS") && !HasProgrammablePulseOutput) { return false; }
        if (StartsWithNode(header, ":SYST:COMM:SER2") && !HasSecondSerialPort) { return false; }
        if (StartsWithNode(header, ":PTIM:PPS:EDG") && !HasPpsEdgeControl) { return false; }

        if (!HasTimestampMemory &&
            (StartsWithNode(header, ":SENS:DATA")
                || StartsWithNode(header, ":SENS:TST")
                || StartsWithNode(header, ":FORM:DATA")))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// Whether a header begins with a node path, allowing either side to be the abbreviation.
    /// </summary>
    /// <remarks>
    /// The same rule the §16.1 inventory (#154, now closed) used, and for the same reason: a
    /// mechanical short form is wrong in both directions because the manuals' own capitalisation is
    /// inconsistent.
    /// </remarks>
    private static bool StartsWithNode(string header, string path)
    {
        string[] wanted = path.Split(':', StringSplitOptions.RemoveEmptyEntries);
        string[] actual = header.Split([':', ' '], StringSplitOptions.RemoveEmptyEntries);

        if (actual.Length < wanted.Length)
        {
            return false;
        }

        for (int i = 0; i < wanted.Length; i++)
        {
            bool matches = actual[i].StartsWith(wanted[i], StringComparison.OrdinalIgnoreCase)
                || wanted[i].StartsWith(actual[i], StringComparison.OrdinalIgnoreCase);

            if (!matches)
            {
                return false;
            }
        }

        return true;
    }
}
