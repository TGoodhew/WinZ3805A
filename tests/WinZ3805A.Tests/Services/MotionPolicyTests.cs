using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// A11Y-13 and §9.8.2's "Nav page change" row.
/// </summary>
/// <remarks>
/// The reduced-motion half of these is the half worth having. A developer with animations on sees
/// only the slides, and a fallback that quietly kept sliding would pass every look at the running
/// application until it reached the one user who cannot tolerate it.
/// </remarks>
public sealed class MotionPolicyTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 8)]
    [InlineData(3, 4)]
    public void MovingDownThePaneRisesFromTheBottom(int from, int to) =>
        Assert.Equal(NavigationMotion.FromBottom, MotionPolicy.ForNavigation(true, from, to));

    /// <remarks>
    /// The policy's answer, not the screen's: since §9.8.2's #120 correction the window draws this
    /// slide <c>FromBottom</c> as well, so this asserts the decision rather than the drawing.
    /// </remarks>
    [Theory]
    [InlineData(1, 0)]
    [InlineData(8, 0)]
    [InlineData(4, 3)]
    public void MovingUpThePaneDropsFromTheTop(int from, int to) =>
        Assert.Equal(NavigationMotion.FromTop, MotionPolicy.ForNavigation(true, from, to));

    [Fact]
    public void ReNavigatingToTheSamePageDoesNotSlide() =>
        Assert.Equal(NavigationMotion.None, MotionPolicy.ForNavigation(true, 2, 2));

    /// <remarks>
    /// The window's first page. -1 is the "no page behind this one" sentinel, and it must not be
    /// read as an index above the target and turned into a slide from the top.
    /// </remarks>
    [Fact]
    public void TheFirstPageOfAWindowDoesNotSlide() =>
        Assert.Equal(NavigationMotion.None, MotionPolicy.ForNavigation(true, -1, 0));

    [Fact]
    public void ADestinationOutsideThePaneDoesNotSlide() =>
        Assert.Equal(NavigationMotion.None, MotionPolicy.ForNavigation(true, 3, -1));

    /// <summary>A11Y-13: with the setting off, nothing slides in either direction.</summary>
    /// <remarks>
    /// Every pair of pane positions, not a sample, because "reduced motion wins" is a claim about
    /// the whole table and an ordering that only holds for the cases someone thought to type is
    /// exactly the failure this asserts against.
    /// </remarks>
    [Fact]
    public void ReducedMotionOverridesEveryDirection()
    {
        int count = DetailsDestinations.All.Count;

        for (int from = -1; from < count; from++)
        {
            for (int to = -1; to < count; to++)
            {
                Assert.Equal(NavigationMotion.None, MotionPolicy.ForNavigation(false, from, to));
            }
        }
    }

    /// <remarks>
    /// The converse, so that the test above cannot pass by the policy having stopped animating
    /// altogether: with the setting on, every move between two different pages does slide.
    /// </remarks>
    [Fact]
    public void WithAnimationsOnEveryMoveBetweenPagesSlides()
    {
        int count = DetailsDestinations.All.Count;

        for (int from = 0; from < count; from++)
        {
            for (int to = 0; to < count; to++)
            {
                if (from == to)
                {
                    continue;
                }

                Assert.NotEqual(NavigationMotion.None, MotionPolicy.ForNavigation(true, from, to));
            }
        }
    }
}
