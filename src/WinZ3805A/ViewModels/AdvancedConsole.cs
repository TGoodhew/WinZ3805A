using System.Globalization;

using WinZ3805A.Device.Commands;

namespace WinZ3805A.ViewModels;

/// <summary>Why a catalogued command cannot be sent from the §10.11 console.</summary>
public enum ConsoleAvailability
{
    /// <summary>It can be selected, given a value if it needs one, and sent.</summary>
    Available = 0,

    /// <summary>
    /// Its parameter is a multi-field composite the console has no editor for.
    /// </summary>
    /// <remarks>
    /// Coordinates, a date and a time. Each already has a validated multi-field editor on the page
    /// that owns it — nine fields for a position — and a second implementation of that validation
    /// in a diagnostic console is exactly where the two would drift apart. The console names the
    /// page instead of offering a text box, because a text box is the thing §10.11 exists to avoid.
    /// </remarks>
    NeedsCompositeEditor,

    /// <summary>
    /// It takes more than one value, and the console offers one field.
    /// </summary>
    /// <remarks>
    /// One command does: <c>:PTIM:TZONe</c>, which takes hours and minutes. Found by a test written
    /// to pin the console's single-field assumption, which is the only reason it is refused rather
    /// than sent with the minutes quietly dropped.
    /// <para>
    /// Two number boxes would be a small change. What is not small is knowing what separator the
    /// receiver wants between them — it is a tier C command that changes every reported time, so
    /// guessing and trying it on a working receiver is not a way to find out. Refused until the
    /// format can be verified against the manual or a receiver that is not in service.
    /// </para>
    /// </remarks>
    TakesSeveralValues,
}

/// <summary>
/// One row of §10.11's command picker.
/// </summary>
/// <param name="Command">The catalogued command, which is the only source of any of this.</param>
public sealed record ConsoleCommand(ScpiCommand Command)
{
    /// <summary>The long form, as the picker lists it.</summary>
    public string Mnemonic => Command.Mnemonic;

    /// <summary>The label beside it.</summary>
    public string DisplayName => Command.DisplayName;

    /// <summary>What it does.</summary>
    public string Description => Command.Description;

    /// <summary>Its first parameter, or null when it takes none.</summary>
    /// <remarks>
    /// Only meaningful where <see cref="Availability"/> is
    /// <see cref="ConsoleAvailability.Available"/>, which is exactly the case where there is at
    /// most one. Reading it for a command that takes two would describe half of what it needs.
    /// </remarks>
    public ParameterSpec? Parameter => Command.Parameters.Count > 0 ? Command.Parameters[0] : null;

    /// <summary>Whether the console can send it, and why not when it cannot.</summary>
    public ConsoleAvailability Availability
    {
        get
        {
            if (Command.Parameters.Count > 1)
            {
                return ConsoleAvailability.TakesSeveralValues;
            }

            return Parameter?.Kind switch
            {
                ParameterKind.Coordinates or ParameterKind.DateParts or ParameterKind.TimeParts =>
                    ConsoleAvailability.NeedsCompositeEditor,
                _ => ConsoleAvailability.Available,
            };
        }
    }

    /// <summary>True for a tier C command, which still raises its §8.3 dialog from here.</summary>
    public bool NeedsConfirmation => Command.Tier == SafetyTier.Confirm;

    /// <summary>The tier, as the picker labels it.</summary>
    public string TierText => Command.Tier == SafetyTier.Confirm ? "Confirm" : "Safe";

    /// <summary>
    /// The one line a screen reader hears for this row.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Overridden because a record's generated <c>ToString</c> is what a <c>ComboBox</c> item
    /// announces.</b> The <c>DataTemplate</c> draws two columns for a sighted user, but the item's
    /// automation name comes from the item object — so before this, a screen-reader user choosing a
    /// command heard "ConsoleCommand { Command = ScpiCommand { Mnemonic = ..., ShortForm = ...,
    /// Tier = ..." and every other field of the record, for every row. Found by driving the picker
    /// through UI Automation rather than by reading it, which is the only way that failure shows.
    /// </para>
    /// <para>
    /// The tier is spoken, not just coloured or implied by an ellipsis on the button (§9.4.3,
    /// A11Y-12): whether the next click raises a confirmation is exactly the kind of thing that
    /// must not be carried by a visual cue alone.
    /// </para>
    /// </remarks>
    public override string ToString() =>
        NeedsConfirmation
            ? $"{Mnemonic} — {DisplayName}, needs confirmation"
            : $"{Mnemonic} — {DisplayName}";
}

/// <summary>
/// The §10.11 console's command list and its filter.
/// </summary>
/// <remarks>
/// <para>
/// <b>The list is the catalog and cannot be added to.</b> Every item is projected from
/// <see cref="CommandCatalog"/> at construction and the collection handed to the picker is
/// read-only, so there is no runtime path — binding, reflection over an observable collection, or a
/// helpful extension method — that puts a command in front of the user which §8.1 did not
/// authorise. The §8.4 exclusions are not in the catalog at all, so no filtering is needed to keep
/// them out and none is done: they are absent, not hidden.
/// </para>
/// <para>
/// <b>§8.5's experimental queries are left out.</b> They are opt-in and off by default, and their
/// opt-in is #56 rather than this issue. Including them here would enable them through a different
/// switch than the one §8.5 describes, which is worse than not offering them yet.
/// </para>
/// </remarks>
public static class ConsoleCatalog
{
    /// <summary>Everything the picker may offer, in mnemonic order.</summary>
    public static IReadOnlyList<ConsoleCommand> All { get; } =
        CommandCatalog.All
            .Where(command => !command.IsExperimental)
            .OrderBy(command => command.Mnemonic, StringComparer.Ordinal)
            .Select(command => new ConsoleCommand(command))
            .ToList()
            .AsReadOnly();

    /// <summary>
    /// The subset matching a filter, matched against the mnemonic, the short form and the label.
    /// </summary>
    /// <param name="filter">
    /// What the user typed. Empty or whitespace returns everything — a filter that emptied the list
    /// when cleared would look like a broken picker.
    /// </param>
    /// <remarks>
    /// A filter over a fixed list, not a search that could reach anything the list does not hold.
    /// The distinction matters: this is the one place a user types free text near a command, and
    /// what they type selects from the allowlist rather than contributing to what is sent.
    /// </remarks>
    public static IReadOnlyList<ConsoleCommand> Matching(string? filter)
    {
        if (string.IsNullOrWhiteSpace(filter))
        {
            return All;
        }

        string needle = filter.Trim();

        return All
            .Where(entry =>
                entry.Mnemonic.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.Command.ShortForm.Contains(needle, StringComparison.OrdinalIgnoreCase)
                || entry.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .ToList()
            .AsReadOnly();
    }
}

/// <summary>
/// Turns a typed value into the argument text for a command, or explains why it will not.
/// </summary>
/// <remarks>
/// Every formatter here is invariant-culture and produces a token the receiver's grammar accepts.
/// Nothing concatenates what the user typed: a number is parsed and re-rendered, a keyword is
/// matched against <see cref="ParameterSpec.Choices"/> and the <b>catalog's</b> spelling is what
/// goes out. That is what keeps §8.1's "no command is built from arbitrary user input" true of a
/// page whose entire purpose is to take user input.
/// </remarks>
public static class ConsoleArgument
{
    /// <summary>The result of validating one value.</summary>
    /// <param name="Text">The argument as it will be sent, or null when there is nothing to send.</param>
    /// <param name="Error">Why it was refused, or null when it was accepted.</param>
    public readonly record struct Result(string? Text, string? Error)
    {
        /// <summary>True when the value may be sent.</summary>
        public bool IsValid => Error is null;
    }

    /// <summary>Accepts a value for a parameter, or refuses it with §9.11's wording.</summary>
    /// <param name="parameter">The spec, or null when the command takes no parameter.</param>
    /// <param name="value">
    /// What the user entered. For a keyword this is the chosen keyword; for a number, its text.
    /// </param>
    public static Result For(ParameterSpec? parameter, string? value)
    {
        if (parameter is null)
        {
            return new Result(null, null);
        }

        string trimmed = value?.Trim() ?? string.Empty;

        if (trimmed.Length == 0)
        {
            return parameter.IsOptional
                ? new Result(null, null)
                : new Result(null, $"Enter a value for {parameter.Name}.");
        }

        return parameter.Kind switch
        {
            ParameterKind.Integer => Number(parameter, trimmed, whole: true),
            ParameterKind.Decimal => Number(parameter, trimmed, whole: false),
            ParameterKind.Keyword => Keyword(parameter, trimmed),
            ParameterKind.PrnList => PrnList(trimmed),
            _ => new Result(null, $"{parameter.Name} needs an editor this console does not have."),
        };
    }

    private static Result Number(ParameterSpec parameter, string value, bool whole)
    {
        if (!double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double parsed)
            || double.IsNaN(parsed)
            || double.IsInfinity(parsed))
        {
            return new Result(null, $"{parameter.Name} must be a number.");
        }

        if (whole && parsed != Math.Truncate(parsed))
        {
            return new Result(null, $"{parameter.Name} must be a whole number.");
        }

        if (parameter.Minimum is double minimum && parsed < minimum)
        {
            return new Result(null, Range(parameter));
        }

        if (parameter.Maximum is double maximum && parsed > maximum)
        {
            return new Result(null, Range(parameter));
        }

        // A keyword-style Choices list on a numeric parameter is a fixed set of legal values —
        // the baud rates — rather than a range. Membership, not bounds.
        if (parameter.Choices is { Count: > 0 } choices
            && !choices.Any(choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase)))
        {
            return new Result(null, $"{parameter.Name} must be one of {string.Join(", ", choices)}.");
        }

        // Re-rendered from the parsed value rather than passed through, so nothing the user typed
        // reaches the wire verbatim. "1e1" for a baud rate is refused above; " 10 " sends as "10".
        return new Result(
            whole
                ? ((long)parsed).ToString(CultureInfo.InvariantCulture)
                : parsed.ToString("0.###########", CultureInfo.InvariantCulture),
            null);
    }

    private static Result Keyword(ParameterSpec parameter, string value)
    {
        if (parameter.Choices is not { Count: > 0 } choices)
        {
            return new Result(null, $"{parameter.Name} has no documented values.");
        }

        string? match = choices.FirstOrDefault(
            choice => string.Equals(choice, value, StringComparison.OrdinalIgnoreCase));

        // The catalog's spelling, not the user's. Matching case-insensitively and then sending what
        // was matched means the wire only ever sees a string this application wrote.
        return match is null
            ? new Result(null, $"{parameter.Name} must be one of {string.Join(", ", choices)}.")
            : new Result(match, null);
    }

    /// <summary>One or more PRNs, 1 to 32, comma-separated.</summary>
    /// <remarks>
    /// Parsed to integers and re-rendered, so a semicolon — SCPI's command separator, and the one
    /// character that would turn a parameter field into the free-text path §10.11 forbids — cannot
    /// survive. It is not stripped or escaped; a value containing one simply fails to parse.
    /// </remarks>
    private static Result PrnList(string value)
    {
        string[] parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (parts.Length == 0)
        {
            return new Result(null, "Enter one or more PRNs, separated by commas.");
        }

        List<int> prns = [];
        foreach (string part in parts)
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out int prn))
            {
                return new Result(null, "Each PRN must be a whole number.");
            }

            if (prn is < 1 or > 32)
            {
                return new Result(null, "Each PRN must be between 1 and 32.");
            }

            prns.Add(prn);
        }

        return new Result(string.Join(",", prns.Select(prn => prn.ToString(CultureInfo.InvariantCulture))), null);
    }

    private static string Range(ParameterSpec parameter)
    {
        string unit = parameter.Unit is null ? string.Empty : $" {parameter.Unit}";

        return (parameter.Minimum, parameter.Maximum) switch
        {
            (double low, double high) => $"Enter a value between {Show(low)} and {Show(high)}{unit}.",
            (double low, null) => $"Enter a value of {Show(low)}{unit} or more.",
            (null, double high) => $"Enter a value of {Show(high)}{unit} or less.",
            _ => $"{parameter.Name} is out of range.",
        };

        static string Show(double value) => value.ToString("0.###########", CultureInfo.InvariantCulture);
    }
}
