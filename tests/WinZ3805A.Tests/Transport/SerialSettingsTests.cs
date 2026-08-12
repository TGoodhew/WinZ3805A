using System.IO.Ports;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>The §7.1 line parameters and the §10.12 auto-detect order.</summary>
public class SerialSettingsTests
{
    /// <summary>The Z3805A ships 9600-8-N-1, which is the only reason it is the default.</summary>
    [Fact]
    public void TheDefaultIsTheZ3805AFactoryConfiguration()
    {
        SerialSettings settings = SerialSettings.Default;

        Assert.Equal(9600, settings.BaudRate);
        Assert.Equal(8, settings.DataBits);
        Assert.Equal(Parity.None, settings.Parity);
        Assert.Equal(StopBits.One, settings.StopBits);
        Assert.Equal("9600-8-N-1", settings.ToString());
    }

    /// <summary>A Z3801A is commonly 19200-7-E-1, which is why nothing may hard-code the defaults (§7.1).</summary>
    [Fact]
    public void SettingsRenderTheWayInstrumentDocumentationWritesThem()
    {
        SerialSettings settings = new() { BaudRate = 19200, DataBits = 7, Parity = Parity.Even, StopBits = StopBits.One };

        Assert.Equal("19200-7-E-1", settings.ToString());
    }

    [Fact]
    public void TheAutoDetectSequenceIsTheEightCombinationsSection1012Lists()
    {
        IReadOnlyList<SerialSettings> sequence = SerialSettings.AutoDetectSequence;

        Assert.Equal(8, sequence.Count);
        Assert.Equal(
            new[]
            {
                "9600-8-N-1", "19200-7-E-1", "9600-7-E-1", "19200-8-N-1",
                "2400-8-N-1", "1200-8-N-1", "9600-7-O-1", "19200-7-O-1",
            },
            sequence.Select(setting => setting.ToString()));
    }

    /// <summary>
    /// Most-likely-first is the point of the order: a Z3805A answers on attempt one and a Z3801A on
    /// attempt two, so the common cases never wait out the slow baud rates at the tail.
    /// </summary>
    [Fact]
    public void TheAutoDetectSequenceLeadsWithTheTwoCommonConfigurations()
    {
        Assert.Equal(SerialSettings.Default, SerialSettings.AutoDetectSequence[0]);
        Assert.Equal("19200-7-E-1", SerialSettings.AutoDetectSequence[1].ToString());
    }

    [Fact]
    public void EveryOfferedParameterIsWithinTheSection71Ranges()
    {
        foreach (SerialSettings settings in SerialSettings.AutoDetectSequence)
        {
            Assert.Contains(settings.BaudRate, SerialSettings.SupportedBaudRates);
            Assert.Contains(settings.DataBits, SerialSettings.SupportedDataBits);
            Assert.Equal(StopBits.One, settings.StopBits);
        }
    }
}
