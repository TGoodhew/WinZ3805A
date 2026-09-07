namespace WinZ3805A.Device.Drivers.Uccm;

/// <summary>Who made the UCCM module, which changes how several answers must be read (#418).</summary>
/// <remarks>
/// <para>
/// <b>This is not decorative and it is not derivable from <c>*IDN?</c> alone.</b> The same commands
/// return different shapes from the two vendors — <c>DIAG:LOOP?</c> answers with seven positional
/// floats from Trimble and labelled key-value lines from Symmetricom — and the status hex encodes
/// lock with entirely different values. A driver that assumes one vendor reports a working receiver
/// of the other as faulty.
/// </para>
/// <para>
/// <see cref="Unknown"/> is a real state and not a failure. A session begins here and may stay here
/// until something vendor-specific has been seen. §11.1's rule applies: a field that cannot be read
/// honestly is absent, never guessed.
/// </para>
/// </remarks>
public enum UccmVendor
{
    /// <summary>Not yet established. Render vendor-specific readings as absent.</summary>
    Unknown = 0,

    /// <summary>Symmetricom — labelled loop output, and the only vendor that reports temperature.</summary>
    Symmetricom,

    /// <summary>Trimble — positional loop output, no temperature, and its own lock codes.</summary>
    Trimble,
}

/// <summary>Which UCCM variant, which changes what may be asked rather than how answers are read.</summary>
/// <remarks>
/// <b>Variant and vendor are orthogonal, and conflating them gets one of the four combinations
/// wrong (#418).</b> Lady Heather's own source is the cautionary example: it assigns
/// <c>UCCMP_TYPE</c> in exactly one place — on seeing the Symmetricom <c>----</c> loop header — so
/// it infers a <i>variant</i> from a <i>vendor</i> signature, while its own comments describe both a
/// "Symmetricom UCCMP" and a "Trimble UCCM-P". We keep the two axes apart deliberately.
/// </remarks>
public enum UccmVariant
{
    /// <summary>Not yet established.</summary>
    Unknown = 0,

    /// <summary>Plain UCCM.</summary>
    Uccm,

    /// <summary>UCCM-P, which also answers holdover duration and the position-survey queries.</summary>
    UccmP,
}

/// <summary>What the driver currently believes about the receiver on the other end (#416, #418).</summary>
/// <param name="Vendor">Who made it, or <see cref="UccmVendor.Unknown"/>.</param>
/// <param name="Variant">Which variant, or <see cref="UccmVariant.Unknown"/>.</param>
/// <remarks>
/// Carried as a value rather than mutated in place so that a parse is a pure function of its input
/// and its starting belief, which is what lets the fixture tests pin both.
/// </remarks>
public readonly record struct UccmProfile(UccmVendor Vendor, UccmVariant Variant)
{
    /// <summary>What is believed before anything has been heard.</summary>
    public static UccmProfile Unknown => new(UccmVendor.Unknown, UccmVariant.Unknown);

    /// <summary>Whether the vendor is settled enough to read vendor-specific answers.</summary>
    public bool VendorKnown => Vendor != UccmVendor.Unknown;
}
