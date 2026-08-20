namespace WinZ3805A.Services;

/// <summary>Which form of the sky the §10.5 page was last showing.</summary>
/// <remarks>
/// A11Y-11's alternate is only an alternate if choosing it sticks. A user who cannot read the polar
/// plot would otherwise re-select the list on every navigation, every reconnect and every launch —
/// which is a fair description of an accessibility feature nobody uses.
/// </remarks>
public enum SkyView
{
    /// <summary>The polar plot (§9.10.2). The default, because it is what most users want.</summary>
    Plot = 0,

    /// <summary>The non-spatial list carrying the same satellites (A11Y-11).</summary>
    List,
}

/// <summary>What the §10.5 Satellites page remembers between sessions.</summary>
public sealed record SatellitesViewPreferences
{
    /// <summary>Plot or list.</summary>
    public SkyView SkyView { get; init; } = SkyView.Plot;

    /// <summary>A fresh install's preferences.</summary>
    public static SatellitesViewPreferences Default { get; } = new();
}

/// <summary>Where <see cref="SatellitesViewPreferences"/> are kept.</summary>
public interface ISatellitesViewPreferenceStore
{
    /// <summary>Reads the stored preferences, or <see cref="SatellitesViewPreferences.Default"/>.</summary>
    SatellitesViewPreferences Load();

    /// <summary>Writes the preferences.</summary>
    void Save(SatellitesViewPreferences preferences);
}

/// <summary>
/// Keeps <see cref="SatellitesViewPreferences"/> in a JSON file beside the others.
/// </summary>
/// <remarks>
/// The first store written on top of <see cref="JsonPreferenceFile{T}"/> rather than by hand, which
/// is what <see cref="DetailsViewPreferences"/>'s note asked for. What remains here is the part
/// that is actually about satellites: the file name, and the default.
/// </remarks>
public sealed class LocalSatellitesViewPreferenceStore : ISatellitesViewPreferenceStore
{
    private readonly JsonPreferenceFile<SatellitesViewPreferences> _file;

    /// <summary>Creates a store over the default location.</summary>
    public LocalSatellitesViewPreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalSatellitesViewPreferenceStore(string path) =>
        _file = new JsonPreferenceFile<SatellitesViewPreferences>(path, SatellitesViewPreferences.Default);

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() => JsonPreferenceFile<SatellitesViewPreferences>.PathFor("satellites-view.json");

    /// <inheritdoc />
    public SatellitesViewPreferences Load() => _file.Load();

    /// <inheritdoc />
    public void Save(SatellitesViewPreferences preferences) => _file.Save(preferences);
}
