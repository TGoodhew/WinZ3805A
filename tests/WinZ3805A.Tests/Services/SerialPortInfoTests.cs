using System.Runtime.InteropServices;
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
    /// §6.1 singles ARM64 out because several USB-serial chipsets ship no driver for it, so an empty
    /// list there points at the driver rather than at the cable.
    /// </remarks>
    [Fact]
    public void NoPortsMessage_NamesTheDriverOnArm64AndTheCableElsewhere()
    {
        string arm = SerialPortInfo.NoPortsMessage(Architecture.Arm64);
        string x64 = SerialPortInfo.NoPortsMessage(Architecture.X64);

        Assert.Contains("ARM64", arm, StringComparison.Ordinal);
        Assert.Contains("driver", arm, StringComparison.Ordinal);
        Assert.DoesNotContain("driver", x64, StringComparison.Ordinal);

        // §9.11: no apology, and an instruction to follow.
        Assert.All([arm, x64], message =>
        {
            Assert.Contains("Refresh", message, StringComparison.Ordinal);
            Assert.DoesNotContain("Sorry", message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Oops", message, StringComparison.OrdinalIgnoreCase);
        });
    }
}
