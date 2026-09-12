using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// How tall the §10.12 dialog may be (#506) — the decision, not the rendering.
/// </summary>
/// <remarks>
/// The figures come from the machine the defect was found on: a 1920×1080 laptop at 125 % scale, so
/// a 1536×864 logical desktop, an 814 px window, and a dialog that reached WinUI's 758 px cap with
/// 20 px of idle slack and a 5 px overflow once the progress area appeared.
/// </remarks>
public class DialogHeightTests
{
    [Fact]
    public void AWindowWithRoomToSpareRaisesTheCap()
    {
        Assert.Equal(814 - DialogHeight.Clearance, DialogHeight.MaxFor(814));
    }

    /// <summary>
    /// The 5 px that were being scrolled on the machine this was measured on, with room over.
    /// </summary>
    [Fact]
    public void TheMeasuredOverflowFitsAfterTheRaise()
    {
        const double dialogWhileWalking = 758 + 5;

        Assert.True(DialogHeight.MaxFor(814) >= dialogWhileWalking);
    }

    /// <summary>
    /// It raises and never lowers. A window too small to help must keep WinUI's own behaviour, and
    /// #26's ScrollViewer with it — shrinking the cap here would clip exactly what that recovered.
    /// </summary>
    [Theory]
    [InlineData(0)]          // XamlRoot not known yet — no opinion, not an error
    [InlineData(-1)]         // and nothing sensible is computable from a negative
    [InlineData(200)]        // a very small window
    [InlineData(758)]        // exactly the stock height, so clearance would eat into it
    [InlineData(789)]        // one pixel short of being worth raising
    public void ASmallWindowLeavesTheStockCapAlone(double available)
    {
        Assert.Equal(DialogHeight.Stock, DialogHeight.MaxFor(available));
    }

    [Fact]
    public void TheThresholdIsTheStockHeightPlusTheClearance()
    {
        Assert.Equal(DialogHeight.Stock, DialogHeight.MaxFor(DialogHeight.Stock + DialogHeight.Clearance));
        Assert.True(DialogHeight.MaxFor(DialogHeight.Stock + DialogHeight.Clearance + 1) > DialogHeight.Stock);
    }

    /// <summary>A maximised window on a tall screen gets the whole thing, less the clearance.</summary>
    [Fact]
    public void ATallScreenIsUsed()
    {
        Assert.Equal(1400 - DialogHeight.Clearance, DialogHeight.MaxFor(1400));
    }
}
