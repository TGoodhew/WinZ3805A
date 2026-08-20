using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The counting rules §10.2 and §9.7.5 put on the navigation pane.
/// </summary>
/// <remarks>
/// None of these can be checked by looking at the window. A ninth destination looks perfectly
/// reasonable in the pane and is simply unreachable from the keyboard, and a duplicated tag shows
/// two identical-looking items that both navigate to the first one.
/// </remarks>
public sealed class DetailsDestinationTests
{
    /// <remarks>
    /// §10.2 caps the numbered set at twelve as of 19 Aug 2026, raised from eight. The Advanced
    /// Console still sits below Settings and is not a destination here.
    /// </remarks>
    [Fact]
    public void TheNumberedSetStaysWithinTheCap()
    {
        Assert.InRange(DetailsDestinations.Numbered.Count, 1, DetailsDestinations.MaxNumbered);
        Assert.Equal(DetailsDestinations.Numbered.Count + 1, DetailsDestinations.All.Count);
    }

    /// <remarks>
    /// Settings has its own accelerator, <c>Ctrl+,</c>. If it ever joined the numbered list it
    /// would take the eighth slot and push Diagnostics out of keyboard reach.
    /// </remarks>
    [Fact]
    public void SettingsIsNotNumbered()
    {
        Assert.DoesNotContain(DetailsDestinations.Settings, DetailsDestinations.Numbered);
        Assert.Same(DetailsDestinations.Settings, DetailsDestinations.All[^1]);
    }

    /// <remarks>The §9.7.1 wireframe's order, which is the one the user sees.</remarks>
    [Fact]
    public void ThePaneIsInWireframeOrder() =>
        Assert.Equal(
            ["overview", "satellites", "position", "timing", "holdover", "time", "registers", "diagnostics"],
            DetailsDestinations.Numbered.Select(destination => destination.Tag));

    [Fact]
    public void EveryDestinationIsDistinctAndComplete()
    {
        Assert.Equal(
            DetailsDestinations.All.Count,
            DetailsDestinations.All.Select(destination => destination.Tag).Distinct().Count());

        Assert.All(DetailsDestinations.All, destination =>
        {
            Assert.False(string.IsNullOrWhiteSpace(destination.Label));
            Assert.False(string.IsNullOrWhiteSpace(destination.Summary));

            // A Segoe Fluent Icons code point, which is one private-use character. An empty glyph
            // renders as nothing and a multi-character one as a run of tofu.
            Assert.Single(destination.Glyph);
            Assert.InRange(destination.Glyph[0], '\uE000', '\uF8FF');
        });
    }

    /// <remarks>
    /// The accelerators are one-based, as §9.7.5 writes them. Off by one here would put every page
    /// under the wrong key and leave the eighth unreachable.
    /// </remarks>
    [Theory]
    [InlineData(1, "overview")]
    [InlineData(6, "time")]
    [InlineData(8, "diagnostics")]
    public void CtrlNumberReachesTheNthDestination(int number, string tag) =>
        Assert.Equal(tag, DetailsDestinations.ByNumber(number)?.Tag);

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(13)]
    public void NoOtherNumberReachesAnything(int number) =>
        Assert.Null(DetailsDestinations.ByNumber(number));

    /// <summary>
    /// §10.2's cap is twelve but the accelerators stop at nine, because there is no
    /// <c>Ctrl+10</c>. A destination past the ninth is reachable by pane navigation and by nothing
    /// on the number row.
    /// </summary>
    [Fact]
    public void NothingPastTheNinthDestinationIsAccelerated()
    {
        Assert.Equal(9, DetailsDestinations.MaxAccelerated);
        Assert.True(DetailsDestinations.MaxAccelerated < DetailsDestinations.MaxNumbered);

        for (int number = DetailsDestinations.MaxAccelerated + 1; number <= DetailsDestinations.MaxNumbered; number++)
        {
            Assert.Null(DetailsDestinations.ByNumber(number));
        }
    }

    /// <summary>
    /// The pane's order therefore decides which destinations are one keystroke away, so the ones
    /// that exist today must all still be inside the accelerated range.
    /// </summary>
    [Fact]
    public void EveryDestinationThatExistsTodayIsStillAccelerated()
    {
        Assert.True(DetailsDestinations.Numbered.Count <= DetailsDestinations.MaxAccelerated);

        for (int number = 1; number <= DetailsDestinations.Numbered.Count; number++)
        {
            Assert.NotNull(DetailsDestinations.ByNumber(number));
        }
    }

    [Fact]
    public void DestinationsAreFoundByTag()
    {
        Assert.Equal("Status Registers", DetailsDestinations.ByTag("registers")?.Label);
        Assert.Same(DetailsDestinations.Settings, DetailsDestinations.ByTag("settings"));
        Assert.Null(DetailsDestinations.ByTag("console"));
        Assert.Null(DetailsDestinations.ByTag(null));
    }

    /// <remarks>
    /// §9.8.2's page transition reads the direction off these positions, so the index has to agree
    /// with the pane the user is looking at - including Settings, which is drawn in the footer but
    /// is still the last thing in the list and still navigated to.
    /// </remarks>
    [Fact]
    public void DestinationsKnowWhereTheySitInThePane()
    {
        Assert.Equal(0, DetailsDestinations.IndexOf("overview"));
        Assert.Equal(DetailsDestinations.All.Count - 1, DetailsDestinations.IndexOf("settings"));

        for (int index = 0; index < DetailsDestinations.All.Count; index++)
        {
            Assert.Equal(index, DetailsDestinations.IndexOf(DetailsDestinations.All[index].Tag));
        }
    }

    /// <summary>The sentinel the transition policy reads as "no page".</summary>
    [Theory]
    [InlineData("console")]
    [InlineData("")]
    [InlineData(null)]
    public void SomethingThatIsNotADestinationHasNoIndex(string? tag) =>
        Assert.Equal(-1, DetailsDestinations.IndexOf(tag));
}
