using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// What survives between launches, and what happens when the file does not.
/// </summary>
public sealed class WindowPlacementStoreTests : IDisposable
{
    private readonly string _folder = Path.Combine(
        Path.GetTempPath(),
        "WinZ3805A.Tests",
        Guid.NewGuid().ToString("N"));

    private string Path_ => Path.Combine(_folder, "window.json");

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    [Fact]
    public void EveryFieldSurvivesTheRoundTrip()
    {
        WindowPlacement saved = new()
        {
            Left = -1720,
            Top = 96,
            Width = 460,
            Height = 320,
            IsMaximized = true,
            IsCompact = true,
            IsAlwaysOnTop = true,
        };

        new LocalWindowPlacementStore(Path_).Save(saved);

        Assert.Equal(saved, new LocalWindowPlacementStore(Path_).Load());
    }

    /// <summary>
    /// §10.3 requires the compact state and always-on-top to persist, and they are independent.
    /// </summary>
    /// <remarks>
    /// A record written whole makes "one true, the other false" the case worth pinning: it is the
    /// shape a bug would take if either were ever saved on its own from a control's own state.
    /// </remarks>
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(false, false)]
    public void TheTwoModeFlagsAreIndependent(bool compact, bool onTop)
    {
        new LocalWindowPlacementStore(Path_).Save(new WindowPlacement
        {
            Left = 0,
            Top = 0,
            Width = 380,
            Height = 240,
            IsCompact = compact,
            IsAlwaysOnTop = onTop,
        });

        WindowPlacement? read = new LocalWindowPlacementStore(Path_).Load();

        Assert.NotNull(read);
        Assert.Equal(compact, read.IsCompact);
        Assert.Equal(onTop, read.IsAlwaysOnTop);
    }

    /// <summary>
    /// A placement written before #54 has no always-on-top field, and must read as "not pinned"
    /// rather than as a parse failure that discards the window's position with it.
    /// </summary>
    [Fact]
    public void APlacementFromBeforeAlwaysOnTopStillLoads()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(
            Path_,
            """{ "Left": 100, "Top": 200, "Width": 380, "Height": 240, "IsCompact": true }""");

        WindowPlacement? read = new LocalWindowPlacementStore(Path_).Load();

        Assert.NotNull(read);
        Assert.Equal(100, read.Left);
        Assert.True(read.IsCompact);
        Assert.False(read.IsAlwaysOnTop);
    }

    [Fact]
    public void AMissingFileHasNoPlacement() =>
        Assert.Null(new LocalWindowPlacementStore(Path_).Load());

    /// <remarks>
    /// The file is written as the window closes, so a machine that loses power mid-write leaves
    /// exactly this behind. Null puts the next launch back on the system's own placement, which is
    /// a far better outcome than a window sized from half a rectangle.
    /// </remarks>
    [Fact]
    public void ATruncatedFileHasNoPlacement()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, "{ \"Left\": 100, \"Top\":");

        Assert.Null(new LocalWindowPlacementStore(Path_).Load());
    }

    /// <remarks>
    /// Every member of the record is <c>required</c>, so a file missing one is refused by the
    /// deserialiser rather than silently completed with a zero — a placement is only meaningful
    /// whole.
    /// </remarks>
    [Fact]
    public void AFileMissingAFieldHasNoPlacement()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path_, "{ \"Left\": 100, \"Top\": 100, \"Width\": 420 }");

        Assert.Null(new LocalWindowPlacementStore(Path_).Load());
    }

    /// <remarks>
    /// Saving runs on every drag of the frame and once more on the way out. A profile that cannot
    /// be written to must cost the user nothing at all — least of all on the last line of code the
    /// application runs.
    /// </remarks>
    [Fact]
    public void AnUnwritableLocationIsNotAnError()
    {
        // A file where the store wants a folder: creating the directory fails, and so would the
        // write behind it.
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "occupied"), string.Empty);

        LocalWindowPlacementStore store = new(Path.Combine(_folder, "occupied", "window.json"));

        store.Save(new WindowPlacement { Left = 0, Top = 0, Width = 380, Height = 240 });
    }

    /// <remarks>
    /// Its own file, next to the connection preferences rather than inside them: a window move must
    /// not be able to rewrite — or lose — the port the user chose.
    /// </remarks>
    [Fact]
    public void ThePlacementLivesBesideTheConnectionPreferences()
    {
        Assert.Equal(
            Path.GetDirectoryName(LocalConnectionPreferenceStore.DefaultPath()),
            Path.GetDirectoryName(LocalWindowPlacementStore.DefaultPath()));

        Assert.NotEqual(LocalConnectionPreferenceStore.DefaultPath(), LocalWindowPlacementStore.DefaultPath());
    }
}
