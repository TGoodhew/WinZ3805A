using System.ComponentModel;
using System.Globalization;

using WinZ3805A.Device.Commands;

namespace WinZ3805A.ViewModels;

/// <summary>
/// What a tier C confirmation dialog says, and when it will let the command through (§8.3, §9.7.4).
/// </summary>
/// <remarks>
/// <para>
/// Every string the dialog shows is derived here from the catalog entry, so the §8.3 text reaches
/// the screen exactly as the table writes it and no page can paraphrase a consequence. The dialog
/// itself owns no copy at all beyond the word "Cancel".
/// </para>
/// <para>
/// The title is the display name with a question mark and the confirm button is the display name
/// unchanged, which is §9.11's rule that a verb keeps its identity from button to dialog to result.
/// The §8.3 sentence then goes in the body verbatim. For entries whose §8.3 text is a bare question
/// — "Reset alarm masks to defaults?" under the title "Reset alarm masks?" — that reads a little
/// redundantly, and that is the accepted cost: the alternative is trimming a sentence off the one
/// piece of copy the specification requires to appear word for word, on the surface where being
/// clever is least appropriate.
/// </para>
/// </remarks>
public sealed class CommandConfirmationViewModel : INotifyPropertyChanged
{
    private bool _isAcknowledged;

    /// <summary>Creates the model for one command about to be confirmed.</summary>
    /// <param name="command">The catalogued command. Must be <see cref="SafetyTier.Confirm"/>.</param>
    /// <param name="argument">The value that will be sent, already formatted for the receiver.</param>
    /// <param name="displayValue">
    /// The value as the user typed or picked it, for the <c>{0}</c> in the §8.3 sentence — so the
    /// dialog states the number actually about to be sent rather than a placeholder.
    /// </param>
    /// <param name="caution">
    /// An extra warning the page knows and the catalog cannot, shown above the acknowledgement.
    /// §10.8's power-up guard is what this exists for.
    /// </param>
    /// <param name="requireAcknowledgement">
    /// Forces the tick on a command that would not otherwise need one. §10.8 requires exactly this
    /// when the time since power-up cannot be determined.
    /// </param>
    public CommandConfirmationViewModel(
        ScpiCommand command,
        string? argument = null,
        string? displayValue = null,
        string? caution = null,
        bool requireAcknowledgement = false)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Tier != SafetyTier.Confirm)
        {
            throw new ArgumentException(
                $"{command.Mnemonic} is not a tier C command and has no confirmation to show.",
                nameof(command));
        }

        Command = command;
        Argument = argument;
        DisplayValue = displayValue ?? argument;
        Caution = string.IsNullOrWhiteSpace(caution) ? null : caution;
        RequiresAcknowledgement = command.RequiresAcknowledgement || requireAcknowledgement;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The command awaiting confirmation.</summary>
    public ScpiCommand Command { get; }

    /// <summary>The value that will be sent, or null for a command that takes none.</summary>
    public string? Argument { get; }

    /// <summary>The value as the user sees it.</summary>
    public string? DisplayValue { get; }

    /// <summary>The page's extra warning, or null.</summary>
    public string? Caution { get; }

    /// <summary>Whether the page wants the caution shown at all.</summary>
    public bool HasCaution => Caution is not null;

    /// <summary>The dialog title — the button's verb, asked as a question.</summary>
    public string Title => $"{Command.DisplayName}?";

    /// <summary>The §8.3 consequence text, with any value substituted.</summary>
    public string Message => Command.ConfirmationText is not string text
        ? Command.Description
        : text.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, text, DisplayValue ?? "the requested value")
            : text;

    /// <summary>The confirm button's label — the same verb the user clicked to get here.</summary>
    public string ConfirmLabel => Command.DisplayName;

    /// <summary>Whether a tick gates the confirm button (§9.7.4).</summary>
    public bool RequiresAcknowledgement { get; }

    /// <summary>The tick's label.</summary>
    public string AcknowledgementText => "I understand";

    /// <summary>Whether the user has ticked it.</summary>
    public bool IsAcknowledged
    {
        get => _isAcknowledged;
        set
        {
            if (_isAcknowledged == value)
            {
                return;
            }

            _isAcknowledged = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsAcknowledged)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanConfirm)));
        }
    }

    /// <summary>
    /// Whether the confirm button may be enabled. False until the tick, for the commands that
    /// require one — §9.7.4 gates the PrimaryButton on it, and P0-8 is the acceptance test.
    /// </summary>
    public bool CanConfirm => !RequiresAcknowledgement || IsAcknowledged;
}
