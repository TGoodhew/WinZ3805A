using System.IO.Ports;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Services;

/// <summary>
/// What the §10.12 dialog remembers between launches: the port, how to open it, and the two
/// checkboxes at the bottom of the wireframe.
/// </summary>
/// <remarks>
/// Stored as scalars rather than as a serialised <see cref="SerialSettings"/> so the values survive
/// a change to that record's shape. The defaults are the Z3805A's factory configuration and the
/// wireframe's own tick marks — a fresh install offers 9600-8-N-1, auto-detect, and both boxes set.
/// </remarks>
public sealed record ConnectionPreferences
{
    /// <summary>The port last connected to, or <see langword="null"/> if there has never been one.</summary>
    public string? PortName { get; init; }

    /// <summary>Whether the dialog opens on Auto-detect rather than Manual.</summary>
    public bool AutoDetect { get; init; } = true;

    /// <summary>Baud rate for a manual connection.</summary>
    public int BaudRate { get; init; } = SerialSettings.Default.BaudRate;

    /// <summary>Data bits for a manual connection.</summary>
    public int DataBits { get; init; } = SerialSettings.Default.DataBits;

    /// <summary>Parity for a manual connection.</summary>
    public Parity Parity { get; init; } = SerialSettings.Default.Parity;

    /// <summary>Stop bits for a manual connection.</summary>
    public StopBits StopBits { get; init; } = SerialSettings.Default.StopBits;

    /// <summary>"Reconnect automatically" — drives <see cref="DeviceSessionService.StayConnected"/>.</summary>
    public bool ReconnectAutomatically { get; init; } = true;

    /// <summary>"Connect to this device on launch".</summary>
    public bool ConnectOnLaunch { get; init; } = true;

    /// <summary>A fresh install's preferences.</summary>
    public static ConnectionPreferences Default { get; } = new();

    /// <summary>The line parameters these preferences describe.</summary>
    public SerialSettings ToSettings() => new()
    {
        BaudRate = BaudRate,
        DataBits = DataBits,
        Parity = Parity,
        StopBits = StopBits,
    };

    /// <summary>Returns a copy carrying the given line parameters.</summary>
    public ConnectionPreferences WithSettings(SerialSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        return this with
        {
            BaudRate = settings.BaudRate,
            DataBits = settings.DataBits,
            Parity = settings.Parity,
            StopBits = settings.StopBits,
        };
    }
}

/// <summary>
/// Where <see cref="ConnectionPreferences"/> are kept.
/// </summary>
/// <remarks>
/// An interface so the rules about what is remembered, and when, can be tested against a recorder
/// rather than a disk. The real store, <see cref="LocalConnectionPreferenceStore"/>, is a JSON file
/// under local application data — not <c>ApplicationData.Current.LocalSettings</c>, for the reason
/// given there — and is itself linked into the test project.
/// </remarks>
public interface IConnectionPreferenceStore
{
    /// <summary>Reads the stored preferences, or <see cref="ConnectionPreferences.Default"/>.</summary>
    ConnectionPreferences Load();

    /// <summary>Writes the preferences.</summary>
    void Save(ConnectionPreferences preferences);
}
