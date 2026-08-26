using System.Reflection;
using System.Xml.Linq;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// Which custom controls are in the tab order, and why each one is (A11Y-1, A11Y-2, #22).
/// </summary>
/// <remarks>
/// <para>
/// <c>Control.IsTabStop</c> defaults to <see langword="true"/>, so a custom control is in the tab
/// order **unless its style says otherwise**. Five of this application's six non-interactive
/// controls said otherwise; <c>TrendChart</c> did not, and the manual pass on #22 found what that
/// costs — Tab landed on both charts on the Timing page and **nothing was drawn**, because there is
/// no focus visual and nothing to give focus to. A stop that neither responds nor indicates is a
/// hole a keyboard user falls into: focus vanishes, and the next Tab moves on.
/// </para>
/// <para>
/// One omission out of six is not a pattern, it is an oversight — which is exactly the kind that
/// comes back when a seventh control is written. So the rule is stated here rather than left to be
/// noticed again: <b>a custom control is a tab stop only if it is on the list below, and being on
/// that list means someone decided it.</b>
/// </para>
/// <para>
/// Read from the embedded <c>Generic.xaml</c>, not from a copy: a rule about what the styles declare
/// is worth having only if it is checked against the file the application actually loads.
/// </para>
/// </remarks>
public class ControlTabStopTests
{
    /// <summary>Where a focusable control's focus visual comes from.</summary>
    private enum FocusVisual
    {
        /// <summary>Declared in the style here, so this test can check it.</summary>
        Style,

        /// <summary>Set in the control's constructor, which this test cannot see.</summary>
        Constructor,
    }

    /// <summary>
    /// The controls that are deliberately focusable, what earns it, and where they show focus.
    /// </summary>
    /// <remarks>
    /// A list rather than a scan, because opting <i>in</i> to the tab order is a decision somebody
    /// has to have made. Both entries here were: one has a keyboard model, the other is a button.
    /// </remarks>
    private static readonly Dictionary<string, (string Why, FocusVisual Focus)> Interactive =
        new(StringComparer.Ordinal)
        {
            ["SkyPlotControl"] =
                ("§9.10.2's arrow-key model: arrows move a ring in PRN order, Enter selects.",
                 FocusVisual.Constructor),

            ["ConnectionStatusPill"] =
                ("It is a Button — §9.7.3's title-bar control, which opens the connection dialog.",
                 FocusVisual.Style),
        };

    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";

    [Fact]
    public void EveryNonInteractiveControlOptsOutOfTheTabOrder()
    {
        List<string> offenders = [];

        foreach (XElement style in ControlStyles())
        {
            string name = TargetType(style);

            if (Interactive.ContainsKey(name))
            {
                continue;
            }

            if (!SetsTabStopFalse(style))
            {
                offenders.Add(name);
            }
        }

        Assert.True(
            offenders.Count == 0,
            "Control.IsTabStop defaults to True, so these are in the tab order with no keyboard "
            + "model and no focus visual — a stop that neither responds nor indicates: "
            + string.Join(", ", offenders));
    }

    /// <remarks>
    /// The reason the allowlist is a list rather than a scan: a control that opts <i>in</i> has to
    /// have been decided about. Without this, silencing the test above by adding a name to
    /// <see cref="Interactive"/> would look like a fix rather than like an argument.
    /// </remarks>
    [Fact]
    public void EveryControlNamedAsInteractiveGivesAReason()
    {
        Assert.All(Interactive, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value.Why)));

        foreach (string name in Interactive.Keys)
        {
            Assert.True(
                ControlStyles().Any(s => TargetType(s) == name),
                $"{name} is named as interactive but has no style in Generic.xaml — the list is stale.");
        }
    }

    /// <summary>The other half of A11Y-2: if it can take focus, it has to show focus.</summary>
    /// <remarks>
    /// <para>
    /// #22 is one rule seen from two sides. A control that should not be focusable must leave the
    /// tab order; a control that should be focusable must draw something when it gets there. Both
    /// failures look identical to a keyboard user — focus goes somewhere and nothing happens.
    /// </para>
    /// <para>
    /// <b>Only the style half is checked here, and that is a real limit rather than an oversight.</b>
    /// <c>SkyPlotControl</c> sets <c>UseSystemFocusVisuals</c> in its constructor, which this test
    /// reads no source for; its entry says so rather than being quietly exempted. What this does
    /// catch is the next control that opts into the tab order through its style and forgets the
    /// other line.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryFocusableControlShowsFocusWhereItSaysItDoes()
    {
        foreach ((string name, (string _, FocusVisual focus)) in Interactive)
        {
            if (focus != FocusVisual.Style)
            {
                continue;
            }

            XElement style = ControlStyles().Single(s => TargetType(s) == name);

            Assert.True(
                SetsTrue(style, "UseSystemFocusVisuals"),
                $"{name} is in the tab order but its style does not turn a focus visual on — "
                + "A11Y-2 asks every focusable element to show focus.");
        }
    }

    /// <summary>The scan has to find something, or every assertion above is a decoration.</summary>
    /// <remarks>#181's rule, applied to a test rather than a gate.</remarks>
    [Fact]
    public void TheStylesAreFound() =>
        Assert.True(ControlStyles().Count >= 6, $"found {ControlStyles().Count} control styles");

    // -------------------------------------------------------------------------------------

    private static List<XElement> ControlStyles()
    {
        XDocument document = XDocument.Parse(ReadGeneric());

        return
        [
            .. document.Descendants(Xaml + "Style")
                .Where(s => TargetType(s).Length > 0)
        ];
    }

    /// <summary>The local name of a <c>TargetType="controls:Foo"</c>, or empty for anything else.</summary>
    private static string TargetType(XElement style)
    {
        string value = (string?)style.Attribute("TargetType") ?? string.Empty;

        return value.StartsWith("controls:", StringComparison.Ordinal)
            ? value["controls:".Length..]
            : string.Empty;
    }

    /// <remarks>
    /// Only the style's own setters. A setter inside the <c>ControlTemplate</c> belongs to a part of
    /// the control, not to the control, and counting one would let a template's inner
    /// <c>IsTabStop</c> stand in for the control's own.
    /// </remarks>
    private static bool SetsTabStopFalse(XElement style) => Sets(style, "IsTabStop", "False");

    private static bool SetsTrue(XElement style, string property) => Sets(style, property, "True");

    private static bool Sets(XElement style, string property, string value) =>
        style.Elements(Xaml + "Setter").Any(setter =>
            (string?)setter.Attribute("Property") == property
            && string.Equals((string?)setter.Attribute("Value"), value, StringComparison.OrdinalIgnoreCase));

    private static string ReadGeneric()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("WinZ3805A.Themes.Generic.xaml")
            ?? throw new InvalidOperationException("Generic.xaml is not embedded in the test assembly.");

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
