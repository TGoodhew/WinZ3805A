using System.Reflection;
using System.Xml.Linq;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The medallion's visual states in <c>Themes/Generic.xaml</c> against the mode table in code.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this exists (#642).</b> The medallion takes its colour and glyph from a visual state named
/// after the mode, not from <see cref="ReceiverModes.SeverityOf"/>, so the two can disagree and no
/// test of the code would notice. The change that made a holdover waiting for GPS draw as holdover
/// updated the code, the taskbar, the tray and the text, every test passed, and the medallion stayed
/// amber with a pause glyph. It took a screenshot from the QA pass to see it.
/// </para>
/// <para>
/// Read from the file the application loads, embedded in this assembly under the same name.
/// </para>
/// </remarks>
public sealed class MedallionVisualStateTests
{
    private static readonly XNamespace Xaml = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace X = "http://schemas.microsoft.com/winfx/2006/xaml";

    public static TheoryData<ReceiverMode> Modes => new(
        [ReceiverMode.Locked, ReceiverMode.Recovering, ReceiverMode.Waiting, ReceiverMode.Holdover, ReceiverMode.PowerUp, ReceiverMode.Off, ReceiverMode.AwaitingReading, ReceiverMode.Disconnected]);

    /// <summary>
    /// Every mode has a state, and draws the glyph the code gives it (#752). A mode with no state
    /// would leave the medallion in whatever state it was last in: a new member added to the enum
    /// and not here would draw a reading that is gone.
    /// </summary>
    [Fact]
    public void EveryModeHasAStateDrawingItsOwnGlyph()
    {
        foreach (ReceiverMode mode in Enum.GetValues<ReceiverMode>())
        {
            string glyph = SettersOf(mode.ToString())["Glyph.Text"];

            Assert.Equal(ReceiverModes.GlyphOf(mode), glyph);
        }
    }

    [Theory]
    [MemberData(nameof(Modes))]
    public void EachModesStateUsesTheBrushItsSeverityNames(ReceiverMode mode)
    {
        string brush = ReceiverModes.SeverityOf(mode) switch
        {
            Severity.Success => "WzSuccessBrush",
            Severity.Caution => "WzCautionBrush",
            Severity.Critical => "WzCriticalBrush",
            _ => "WzNeutralBrush",
        };

        Dictionary<string, string> setters = SettersOf(mode.ToString());

        Assert.Contains(brush, setters["Glyph.Foreground"], StringComparison.Ordinal);
        Assert.Contains(brush, setters["PART_Ring.Stroke"], StringComparison.Ordinal);
    }

    [Fact]
    public void BothHoldoversDrawAlike()
    {
        // WAIT and HOLD are both holdover and draw alike by decision (#642); only the text differs.
        Assert.Equal(SettersOf(nameof(ReceiverMode.Holdover)), SettersOf(nameof(ReceiverMode.Waiting)));
    }

    /// <summary>The setters of the medallion's visual state with this name, target to value.</summary>
    private static Dictionary<string, string> SettersOf(string state)
    {
        XElement visualState = XDocument.Parse(ReadGeneric())
            .Descendants(Xaml + "VisualState")
            .Single(s => (string?)s.Attribute(X + "Name") == state && s.Descendants(Xaml + "Setter").Any(t => (string?)t.Attribute("Target") == "PART_Ring.Stroke"));

        return visualState.Descendants(Xaml + "Setter")
            .ToDictionary(s => (string)s.Attribute("Target")!, s => (string)s.Attribute("Value")!, StringComparer.Ordinal);
    }

    private static string ReadGeneric()
    {
        using Stream stream = Assembly.GetExecutingAssembly()
            .GetManifestResourceStream("WinZ3805A.Themes.Generic.xaml")
            ?? throw new InvalidOperationException("Generic.xaml is not embedded in the test assembly.");

        using StreamReader reader = new(stream);
        return reader.ReadToEnd();
    }
}
