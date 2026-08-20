using System.Globalization;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>
/// Runs a confirmed tier C command and reports it in §9.11's terms.
/// </summary>
/// <remarks>
/// <para>
/// This is the half of §15 step 10 that has nothing to do with a window. The dialog decides
/// <i>whether</i> the command runs; this decides what happened when it did, which is where §7.2's
/// error-queue rule and §9.11's copy rules actually live. Keeping them apart is what lets both be
/// tested without a `XamlRoot`, and there is exactly one place that formats a failure sentence
/// rather than one per page.
/// </para>
/// <para>
/// It deliberately refuses anything that is not tier C. Not because a safe command would be
/// dangerous here, but because this path issues <c>:SYST:ERR?</c> after every command, and §7.2 is
/// explicit that doing that after tier S queries doubles the traffic for no benefit. A safe command
/// arriving here would be a caller that has confused the two, and silently doing the right thing
/// for it would hide that.
/// </para>
/// </remarks>
public sealed class CommandInvoker
{
    private readonly DeviceSessionService _session;

    /// <summary>
    /// The error-queue read §7.2 requires after every tier C command, resolved from the catalog
    /// rather than typed, so it is subject to the same allowlist as everything else (§8.1).
    /// </summary>
    private static readonly ScpiCommand NextError =
        CommandCatalog.Find(":SYST:ERR?")
        ?? throw new InvalidOperationException("The catalog has no :SYST:ERR? entry, which §7.2 requires.");

    /// <summary>Creates an invoker over the shared session.</summary>
    public CommandInvoker(DeviceSessionService session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <summary>
    /// Sends a tier C command, reads the error queue after it, and describes the result.
    /// </summary>
    /// <param name="command">The catalogued command. Must be <see cref="SafetyTier.Confirm"/>.</param>
    /// <param name="argument">The value to send, already formatted for the receiver, or null.</param>
    /// <param name="displayValue">
    /// The value as the user saw it, for the <c>{0}</c> in the success sentence. Falls back to
    /// <paramref name="argument"/>, which is usually the same text and always close enough to be
    /// better than a literal "{0}" reaching the screen.
    /// </param>
    /// <param name="cancellationToken">Cancels the wait, not the receiver.</param>
    public async Task<CommandOutcome> ExecuteAsync(
        ScpiCommand command,
        string? argument = null,
        string? displayValue = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(command);

        if (command.Tier != SafetyTier.Confirm)
        {
            throw new ArgumentException(
                $"{command.Mnemonic} is not a tier C command. Only tier C runs through the confirmation path.",
                nameof(command));
        }

        if (_session.Status != ConnectionStatus.Connected)
        {
            return Failure(command, "The receiver is not connected.");
        }

        Transaction transaction;
        try
        {
            transaction = await _session.ExecuteAsync(command, argument, cancellationToken: cancellationToken).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException exception)
        {
            // The queue closes when the session is torn down under a command in flight. That is a
            // disconnect the user is about to be told about anyway, so it is reported, not thrown.
            return Failure(command, exception.Message);
        }

        if (transaction.Outcome == TransactionOutcome.TimedOut)
        {
            return Failure(
                command,
                $"The receiver did not answer within {FormatSeconds(transaction.Elapsed)} seconds.");
        }

        if (transaction.Outcome == TransactionOutcome.Faulted)
        {
            return Failure(command, transaction.FaultMessage ?? "The serial link failed.");
        }

        // §7.2: after every tier C command, read the error queue and surface anything non-zero.
        // The prompt's own E-nnn token is a weaker signal — it says a command was rejected without
        // saying which error — so it is used only to decide whether an unreadable queue response is
        // worth reporting, never as the report itself.
        ScpiError? error = await ReadErrorAsync(cancellationToken).ConfigureAwait(true);

        if (error is { IsError: true })
        {
            return new CommandOutcome
            {
                Kind = CommandOutcomeKind.Rejected,
                Command = command,
                Error = error,
                Message = $"{Preamble(command)} {error.Describe()}{Remedy(command)}",
                Lines = transaction.Lines,
            };
        }

        if (error is null && transaction.HasDeviceError)
        {
            // The prompt said the command was rejected but the queue would not say why — an empty
            // queue after E-nnn means something else drained it. Report what is known rather than
            // claiming success.
            return new CommandOutcome
            {
                Kind = CommandOutcomeKind.Rejected,
                Command = command,
                Message = $"{Preamble(command)} The receiver rejected it ({transaction.PromptStatus}) "
                          + "and its error queue was already empty.",
                Lines = transaction.Lines,
            };
        }

        return new CommandOutcome
        {
            Kind = CommandOutcomeKind.Succeeded,
            Command = command,
            Message = FormatSuccess(command, displayValue ?? argument),
            Lines = transaction.Lines,
        };
    }

    // ===========================================================================================

    private async Task<ScpiError?> ReadErrorAsync(CancellationToken cancellationToken)
    {
        try
        {
            Transaction reply = await _session
                .ExecuteAsync(NextError, cancellationToken: cancellationToken)
                .ConfigureAwait(true);

            return reply.Succeeded ? ScpiError.TryParse(reply.FirstLine) : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (InvalidOperationException)
        {
            // The session went away between the command and the check. The command's own result is
            // still worth reporting, so this is an unknown error rather than a failure.
            return null;
        }
    }

    private static CommandOutcome Failure(ScpiCommand command, string detail) => new()
    {
        Kind = CommandOutcomeKind.Failed,
        Command = command,
        Message = $"{Preamble(command)} {detail}",
    };

    /// <summary>
    /// "Couldn't set antenna delay." — §9.11's error pattern, carrying the same verb as the button
    /// that started it.
    /// </summary>
    private static string Preamble(ScpiCommand command) =>
        $"Couldn't {Decapitalise(command.DisplayName)}.";

    /// <summary>
    /// The "what to do next" §9.11 asks errors to end with, where the catalog knows enough to say
    /// it. A range is the only remedy derivable from the command itself; anything else would be
    /// guesswork dressed as advice, so commands without one simply end at the error.
    /// </summary>
    private static string Remedy(ScpiCommand command)
    {
        if (command.Parameters.Count != 1)
        {
            return string.Empty;
        }

        ParameterSpec parameter = command.Parameters[0];
        if (parameter is not { Minimum: double minimum, Maximum: double maximum })
        {
            return string.Empty;
        }

        string unit = string.IsNullOrEmpty(parameter.Unit) ? string.Empty : $" {parameter.Unit}";
        return $" Enter a value between {Format(minimum)} and {Format(maximum)}{unit}.";
    }

    private static string FormatSuccess(ScpiCommand command, string? value)
    {
        string template = command.SuccessText ?? $"{command.DisplayName} completed.";
        return template.Contains("{0}", StringComparison.Ordinal)
            ? string.Format(CultureInfo.CurrentCulture, template, value ?? "the requested value")
            : template;
    }

    private static string Format(double value) =>
        value.ToString("#,##0.###", CultureInfo.CurrentCulture);

    private static string FormatSeconds(TimeSpan elapsed) =>
        elapsed.TotalSeconds.ToString("0.#", CultureInfo.CurrentCulture);

    /// <summary>
    /// Lower-cases the first word of a display name so it reads inside a sentence, without touching
    /// the rest — "Set antenna delay" becomes "set antenna delay", and "Run GPS diagnostic" keeps
    /// its GPS.
    /// </summary>
    private static string Decapitalise(string text) =>
        text.Length == 0 ? text : char.ToLower(text[0], CultureInfo.CurrentCulture) + text[1..];
}
