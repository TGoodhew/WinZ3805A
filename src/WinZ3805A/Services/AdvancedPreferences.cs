namespace WinZ3805A.Services;

/// <summary>The opt-in advanced features, off on a fresh install.</summary>
/// <remarks>
/// <para>
/// <b>Opting in changes what is reachable, never what is permitted.</b> The §10.11 console is a
/// picker over the same §8.1 allowlist every other page uses, so turning it on adds no command the
/// application could not already send — it adds a way to reach the ones nothing else surfaces yet.
/// The §8.4 exclusions are absent from the catalog, so they are absent from the console, opted in
/// or not.
/// </para>
/// <para>
/// Off by default because §10.11 says hidden by default, and because a picker listing every
/// catalogued command including the tier C ones is not what a user wanting a glanceable clock
/// should meet on their first run.
/// </para>
/// </remarks>
public sealed record AdvancedPreferences
{
    /// <summary>Whether the §10.11 Advanced Console appears in the navigation pane.</summary>
    public bool IsConsoleEnabled { get; init; }

    /// <summary>A fresh install's preferences.</summary>
    public static AdvancedPreferences Default { get; } = new();
}

/// <summary>Where <see cref="AdvancedPreferences"/> are kept.</summary>
public interface IAdvancedPreferenceStore
{
    /// <summary>Reads the stored preferences, or <see cref="AdvancedPreferences.Default"/>.</summary>
    AdvancedPreferences Load();

    /// <summary>Writes the preferences.</summary>
    void Save(AdvancedPreferences preferences);
}

/// <summary>Keeps <see cref="AdvancedPreferences"/> in a JSON file beside the others.</summary>
public sealed class LocalAdvancedPreferenceStore : IAdvancedPreferenceStore
{
    private readonly JsonPreferenceFile<AdvancedPreferences> _file;

    /// <summary>Creates a store over the default location.</summary>
    public LocalAdvancedPreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalAdvancedPreferenceStore(string path) =>
        _file = new JsonPreferenceFile<AdvancedPreferences>(path, AdvancedPreferences.Default);

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() => JsonPreferenceFile<AdvancedPreferences>.PathFor("advanced.json");

    /// <inheritdoc />
    public AdvancedPreferences Load() => _file.Load();

    /// <inheritdoc />
    public void Save(AdvancedPreferences preferences) => _file.Save(preferences);
}
