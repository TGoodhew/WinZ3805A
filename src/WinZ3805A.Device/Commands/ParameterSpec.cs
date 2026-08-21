namespace WinZ3805A.Device.Commands;

/// <summary>What kind of value a command parameter takes.</summary>
/// <remarks>
/// The kind drives both the editor the UI offers and the validation applied before a string is
/// ever built, which is how §8.1's "no code path constructs a command from arbitrary user input"
/// is kept true for commands that do take a value.
/// </remarks>
public enum ParameterKind
{
    /// <summary>A whole number.</summary>
    Integer = 0,

    /// <summary>A real number, which the receiver accepts in plain or scientific notation.</summary>
    Decimal,

    /// <summary>One of a fixed set of SCPI keywords, listed in <see cref="ParameterSpec.Choices"/>.</summary>
    Keyword,

    /// <summary>One or more satellite PRN numbers.</summary>
    PrnList,

    // Coordinates, DateParts and TimeParts used to live here, one value standing for a whole
    // date, time or position. They were removed with #147. A "kind" that means "several numbers,
    // and some editor elsewhere knows how many" is not a description of a parameter - it is a
    // note about the user interface, in a library that is not allowed to have one, and it left
    // four catalogued commands unsendable because the console could not know what to draw. Those
    // commands now declare their parts: three ParameterSpecs for a date, nine for a position.
}

/// <summary>
/// One parameter of an <see cref="ScpiCommand"/>, with everything needed to validate a value
/// before it is formatted into a command string.
/// </summary>
/// <param name="Name">The parameter's name, used for the field label and in confirmation text.</param>
/// <param name="Kind">What sort of value it takes.</param>
/// <param name="Unit">
/// The unit the value is expressed in, if any — seconds, degrees, and so on — and the unit
/// <see cref="Minimum"/> and <see cref="Maximum"/> are stated in.
/// <para>
/// This is the unit the <b>user</b> works in, which is almost always the one the receiver takes as
/// well. <c>:GPS:REF:ADELay</c> is the exception: §8.3 writes its confirmation in nanoseconds and
/// §10.7 labels its field in nanoseconds, while the receiver takes seconds. The parameter follows
/// the two sections that are user-facing, and the caller scales on the way out — which it has to
/// do regardless, since no range expressed in seconds would produce §9.11's own example sentence,
/// "Enter a value between 0 and 999,999 ns."
/// </para>
/// </param>
/// <param name="Minimum">The smallest acceptable value, in <see cref="Unit"/>, where one is documented.</param>
/// <param name="Maximum">The largest acceptable value, in <see cref="Unit"/>, where one is documented.</param>
/// <param name="Choices">
/// The permitted keywords when <see cref="Kind"/> is <see cref="ParameterKind.Keyword"/>.
/// </param>
/// <param name="IsOptional">
/// True when the command is valid with the parameter omitted — the log read, which answers with
/// the whole log when given no entry number, is the case this exists for.
/// </param>
public sealed record ParameterSpec(
    string Name,
    ParameterKind Kind,
    string? Unit = null,
    double? Minimum = null,
    double? Maximum = null,
    IReadOnlyList<string>? Choices = null,
    bool IsOptional = false);
