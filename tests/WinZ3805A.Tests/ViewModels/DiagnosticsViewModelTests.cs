using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Drivers.Nmea;
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

    /// <summary>
    /// The same page with a talker selected — the family the #435 audit actually ran against.
    /// </summary>
    /// <remarks>
    /// The real <c>NmeaDriver</c> rather than a fake that answers false: what is being tested is
    /// that this page reads the driver's own answer, and a stub would let the test pass while the
    /// shipped driver said something else.
    /// </remarks>
    private static DiagnosticsViewModel TalkerModel()
    {
        FakeTimeProvider clock = new();
        return new DiagnosticsViewModel(new DeviceSessionService(
            (_, _) => new FakeTransport(),
            clock,
            drivers: [new NmeaDriver(clock)]));
    }

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

    // ---- #435: three cards that asserted SmartClock facts to whatever was connected -------------

    /// <summary>
    /// The Lifetime caption stops explaining an oven-controlled oscillator to a receiver without one.
    /// </summary>
    /// <remarks>
    /// The sentence was a literal in the XAML, which is why it survived: nothing that reads the view
    /// model could see it, and nothing that reads the XAML knows what is connected. Asserting it
    /// here puts the claim where the condition is.
    /// </remarks>
    [Fact]
    public void TheLifetimeCaptionDoesNotExplainAnOscillatorToATalker()
    {
        Assert.Contains("oven-controlled oscillator", Model().PowerOnHoursCaption, StringComparison.Ordinal);

        string caption = TalkerModel().PowerOnHoursCaption;

        Assert.DoesNotContain("oscillator", caption, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(
            "This receiver does not report how long it has been powered. The NMEA 0183 protocol does not carry it.",
            caption);
    }

    /// <remarks>
    /// "No errors." is the useful negative for a receiver that keeps a queue, and a clean bill of
    /// health invented from nothing for one that does not. The count branches stay untouched — this
    /// is about whether the queue exists, not about how many entries came out of it.
    /// </remarks>
    [Fact]
    public void TheErrorCardDoesNotReportAnEmptyQueueThatDoesNotExist()
    {
        Assert.Equal("No errors.", Model().ErrorSummaryText);

        Assert.Equal(
            "This receiver does not report an error queue. The NMEA 0183 protocol does not carry it.",
            TalkerModel().ErrorSummaryText);
    }

    /// <remarks>
    /// §9.11's empty state says what <i>will</i> appear, which is a promise. For a family with no log
    /// of its own it is one that cannot be kept, and "has not been read yet" invites a user to keep
    /// pressing a button that will never fill it.
    /// </remarks>
    [Fact]
    public void TheLogCardDoesNotPromiseEntriesThatCannotArrive()
    {
        Assert.Equal(
            "The log has not been read yet, or the receiver has nothing in it.",
            Model().LogEmptyText);

        Assert.Equal(
            "This receiver does not report a diagnostic log of its own. The NMEA 0183 protocol does not carry it.",
            TalkerModel().LogEmptyText);
    }
}
