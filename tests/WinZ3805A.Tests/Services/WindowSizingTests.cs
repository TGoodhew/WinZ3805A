using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// Turning a §9.6.1 content width into a window size that actually delivers it.
/// </summary>
/// <remarks>
/// Every case here is unreachable by running the application on this machine, which has one display
/// at 100% scaling. The 150% and 350% rows are the ones that matter — A11Y-7 requires the app to be
/// usable at 350%, and that is where reading §9.6.2's figure as a physical window size fails worst.
/// </remarks>
public sealed class WindowSizingTests
{
    private const int DetailsWidth = 1024;
    private const int DetailsHeight = 720;

    /// <remarks>
    /// The measured case: a 1024 px window has a 1008 px client area, so §9.6.2's literal figure
    /// lands 16 px below the Expanded threshold and opens in the icon rail it was raised to avoid.
    /// </remarks>
    [Fact]
    public void AtOneHundredPercentTheChromeIsStillAdded()
    {
        (int width, int height) = WindowSizing.PhysicalMinimum(DetailsWidth, DetailsHeight, 1.0, 16, 8);

        Assert.Equal(1040, width);
        Assert.Equal(728, height);
    }

    /// <summary>Scaling multiplies the content, and the chrome is already physical.</summary>
    [Theory]
    [InlineData(1.25, 1280)]
    [InlineData(1.5, 1536)]
    [InlineData(2.0, 2048)]
    [InlineData(3.5, 3584)]
    public void ScalingIsAppliedToTheContentOnly(double scale, int expectedContent)
    {
        (int width, _) = WindowSizing.PhysicalMinimum(DetailsWidth, DetailsHeight, scale, 16, 8);

        Assert.Equal(expectedContent + 16, width);
    }

    /// <remarks>
    /// Rounded up. Landing half a pixel below a breakpoint puts the entire layout in the wrong mode,
    /// and being one pixel over costs nothing at all.
    /// </remarks>
    [Fact]
    public void AFractionalResultIsRoundedUp()
    {
        (int width, _) = WindowSizing.PhysicalMinimum(1025, 720, 1.5, 0, 0);

        Assert.Equal(1538, width); // 1537.5
    }

    /// <remarks>
    /// The scale comes from a <c>XamlRoot</c> that may not exist yet, so zero is reachable. A window
    /// of no width is not a failure anybody would diagnose from the symptom.
    /// </remarks>
    [Theory]
    [InlineData(0)]
    [InlineData(-1.5)]
    [InlineData(double.NaN)]
    [InlineData(1000)]
    public void AnImpossibleScaleFallsBackToOne(double scale)
    {
        (int width, int height) = WindowSizing.PhysicalMinimum(DetailsWidth, DetailsHeight, scale, 0, 0);

        Assert.Equal(DetailsWidth, width);
        Assert.Equal(DetailsHeight, height);
    }

    [Fact]
    public void NegativeChromeIsIgnored()
    {
        (int width, int height) = WindowSizing.PhysicalMinimum(DetailsWidth, DetailsHeight, 1.0, -40, -40);

        Assert.Equal(DetailsWidth, width);
        Assert.Equal(DetailsHeight, height);
    }

    /// <summary>A floor that fits stays exactly as it was computed.</summary>
    [Fact]
    public void AFloorInsideTheWorkAreaIsUntouched()
    {
        (int width, int height) = WindowSizing.ClampToWorkArea(1040, 728, Screen(3840, 2120));

        Assert.Equal(1040, width);
        Assert.Equal(728, height);
    }

    /// <remarks>
    /// The defect this was written for. At 350% §9.6.2's content size needs 3600 px of window, and
    /// enforcing that on a 1920-wide display opens the Details window wider than the screen with no
    /// gesture that brings it back — the resize edges are past the right edge of the desktop.
    /// </remarks>
    [Theory]
    [InlineData(2.0, 2048 + 16, 1920)]
    [InlineData(3.5, 3584 + 16, 1920)]
    public void AFloorWiderThanTheDisplayIsCappedAtIt(double scale, int unclamped, int expected)
    {
        (int width, _) = WindowSizing.PhysicalMinimum(DetailsWidth, DetailsHeight, scale, 16, 8);
        Assert.Equal(unclamped, width);

        (int clamped, _) = WindowSizing.ClampToWorkArea(width, 720, Screen(1920, 1032));

        Assert.Equal(expected, clamped);
    }

    /// <summary>Each axis is capped on its own — the taskbar takes height, not width.</summary>
    [Fact]
    public void HeightIsCappedIndependentlyOfWidth()
    {
        (int width, int height) = WindowSizing.ClampToWorkArea(1600, 1200, Screen(1920, 1032));

        Assert.Equal(1600, width);
        Assert.Equal(1032, height);
    }

    /// <remarks>
    /// The work area is at the display's own origin, which is negative for a display to the left of
    /// the primary. Only the extent may cap the floor; a rectangle at (-1920, 0) constrains a window
    /// exactly as much as the same rectangle at the origin.
    /// </remarks>
    [Fact]
    public void OnlyTheExtentOfTheWorkAreaMatters()
    {
        WindowRect left = new(-1920, -120, 1920, 1032);

        (int width, int height) = WindowSizing.ClampToWorkArea(3600, 2528, left);

        Assert.Equal(1920, width);
        Assert.Equal(1032, height);
    }

    /// <remarks>
    /// <c>DisplayArea.GetFromWindowId</c> is documented to answer for any window, but it is a
    /// nullable projection and the window may not be shown yet. Returning the computed floor
    /// unclamped is the honest answer: it is the floor that was asked for, and the next move of the
    /// window recomputes it against a display that does exist.
    /// </remarks>
    [Fact]
    public void AnUnknownDisplayLeavesTheFloorAlone()
    {
        (int width, int height) = WindowSizing.ClampToWorkArea(3600, 2528, null);

        Assert.Equal(3600, width);
        Assert.Equal(2528, height);
    }

    /// <summary>A work area of no extent is not a constraint, it is a display that is not there.</summary>
    [Theory]
    [InlineData(0, 1032)]
    [InlineData(1920, 0)]
    [InlineData(-1, -1)]
    public void AnEmptyWorkAreaLeavesTheFloorAlone(int areaWidth, int areaHeight)
    {
        (int width, int height) = WindowSizing.ClampToWorkArea(3600, 2528, Screen(areaWidth, areaHeight));

        Assert.Equal(3600, width);
        Assert.Equal(2528, height);
    }

    // -------------------------------------------------------------------------------------
    // Leaving compact mode (#307)
    // -------------------------------------------------------------------------------------

    /// <summary>The size the user had in the standard layout is the size they get back.</summary>
    [Fact]
    public void LeavingCompactRestoresTheRememberedStandardSize()
    {
        (int width, int height) = WindowSizing.SizeLeavingCompact(900, 700, 396, 272, Screen(1920, 1032));

        Assert.Equal(900, width);
        Assert.Equal(700, height);
    }

    /// <remarks>
    /// A launch straight into compact from a stored compact state may have no standard size on
    /// record. The floor is the honest answer; a "nice" size invented here would be one the user
    /// never chose.
    /// </remarks>
    [Fact]
    public void WithNothingRememberedLeavingCompactLandsOnTheFloor()
    {
        (int width, int height) = WindowSizing.SizeLeavingCompact(null, null, 396, 272, Screen(1920, 1032));

        Assert.Equal(396, width);
        Assert.Equal(272, height);
    }

    /// <summary>A size remembered before the display scaling changed can be under today's floor.</summary>
    [Fact]
    public void ARememberedSizeUnderTheFloorIsRaisedToIt()
    {
        (int width, int height) = WindowSizing.SizeLeavingCompact(380, 240, 594, 408, Screen(1920, 1032));

        Assert.Equal(594, width);
        Assert.Equal(408, height);
    }

    /// <summary>Each axis is decided on its own.</summary>
    [Fact]
    public void AHalfRememberedSizeFillsTheMissingAxisFromTheFloor()
    {
        (int width, int height) = WindowSizing.SizeLeavingCompact(900, null, 396, 272, Screen(1920, 1032));

        Assert.Equal(900, width);
        Assert.Equal(272, height);
    }

    /// <summary>A size remembered on a larger display is capped at the one the window is on now.</summary>
    [Fact]
    public void ARememberedSizeLargerThanTheDisplayIsCappedAtIt()
    {
        (int width, int height) = WindowSizing.SizeLeavingCompact(2600, 1500, 396, 272, Screen(1920, 1032));

        Assert.Equal(1920, width);
        Assert.Equal(1032, height);
    }

    /// <remarks>
    /// The two functions compose in one direction only. Clamping first and scaling afterwards would
    /// multiply the display's own size by the scaling factor, which is how a floor ends up larger
    /// than the screen by exactly the amount the clamp was supposed to remove.
    /// </remarks>
    [Fact]
    public void TheClampIsTheLastStep()
    {
        (int width, int height) = WindowSizing.PhysicalMinimum(DetailsWidth, DetailsHeight, 3.5, 16, 8);
        (width, height) = WindowSizing.ClampToWorkArea(width, height, Screen(1920, 1032));

        Assert.Equal(1920, width);
        Assert.Equal(1032, height);

        // What the reversed order gives, for the record: 1920 x 1032 of display, multiplied.
        (int reversedWidth, _) = WindowSizing.PhysicalMinimum(1920, 1032, 3.5, 16, 8);
        Assert.True(reversedWidth > 1920);
    }

    /// <remarks>
    /// §10.3's compact layout, at the scaling A11Y-7 requires, on the smallest display Windows 11
    /// supports. 380 x 120 of content is 1330 x 420 of window there — the height fits, the width
    /// does not, and the main window has to remain draggable either way.
    /// </remarks>
    [Fact]
    public void TheCompactMainWindowIsAlsoCappedAtTheDisplay()
    {
        (int width, int height) = WindowSizing.PhysicalMinimum(380, 120, 3.5, 16, 8);
        (width, height) = WindowSizing.ClampToWorkArea(width, height, Screen(1024, 720));

        Assert.Equal(1024, width);
        Assert.Equal(428, height);
    }

    private static WindowRect Screen(int width, int height) => new(0, 0, width, height);
    // -------------------------------------------------------------------------------------
    // CompactMinimumHeight (#215)
    // -------------------------------------------------------------------------------------

    /// <summary>§9.6.2's own number comes back unchanged at 100 % text.</summary>
    /// <remarks>
    /// The decomposition is only defensible if it reproduces the specification exactly at the scale
    /// the specification was written for. 96 fixed + 48 scaling is not an approximation of 144, it
    /// is 144.
    /// </remarks>
    [Fact]
    public void TheCompactFloorIsExactlySpecifiedAtNormalTextScale() =>
        Assert.Equal(144, WindowSizing.CompactMinimumHeight(1.0));

    /// <summary>
    /// The floor grows with text, but only the part of it that holds text.
    /// </summary>
    /// <remarks>
    /// <b>This is what #215 was.</b> A fixed 144 cannot hold constant content across text scales,
    /// and at 200 % the satellite count — which §9.6.2 requires, and which has since moved into the
    /// medallion's centre (#279) — was the part pushed out of it. Scaling the whole floor would be
    /// wrong in the other direction: §9.6.2 decomposes 144 as 32 + 24 + 64 + 24, and the 32 px
    /// title bar and 64 px medallion are fixed by construction; only the two 24 px margins grow.
    /// </remarks>
    [Theory]
    [InlineData(1.25, 156)]
    [InlineData(1.5, 168)]
    [InlineData(2.0, 192)]
    [InlineData(2.25, 204)]
    public void TheCompactFloorGrowsOnlyByItsTextPortion(double textScale, int expected) =>
        Assert.Equal(expected, WindowSizing.CompactMinimumHeight(textScale));

    /// <summary>A scale below 1 does not lower §9.6.2's floor.</summary>
    /// <remarks>
    /// Windows offers no text smaller than 100 %, so this is guarding against a bad reading rather
    /// than a real setting — but a floor that can be argued downward is not a floor, and
    /// <c>UISettings.TextScaleFactor</c> is a value from outside this process.
    /// </remarks>
    [Theory]
    [InlineData(0.5)]
    [InlineData(0.0)]
    [InlineData(double.NaN)]
    public void TheCompactFloorIsNeverLoweredBelowTheSpecification(double textScale) =>
        Assert.Equal(144, WindowSizing.CompactMinimumHeight(textScale));

}
