namespace WinZ3805A.Device.Commands;

/// <summary>
/// The shape of what a command answers with, so a caller knows which parser to reach for.
/// </summary>
/// <remarks>
/// Taken from responses observed on the reference unit rather than from the manual's prose. Note
/// that every value arrives with a leading space — <c>_+3</c> rather than <c>+3</c> — which is a
/// framing artefact of the receiver and belongs to none of these formats; trim before parsing.
/// </remarks>
public enum ResponseFormat
{
    /// <summary>
    /// Nothing. A setter answers with the prompt alone, which is why §7.2 makes a setter and a
    /// multi-line block the same shape of read.
    /// </summary>
    None = 0,

    /// <summary>A single signed integer, as <c>+3</c>.</summary>
    Integer,

    /// <summary>A single real number, plain or scientific, as <c>-5.4E-009</c>.</summary>
    Decimal,

    /// <summary>A boolean the receiver spells <c>0</c> or <c>1</c>.</summary>
    Boolean,

    /// <summary>One enumerated keyword, as <c>LOCK</c>.</summary>
    Keyword,

    /// <summary>Comma-separated integers, as <c>+2006,+12,+27</c>.</summary>
    IntegerList,

    /// <summary>Comma-separated values of mixed kinds, as <c>+6.00000E+002,0</c>.</summary>
    ValueList,

    /// <summary>One line of free text, as the identity string.</summary>
    Text,

    /// <summary>Several lines of free text, as a log read.</summary>
    MultiLine,

    /// <summary>The full status screen, which <c>StatusScreenParser</c> decodes (§11).</summary>
    StatusScreen,
}
