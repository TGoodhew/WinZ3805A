using System.IO.Ports;
using System.Runtime.Versioning;
using Microsoft.Win32;

namespace WinZ3805A.Services;

/// <summary>
/// Finds the serial ports the §10.12 picker offers, with friendly names where Windows has one.
/// </summary>
/// <remarks>
/// <para>
/// <b>Three sources, in order of authority.</b> <see cref="SerialPort.GetPortNames"/> says which
/// ports exist. <c>HKLM\HARDWARE\DEVICEMAP\SERIALCOMM</c> is read as well, because a port present in
/// the device map but missing from the framework's list is still openable and a picker that omits it
/// is simply wrong. Neither of those carries a name a person would recognise, so the friendly names
/// come from the device tree under <c>HKLM\SYSTEM\CurrentControlSet\Enum</c>.
/// </para>
/// <para>
/// <b>Registry rather than WMI.</b> §6.3 allows either, with WMI as the best-effort path. The
/// registry supplies the same <c>FriendlyName</c> that <c>Win32_PnPEntity</c> would report, without
/// a <c>System.Management</c> dependency and without WMI's first-query cost, which on a cold service
/// is seconds rather than milliseconds — the exact thing §6.3 says must never block the UI. If a
/// field report ever shows a device whose name only WMI knows, add it as a second enrichment pass;
/// the merge in <see cref="SerialPortInfo.Merge"/> already takes descriptions from any source.
/// </para>
/// <para>
/// <b>Nothing here throws.</b> Parts of the device tree are ACL'd away from a standard user, and one
/// unreadable key must cost that device its description, not the user their port list.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SerialPortEnumerator : ISerialPortSource
{
    private const string DeviceMapKey = @"HARDWARE\DEVICEMAP\SERIALCOMM";
    private const string DeviceTreeKey = @"SYSTEM\CurrentControlSet\Enum";

    /// <summary>Lists the ports, off the UI thread.</summary>
    /// <remarks>
    /// The device-tree walk is bounded — three levels, a few thousand keys — but it is still a
    /// synchronous registry crawl, and §6.3 does not allow the UI thread to pay for it. The caller
    /// awaits; the dialog stays responsive while it does.
    /// </remarks>
    public Task<IReadOnlyList<SerialPortInfo>> ListAsync(CancellationToken cancellationToken = default) =>
        Task.Run(() => Enumerate(cancellationToken), cancellationToken);

    /// <summary>Lists the ports on the calling thread.</summary>
    public IReadOnlyList<SerialPortInfo> Enumerate(CancellationToken cancellationToken = default)
    {
        List<string> names = [.. SafePortNames()];
        names.AddRange(DeviceMapPortNames());

        return SerialPortInfo.Merge(names, FriendlyNames(cancellationToken));
    }

    /// <summary>The copy for an empty list (§9.11).</summary>
    public string EmptyMessage => SerialPortInfo.NoPortsMessage();

    private static IEnumerable<string> SafePortNames()
    {
        try
        {
            return SerialPort.GetPortNames();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    /// <summary>
    /// Reads <c>HKLM\HARDWARE\DEVICEMAP\SERIALCOMM</c>, whose values are the port names.
    /// </summary>
    /// <remarks>
    /// The value <em>name</em> is the device-stack path (<c>\Device\Silabser0</c>) and the value
    /// <em>data</em> is the port name. Only the data is wanted here: the stack path names a driver,
    /// not a device, and "COM3 — Silabser0" is a worse label than "COM3".
    /// </remarks>
    private static IEnumerable<string> DeviceMapPortNames()
    {
        using RegistryKey? map = OpenLocalMachine(DeviceMapKey);
        if (map is null)
        {
            yield break;
        }

        foreach (string valueName in SafeValueNames(map))
        {
            if (map.GetValue(valueName) is string portName && !string.IsNullOrWhiteSpace(portName))
            {
                yield return portName;
            }
        }
    }

    /// <summary>
    /// Walks the device tree for the friendly name of every enumerated COM port.
    /// </summary>
    /// <remarks>
    /// The tree is <c>Enum\{bus}\{device}\{instance}</c>, and an instance owns a serial port when it
    /// has a <c>Device Parameters\PortName</c> value. Keyed on that value rather than on the
    /// instance ID, so a device that moves between USB sockets still matches the port it now holds.
    /// </remarks>
    private static Dictionary<string, string> FriendlyNames(CancellationToken cancellationToken)
    {
        Dictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

        using RegistryKey? tree = OpenLocalMachine(DeviceTreeKey);
        if (tree is null)
        {
            return names;
        }

        foreach (string busName in SafeSubKeyNames(tree))
        {
            cancellationToken.ThrowIfCancellationRequested();

            using RegistryKey? bus = SafeOpenSubKey(tree, busName);
            if (bus is null)
            {
                continue;
            }

            foreach (string deviceName in SafeSubKeyNames(bus))
            {
                using RegistryKey? device = SafeOpenSubKey(bus, deviceName);
                if (device is null)
                {
                    continue;
                }

                foreach (string instanceName in SafeSubKeyNames(device))
                {
                    using RegistryKey? instance = SafeOpenSubKey(device, instanceName);
                    if (instance is not null)
                    {
                        Record(instance, names);
                    }
                }
            }
        }

        return names;
    }

    private static void Record(RegistryKey instance, Dictionary<string, string> names)
    {
        using RegistryKey? parameters = SafeOpenSubKey(instance, "Device Parameters");
        if (parameters?.GetValue("PortName") is not string portName || string.IsNullOrWhiteSpace(portName))
        {
            return;
        }

        string? friendly = instance.GetValue("FriendlyName") as string
            ?? instance.GetValue("DeviceDesc") as string;

        friendly = Tidy(friendly);
        if (friendly is not null)
        {
            names[portName.Trim()] = friendly;
        }
    }

    /// <summary>
    /// Turns a raw registry name into something worth putting after an em dash.
    /// </summary>
    /// <remarks>
    /// Two shapes need handling. <c>FriendlyName</c> usually already ends in the port —
    /// "USB Serial Port (COM3)" — and repeating it as "COM3 — USB Serial Port (COM3)" reads as a
    /// bug. <c>DeviceDesc</c>, the fallback, is often an indirect string of the form
    /// <c>@oem12.inf,%description%;USB Serial Port</c>, where only the tail is displayable.
    /// </remarks>
    private static string? Tidy(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        if (name.StartsWith('@'))
        {
            int tail = name.LastIndexOf(';');
            if (tail < 0 || tail == name.Length - 1)
            {
                return null;
            }

            name = name[(tail + 1)..];
        }

        int suffix = name.LastIndexOf(" (COM", StringComparison.OrdinalIgnoreCase);
        if (suffix > 0 && name.EndsWith(')'))
        {
            name = name[..suffix];
        }

        name = name.Trim();
        return name.Length == 0 ? null : name;
    }

    private static RegistryKey? OpenLocalMachine(string path)
    {
        try
        {
            return Registry.LocalMachine.OpenSubKey(path);
        }
        catch (Exception exception) when (IsRegistryFault(exception))
        {
            return null;
        }
    }

    private static RegistryKey? SafeOpenSubKey(RegistryKey parent, string name)
    {
        try
        {
            return parent.OpenSubKey(name);
        }
        catch (Exception exception) when (IsRegistryFault(exception))
        {
            return null;
        }
    }

    private static string[] SafeSubKeyNames(RegistryKey key)
    {
        try
        {
            return key.GetSubKeyNames();
        }
        catch (Exception exception) when (IsRegistryFault(exception))
        {
            return [];
        }
    }

    private static string[] SafeValueNames(RegistryKey key)
    {
        try
        {
            return key.GetValueNames();
        }
        catch (Exception exception) when (IsRegistryFault(exception))
        {
            return [];
        }
    }

    /// <summary>
    /// The failures a device-tree walk meets in the field: keys ACL'd away from a standard user, and
    /// keys deleted between being listed and being opened, which a hot-plugged adapter does.
    /// </summary>
    private static bool IsRegistryFault(Exception exception) => exception is
        System.Security.SecurityException or
        UnauthorizedAccessException or
        IOException or
        ObjectDisposedException;
}
