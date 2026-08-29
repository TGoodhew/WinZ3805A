using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The one route from a button to a tier C command: confirm, run, then report (§8.3, §9.7.4, §9.11).
/// </summary>
/// <remarks>
/// <para>
/// Pages call this and nothing else. They never construct
/// <see cref="CommandConfirmationDialog"/> themselves and never reach
/// <see cref="CommandInvoker"/> directly, because either would be a second path to a destructive
/// command — one that could be written without a confirmation and would look perfectly ordinary in
/// review.
/// </para>
/// <para>
/// Returns null when the user cancelled, which is not an outcome to report: §9.11 gives no surface
/// to "you decided not to do that".
/// </para>
/// </remarks>
public static class CommandConfirmation
{
    /// <summary>
    /// Shows the §8.3 confirmation and, if the user goes ahead, runs the command and returns what
    /// happened.
    /// </summary>
    /// <param name="root">The <see cref="XamlRoot"/> the dialog belongs to.</param>
    /// <param name="invoker">The invoker over the device session.</param>
    /// <param name="command">The catalogued tier C command.</param>
    /// <param name="argument">The value to send, already formatted for the receiver.</param>
    /// <param name="displayValue">The value as the user sees it, for the dialog and the result.</param>
    /// <param name="caution">An extra warning only the page knows — §10.8's power-up guard.</param>
    /// <param name="requireAcknowledgement">Forces the tick on a command that would not need one.</param>
    /// <param name="cancellationToken">Cancels the wait for the receiver, not the dialog.</param>
    public static async Task<CommandOutcome?> RunAsync(
        XamlRoot root,
        CommandInvoker invoker,
        ScpiCommand command,
        string? argument = null,
        string? displayValue = null,
        string? caution = null,
        bool requireAcknowledgement = false,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(invoker);
        ArgumentNullException.ThrowIfNull(command);

        CommandConfirmationViewModel model = new(
            command, argument, displayValue, caution, requireAcknowledgement);

        CommandConfirmationDialog dialog = new(model) { XamlRoot = root };

        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return null;
        }

        return await invoker
            .ExecuteAsync(command, argument, displayValue, cancellationToken)
            .ConfigureAwait(true);
    }

    /// <summary>
    /// Looks a tier C command up in the driver's catalog, or fails loudly.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A page naming a mnemonic the catalog does not hold is a bug in the page, not a condition to
    /// degrade around: §8.1 makes the catalog the only source of commands, so the absence means the
    /// page and the catalog disagree about what exists. Better found on the first navigation than
    /// by a button that silently does nothing.
    /// </para>
    /// <para>
    /// Takes the driver since #287, which moved every caller from a static field to a lookup at
    /// the point of use — the commands a page runs are the connected receiver's, not the
    /// process's, and the receiver can change between navigations. The failure moved from
    /// type-initialisation to the first click, and the early loudness the static fields provided
    /// lives in a test now: <c>ReceiverDriverTests</c> pins that every mnemonic a page requires
    /// resolves through the SmartClock driver, which fails the build's test run rather than the
    /// running application. Note what this contract means for a new family: the pages that call
    /// this require these mnemonics of any driver they are used with — the capability-gating that
    /// would soften that is #287's deferred item 4, and docs/adding-a-receiver.md names it
    /// honestly.
    /// </para>
    /// </remarks>
    public static ScpiCommand Require(IReceiverDriver driver, string mnemonic)
    {
        ArgumentNullException.ThrowIfNull(driver);

        return driver.Find(mnemonic)
            ?? throw new InvalidOperationException(
                $"{mnemonic} is not in the {driver.Family} driver's command catalog.");
    }
}
