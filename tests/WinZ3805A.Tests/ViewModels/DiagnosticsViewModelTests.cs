using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// The two §10.9 cards #320 added: the §11.2 parse-warning report and the lifetime readout.
/// </summary>
/// <remarks>
/// Both are set from outside — the warnings by the page from the store, the hours by
/// <c>RefreshAsync</c> — so what is worth testing here is the presentation rather than the read.
/// </remarks>
public sealed class DiagnosticsViewModelTests
{
    private static DiagnosticsViewModel Model() =>
        new(new DeviceSessionService(
            (_, _) => new FakeTransport(),
            new FakeTimeProvider()));

    /// <remarks>
    /// The negative is the point. §11.1 makes an unreadable field render as a dash, which tells a
    /// reader nothing about whether the dash is the receiver's answer or the parser's failure. A
    /// card that goes blank when all is well cannot distinguish "nothing wrong" from "not built",
    /// so it says so.
    /// </remarks>
    [Fact]
    public void SummaryStatesTheScreenParsedWhenNothingFailed()
    {
        DiagnosticsViewModel model = Model();

        Assert.Empty(model.ParseWarnings);
        Assert.Equal("The last status screen parsed completely.", model.ParseWarningSummary);
    }

    /// <remarks>
    /// One warning is not "1 fields". The count is read aloud by a screen reader (§9.9), and the
    /// plural is the sort of thing that survives review and then grates on every use.
    /// </remarks>
    [Theory]
    [InlineData(1, "1 field in the last status screen could not be read.")]
    [InlineData(2, "2 fields in the last status screen could not be read.")]
    [InlineData(11, "11 fields in the last status screen could not be read.")]
    public void SummaryCountsWarnings(int count, string expected)
    {
        DiagnosticsViewModel model = Model();

        model.ParseWarnings = [.. Enumerable.Range(0, count).Select(i => $"warning {i}")];

        Assert.Equal(expected, model.ParseWarningSummary);
    }

    /// <remarks>
    /// The store raises <c>PropertyChanged</c> on every sweep, so the page assigns this list once a
    /// second whether or not the screen changed. Comparing by sequence rather than by reference is
    /// what stops a settled receiver from re-rendering the card 86 400 times a day.
    /// </remarks>
    [Fact]
    public void ReassigningTheSameWarningsRaisesNothing()
    {
        DiagnosticsViewModel model = Model();
        model.ParseWarnings = ["unrecognised health item 'Xtal Pwr'"];

        int raised = 0;
        model.PropertyChanged += (_, _) => raised++;

        model.ParseWarnings = ["unrecognised health item 'Xtal Pwr'"];
        Assert.Equal(0, raised);

        model.ParseWarnings = ["unrecognised health item 'Xtal Pwr'", "another"];
        Assert.NotEqual(0, raised);
    }

    /// <remarks>
    /// A null assignment is the store's empty case reaching the page before the first sweep, not a
    /// caller error, so it becomes an empty list rather than a throw — the property is set from a
    /// <c>DispatcherQueue</c> callback where an exception has nowhere to go.
    /// </remarks>
    [Fact]
    public void NullWarningsBecomeEmpty()
    {
        DiagnosticsViewModel model = Model();
        model.ParseWarnings = ["something"];

        model.ParseWarnings = null!;

        Assert.Empty(model.ParseWarnings);
        Assert.Equal("The last status screen parsed completely.", model.ParseWarningSummary);
    }

    /// <remarks>
    /// Unread is a dash and not "0 h": the receiver has certainly been running, and a zero would be
    /// a claim about the hardware rather than about the read (§11.1).
    /// </remarks>
    [Fact]
    public void LifetimeReadsAsNoValueUntilItIsRead()
    {
        Assert.Equal(ReadoutFormatter.NoValue, Model().PowerOnHoursText);
    }
}
