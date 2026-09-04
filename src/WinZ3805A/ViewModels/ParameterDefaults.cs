using System.Globalization;

using WinZ3805A.Device.Commands;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The value a parameter's editor opens with (#404).
/// </summary>
/// <remarks>
/// <para>
/// <b>A default that is not legal is worse than no default.</b> The console builds an editor the
/// moment a command is picked and validates it immediately, so a starting value the validator
/// rejects puts an error on screen and disables Send before the user has touched anything - which
/// reads, reasonably, as the console refusing to accept the command at all. That is what
/// <c>:SYST:COMM:SER1:BAUD</c> did: its editor opened on <c>0</c> against a parameter whose legal
/// values are 1200, 2400, 9600 and 19200.
/// </para>
/// <para>
/// The rule lives here rather than inside the editor-building code so it can be tested against the
/// real catalog. <c>ConsoleCommandDefaultsTests</c> asserts that every parameter of every command
/// starts on a value <see cref="ConsoleArgument.For(ParameterSpec?, string?)"/> accepts, which is the
/// check that would have caught this - and the two before it, §10.8's duration limit opening on 1
/// against a receiver holding 86 400 and §10.5's mask opening on 10. This is the third time a
/// hard-coded editor default has shipped wrong; the pattern is that nobody re-reads a default,
/// so a test has to.
/// </para>
/// </remarks>
public static class ParameterDefaults
{
    /// <summary>The text a fresh editor for <paramref name="parameter"/> should hold.</summary>
    /// <param name="parameter">The parameter being edited, or null when the command takes none.</param>
    /// <returns>The starting value, which must satisfy the validator.</returns>
    public static string? For(ParameterSpec? parameter)
    {
        if (parameter is null)
        {
            return null;
        }

        // A constrained set answers the question by itself, whatever the kind says. The catalog
        // declares baud as an Integer that happens to carry Choices, and the validator enforces
        // those choices for every kind - so the editor has to read the same signal the validator
        // does, or the two disagree about what is legal (#404).
        if (parameter.Choices is { Count: > 0 } choices)
        {
            return choices[0];
        }

        if (parameter.Kind == ParameterKind.PrnList)
        {
            // No sensible default: any PRN this invented would be a satellite the user did not ask
            // for. Empty is caught by the validator as "required", which is honest.
            return string.Empty;
        }

        double value = parameter.Minimum ?? 0;
        return parameter.Kind == ParameterKind.Integer
            ? ((long)value).ToString(CultureInfo.InvariantCulture)
            : value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The numeric form of <see cref="For(ParameterSpec?)"/>, for a NumberBox.</summary>
    /// <param name="parameter">The parameter being edited.</param>
    /// <returns>The starting value as a double.</returns>
    public static double NumberFor(ParameterSpec? parameter)
    {
        string? text = For(parameter);

        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value)
            ? value
            : 0;
    }
}
