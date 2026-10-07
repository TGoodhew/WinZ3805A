using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The noun phrase one sentence names when a card loses several rows (#751).
/// </summary>
public sealed class AbsentReadingsTests
{
    [Fact]
    public void NothingAbsentIsNoSentence() => Assert.Null(AbsentReadings.Join([]));

    [Fact]
    public void OneReadingIsNamedAlone() => Assert.Equal("a b", AbsentReadings.Join(["a b"]));

    [Fact]
    public void TwoReadingsAreJoinedWithOr() =>
        Assert.Equal("a or b", AbsentReadings.Join(["a", "b"]));

    [Fact]
    public void ThreeReadingsAreAListEndingInOr() =>
        Assert.Equal("a, b or c", AbsentReadings.Join(["a", "b", "c"]));
}
