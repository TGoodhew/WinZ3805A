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

    /// <summary>
    /// #725: in a window narrow enough that a centred dialog reaches the caption buttons, the dialog
    /// starts below the title bar - the Manage satellites dialog at 640 × 480 had them on its corner.
    /// </summary>
    [Theory]
    [InlineData(640, 480)]   // the Details window's Minimal breakpoint, where it was found
    [InlineData(800, 600)]   // Compact, where it cleared the buttons by about 4 px
    [InlineData(553, 504)]   // the main window at its default size
    public void ANarrowWindowKeepsTheDialogBelowTheTitleBar(double width, double height)
    {
        double cap = DialogHeight.MaxForWindow(height, width);

        // Every height the dialog can take there, from a one-line question to the cap: placed where
        // DialogFit puts it, it starts below the strip and ends inside the window.
        for (double dialog = 0; dialog <= cap; dialog++)
        {
            double top = DialogHeight.TopForDialog(dialog, height, width) ?? (height - dialog) / 2;
            Assert.True(top >= DialogHeight.TitleBar, $"a {dialog} px dialog in {width} × {height} starts at {top}");
            Assert.True(top + dialog <= height, $"a {dialog} px dialog in {width} × {height} ends at {top + dialog}");
        }
    }

    /// <summary>
    /// #773: the cap gives back the foot of the window. #725's centred cap left a title bar's height
    /// empty below the dialog as well as above it, and at 200 % text in the Minimal window #767's cue
    /// line then took the last whole row of PRNs.
    /// </summary>
    [Theory]
    [InlineData(640, 480)]
    [InlineData(800, 600)]
    [InlineData(553, 504)]
    public void ANarrowWindowsCapRunsToHalfTheClearanceFromTheFoot(double width, double height)
    {
        double centredCap = height - (2 * DialogHeight.TitleBar);

        Assert.Equal(height - DialogHeight.TitleBar - (DialogHeight.Clearance / 2), DialogHeight.MaxForWindow(height, width));
        Assert.Equal(DialogHeight.TitleBar - (DialogHeight.Clearance / 2), DialogHeight.MaxForWindow(height, width) - centredCap);
    }

    /// <summary>
    /// Only a dialog that would reach the strip moves. A short one stays centred, where every other
    /// Windows dialog is, and so does any dialog in a window where it cannot reach the buttons.
    /// </summary>
    [Theory]
    [InlineData(200, 640, 480, null)]                       // a confirmation: centred, at 140
    [InlineData(480 - 96, 640, 480, null)]                  // exactly clears the strip centred
    [InlineData(480 - 95, 640, 480, DialogHeight.TitleBar)]
    [InlineData(480 - 64, 640, 480, DialogHeight.TitleBar)] // at the cap
    [InlineData(700, 1024, 720, null)]                      // wide: #506's window, untouched
    [InlineData(300, 0, 0, null)]                           // window not known: no opinion
    public void OnlyADialogThatWouldReachTheStripIsMoved(double dialog, double width, double height, double? expected)
    {
        Assert.Equal(expected, DialogHeight.TopForDialog(dialog, height, width));
    }

    /// <summary>
    /// Where the dialog cannot reach the buttons the cap is #506's, untouched: lowering it there
    /// would scroll the connection dialog for nothing.
    /// </summary>
    [Theory]
    [InlineData(1024, 720)]  // the Details window's default size
    [InlineData(1040, 814)]  // #506's window, where the cap is raised
    [InlineData(1920, 1400)]
    public void AWideWindowKeepsTheHeightOnlyCap(double width, double height)
    {
        Assert.Equal(DialogHeight.MaxFor(height), DialogHeight.MaxForWindow(height, width));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(480, 0)]
    [InlineData(0, 640)]
    public void AnUnknownWindowHasNoOpinion(double height, double width)
    {
        Assert.Equal(DialogHeight.MaxFor(height), DialogHeight.MaxForWindow(height, width));
    }
}
