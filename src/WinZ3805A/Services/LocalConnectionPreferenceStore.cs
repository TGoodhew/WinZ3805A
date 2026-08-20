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
/// works unpackaged as well. This paragraph is why <see cref="JsonPreferenceFile{T}"/> exists and
/// why every store in this folder goes through it.
/// </para>
/// <para>
/// Every access is guarded and every failure is silent. Losing a remembered port is a smaller harm
/// than refusing to connect, and there is no state here worth an error dialog. A truncated or
/// hand-edited file falls back to the defaults, which are the Z3805A's factory settings — where a
/// user with no stored preferences should start anyway.
/// </para>
/// </remarks>
public sealed class LocalConnectionPreferenceStore : IConnectionPreferenceStore
{
    private readonly JsonPreferenceFile<ConnectionPreferences> _file;

    /// <summary>Creates a store over the default location.</summary>
    public LocalConnectionPreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalConnectionPreferenceStore(string path) =>
        _file = new JsonPreferenceFile<ConnectionPreferences>(path, ConnectionPreferences.Default);

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() => JsonPreferenceFile<ConnectionPreferences>.PathFor("connection.json");

    /// <inheritdoc />
    public ConnectionPreferences Load() => _file.Load();

    /// <inheritdoc />
    public void Save(ConnectionPreferences preferences) => _file.Save(preferences);
}
