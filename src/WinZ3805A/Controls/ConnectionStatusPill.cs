using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WinZ3805A.Controls;

/// <summary>
/// The §9.7.3 title-bar element: severity shape, state text, and the port it is talking to.
/// </summary>
/// <remarks>
/// <para>
/// A <see cref="Button"/> rather than a bare <see cref="Control"/> because §9.10.2 says clicking it
/// opens the connection dialog, and deriving from the stock control brings the keyboard model, the
/// invoke pattern and the focus visual with it rather than reimplementing three things that have to
/// be right for §9.12.
/// </para>
/// <para>
/// The severity is rendered by composing <see cref="SeverityPill"/>, never by drawing a dot here.
/// P0-19 makes that the only permitted route, and the composition is what keeps this control
/// honest: it has no way to express a colour of its own.
/// </para>
/// <para>
/// <b>It does not dim when the window is deactivated.</b> §9.7.3 drops the rest of the title bar's
/// text and icons to <c>WzTextTertiaryBrush</c> on deactivation and exempts this control, because a
/// deactivated window is exactly when someone is glancing at it from across the room. The exemption
/// is implemented by the window setting the tertiary brush on the elements it owns and never on
/// this one — there is no inactive visual state here to get wrong.
/// </para>
/// </remarks>
public sealed class ConnectionStatusPill : Button
{
    /// <summary>Identifies the <see cref="Severity"/> dependency property.</summary>
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity),
        typeof(Severity),
        typeof(ConnectionStatusPill),
        new PropertyMetadata(Severity.Neutral, OnDescriptionChanged));

    /// <summary>Identifies the <see cref="StateText"/> dependency property.</summary>
    public static readonly DependencyProperty StateTextProperty = DependencyProperty.Register(
        nameof(StateText),
        typeof(string),
        typeof(ConnectionStatusPill),
        new PropertyMetadata(string.Empty, OnDescriptionChanged));

    /// <summary>Identifies the <see cref="PortName"/> dependency property.</summary>
    public static readonly DependencyProperty PortNameProperty = DependencyProperty.Register(
        nameof(PortName),
        typeof(string),
        typeof(ConnectionStatusPill),
        new PropertyMetadata(null, OnDescriptionChanged));

    /// <summary>Initialises a new pill.</summary>
    public ConnectionStatusPill()
    {
        DefaultStyleKey = typeof(ConnectionStatusPill);
    }

    /// <summary>How the connection is doing. Drives the shape and the colour, and nothing else.</summary>
    public Severity Severity
    {
        get => (Severity)GetValue(SeverityProperty);
        set => SetValue(SeverityProperty, value);
    }

    /// <summary>What state that is, in words — "Locked", "Holdover", "Disconnected".</summary>
    public string StateText
    {
        get => (string)GetValue(StateTextProperty);
        set => SetValue(StateTextProperty, value);
    }

    /// <summary>The port, or <see langword="null"/> when there is no connection to name.</summary>
    public string? PortName
    {
        get => (string?)GetValue(PortNameProperty);
        set => SetValue(PortNameProperty, value);
    }

    /// <summary>The separator and port label, hidden together when there is no port.</summary>
    /// <remarks>
    /// A dangling "·" after "Disconnected" reads as a value that failed to load rather than as one
    /// that does not apply, so the two collapse as a unit.
    /// </remarks>
    private FrameworkElement? _portGroup;

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        _portGroup = GetTemplateChild("PART_PortGroup") as FrameworkElement;
        UpdateDescription();
    }

    private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((ConnectionStatusPill)d).UpdateDescription();

    /// <remarks>
    /// One sentence naming the state, the port, and what invoking it does. §9.9 requires a
    /// title-bar control to carry both an automation name and a tooltip; the pill has visible text,
    /// but that text does not say it is actionable, and a screen reader user has no other way to
    /// learn that the connection dialog is behind it.
    /// </remarks>
    private void UpdateDescription()
    {
        string state = string.IsNullOrWhiteSpace(StateText) ? "Unknown" : StateText;

        string description = string.IsNullOrWhiteSpace(PortName)
            ? $"{state}. Opens the connection dialog."
            : $"{state} on {PortName}. Opens the connection dialog.";

        AutomationProperties.SetName(this, description);
        ToolTipService.SetToolTip(this, description);

        if (_portGroup is FrameworkElement group)
        {
            group.Visibility = string.IsNullOrWhiteSpace(PortName)
                ? Visibility.Collapsed
                : Visibility.Visible;
        }
    }
}
