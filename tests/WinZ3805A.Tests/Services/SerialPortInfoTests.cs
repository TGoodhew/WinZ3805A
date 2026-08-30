using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// The §10.12 picker's list: what it shows, in what order, and what it says when there is nothing
/// to show.
/// </summary>
public sealed class SerialPortInfoTests
{
    [Fact]
    public void DisplayLabel_UsesTheWireframeForm_WhenADescriptionIsKnown()
    {
        SerialPortInfo port = new() { PortName = "COM3", Description = "USB Serial Port" };

        Assert.Equal("COM3 — USB Serial Port", port.DisplayLabel);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void DisplayLabel_FallsBackToThePortName_WhenNoDescriptionSurvived(string? description)
    {
        SerialPortInfo port = new() { PortName = "COM3", Description = description };

        Assert.Equal("COM3", port.DisplayLabel);
    }

    [Fact]
    public void Merge_AttachesDescriptionsByPortName()
    {
        IReadOnlyList<SerialPortInfo> ports = SerialPortInfo.Merge(
            ["COM1", "COM3"],
            new Dictionary<string, string> { ["COM3"] = "USB Serial Port" });

        Assert.Equal(["COM1", "COM3"], ports.Select(port => port.PortName));
        Assert.Null(ports[0].Description);
        Assert.Equal("USB Serial Port", ports[1].Description);
    }

    /// <remarks>
    /// A description with no port behind it is a stale registry entry for a device that has been
    /// unplugged. Listing it would offer the user a port that cannot be opened.
    /// </remarks>
    [Fact]
    public void Merge_DropsDescriptionsForPortsThatAreNotThere()
    {
        IReadOnlyList<SerialPortInfo> ports = SerialPortInfo.Merge(
            ["COM1"],
            new Dictionary<string, string> { ["COM7"] = "Gone" });

        Assert.Equal("COM1", Assert.Single(ports).PortName);
    }

    /// <remarks>
    /// The two registry sources overlap by design — the framework's list and the device map name the
    /// same ports — so the merge has to collapse them or the picker lists everything twice.
    /// </remarks>
    [Fact]
    public void Merge_CollapsesDuplicates()
    {
        IReadOnlyList<SerialPortInfo> ports = SerialPortInfo.Merge(["COM3", "com3", " COM3 "]);

        Assert.Equal("COM3", Assert.Single(ports).PortName);
    }

    /// <remarks>
    /// Ordinal order puts COM10 second and COM9 last, which on a multi-port card means scrolling
    /// past the port you wanted.
    /// </remarks>
    [Fact]
    public void Merge_OrdersNumerically()
    {
        IReadOnlyList<SerialPortInfo> ports = SerialPortInfo.Merge(["COM10", "COM2", "COM1", "COM9"]);

        Assert.Equal(["COM1", "COM2", "COM9", "COM10"], ports.Select(port => port.PortName));
    }

    [Fact]
    public void Merge_OrdersPortsWithoutANumberByName()
    {
        IReadOnlyList<SerialPortInfo> ports = SerialPortInfo.Merge(["LPT", "COM2", "AUX"]);

        Assert.Equal(["AUX", "COM2", "LPT"], ports.Select(port => port.PortName));
    }

    [Fact]
    public void Merge_IgnoresBlankNames()
    {
        IReadOnlyList<SerialPortInfo> ports = SerialPortInfo.Merge(["", "  ", "COM1"]);

        Assert.Equal("COM1", Assert.Single(ports).PortName);
    }

    /// <remarks>
    /// One message, because §6.1 has one supported architecture. This asserted a second one that
    /// named a missing ARM64 driver, which the specification required until it was amended on
    /// 29 Aug 2026 to stop describing ARM64 as a target — and which no caller could ever select,
    /// because an x64 package reports <c>X64</c> for its process even on an ARM64 machine (#319).
    /// </remarks>
    [Fact]
    public void NoPortsMessage_NamesTheAdapterAndWhatToDo()
    {
        string message = SerialPortInfo.NoPortsMessage();

        Assert.Contains("adapter", message, StringComparison.Ordinal);

        // §9.11: no apology, and an instruction to follow.
        Assert.Contains("Refresh", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Sorry", message, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Oops", message, StringComparison.OrdinalIgnoreCase);
    }
}
