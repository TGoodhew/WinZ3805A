using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// What the §9.7 navigation pane remembers between launches.
/// </summary>
public sealed class DetailsViewPreferenceStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "WinZ3805A.Tests",
        Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "details-view.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    /// <remarks>
    /// Open by default: §9.7.1 draws the pane expanded, and a first run that hid the navigation
    /// would leave a new user looking at one page with no sign there are eight.
    /// </remarks>
    [Fact]
    public void TheDefaultIsAnOpenPane() =>
        Assert.True(DetailsViewPreferences.Default.IsPaneOpen);

    [Fact]
    public void TheCollapsedPaneSurvivesTheRoundTrip()
    {
        DetailsViewPreferences saved = new() { IsPaneOpen = false };

        new LocalDetailsViewPreferenceStore(Path_).Save(saved);

        Assert.Equal(saved, new LocalDetailsViewPreferenceStore(Path_).Load());
    }

    [Fact]
    public void AMissingFileGivesTheDefault() =>
        Assert.Equal(DetailsViewPreferences.Default, new LocalDetailsViewPreferenceStore(Path_).Load());

    [Fact]
    public void AnUnreadableFileGivesTheDefault()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, "{ \"IsPaneOpen\":");

        Assert.Equal(DetailsViewPreferences.Default, new LocalDetailsViewPreferenceStore(Path_).Load());
    }

    /// <remarks>
    /// Saving runs on every pane toggle and once more as the window closes. A profile that cannot
    /// be written to must cost the user nothing.
    /// </remarks>
    [Fact]
    public void AnUnwritableLocationIsNotAnError()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "occupied"), string.Empty);

        new LocalDetailsViewPreferenceStore(Path.Combine(_folder, "occupied", "details-view.json"))
            .Save(new DetailsViewPreferences { IsPaneOpen = false });
    }

    /// <remarks>
    /// Three files under one folder, one per concern. A window move must not be able to rewrite the
    /// remembered port, and collapsing the pane must not be able to lose either.
    /// </remarks>
    [Fact]
    public void EachConcernHasItsOwnFile()
    {
        string[] paths =
        [
            LocalConnectionPreferenceStore.DefaultPath(),
            LocalWindowPlacementStore.DefaultPath(),
            LocalWindowPlacementStore.PathFor("details-window"),
            LocalDetailsViewPreferenceStore.DefaultPath(),
        ];

        Assert.Equal(paths.Length, paths.Distinct().Count());
        Assert.Single(paths.Select(Path.GetDirectoryName).Distinct());
    }
}
