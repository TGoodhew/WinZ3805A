namespace WinZ3805A.Services;

/// <summary>
/// What §9.4.2's accent opt-in remembers.
/// </summary>
/// <remarks>
/// Its own file rather than a field on <see cref="AdvancedPreferences"/>. Those settings change
/// what the application will <i>do</i> — which commands are reachable, whether it raises
/// notifications — and this one changes what it looks like. Reading a record to find out whether
/// the console is enabled should not require thinking about colour.
/// </remarks>
public sealed record AppearancePreferences
{
    /// <summary>
    /// Whether to draw the accent from Windows instead of the brand ramp.
    /// </summary>
    /// <remarks>
    /// Off by default, which is §9.4's position rather than an oversight: the brand accent is
    /// chosen for hue separation from the severity colours, and a default that abandoned that
    /// would make the guarantee depend on a control-panel setting nobody thinks of as safety
    /// critical.
    /// </remarks>
    public bool UseSystemAccent { get; init; }

    /// <summary>
    /// Whether the collision warning has already been shown and dismissed.
    /// </summary>
    /// <remarks>
    /// §9.4.2 makes the warning one-time. It is advice, not a permission gate — the user has
    /// already been told and has already chosen, and a tip that reappeared on every launch would
    /// be nagging about a decision that is theirs to make.
    /// </remarks>
    public bool HasAcknowledgedCollision { get; init; }

    /// <summary>
    /// The accent the warning was last shown for, as <c>#RRGGBB</c>, or null.
    /// </summary>
    /// <remarks>
    /// The acknowledgement is of a <i>particular</i> collision, not of the feature. If the user
    /// changes their Windows accent from a safe blue to a red one, the situation they agreed to is
    /// not the situation they are now in, and the warning is owed again. Storing the colour is what
    /// distinguishes "already told them this" from "never mention it again".
    /// </remarks>
    public string? AcknowledgedAccent { get; init; }

    /// <summary>A fresh install's preferences.</summary>
    public static AppearancePreferences Default { get; } = new();
}

/// <summary>Where <see cref="AppearancePreferences"/> are kept.</summary>
public interface IAppearancePreferenceStore
{
    /// <summary>Reads the stored preferences, or <see cref="AppearancePreferences.Default"/>.</summary>
    AppearancePreferences Load();

    /// <summary>Writes the preferences.</summary>
    void Save(AppearancePreferences preferences);
}

/// <summary>Keeps <see cref="AppearancePreferences"/> in a JSON file beside the others.</summary>
public sealed class LocalAppearancePreferenceStore : IAppearancePreferenceStore
{
    private readonly JsonPreferenceFile<AppearancePreferences> _file;

    /// <summary>Creates a store over the default location.</summary>
    public LocalAppearancePreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalAppearancePreferenceStore(string path) =>
        _file = new JsonPreferenceFile<AppearancePreferences>(path, AppearancePreferences.Default);

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() =>
        JsonPreferenceFile<AppearancePreferences>.PathFor("appearance.json");

    /// <inheritdoc />
    public AppearancePreferences Load() => _file.Load();

    /// <inheritdoc />
    public void Save(AppearancePreferences preferences) => _file.Save(preferences);
}
