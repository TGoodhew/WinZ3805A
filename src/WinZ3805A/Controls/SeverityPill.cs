using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace WinZ3805A.Controls;

/// <summary>
/// The one way severity is rendered anywhere in this application (§9.10.2, P0-19).
/// </summary>
/// <remarks>
/// <para>
/// Every indication is a triple — colour, shape, and text — and all three come from
/// <see cref="Severity"/>. The shape channel is the load-bearing one: success and critical
/// converge under deuteranopia and protanopia, and a circle and a hexagon do not. Colour is the
/// channel most likely to be lost, so it is never the only one carrying meaning (§9.4.3).
/// </para>
/// <para>
/// The shapes are <c>Path</c> geometry rather than glyphs from a font. A glyph depends on a font
/// being present and on its metrics; geometry renders identically everywhere, including under high
/// contrast where every severity brush collapses to the system window text colour and the shape
/// becomes the *only* thing distinguishing one level from another.
/// </para>
/// <para>
/// It takes an enum and never a brush, which is what makes §9.13 item 10 enforceable rather than
/// aspirational: a page cannot render a bare coloured dot through this control, because there is no
/// way to hand it a colour.
/// </para>
/// </remarks>
public sealed class SeverityPill : Control
{
    /// <summary>Identifies the <see cref="Severity"/> dependency property.</summary>
    public static readonly DependencyProperty SeverityProperty = DependencyProperty.Register(
        nameof(Severity),
        typeof(Severity),
        typeof(SeverityPill),
        new PropertyMetadata(Severity.Neutral, OnSeverityChanged));

    /// <summary>Identifies the <see cref="Text"/> dependency property.</summary>
    public static readonly DependencyProperty TextProperty = DependencyProperty.Register(
        nameof(Text),
        typeof(string),
        typeof(SeverityPill),
        new PropertyMetadata(string.Empty, OnTextChanged));

    /// <summary>Initialises a new pill.</summary>
    public SeverityPill()
    {
        DefaultStyleKey = typeof(SeverityPill);
    }

    /// <summary>How bad it is. Drives the colour, the shape, and nothing else about the control.</summary>
    /// <remarks>
    /// Assigned only when it differs (#403). See <see cref="_severityShown"/>.
    /// </remarks>
    public Severity Severity
    {
        get => (Severity)GetValue(SeverityProperty);
        set
        {
            if (_severityShown == value)
            {
                return;
            }

            SetValue(SeverityProperty, value);
        }
    }

    /// <summary>
    /// The label, which is the third channel of the triple and is never optional.
    /// </summary>
    /// <remarks>
    /// A pill with no text is a coloured shape, and a coloured shape is what §9.13 item 10 calls a
    /// defect. Say what the state is: "Locked", "Holdover", "Reduced accuracy".
    /// </remarks>
    public string Text
    {
        get => (string)GetValue(TextProperty);
        set
        {
            if (string.Equals(_textShown, value, StringComparison.Ordinal))
            {
                return;
            }

            SetValue(TextProperty, value);
        }
    }

    /// <summary>
    /// What the properties above currently hold, so an assignment that changes nothing is skipped.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <c>SetValue</c> takes its value as an <c>IInspectable</c>, so every assignment boxes and
    /// mints a COM callable wrapper — and the runtime appends every wrapper to storage it never
    /// shrinks (#399, #403). A pill on a page that redraws several times a second carries a
    /// severity that changes a few times a day.
    /// </para>
    /// <para>
    /// <b>Kept by the change callbacks, not by the setters.</b> That is the whole point: this
    /// control's own default template contains a <c>SeverityPill</c> whose severity is a
    /// <c>{TemplateBinding}</c>, and a binding writes through the property system without ever
    /// touching the CLR setter. A shadow maintained in the setter would go stale for exactly that
    /// pill, and the next assignment matching the stale value would be skipped — a wrong colour
    /// that no test would catch. Updated where every writer converges instead.
    /// </para>
    /// <para>
    /// The comparison is against this rather than against <c>GetValue</c> because a read crosses
    /// the same boundary a write does, and would cost what it saves.
    /// </para>
    /// </remarks>
    private Severity _severityShown = Severity.Neutral;

    /// <inheritdoc cref="_severityShown" />
    private string _textShown = string.Empty;

    /// <inheritdoc />
    protected override void OnApplyTemplate()
    {
        base.OnApplyTemplate();
        UpdateVisualState(useTransitions: false);
        UpdateAutomationName();
    }

    private static void OnSeverityChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (SeverityPill)d;

        // Here rather than in the setter, so a {TemplateBinding} or a Style Setter cannot leave it
        // stale. See _severityShown.
        pill._severityShown = (Severity)e.NewValue;
        pill.UpdateVisualState(useTransitions: false);
    }

    private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var pill = (SeverityPill)d;
        pill._textShown = (string?)e.NewValue ?? string.Empty;
        pill.UpdateAutomationName();
    }

    /// <remarks>
    /// <c>useTransitions: false</c> always. §9.8.2 gives severity state changes
    /// <c>WzDurationInstant</c>: an animated severity change reads as movement in peripheral
    /// vision, and on an instrument left running on a second monitor that is a false alarm.
    /// </remarks>
    private void UpdateVisualState(bool useTransitions) =>
        VisualStateManager.GoToState(this, Severity.ToString(), useTransitions);

    /// <remarks>
    /// The label already carries the meaning, so the pill announces exactly it rather than
    /// inventing a longer sentence that would then disagree with what is on screen. The shape is
    /// marked <c>Raw</c> in the template because it duplicates the text for anyone reading it
    /// visually and would otherwise be announced as an unnamed graphic.
    /// </remarks>
    private void UpdateAutomationName() =>
        AutomationProperties.SetName(this, Text ?? string.Empty);
}
