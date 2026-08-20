using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #60's remembered choice, and the file handling every preference store now shares.
/// </summary>
/// <remarks>
/// The persistence is not a nicety here. A11Y-11's alternate is for users who cannot read the polar
/// plot at all, and a toggle that resets on every navigation would put them back in front of it
/// several times a session.
/// </remarks>
public sealed class SatellitesViewPreferenceStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    private string Path_(string name) => Path.Combine(_folder, name);

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void TheDefaultIsThePlot() =>
        Assert.Equal(SkyView.Plot, SatellitesViewPreferences.Default.SkyView);

    [Fact]
    public void AMissingFileReadsAsTheDefault()
    {
        LocalSatellitesViewPreferenceStore store = new(Path_("nothing-here.json"));

        Assert.Equal(SkyView.Plot, store.Load().SkyView);
    }

    [Fact]
    public void TheChoiceSurvivesARoundTrip()
    {
        LocalSatellitesViewPreferenceStore store = new(Path_("satellites-view.json"));

        store.Save(new SatellitesViewPreferences { SkyView = SkyView.List });

        // A second store over the same path, because the point is that it survives the process.
        Assert.Equal(SkyView.List, new LocalSatellitesViewPreferenceStore(Path_("satellites-view.json")).Load().SkyView);
    }

    /// <summary>
    /// A hand-edited or truncated file falls back rather than throwing. The page reads this during
    /// navigation, so an exception here would be a page that cannot be opened.
    /// </summary>
    [Fact]
    public void ACorruptFileReadsAsTheDefault()
    {
        string path = Path_("corrupt.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ this is not json");

        Assert.Equal(SkyView.Plot, new LocalSatellitesViewPreferenceStore(path).Load().SkyView);
    }

    [Fact]
    public void AnUnwritablePathIsSilentRatherThanFatal()
    {
        // A directory where the file should be: every write fails, and none of them may escape.
        string path = Path_("blocked.json");
        Directory.CreateDirectory(path);

        LocalSatellitesViewPreferenceStore store = new(path);

        store.Save(new SatellitesViewPreferences { SkyView = SkyView.List });
        Assert.Equal(SkyView.Plot, store.Load().SkyView);
    }

    [Fact]
    public void TheDefaultPathSitsBesideTheOtherStores() =>
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinZ3805A",
                "satellites-view.json"),
            LocalSatellitesViewPreferenceStore.DefaultPath());

    // ------------------------------------------------------- the shared file, on its own terms

    private sealed record Sample
    {
        public string Value { get; init; } = "default";

        public static Sample Default { get; } = new();
    }

    [Fact]
    public void TheSharedFileRoundTripsAnyRecord()
    {
        JsonPreferenceFile<Sample> file = new(Path_("sample.json"), Sample.Default);

        file.Save(new Sample { Value = "written" });

        Assert.Equal("written", file.Load().Value);
    }

    [Fact]
    public void TheSharedFileCreatesItsFolder()
    {
        string path = Path.Combine(_folder, "one", "two", "sample.json");

        new JsonPreferenceFile<Sample>(path, Sample.Default).Save(new Sample { Value = "nested" });

        Assert.True(File.Exists(path));
    }

    /// <summary>
    /// The exception filter is a closed list on purpose: a bug in the store must still crash. This
    /// pins the boundary, because a bare catch here would turn one into preferences that silently
    /// stop being saved.
    /// </summary>
    [Fact]
    public void TheSharedFileDoesNotSwallowItsOwnBugs()
    {
        JsonPreferenceFile<Sample> file = new(Path_("sample.json"), Sample.Default);

        Assert.Throws<ArgumentNullException>(() => file.Save(null!));
    }

    [Fact]
    public void TheSharedFileRejectsAnEmptyPath() =>
        Assert.ThrowsAny<ArgumentException>(() => new JsonPreferenceFile<Sample>("  ", Sample.Default));

    [Fact]
    public void PathForComposesUnderTheApplicationFolder() =>
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinZ3805A",
                "anything.json"),
            JsonPreferenceFile<Sample>.PathFor("anything.json"));
}
