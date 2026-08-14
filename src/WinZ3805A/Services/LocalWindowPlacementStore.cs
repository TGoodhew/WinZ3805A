using System.Text.Json;

namespace WinZ3805A.Services;

/// <summary>
/// Keeps the main window's §10.3 placement in a small JSON file beside the connection preferences.
/// </summary>
/// <remarks>
/// <para>
/// A file under <see cref="Environment.SpecialFolder.LocalApplicationData"/> for the reason set out
/// at length on <see cref="LocalConnectionPreferenceStore"/>: reading
/// <c>ApplicationData.Current</c> in this app terminates the process with no catchable exception.
/// Do not "improve" this to <c>LocalSettings</c>.
/// </para>
/// <para>
/// Its own file rather than another few fields in <c>connection.json</c>, because the two are
/// written on completely different occasions and a dropped window move must not be able to cost
/// the user their remembered port.
/// </para>
/// </remarks>
public sealed class LocalWindowPlacementStore : IWindowPlacementStore
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Creates a store over the default location.</summary>
    public LocalWindowPlacementStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalWindowPlacementStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Where the main window's placement lives.</summary>
    public static string DefaultPath() => PathFor("window");

    /// <summary>
    /// Where a named window's placement lives.
    /// </summary>
    /// <param name="window">
    /// Identifies the window, and becomes the file name. Each window keeps its own file: the
    /// §10.4 Details window is a different size on a different part of the desktop, and the two
    /// are moved and closed independently.
    /// </param>
    public static string PathFor(string window)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(window);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WinZ3805A",
            $"{window}.json");
    }

    /// <inheritdoc />
    public WindowPlacement? Load()
    {
        try
        {
            return File.Exists(_path)
                ? JsonSerializer.Deserialize<WindowPlacement>(File.ReadAllText(_path))
                : null;
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // A file written by a half-finished shutdown is missing a required member and lands
            // here. Null means "place it as if this were a first run", which is the right answer:
            // there is no half-placement worth honouring.
            return null;
        }
    }

    /// <inheritdoc />
    public void Save(WindowPlacement placement)
    {
        ArgumentNullException.ThrowIfNull(placement);

        try
        {
            string? folder = Path.GetDirectoryName(_path);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(placement, Format));
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // Silent, like the connection store. This runs on every drag of the frame and again as
            // the window closes; a dialog on the way out over a window position would be absurd.
        }
    }

    private static bool IsStorageFault(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException or
        JsonException;
}
