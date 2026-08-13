using System.Text.Json;

namespace WinZ3805A.Services;

/// <summary>
/// Keeps the §10.12 preferences in a small JSON file in the user's local application data.
/// </summary>
/// <remarks>
/// <para>
/// <b>Not <c>ApplicationData.Current.LocalSettings</c>, deliberately.</b> That is the obvious store
/// for a packaged app and it was the first implementation, but reading
/// <c>Windows.Storage.ApplicationData.Current</c> in this app terminates the process: exception
/// code <c>0xc000027b</c> raised inside <c>Microsoft.UI.Xaml.dll</c>, with no managed exception to
/// catch — <c>Application.UnhandledException</c>, <c>AppDomain.UnhandledException</c> and a
/// <c>catch (Exception)</c> wrapped directly around the property all fail to see it. The window had
/// already been shown, so this is not an identity problem, and <c>Package.Current.DisplayName</c>
/// works from the same process. A store that can kill the application is not a store, and one whose
/// failure cannot be caught cannot even be made best-effort.
/// </para>
/// <para>
/// A file under <see cref="Environment.SpecialFolder.LocalApplicationData"/> costs nothing by
/// comparison. MSIX redirects writes there into the package's own writable location, so it is
/// per-package and removed on uninstall exactly as <c>LocalSettings</c> would have been, and it
/// works unpackaged as well. §6.3's choice of <c>LocalFolder</c> for the rolling log will meet the
/// same wall when logging is implemented, and should take the same route.
/// </para>
/// <para>
/// Every access is guarded and every failure is silent. Losing a remembered port is a smaller harm
/// than refusing to connect, and there is no state here worth an error dialog.
/// </para>
/// </remarks>
public sealed class LocalConnectionPreferenceStore : IConnectionPreferenceStore
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Creates a store over the default location.</summary>
    public LocalConnectionPreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalConnectionPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinZ3805A",
        "connection.json");

    /// <inheritdoc />
    public ConnectionPreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return ConnectionPreferences.Default;
            }

            return JsonSerializer.Deserialize<ConnectionPreferences>(File.ReadAllText(_path))
                ?? ConnectionPreferences.Default;
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // A truncated or hand-edited file is not worth a message. The defaults are the Z3805A's
            // factory settings, which is where a user with no stored preferences should start.
            return ConnectionPreferences.Default;
        }
    }

    /// <inheritdoc />
    public void Save(ConnectionPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(preferences);

        try
        {
            string? folder = Path.GetDirectoryName(_path);
            if (folder is not null)
            {
                Directory.CreateDirectory(folder);
            }

            File.WriteAllText(_path, JsonSerializer.Serialize(preferences, Format));
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            // Read-only profile, roaming folder offline, disk full. None of them should cost the
            // user the connection they just made.
        }
    }

    private static bool IsStorageFault(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException or
        JsonException;
}
