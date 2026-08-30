using System.Globalization;

namespace WinZ3805A.Services;

/// <summary>
/// One serial port as the §10.12 picker shows it: the name Windows opens it by, and the
/// human-readable description of what is on the other end.
/// </summary>
/// <remarks>
/// The description is optional and always may be <see langword="null"/>. Windows reports the port
/// name from one place and the friendly name from another, and the second lookup is allowed to fail
/// — a picker that lists "COM3" is usable, a picker that lists nothing because a registry key was
/// unreadable is not.
/// </remarks>
public sealed record SerialPortInfo
{
    /// <summary>The name the port is opened by, e.g. <c>COM3</c>.</summary>
    public required string PortName { get; init; }

    /// <summary>What Windows calls the device, e.g. <c>USB Serial Port</c>, or <see langword="null"/>.</summary>
    public string? Description { get; init; }

    /// <summary>The §10.12 wireframe's label: <c>COM3 — USB Serial Port</c>, or just <c>COM3</c>.</summary>
    public string DisplayLabel => string.IsNullOrWhiteSpace(Description)
        ? PortName
        : $"{PortName} — {Description}";

    /// <summary>
    /// Merges the port names Windows reports with whatever descriptions could be found for them.
    /// </summary>
    /// <param name="portNames">Every port name, as <c>SerialPort.GetPortNames</c> returns them.</param>
    /// <param name="descriptions">Friendly names by port name; may be empty, and may name ports that no longer exist.</param>
    /// <remarks>
    /// The port names are authoritative and the descriptions are decoration: a description with no
    /// matching port is dropped rather than listed, because the picker must not offer a port that
    /// cannot be opened. Duplicates are collapsed — the two registry sources overlap by design.
    /// </remarks>
    public static IReadOnlyList<SerialPortInfo> Merge(
        IEnumerable<string> portNames,
        IReadOnlyDictionary<string, string>? descriptions = null)
    {
        ArgumentNullException.ThrowIfNull(portNames);

        return [.. portNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(name => new SerialPortInfo
            {
                PortName = name,
                Description = descriptions is not null
                    && descriptions.TryGetValue(name, out string? description)
                    && !string.IsNullOrWhiteSpace(description)
                        ? description.Trim()
                        : null,
            })
            .OrderBy(port => port, PortNameOrder.Instance)];
    }

    /// <summary>
    /// The copy shown when Windows reports no serial ports at all (§9.11).
    /// </summary>
    /// <remarks>
    /// One message, because there is one supported architecture. This took the machine's
    /// architecture and named a missing ARM64 driver as the likely cause on ARM64, which §6.1
    /// required until it was amended on 29 Aug 2026 to stop describing ARM64 as a target at all.
    /// The message that remains names the adapter and Device Manager, which is the right first
    /// step for a missing driver on any architecture — so nothing that mattered was in the branch.
    /// </remarks>
    public static string NoPortsMessage() =>
        "Windows reports no serial ports. Connect the receiver's serial adapter, then choose Refresh.";

    /// <summary>Orders ports the way a person reads them, so COM9 precedes COM10.</summary>
    /// <remarks>
    /// Ordinal sort puts COM10 second in the list and COM9 last, which in a lab with a multi-port
    /// card is not a cosmetic problem — it is the user scrolling past the port they wanted.
    /// </remarks>
    private sealed class PortNameOrder : IComparer<SerialPortInfo>
    {
        public static PortNameOrder Instance { get; } = new();

        public int Compare(SerialPortInfo? x, SerialPortInfo? y)
        {
            if (ReferenceEquals(x, y))
            {
                return 0;
            }

            if (x is null)
            {
                return -1;
            }

            if (y is null)
            {
                return 1;
            }

            (string leftPrefix, int leftNumber) = Split(x.PortName);
            (string rightPrefix, int rightNumber) = Split(y.PortName);

            int byPrefix = string.Compare(leftPrefix, rightPrefix, StringComparison.OrdinalIgnoreCase);
            return byPrefix != 0
                ? byPrefix
                : leftNumber != rightNumber
                    ? leftNumber.CompareTo(rightNumber)
                    : string.Compare(x.PortName, y.PortName, StringComparison.OrdinalIgnoreCase);
        }

        private static (string Prefix, int Number) Split(string portName)
        {
            int digits = portName.Length;
            while (digits > 0 && char.IsAsciiDigit(portName[digits - 1]))
            {
                digits--;
            }

            return digits == portName.Length
                ? (portName, -1)
                : (portName[..digits],
                   int.Parse(portName[digits..], CultureInfo.InvariantCulture));
        }
    }
}

/// <summary>
/// Where the §10.12 picker gets its ports from.
/// </summary>
/// <remarks>
/// An interface because the only real implementation reads the Windows registry, which a headless
/// test run cannot usefully do — while the rules about what the picker does with the answer, an
/// empty list included, are exactly what wants testing.
/// </remarks>
public interface ISerialPortSource
{
    /// <summary>Lists the ports currently present.</summary>
    Task<IReadOnlyList<SerialPortInfo>> ListAsync(CancellationToken cancellationToken = default);

    /// <summary>The copy to show when the list comes back empty.</summary>
    string EmptyMessage { get; }
}
