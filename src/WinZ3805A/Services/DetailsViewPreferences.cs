namespace WinZ3805A.Services;

/// <summary>
/// What the §10.4 Details window remembers about how it was being looked at.
/// </summary>
/// <remarks>
/// Separate from <see cref="WindowPlacement"/> on purpose. That record is where a window is; this
/// is what is showing inside it. Folding the two together would leave <c>WindowPlacement</c> with a
/// compact flag the Details window ignores beside a pane flag the main window ignores, and a record
/// half of whose fields are meaningless to half its users stops documenting anything.
/// </remarks>
public sealed record DetailsViewPreferences
{
    /// <summary>Whether the §9.7 navigation pane was left open.</summary>
    /// <remarks>
    /// Only meaningful at the Expanded breakpoint — below it <c>NavigationView</c> owns the pane
    /// state itself, and restoring a remembered "open" into a 640 px window would overlay the
    /// content the user was trying to read.
    /// </remarks>
    public bool IsPaneOpen { get; init; } = true;

    /// <summary>A fresh install's preferences.</summary>
    public static DetailsViewPreferences Default { get; } = new();
}

/// <summary>Where <see cref="DetailsViewPreferences"/> are kept.</summary>
public interface IDetailsViewPreferenceStore
{
    /// <summary>Reads the stored preferences, or <see cref="DetailsViewPreferences.Default"/>.</summary>
    DetailsViewPreferences Load();

    /// <summary>Writes the preferences.</summary>
    void Save(DetailsViewPreferences preferences);
}

/// <summary>
/// Keeps <see cref="DetailsViewPreferences"/> in a JSON file beside the others.
/// </summary>
/// <remarks>
/// This used to carry its own copy of the file handling, with a note saying the next store to be
/// written should factor it out. #60 was the next store, so the shared part now lives in
/// <see cref="JsonPreferenceFile{T}"/> and what is left here is the file name and the default.
/// </remarks>
public sealed class LocalDetailsViewPreferenceStore : IDetailsViewPreferenceStore
{
    private readonly JsonPreferenceFile<DetailsViewPreferences> _file;

    /// <summary>Creates a store over the default location.</summary>
    public LocalDetailsViewPreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalDetailsViewPreferenceStore(string path) =>
        _file = new JsonPreferenceFile<DetailsViewPreferences>(path, DetailsViewPreferences.Default);

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() => JsonPreferenceFile<DetailsViewPreferences>.PathFor("details-view.json");

    /// <inheritdoc />
    public DetailsViewPreferences Load() => _file.Load();

    /// <inheritdoc />
    public void Save(DetailsViewPreferences preferences) => _file.Save(preferences);
}
