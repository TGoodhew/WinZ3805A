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
}
