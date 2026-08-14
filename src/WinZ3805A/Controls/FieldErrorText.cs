using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;

namespace WinZ3805A.Controls;

/// <summary>
/// The error line §9.11 puts directly below an invalid field.
/// </summary>
/// <remarks>
/// <para>
/// §9.11 spells this out precisely: <c>WzCaptionTextStyle</c> in <c>WzCriticalBrush</c>, preceded
/// by a 16 px <c></c> glyph, with the field's own border going critical as well — <b>glyph
/// plus text plus border</b>, so the error is never carried by border colour alone. That is the
/// same rule §9.4.3 applies to severity, for the same reason (A11Y-12).
/// </para>
/// <para>
/// A templated control with its style in <c>Generic.xaml</c>, like <see cref="SeverityPill"/> and
/// for the same reason: the brushes have to be <c>{ThemeResource}</c> so they re-resolve when the
/// theme changes (§9.13 item 2). A version that read them out of
/// <c>Application.Current.Resources</c> in its constructor would look identical and would then be
/// wrong from the first theme switch — including into high contrast, where it matters most.
/// </para>
/// <para>
/// Announced as a live region, so a screen reader hears the error when it appears rather than only
/// when focus next lands on the field (A11Y-9).
/// </para>
/// </remarks>
public sealed class FieldErrorText : Control
{
    /// <summary>Identifies the <see cref="Message"/> dependency property.</summary>
    public static readonly DependencyProperty MessageProperty = DependencyProperty.Register(
        nameof(Message),
        typeof(string),
        typeof(FieldErrorText),
        new PropertyMetadata(null, OnMessageChanged));

    /// <summary>Initialises a new error line, collapsed.</summary>
    public FieldErrorText()
    {
        DefaultStyleKey = typeof(FieldErrorText);
        Visibility = Visibility.Collapsed;
        AutomationProperties.SetLiveSetting(this, AutomationLiveSetting.Assertive);
    }

    /// <summary>What is wrong with the field above, or null when nothing is.</summary>
    public string? Message
    {
        get => (string?)GetValue(MessageProperty);
        set => SetValue(MessageProperty, value);
    }

    /// <summary>True while an error is showing.</summary>
    public bool HasError => !string.IsNullOrEmpty(Message);

    private static void OnMessageChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        FieldErrorText control = (FieldErrorText)sender;
        string? message = e.NewValue as string;

        control.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;

        // The glyph carries no text of its own, so the automation name is the whole of what a
        // screen reader has to work with.
        AutomationProperties.SetName(control, message ?? string.Empty);
    }
}
