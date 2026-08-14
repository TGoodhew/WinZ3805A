using System.Text.Json;

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
/// Keeps <see cref="DetailsViewPreferences"/> in a JSON file beside the other two.
/// </summary>
/// <remarks>
/// The third store in this shape, and the last that should be written by hand: the next one wants
/// the file handling factored out. It is not factored out here because doing so would rewrite two
/// working, tested stores inside a change about a window, and neither of them is asking.
/// <b>Not <c>ApplicationData.Current</c></b> — see <see cref="LocalConnectionPreferenceStore"/>.
/// </remarks>
public sealed class LocalDetailsViewPreferenceStore : IDetailsViewPreferenceStore
{
    private static readonly JsonSerializerOptions Format = new() { WriteIndented = true };

    private readonly string _path;

    /// <summary>Creates a store over the default location.</summary>
    public LocalDetailsViewPreferenceStore()
        : this(DefaultPath())
    {
    }

    /// <summary>Creates a store over a given file, which the tests use.</summary>
    public LocalDetailsViewPreferenceStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        _path = path;
    }

    /// <summary>Where the preferences live.</summary>
    public static string DefaultPath() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinZ3805A",
        "details-view.json");

    /// <inheritdoc />
    public DetailsViewPreferences Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return DetailsViewPreferences.Default;
            }

            return JsonSerializer.Deserialize<DetailsViewPreferences>(File.ReadAllText(_path))
                ?? DetailsViewPreferences.Default;
        }
        catch (Exception exception) when (IsStorageFault(exception))
        {
            return DetailsViewPreferences.Default;
        }
    }

    /// <inheritdoc />
    public void Save(DetailsViewPreferences preferences)
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
            // Silent, like the other two. A pane the user has to collapse again is not worth a
            // dialog, and there is nothing here they could act on.
        }
    }

    private static bool IsStorageFault(Exception exception) => exception is
        IOException or
        UnauthorizedAccessException or
        System.Security.SecurityException or
        NotSupportedException or
        JsonException;
}
