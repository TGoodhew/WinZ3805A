namespace WinZ3805A.Controls;

/// <summary>
/// The five severity levels §9.4.3 defines, and the only vocabulary in which the application
/// expresses "how bad is this".
/// </summary>
/// <remarks>
/// <para>
/// There are five. Do not add a sixth: each one is a fixed triple of colour token,
/// <c>Path</c> shape, and glyph in the §9.4.3 table, and a value without a shape of its own
/// silently degrades to colour-only meaning — which is the thing the whole scheme exists to
/// prevent.
/// </para>
/// <para>
/// <c>SeverityPill</c> takes this enum and never a brush. That is what makes the
/// colour-blindness guarantee structural rather than something each page has to remember (named in plain text rather than with a cref, because this file is also compiled into the headless test assembly where the control does not exist):
/// a caller cannot pass "red" because there is no way to say it.
/// </para>
/// </remarks>
public enum Severity
{
    /// <summary>Unknown, powering up, or not applicable. Ring outline.</summary>
    Neutral = 0,

    /// <summary>Locked, valid, test passed. Filled circle.</summary>
    Success,

    /// <summary>Recovering, waiting, reduced accuracy, or stale data. Triangle.</summary>
    Caution,

    /// <summary>Holdover, hardware failure, or disconnected with an error. Hexagon.</summary>
    Critical,

    /// <summary>A neutral advisory such as the week-rollover notice. Circled i.</summary>
    Info,
}
