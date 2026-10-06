using WinZ3805A.Controls;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The figures of merit's pills (#719): FFOM judged by the state of its loop, TFOM never judged.
/// </summary>
/// <remarks>
/// Both pills once ran TFOM's 0-9 thresholds, so FFOM 3 - "do not use the output" - was green. The
/// 3 row below is the case that shipped wrong; the TFOM rows hold the decision that an amount whose
/// best on this family is 3 is not given a colour.
/// </remarks>
public sealed class MeritSeverityTests
{
    [Theory]
    [InlineData(0, Severity.Success)]
    [InlineData(1, Severity.Caution)]
    [InlineData(2, Severity.Caution)]
    [InlineData(3, Severity.Critical)]
    [InlineData(null, Severity.Neutral)]
    [InlineData(4, Severity.Neutral)]
    public void FfomIsJudgedByTheStateOfTheLoopItReports(int? ffom, Severity expected) =>
        Assert.Equal(expected, MeritSeverity.OfFfom(ffom));

    [Theory]
    [InlineData(null)]
    [InlineData(0)]
    [InlineData(3)]
    [InlineData(6)]
    [InlineData(9)]
    public void TfomIsNeverGivenAColour(int? tfom) =>
        Assert.Equal(Severity.Neutral, MeritSeverity.OfTfom(tfom));
}
