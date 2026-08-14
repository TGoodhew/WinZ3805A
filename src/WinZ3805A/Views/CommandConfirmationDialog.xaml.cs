using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The tier C confirmation dialog (§8.3, §9.7.4).
/// </summary>
/// <remarks>
/// Wiring only. What it says, and whether the confirm button may enable, are
/// <see cref="CommandConfirmationViewModel"/>'s, where P0-8's acceptance criterion is tested
/// without a window.
/// </remarks>
public sealed partial class CommandConfirmationDialog : ContentDialog
{
    private readonly CommandConfirmationViewModel _model;

    /// <summary>Creates the dialog over a command awaiting confirmation.</summary>
    public CommandConfirmationDialog(CommandConfirmationViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();

        _model = model;

        Title = model.Title;
        PrimaryButtonText = model.ConfirmLabel;
        MessageText.Text = model.Message;

        if (model.HasCaution)
        {
            CautionBar.Message = model.Caution;
            CautionBar.IsOpen = true;
        }

        if (model.RequiresAcknowledgement)
        {
            AcknowledgeCheck.Content = model.AcknowledgementText;
            AcknowledgeCheck.Visibility = Visibility.Visible;
        }

        IsPrimaryButtonEnabled = model.CanConfirm;

        Opened += OnOpened;
    }

    private void OnAcknowledgementChanged(object sender, RoutedEventArgs e)
    {
        _model.IsAcknowledged = AcknowledgeCheck.IsChecked == true;
        IsPrimaryButtonEnabled = _model.CanConfirm;
    }

    /// <summary>
    /// Puts initial focus on Cancel, which §9.7.4 requires and which <c>DefaultButton</c> alone does
    /// not deliver.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>DefaultButton="Close"</c> governs which button Enter activates and which one wears the
    /// accent. It does <b>not</b> govern initial focus: <c>ContentDialog</c> focuses the first
    /// focusable element of its <i>content</i> when there is one, so the acknowledgement checkbox
    /// takes focus and the user is one space bar away from arming a destructive command they have
    /// not read. Found by running it, because the tree looks correct either way.
    /// </para>
    /// <para>
    /// The close button is reached through the template rather than held as a field, since
    /// <c>ContentDialog</c> builds its own button row. A template that does not carry the part is
    /// no reason to fail — focus simply stays where the framework put it.
    /// </para>
    /// <para>
    /// <c>FocusState.Keyboard</c> rather than <c>Programmatic</c>, so the focus rectangle moves as
    /// well as the focus. Under <c>Programmatic</c> the checkbox keeps the visual the framework
    /// gave it while Cancel holds the actual focus, which is worse than either alone: the eye is
    /// pointed at the destructive half of the dialog and the keyboard is not (A11Y-2).
    /// </para>
    /// </remarks>
    private void OnOpened(ContentDialog sender, ContentDialogOpenedEventArgs args)
    {
        if (GetTemplateChild("CloseButton") is not Control close)
        {
            return;
        }

        // Enqueued rather than called here: ContentDialog sets its own focus after Opened has
        // fired, so focusing synchronously is overwritten and looks, from the element tree, exactly
        // like not having tried. Low priority puts this after that.
        DispatcherQueue.TryEnqueue(
            DispatcherQueuePriority.Low,
            () => close.Focus(FocusState.Keyboard));
    }
}
