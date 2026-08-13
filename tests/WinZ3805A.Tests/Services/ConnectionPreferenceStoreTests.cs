using System.IO.Ports;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// What the §10.12 dialog remembers between launches, and what it does when the file is not there
/// or not readable.
/// </summary>
/// <remarks>
/// Testable at all only because the store is a file rather than
/// <c>ApplicationData.Current.LocalSettings</c>. Reading that container terminated the process —
/// see the store's own remarks — so what is exercised here is the implementation that shipped, not
/// a stand-in for it.
/// </remarks>
public sealed class ConnectionPreferenceStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "WinZ3805A.Tests",
        Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "connection.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        ConnectionPreferences saved = new()
        {
            PortName = "COM7",
            AutoDetect = false,
            BaudRate = 19200,
            DataBits = 7,
            Parity = Parity.Odd,
            StopBits = StopBits.Two,
            ReconnectAutomatically = false,
            ConnectOnLaunch = false,
        };

        LocalConnectionPreferenceStore store = new(Path_);
        store.Save(saved);

        Assert.Equal(saved, new LocalConnectionPreferenceStore(Path_).Load());
    }

    /// <remarks>
    /// A first run has no file, and the defaults it falls back to are the Z3805A's factory
    /// configuration — the setting the reference unit actually ships on.
    /// </remarks>
    [Fact]
    public void AMissingFileGivesTheFactoryDefaults()
    {
        ConnectionPreferences loaded = new LocalConnectionPreferenceStore(Path_).Load();

        Assert.Equal(ConnectionPreferences.Default, loaded);
        Assert.Null(loaded.PortName);
        Assert.True(loaded.AutoDetect);
        Assert.Equal(SerialSettings.Default, loaded.ToSettings());
    }

    /// <remarks>
    /// The file is in a folder a user can open and edit. A truncated or hand-mangled one must cost
    /// them their remembered port, not their ability to start the application.
    /// </remarks>
    [Fact]
    public void ACorruptFileGivesTheDefaultsRatherThanThrowing()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, "{ this is not json");

        Assert.Equal(ConnectionPreferences.Default, new LocalConnectionPreferenceStore(Path_).Load());
    }

    [Fact]
    public void SavingCreatesTheFolderItNeeds()
    {
        new LocalConnectionPreferenceStore(Path_).Save(new ConnectionPreferences { PortName = "COM3" });

        Assert.True(File.Exists(Path_));
        Assert.Equal("COM3", new LocalConnectionPreferenceStore(Path_).Load().PortName);
    }

    [Fact]
    public void SavingTwiceKeepsOnlyTheSecond()
    {
        LocalConnectionPreferenceStore store = new(Path_);
        store.Save(new ConnectionPreferences { PortName = "COM3" });
        store.Save(new ConnectionPreferences { PortName = "COM4" });

        Assert.Equal("COM4", store.Load().PortName);
    }
}
