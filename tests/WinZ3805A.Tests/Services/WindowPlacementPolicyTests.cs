using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// Where the §10.3 main window opens, given where it was left and which displays are attached now.
/// </summary>
/// <remarks>
/// The interesting cases are all display changes between launches, and a display change is the one
/// thing a bench with one screen cannot produce on demand. Coordinates below follow the desktop
/// convention <c>AppWindow</c> uses: the primary display starts at the origin, and a display placed
/// to its left is at negative x.
/// </remarks>
public sealed class WindowPlacementPolicyTests
{
    private const int MinimumWidth = 380;
    private const int MinimumHeight = 240;

    private static readonly WindowRect Primary = new(0, 0, 1920, 1040);
    private static readonly WindowRect Secondary = new(1920, 0, 1920, 1040);
    private static readonly WindowRect ToTheLeft = new(-1920, 0, 1920, 1040);

    private static WindowPlacement Saved(int left, int top, int width, int height) =>
        new() { Left = left, Top = top, Width = width, Height = height };

    private static WindowPlacement? Restore(WindowPlacement? saved, params WindowRect[] displays) =>
        WindowPlacementPolicy.Restore(saved, displays, MinimumWidth, MinimumHeight);

    [Fact]
    public void AFirstRunHasNothingToRestore() =>
        Assert.Null(Restore(null, Primary));

    /// <remarks>
    /// Not merely defensive: <c>DisplayArea.FindAll</c> returns nothing during a remote-session
    /// handover, and a window placed against no displays at all would go to the origin of a
    /// coordinate space that does not exist yet.
    /// </remarks>
    [Fact]
    public void NoDisplaysMeansTheSystemPlacesIt() =>
        Assert.Null(Restore(Saved(100, 100, 420, 300)));

    [Fact]
    public void APlacementWhollyOnADisplayIsRestoredExactly()
    {
        WindowPlacement saved = Saved(300, 200, 420, 300) with { IsMaximized = true, IsCompact = true };

        Assert.Equal(saved, Restore(saved, Primary));
    }

    [Fact]
    public void ASecondaryDisplayAtNegativeCoordinatesIsNotOffScreen()
    {
        WindowPlacement saved = Saved(-1500, 120, 420, 300);

        Assert.Equal(saved, Restore(saved, Primary, ToTheLeft));
    }

    /// <remarks>
    /// The case this whole class exists for. §1 expects the window to be left on a second monitor
    /// for weeks, so the monitor it was left on is the one most likely to be missing next launch —
    /// and restoring the rectangle verbatim then opens it where the user cannot see or reach it.
    /// </remarks>
    [Fact]
    public void APlacementOnADisplayThatIsGoneFallsBackToTheSystem() =>
        Assert.Null(Restore(Saved(2400, 200, 420, 300), Primary));

    /// <remarks>
    /// A window spanning two adjacent displays is fully visible and almost certainly deliberate.
    /// Pulling it onto one of them would rearrange a working desktop, which is why the visibility
    /// test sums coverage across displays rather than asking whether one display contains it.
    /// </remarks>
    [Fact]
    public void APlacementSpanningTwoDisplaysIsLeftAlone()
    {
        WindowPlacement saved = Saved(1700, 300, 440, 300);

        Assert.Equal(saved, Restore(saved, Primary, Secondary));
    }

    [Fact]
    public void APlacementHangingOffTheEdgeIsPulledBackOn()
    {
        WindowPlacement? restored = Restore(Saved(1700, 900, 420, 300), Primary);

        Assert.Equal(new WindowRect(1500, 740, 420, 300), restored?.Bounds);
    }

    /// <remarks>
    /// Lowering the resolution leaves the saved rectangle overlapping the display but larger than
    /// it. The window has to shrink as well as move, or it comes back with its buttons off-screen.
    /// </remarks>
    [Fact]
    public void AWindowLargerThanTheDisplayIsShrunkToFit()
    {
        WindowRect small = new(0, 0, 800, 600);

        Assert.Equal(new WindowRect(50, 0, 700, 600), Restore(Saved(50, 40, 700, 900), small)?.Bounds);
    }

    /// <remarks>
    /// A display too small for the §10.3 floor is not a reason to break the floor. The window
    /// overhangs instead, exactly as it would if it were opened there for the first time.
    /// </remarks>
    [Fact]
    public void TheMinimumSizeWinsOverATinyDisplay()
    {
        WindowRect tiny = new(0, 0, 320, 200);

        Assert.Equal(new WindowRect(0, 0, MinimumWidth, MinimumHeight), Restore(Saved(0, 0, 320, 200), tiny)?.Bounds);
    }

    /// <remarks>
    /// Restored rather than discarded: a stored size under the floor means the floor changed, or
    /// the file was hand-edited, and the position is still worth honouring.
    /// </remarks>
    [Fact]
    public void ASizeBelowTheFloorIsRaisedToIt()
    {
        WindowPlacement? restored = Restore(Saved(200, 150, 100, 80), Primary);

        Assert.Equal(new WindowRect(200, 150, MinimumWidth, MinimumHeight), restored?.Bounds);
    }

    /// <summary>
    /// A window with only a corner showing is as lost as one placed entirely off-screen.
    /// </summary>
    [Theory]
    [InlineData(1900, 200)]  // 20 px of width left on the display
    [InlineData(300, 1030)]  // 10 px of height left on the display
    public void APlacementTooFarOffToGrabIsDiscarded(int left, int top) =>
        Assert.Null(Restore(Saved(left, top, 420, 300), Primary));

    /// <remarks>
    /// The compact layout is 380 x 120 (§10.3), so restoring it against the standard 240 floor
    /// would double its height every launch until it stopped being compact at all.
    /// </remarks>
    [Fact]
    public void TheCompactFloorIsThePassedOne()
    {
        WindowPlacement saved = Saved(100, 100, 380, 120) with { IsCompact = true };

        WindowPlacement? restored = WindowPlacementPolicy.Restore(saved, [Primary], MinimumWidth, 120);

        Assert.Equal(new WindowRect(100, 100, 380, 120), restored?.Bounds);
        Assert.True(restored?.IsCompact);
    }

    /// <remarks>
    /// Whatever the rectangle does, the two flags come back untouched — the clamp moves windows,
    /// it does not decide which layout the user was in.
    /// </remarks>
    [Fact]
    public void ClampingPreservesTheFlags()
    {
        WindowPlacement saved = Saved(1800, 950, 420, 300) with { IsMaximized = true, IsCompact = true };

        WindowPlacement? restored = Restore(saved, Primary);

        Assert.NotEqual(saved.Bounds, restored?.Bounds);
        Assert.True(restored?.IsMaximized);
        Assert.True(restored?.IsCompact);
    }
}
