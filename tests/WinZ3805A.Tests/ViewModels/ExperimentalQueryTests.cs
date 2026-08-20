using WinZ3805A.Device.Commands;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// §8.5's opt-in list: fixed, query-only, and off until asked for.
/// </summary>
/// <remarks>
/// #56's verification asks for exactly two properties — fixed-length and query-only — and both are
/// worth asserting rather than commenting, because the list is the entire safety argument for
/// offering an opt-in at all. A user turning this on gains six questions; if the list could grow, or
/// could contain something that is not a question, they would be gaining something else.
/// </remarks>
public sealed class ExperimentalQueryTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    // ------------------------------------------------------------------------- the list is fixed

    [Fact]
    public void ThereAreExactlySixOfThem()
    {
        Assert.Equal(ExperimentalQueries.Count, CommandCatalog.Experimental.Count);
        Assert.Equal(ExperimentalQueries.Count, ExperimentalQueries.Create().Count);
    }

    /// <summary>
    /// The six §8.5 names. Written out here — unlike §8.4's exclusions, which may never appear in a
    /// test — because §8.5 publishes this list and the point is that it is exactly this list.
    /// </summary>
    [Fact]
    public void TheyAreTheSixTheSpecificationNames() =>
        Assert.Equal(
            [
                ":DIAG:ROSC:EFC:ABSolute?",
                ":DIAG:ROSC:EFC:TCOefficient?",
                ":SYST:STAT:SLOG?",
                ":DIAG:STACk?",
                ":DIAG:PROCess?",
                ":DIAG:MEMory?",
            ],
            ExperimentalQueries.Create().Select(row => row.Mnemonic));

    [Fact]
    public void TheListCannotBeAddedTo()
    {
        IReadOnlyList<ExperimentalQueryRow> rows = ExperimentalQueries.Create();

        Assert.IsNotType<List<ExperimentalQueryRow>>(rows);
        Assert.Throws<NotSupportedException>(() =>
            ((ICollection<ExperimentalQueryRow>)rows).Add(rows[0]));
    }

    /// <summary>Two pages must not share one set of rows, or each would show the other's answers.</summary>
    [Fact]
    public void EachCallGetsItsOwnRows()
    {
        IReadOnlyList<ExperimentalQueryRow> first = ExperimentalQueries.Create();
        IReadOnlyList<ExperimentalQueryRow> second = ExperimentalQueries.Create();

        first[0].Result = "something";

        Assert.NotSame(first[0], second[0]);
        Assert.Null(second[0].Result);
    }

    // -------------------------------------------------------------------------- query-only

    [Fact]
    public void EveryOneIsAQuery() =>
        Assert.All(ExperimentalQueries.Create(), row => Assert.True(row.Command.IsQuery));

    [Fact]
    public void EveryOneIsTierSafe() =>
        Assert.All(ExperimentalQueries.Create(), row => Assert.Equal(SafetyTier.Safe, row.Command.Tier));

    [Fact]
    public void EveryOneTakesNoParameter() =>
        Assert.All(ExperimentalQueries.Create(), row => Assert.Empty(row.Command.Parameters));

    /// <summary>
    /// A row cannot be built over anything else, which is what keeps the card's list and §8.5's the
    /// same list even if someone binds it to a different source.
    /// </summary>
    [Fact]
    public void ARowRefusesACommandThatIsNotExperimental()
    {
        ScpiCommand ordinary = CommandCatalog.Find("*IDN?")!;

        ArgumentException thrown = Assert.Throws<ArgumentException>(() => new ExperimentalQueryRow(ordinary));

        Assert.Contains("§8.5", thrown.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ARowRefusesNull() =>
        Assert.Throws<ArgumentNullException>(() => new ExperimentalQueryRow(null!));

    /// <summary>
    /// §8.4 keeps the set forms of these nodes out of the catalog entirely, so <c>Experimental</c>
    /// cannot contain one. Asserted over the property rather than over a list of names.
    /// </summary>
    [Fact]
    public void NoneOfThemSetsAnything() =>
        Assert.All(
            CommandCatalog.Experimental,
            command =>
            {
                Assert.True(command.IsQuery, $"{command.Mnemonic} is not a query.");
                Assert.EndsWith("?", command.Mnemonic, StringComparison.Ordinal);
                Assert.Null(command.ConfirmationText);
            });

    // ------------------------------------------------------------------------------ the row state

    [Fact]
    public void ARowStartsWithNothingToShow()
    {
        ExperimentalQueryRow row = ExperimentalQueries.Create()[0];

        Assert.False(row.HasResult);
        Assert.False(row.IsError);
        Assert.True(row.CanRun);
    }

    [Fact]
    public void ARunningRowCannotBeRunAgain()
    {
        ExperimentalQueryRow row = ExperimentalQueries.Create()[0];

        row.IsBusy = true;

        Assert.False(row.CanRun);
    }

    [Fact]
    public void SettingAResultAnnouncesBothItAndWhetherThereIsOne()
    {
        ExperimentalQueryRow row = ExperimentalQueries.Create()[0];
        List<string?> changed = [];
        row.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        row.Result = "+1.23";

        Assert.Contains(nameof(ExperimentalQueryRow.Result), changed);
        Assert.Contains(nameof(ExperimentalQueryRow.HasResult), changed);
        Assert.True(row.HasResult);
    }

    /// <summary>
    /// An error is a fact about the row, not only a colour. §9.4.3 and A11Y-12 forbid carrying the
    /// distinction in hue alone, and an error from an undocumented node reads exactly like a short
    /// answer otherwise.
    /// </summary>
    [Fact]
    public void AnErrorIsRecordedAsWellAsShown()
    {
        ExperimentalQueryRow row = ExperimentalQueries.Create()[0];

        row.IsError = true;
        row.Result = "The receiver answered E-113.";

        Assert.True(row.IsError);
        Assert.True(row.HasResult);
    }

    // ------------------------------------------------------------------------------- the opt-in

    [Fact]
    public void TheOptInIsOffOnAFreshInstall() =>
        Assert.False(AdvancedPreferences.Default.AreExperimentalQueriesEnabled);

    /// <summary>
    /// The two Advanced switches are independent, and the record is written whole. Saving one field
    /// while constructing the other from the default would silently turn the other one off, which is
    /// exactly the bug this asserts against.
    /// </summary>
    [Fact]
    public void TheTwoAdvancedSwitchesDoNotTurnEachOtherOff()
    {
        string path = Path.Combine(_folder, "advanced.json");
        LocalAdvancedPreferenceStore store = new(path);

        store.Save(new AdvancedPreferences { IsConsoleEnabled = true, AreExperimentalQueriesEnabled = true });

        AdvancedPreferences read = new LocalAdvancedPreferenceStore(path).Load();

        Assert.True(read.IsConsoleEnabled);
        Assert.True(read.AreExperimentalQueriesEnabled);
    }

    [Fact]
    public void EitherCanBeOnWithoutTheOther()
    {
        string path = Path.Combine(_folder, "one.json");

        new LocalAdvancedPreferenceStore(path).Save(
            new AdvancedPreferences { AreExperimentalQueriesEnabled = true });

        AdvancedPreferences read = new LocalAdvancedPreferenceStore(path).Load();

        Assert.False(read.IsConsoleEnabled);
        Assert.True(read.AreExperimentalQueriesEnabled);
    }

    /// <summary>A corrupt file leaves both off, which is the safe direction for an opt-in.</summary>
    [Fact]
    public void ACorruptFileLeavesBothOff()
    {
        string path = Path.Combine(_folder, "corrupt.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ not json");

        AdvancedPreferences read = new LocalAdvancedPreferenceStore(path).Load();

        Assert.False(read.IsConsoleEnabled);
        Assert.False(read.AreExperimentalQueriesEnabled);
    }
}
