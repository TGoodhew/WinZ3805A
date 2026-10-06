using WinZ3805A.Controls;

namespace WinZ3805A.Tests.Controls;

/// <summary>
/// The word a pill reports as its UI Automation item status (#728).
/// </summary>
/// <remarks>
/// The QA pass records these beside every photograph and compares them exactly with the baseline's,
/// so a pill that changes colour is a fact rather than a pixel share. That only works while every
/// level has its own word and the words do not drift.
/// </remarks>
public sealed class SeverityStatusTests
{
    [Theory]
    [InlineData(Severity.Neutral, "neutral")]
    [InlineData(Severity.Success, "success")]
    [InlineData(Severity.Caution, "caution")]
    [InlineData(Severity.Critical, "critical")]
    [InlineData(Severity.Info, "info")]
    public void EachLevelReportsItsOwnName(Severity severity, string expected) =>
        Assert.Equal(expected, SeverityStatus.Word(severity));

    [Fact]
    public void NoTwoLevelsShareAWord()
    {
        string[] words = [.. Enum.GetValues<Severity>().Select(SeverityStatus.Word)];

        Assert.Equal(words.Length, words.Distinct(StringComparer.Ordinal).Count());
    }
}
