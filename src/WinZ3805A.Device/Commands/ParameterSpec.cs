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

    /// <summary>A latitude, longitude, and height triple.</summary>
    Coordinates,

    /// <summary>A year, month, and day triple.</summary>
    DateParts,

    /// <summary>An hour, minute, and second triple.</summary>
    TimeParts,
}

/// <summary>
/// One parameter of an <see cref="ScpiCommand"/>, with everything needed to validate a value
/// before it is formatted into a command string.
/// </summary>
/// <param name="Name">The parameter's name, used for the field label and in confirmation text.</param>
/// <param name="Kind">What sort of value it takes.</param>
/// <param name="Unit">The unit the receiver expects, if any — seconds, degrees, and so on.</param>
/// <param name="Minimum">The smallest value the receiver accepts, where the manual gives one.</param>
/// <param name="Maximum">The largest value the receiver accepts, where the manual gives one.</param>
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
