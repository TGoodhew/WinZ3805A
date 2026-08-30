using WinZ3805A.Device.Commands;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// #51's state read, against the formats this receiver actually uses.
/// </summary>
/// <remarks>
/// Both formats were captured from the bench Z3805A rather than inferred, and both would have been
/// got wrong by a reasonable guess: an empty list answers <c>+0</c>, and a non-empty one arrives on
/// the <b>second</b> line. Every other query in this application puts its value on the first.
/// </remarks>
public sealed class SatelliteTrackingParserTests
{
    // ------------------------------------------------------------- what the receiver really sends

    /// <summary>
    /// <c>:GPS:SAT:TRAC:IGN?</c> with nothing excluded, verbatim from the transcript.
    /// </summary>
    [Fact]
    public void AnEmptyListAnswersZeroAndReadsAsEmpty() =>
        Assert.Empty(SatelliteTrackingParser.ParsePrnList(["+0"]));

    /// <summary>
    /// <c>:GPS:SAT:TRAC:INCL?</c> with everything included, verbatim: a blank line, then the list.
    /// </summary>
    [Fact]
    public void AFullListArrivesOnTheSecondLine()
    {
        IReadOnlySet<int> prns = SatelliteTrackingParser.ParsePrnList(
        [
            string.Empty,
            "+1,+2,+3,+4,+5,+6,+7,+8,+9,+10,+11,+12,+13,+14,+15,+16,"
            + "+17,+18,+19,+20,+21,+22,+23,+24,+25,+26,+27,+28,+29,+30,+31,+32",
        ]);

        Assert.Equal(32, prns.Count);
        Assert.Equal(SatelliteTrackingState.AllPrns.Order(), prns.Order());
    }

    /// <summary>
    /// The list is found wherever it is, because a sibling model may not use the second line. This
    /// is the same response on one line.
    /// </summary>
    [Fact]
    public void AListOnTheFirstLineIsReadJustAsWell() =>
        Assert.Equal([3, 17, 28], SatelliteTrackingParser.ParsePrnList(["+3,+17,+28"]).Order());

    [Fact]
    public void LeadingSpacesDoNotStopIt() =>
        Assert.Equal([3, 17], SatelliteTrackingParser.ParsePrnList([" +3, +17 "]).Order());

    // ------------------------------------------------------------------------------ §11.1's rule

    [Fact]
    public void NothingAtAllIsAnEmptyListRatherThanAnException()
    {
        Assert.Empty(SatelliteTrackingParser.ParsePrnList(null));
        Assert.Empty(SatelliteTrackingParser.ParsePrnList([]));
        Assert.Empty(SatelliteTrackingParser.ParsePrnList([string.Empty]));
    }

    [Fact]
    public void UnreadableTokensAreDroppedRatherThanGuessedAt() =>
        Assert.Equal([3, 17], SatelliteTrackingParser.ParsePrnList(["+3,rubbish,+17,"]).Order());

    /// <summary>
    /// A PRN outside the constellation cannot be a satellite, so it is not one. Zero is the case
    /// this exists for and 33 is the case that proves it is not a special-case for zero.
    /// </summary>
    [Theory]
    [InlineData("+0")]
    [InlineData("+33")]
    [InlineData("-4")]
    [InlineData("+999")]
    public void APrnOutsideOneToThirtyTwoIsNotASatellite(string token) =>
        Assert.Empty(SatelliteTrackingParser.ParsePrnList([token]));

    [Fact]
    public void ARepeatedPrnIsCountedOnce() =>
        Assert.Single(SatelliteTrackingParser.ParsePrnList(["+7,+7,+7"]));

    // --------------------------------------------------------------------------- what is sent out

    [Fact]
    public void AListIsSentAscendingAndCommaSeparated() =>
        Assert.Equal("3,17,28", SatelliteTrackingParser.FormatPrnList([28, 3, 17]));

    [Fact]
    public void DuplicatesAreCollapsedOnTheWayOut() =>
        Assert.Equal("3,17", SatelliteTrackingParser.FormatPrnList([17, 3, 17, 3]));

    [Fact]
    public void AnEmptySelectionFormatsAsNothing() =>
        Assert.Equal(string.Empty, SatelliteTrackingParser.FormatPrnList([]));

    /// <summary>
    /// The same rule the Advanced Console's PRN field follows: what reaches the wire is built from
    /// parsed integers, so nothing that could carry a command separator can survive the trip.
    /// </summary>
    [Fact]
    public void OnlyDigitsAndCommasEverComeOut()
    {
        string sent = SatelliteTrackingParser.FormatPrnList([1, 32, 16]);

        Assert.True(sent.All(character => char.IsAsciiDigit(character) || character == ','));
    }

    [Fact]
    public void AnImpossiblePrnIsNotSentEvenIfSomethingAsksForIt() =>
        Assert.Equal("5", SatelliteTrackingParser.FormatPrnList([0, 5, 33, -1]));

    // --------------------------------------------------------------------------------- the state

    [Fact]
    public void TheConstellationIsThirtyTwoSatellites()
    {
        Assert.Equal(32, SatelliteTrackingState.AllPrns.Count());
        Assert.Equal(1, SatelliteTrackingState.AllPrns.First());
        Assert.Equal(32, SatelliteTrackingState.AllPrns.Last());
    }

    /// <summary>
    /// "Nothing read yet" and "nothing included" are different, and a dialog that confused them
    /// would open showing every satellite excluded on a receiver it had not managed to ask.
    /// </summary>
    [Fact]
    public void UnknownIsEmptyOnBothLists()
    {
        Assert.Empty(SatelliteTrackingState.Unknown.Included);
        Assert.Empty(SatelliteTrackingState.Unknown.Excluded);
    }

    [Fact]
    public void TheTwoListsAreIndependent()
    {
        SatelliteTrackingState state = new(
            SatelliteTrackingParser.ParsePrnList(["+1,+2,+3"]),
            SatelliteTrackingParser.ParsePrnList(["+17"]));

        Assert.Contains(17, state.Excluded);
        Assert.DoesNotContain(17, state.Included);

        // And a PRN may be on neither, which is why they are two sets rather than one flag.
        Assert.DoesNotContain(20, state.Included);
        Assert.DoesNotContain(20, state.Excluded);
    }
}

/// <summary>
/// #51's per-satellite row: three reported states, two of which a toggle cannot express.
/// </summary>
public sealed class SatelliteChoiceTests
{
    [Fact]
    public void AnIncludedSatelliteStartsSelected() =>
        Assert.True(new SatelliteChoice(17, isIncluded: true, isExcluded: false).IsSelected);

    /// <summary>
    /// Excluded wins over included. The receiver can have a PRN on both lists, and in that case it
    /// is not being tracked — so a dialog that started it selected would be describing the wrong
    /// one of the two facts.
    /// </summary>
    [Fact]
    public void AnExcludedSatelliteStartsUnselectedEvenIfAlsoIncluded() =>
        Assert.False(new SatelliteChoice(17, isIncluded: true, isExcluded: true).IsSelected);

    [Fact]
    public void ASatelliteOnNeitherListStartsUnselected() =>
        Assert.False(new SatelliteChoice(17, isIncluded: false, isExcluded: false).IsSelected);

    /// <summary>
    /// Three states, and a toggle has two. The third is carried by a glyph and by the wording,
    /// never by colour (§9.4.3, A11Y-12).
    /// </summary>
    [Fact]
    public void TheThreeReportedStatesAreDistinguishable()
    {
        SatelliteChoice included = new(1, isIncluded: true, isExcluded: false);
        SatelliteChoice excluded = new(2, isIncluded: false, isExcluded: true);
        SatelliteChoice neither = new(3, isIncluded: false, isExcluded: false);

        Assert.Equal(3, new HashSet<string>([included.StateText, excluded.StateText, neither.StateText]).Count);
        Assert.NotEqual(string.Empty, excluded.Marker);
        Assert.Equal(string.Empty, included.Marker);
        Assert.Equal(string.Empty, neither.Marker);
    }

    /// <summary>
    /// The spoken name carries both what the receiver reports and what the user picked, because the
    /// toggle announces only the second and the two differing is the point of the dialog.
    /// </summary>
    [Fact]
    public void TheSpokenNameCarriesBothFacts()
    {
        SatelliteChoice choice = new(17, isIncluded: false, isExcluded: true);

        Assert.Contains("excluded", choice.AutomationName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Not selected", choice.AutomationName, StringComparison.Ordinal);

        choice.IsSelected = true;

        Assert.Contains("excluded", choice.AutomationName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Not selected", choice.AutomationName, StringComparison.Ordinal);
    }

    [Fact]
    public void ChangingTheSelectionAnnouncesTheNameToo()
    {
        SatelliteChoice choice = new(17, isIncluded: true, isExcluded: false);
        List<string?> changed = [];
        choice.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        choice.IsSelected = false;

        Assert.Contains(nameof(SatelliteChoice.IsSelected), changed);
        Assert.Contains(nameof(SatelliteChoice.AutomationName), changed);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(33)]
    [InlineData(-1)]
    public void APrnOutsideTheConstellationIsRefused(int prn) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new SatelliteChoice(prn, false, false));
}

/// <summary>
/// #51's ceremony: the two commands that drive the receiver into holdover are treated alike.
/// </summary>
/// <remarks>
/// Emptying the inclusion list and excluding every satellite are the same outcome reached from two
/// directions. Until #51 put them side by side in one dialog, only one of them said so — the other
/// carried the ordinary "Update the tracking inclusion list?" behind an acknowledgement checkbox,
/// which reads as a mistake in the checkbox rather than a warning.
/// </remarks>
public sealed class SatelliteCommandCeremonyTests
{
    private static ScpiCommand Find(string mnemonic) =>
        CommandCatalog.Find(mnemonic) ?? throw new InvalidOperationException($"{mnemonic} is not catalogued.");

    [Theory]
    [InlineData(":GPS:SAT:TRAC:IGNore ALL")]
    [InlineData(":GPS:SAT:TRAC:INCLude NONE")]
    public void BothHoldoverVariantsRequireTheAcknowledgement(string mnemonic) =>
        Assert.True(Find(mnemonic).RequiresAcknowledgement);

    [Theory]
    [InlineData(":GPS:SAT:TRAC:IGNore ALL")]
    [InlineData(":GPS:SAT:TRAC:INCLude NONE")]
    public void BothSayTheReceiverWillEnterHoldover(string mnemonic) =>
        Assert.Contains("holdover", Find(mnemonic).ConfirmationText!, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Clearing the exclusion list is confirmed by a sentence about clearing it, and not by the
    /// one belonging to the command that excludes.
    /// </summary>
    /// <remarks>
    /// <c>:IGNore NONE</c> carried <c>:IGNore</c>'s sentence — *"Exclude the selected satellites
    /// from tracking?"* — until #320, in the catalog and in §8.3 alike, for the command that makes
    /// every satellite eligible again. The dialog is the safety mechanism, so a dialog naming the
    /// reverse of what it is about to do is the one kind of wrong copy that matters: a user reading
    /// it carefully would have been misled precisely because they read it.
    /// <para>
    /// The two sentences are asserted different rather than merely checked for a keyword, because
    /// the fault was that they were identical.
    /// </para>
    /// </remarks>
    [Fact]
    public void ClearingTheExclusionListDoesNotBorrowTheExcludeSentence()
    {
        string clear = Find(":GPS:SAT:TRAC:IGNore NONE").ConfirmationText!;
        string exclude = Find(":GPS:SAT:TRAC:IGNore").ConfirmationText!;

        Assert.NotEqual(exclude, clear);
        Assert.Contains("Clear the exclusion list", clear, StringComparison.Ordinal);
        Assert.DoesNotContain("Exclude the selected", clear, StringComparison.Ordinal);
    }

    /// <summary>
    /// And the four that do not drive holdover neither claim it nor demand the tick, so the
    /// ceremony still distinguishes them.
    /// </summary>
    [Theory]
    [InlineData(":GPS:SAT:TRAC:IGNore")]
    [InlineData(":GPS:SAT:TRAC:IGNore NONE")]
    [InlineData(":GPS:SAT:TRAC:INCLude")]
    [InlineData(":GPS:SAT:TRAC:INCLude ALL")]
    public void TheOthersAreOrdinaryConfirmations(string mnemonic)
    {
        ScpiCommand command = Find(mnemonic);

        Assert.False(command.RequiresAcknowledgement);
        Assert.DoesNotContain("holdover", command.ConfirmationText!, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Every command the dialog offers is tier C, which is #51's first criterion.</summary>
    [Theory]
    [InlineData(":GPS:SAT:TRAC:IGNore")]
    [InlineData(":GPS:SAT:TRAC:IGNore ALL")]
    [InlineData(":GPS:SAT:TRAC:IGNore NONE")]
    [InlineData(":GPS:SAT:TRAC:INCLude")]
    [InlineData(":GPS:SAT:TRAC:INCLude ALL")]
    [InlineData(":GPS:SAT:TRAC:INCLude NONE")]
    public void EveryWriteIsTierC(string mnemonic) =>
        Assert.Equal(SafetyTier.Confirm, Find(mnemonic).Tier);

    /// <summary>And the state the dialog opens with comes from tier S queries, so reading is free.</summary>
    [Theory]
    [InlineData(":GPS:SAT:TRAC:INCL?")]
    [InlineData(":GPS:SAT:TRAC:IGN?")]
    public void TheStateQueriesAreSafe(string mnemonic)
    {
        ScpiCommand command = Find(mnemonic);

        Assert.Equal(SafetyTier.Safe, command.Tier);
        Assert.True(command.IsQuery);
    }
}
