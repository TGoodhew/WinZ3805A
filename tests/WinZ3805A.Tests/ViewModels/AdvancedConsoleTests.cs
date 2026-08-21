using WinZ3805A.Device.Commands;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §10.11's console, and the property the whole page rests on: it can only reach the catalog.
/// </summary>
/// <remarks>
/// <para>
/// These tests never name an excluded command, and must not be edited to. §8.4 says those names do
/// not appear in any test fixture, and the properties below are asserted the way they have to be
/// asserted anyway — over what the picker <i>does</i> hold, not over a list of what it must not.
/// </para>
/// <para>
/// That is not a limitation. "Every item is a catalogued command and none matches the exclusions"
/// is a stronger statement than "these four particular strings are absent", because it stays true
/// when the catalog changes.
/// </para>
/// </remarks>
public sealed class AdvancedConsoleTests
{
    // ------------------------------------------------------------------------- the picker's list

    /// <summary>
    /// #55's acceptance criterion, first half: the item source is the catalog.
    /// </summary>
    [Fact]
    public void EveryItemInThePickerIsACataloguedCommand() =>
        Assert.All(
            ConsoleCatalog.All,
            entry => Assert.True(
                CommandCatalog.Contains(entry.Mnemonic),
                $"{entry.Mnemonic} is offered by the picker but is not in the catalog."));

    /// <summary>
    /// And the other direction, so the picker is the catalog rather than merely a subset of it.
    /// §8.5's experimental queries are the one documented omission — they have their own opt-in.
    /// </summary>
    [Fact]
    public void EveryNonExperimentalCatalogueCommandIsOffered()
    {
        IEnumerable<string> expected = CommandCatalog.All
            .Where(command => !command.IsExperimental)
            .Select(command => command.Mnemonic)
            .Order(StringComparer.Ordinal);

        Assert.Equal(expected, ConsoleCatalog.All.Select(entry => entry.Mnemonic));
    }

    [Fact]
    public void TheExperimentalQueriesAreNotOffered()
    {
        // §8.5 makes them opt-in and off by default. Their switch is #56, not this page.
        Assert.NotEmpty(CommandCatalog.Experimental);
        Assert.All(ConsoleCatalog.All, entry => Assert.False(entry.Command.IsExperimental));
    }

    /// <summary>
    /// The safety property, stated over what the picker holds rather than over a list of names.
    /// </summary>
    [Fact]
    public void NothingThePickerOffersIsAnExcludedCommand() =>
        Assert.All(
            ConsoleCatalog.All,
            entry => Assert.False(
                CommandCatalog.IsBlocked(entry.Mnemonic),
                "The picker offered a command the safety model excludes."));

    /// <summary>
    /// #55's acceptance criterion, second half: it cannot be extended at runtime.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A bare <c>List&lt;T&gt;</c> behind an <c>IReadOnlyList&lt;T&gt;</c> can be cast back and added
    /// to by anything holding the reference, which is exactly the runtime path §8.1 exists to close.
    /// </para>
    /// <para>
    /// The cast itself is not what to assert on. <c>ReadOnlyCollection&lt;T&gt;</c> does implement
    /// <c>ICollection&lt;T&gt;</c> — explicitly, throwing on every mutation — so a type check would
    /// pass for a plain list and fail for the wrapper that is actually safe. What matters is that
    /// the attempt fails, so the attempt is what is made.
    /// </para>
    /// </remarks>
    [Fact]
    public void ThePickersListCannotBeAddedTo()
    {
        Assert.IsNotType<List<ConsoleCommand>>(ConsoleCatalog.All);

        ConsoleCommand smuggled = ConsoleCatalog.All[0];

        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<ConsoleCommand>)ConsoleCatalog.All).Add(smuggled));
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ConsoleCommand>)ConsoleCatalog.All).Insert(0, smuggled));
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<ConsoleCommand>)ConsoleCatalog.All).Clear());
    }

    [Fact]
    public void AFilteredListCannotBeAddedToEither()
    {
        IReadOnlyList<ConsoleCommand> matches = ConsoleCatalog.Matching("SYNC");

        Assert.IsNotType<List<ConsoleCommand>>(matches);
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<ConsoleCommand>)matches).Add(ConsoleCatalog.All[0]));
    }

    /// <summary>
    /// Every parameter of every catalogued command is a kind the console has an editor for.
    /// </summary>
    /// <remarks>
    /// <para>
    /// #147's acceptance criterion, and it replaces three tests that asserted the opposite. They
    /// were right when written: the console drew one editor, four commands wanted three, three,
    /// three and nine values between them, and the tests pinned the refusal so none could quietly
    /// be sent with the rest left off.
    /// </para>
    /// <para>
    /// Stated as editor coverage rather than by building a sample value for each parameter. The
    /// first attempt did that and failed on two commands that had nothing to do with this issue —
    /// a keyword with no choices, and a baud rate whose legal values are a list rather than a
    /// range — because constructing a valid sample means reimplementing the validator in the test,
    /// and a test that reimplements what it checks is testing itself.
    /// </para>
    /// </remarks>
    [Fact]
    public void EveryParameterHasAnEditor() =>
        Assert.All(
            ConsoleCatalog.All.SelectMany(entry => entry.Parameters),
            parameter => Assert.Contains(
                parameter.Kind,
                new[]
                {
                    ParameterKind.Integer,
                    ParameterKind.Decimal,
                    ParameterKind.Keyword,
                    ParameterKind.PrnList,
                }));

    /// <summary>The four commands #147 named, each now taking the values it actually wants.</summary>
    [Theory]
    [InlineData(":PTIM:TZONe", 2)]
    [InlineData(":GPS:INIT:DATE", 3)]
    [InlineData(":GPS:INIT:TIME", 3)]
    [InlineData(":GPS:INIT:POSition", 9)]
    [InlineData(":GPS:POSition", 9)]
    public void TheCompositeCommandsDeclareTheirParts(string mnemonic, int parts)
    {
        ConsoleCommand entry = Assert.Single(
            ConsoleCatalog.All,
            candidate => candidate.Mnemonic == mnemonic);

        Assert.Equal(parts, entry.Parameters.Count);
    }

    /// <summary>
    /// A date is joined with commas, which is the form the manual gives and the whole reason
    /// these commands were refused rather than guessed at.
    /// </summary>
    /// <remarks>
    /// The 58503A programming guide prints this exact example: <c>:GPS:INIT:DATE 1994,7,4</c>.
    /// Pinned as a literal so that a change of separator has to be a deliberate edit to a test
    /// carrying its source, rather than a plausible-looking tidy-up.
    /// </remarks>
    [Fact]
    public void SeveralValuesAreJoinedWithCommas()
    {
        ConsoleCommand date = Assert.Single(
            ConsoleCatalog.All,
            candidate => candidate.Mnemonic == ":GPS:INIT:DATE");

        Assert.Equal("1994,7,4", ConsoleArgument.For(date.Parameters, ["1994", "7", "4"]).Text);
    }

    /// <summary>One bad field refuses the whole thing, and the message names that field.</summary>
    /// <remarks>
    /// Naming it matters more here than for a single value: with nine boxes on screen, "out of
    /// range" without a field name is a puzzle rather than an error.
    /// </remarks>
    [Fact]
    public void ABadFieldRefusesTheWholeArgumentAndNamesItself()
    {
        ConsoleCommand time = Assert.Single(
            ConsoleCatalog.All,
            candidate => candidate.Mnemonic == ":GPS:INIT:TIME");

        ConsoleArgument.Result result = ConsoleArgument.For(time.Parameters, ["12", "61", "56"]);

        Assert.False(result.IsValid);
        Assert.Null(result.Text);
        Assert.Contains("Minute", result.Error!, StringComparison.Ordinal);
    }

    /// <summary>
    /// A missing value in the middle refuses rather than shifting every later value one place left.
    /// </summary>
    /// <remarks>
    /// The failure this prevents is silent and severe: dropping the minutes from a position would
    /// send the seconds as minutes and the hemisphere as nothing, and the receiver would accept a
    /// coordinate the user never typed.
    /// </remarks>
    [Fact]
    public void AnOmittedValueInTheMiddleIsRefused()
    {
        ConsoleCommand date = Assert.Single(
            ConsoleCatalog.All,
            candidate => candidate.Mnemonic == ":GPS:INIT:DATE");

        Assert.False(ConsoleArgument.For(date.Parameters, ["1994", "", "4"]).IsValid);
    }

    /// <summary>The wrong number of values is refused rather than padded or truncated.</summary>
    [Fact]
    public void TheWrongNumberOfValuesIsRefused()
    {
        ConsoleCommand date = Assert.Single(
            ConsoleCatalog.All,
            candidate => candidate.Mnemonic == ":GPS:INIT:DATE");

        Assert.False(ConsoleArgument.For(date.Parameters, ["1994", "7"]).IsValid);
    }

    /// <summary>
    /// What a screen reader hears for a picker row. A <c>ComboBox</c> item announces the item
    /// object's <c>ToString</c>, and a record's generated one is every field of the record — which
    /// is what this used to be, for every row in the list.
    /// </summary>
    [Fact]
    public void APickerRowAnnouncesItselfAsASentence()
    {
        ConsoleCommand query = Assert.Single(ConsoleCatalog.All, entry => entry.Mnemonic == "*IDN?");

        Assert.Equal("*IDN? — Identify", query.ToString());
    }

    /// <summary>
    /// And whether the next click raises a confirmation is spoken, not left to the ellipsis on the
    /// button (§9.4.3, A11Y-12).
    /// </summary>
    [Fact]
    public void ARowThatWillRaiseADialogSaysSo()
    {
        ConsoleCommand holdover = Assert.Single(
            ConsoleCatalog.All,
            entry => entry.Mnemonic == ":SYNC:HOLDover:INITiate");

        Assert.EndsWith("needs confirmation", holdover.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void NoRowAnnouncesItselfAsARecordDump() =>
        Assert.All(
            ConsoleCatalog.All,
            entry => Assert.DoesNotContain("ScpiCommand {", entry.ToString(), StringComparison.Ordinal));

    // ------------------------------------------------------------------------------- the filter

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void AnEmptyFilterShowsEverything(string? filter) =>
        Assert.Equal(ConsoleCatalog.All.Count, ConsoleCatalog.Matching(filter).Count);

    [Fact]
    public void AFilterMatchesTheMnemonic()
    {
        IReadOnlyList<ConsoleCommand> matches = ConsoleCatalog.Matching(":SYNC");

        Assert.NotEmpty(matches);
        Assert.All(matches, entry => Assert.Contains(":SYNC", entry.Mnemonic, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AFilterMatchesTheLabelToo()
    {
        // "Identify" is *IDN?'s display name, and nothing about the mnemonic contains it.
        IReadOnlyList<ConsoleCommand> matches = ConsoleCatalog.Matching("Identify");

        Assert.Contains(matches, entry => entry.Mnemonic == "*IDN?");
    }

    [Fact]
    public void AFilterMatchingNothingReturnsNothingRatherThanEverything() =>
        Assert.Empty(ConsoleCatalog.Matching("zzzz-not-a-command"));

    /// <summary>
    /// A filter is a filter, not a search: it selects from the list and can never introduce
    /// something the list does not hold, whatever is typed into it.
    /// </summary>
    [Theory]
    [InlineData("*RST")]
    [InlineData(":FOO:BAR")]
    [InlineData("anything at all")]
    public void AFilterOnlyEverReturnsItemsTheFullListAlreadyHeld(string filter) =>
        Assert.All(
            ConsoleCatalog.Matching(filter),
            entry => Assert.Contains(entry, ConsoleCatalog.All));

    // -------------------------------------------------------------------------- what can be sent

    [Fact]
    public void AConfirmTierCommandIsMarkedAsNeedingItsDialog()
    {
        ConsoleCommand entry = Assert.Single(
            ConsoleCatalog.All,
            candidate => candidate.Mnemonic == ":SYNC:HOLDover:INITiate");

        Assert.True(entry.NeedsConfirmation);
        Assert.Equal("Confirm", entry.TierText);
    }

    [Fact]
    public void AQueryNeedsNoConfirmation()
    {
        ConsoleCommand entry = Assert.Single(
            ConsoleCatalog.All,
            candidate => candidate.Mnemonic == "*IDN?");

        Assert.False(entry.NeedsConfirmation);
    }

}
