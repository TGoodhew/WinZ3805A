using WinZ3805A.Device.Commands;

namespace WinZ3805A.Tests.Commands;

/// <summary>
/// Asserts the safety model holds (§8, P0-6, P0-7).
/// </summary>
/// <remarks>
/// The tests that matter here are the negative ones. A catalog with a missing entry is a feature
/// that does not work; a catalog with an entry it should not have is a receiver someone bricks.
/// </remarks>
public class CommandCatalogTests
{
    // -------------------------------------------------------------------------------------------
    // The allowlist invariants (P0-6, P0-7)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// P0-6's stated acceptance criterion. Written against <c>IsBlocked</c> rather than against the
    /// pattern list, because the list is deliberately not reachable from outside the assembly —
    /// which is itself the §8.4 requirement.
    /// </summary>
    [Fact]
    public void NoCatalogedCommandIsOneTheExclusionsCover()
    {
        Assert.All(CommandCatalog.All, command =>
        {
            Assert.False(CommandCatalog.IsBlocked(command.Mnemonic), $"{command.Mnemonic} is excluded by §8.4.");
            Assert.False(CommandCatalog.IsBlocked(command.ShortForm), $"{command.ShortForm} is excluded by §8.4.");
        });
    }

    /// <summary>
    /// §8.1: blocked commands are not entries carrying a flag, they are absent. So the tier exists
    /// as a value and is never used as data.
    /// </summary>
    [Fact]
    public void NoCatalogEntryCarriesTheBlockedTier()
    {
        Assert.DoesNotContain(CommandCatalog.All, command => command.Tier == SafetyTier.Blocked);
    }

    /// <summary>
    /// Nothing an excluded command is named after may leak through display text either. A picker,
    /// an autocomplete, or a tooltip built from these strings is a §8.4 breach just as much as an
    /// entry would be.
    /// </summary>
    [Fact]
    public void NoDisplayTextNamesAnExcludedCommand()
    {
        Assert.All(CommandCatalog.All, command =>
        {
            Assert.False(CommandCatalog.IsBlocked(command.DisplayName));
            Assert.False(CommandCatalog.IsBlocked(command.Description));
            Assert.False(CommandCatalog.IsBlocked(command.ConfirmationText));
        });
    }

    [Fact]
    public void EveryCommandIsReachableByBothItsForms()
    {
        Assert.All(CommandCatalog.All, command =>
        {
            Assert.Same(command, CommandCatalog.Find(command.Mnemonic));
            Assert.Same(command, CommandCatalog.Find(command.ShortForm));
        });
    }

    [Fact]
    public void LookupIgnoresCase()
    {
        Assert.NotNull(CommandCatalog.Find(":syst:stat?"));
        Assert.NotNull(CommandCatalog.Find(":SYST:STAT?"));
    }

    [Fact]
    public void AnUncatalogedStringIsNotFound()
    {
        Assert.Null(CommandCatalog.Find(":SYST:NOSUCHTHING?"));
        Assert.Null(CommandCatalog.Find(null));
        Assert.Null(CommandCatalog.Find(""));
        Assert.False(CommandCatalog.Contains("   "));
    }

    /// <summary>Two commands sharing a mnemonic would make one of them unreachable by name.</summary>
    [Fact]
    public void NoTwoCommandsShareAMnemonic()
    {
        List<string> duplicates = CommandCatalog.All
            .GroupBy(c => c.Mnemonic, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        Assert.Empty(duplicates);
    }

    // -------------------------------------------------------------------------------------------
    // Tier assignment (§8.2, §8.3)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// A sample across every subsystem §8.2 lists. Not exhaustive by design — the exhaustive claim
    /// is made by the two structural tests below, which no future entry can quietly slip past.
    /// </summary>
    [Theory]
    [InlineData("*IDN?")]
    [InlineData("*CLS")]
    [InlineData("*STB?")]
    [InlineData(":SYST:STAT?")]
    [InlineData(":SYST:ERR?")]
    [InlineData(":SYNC:STAT?")]
    [InlineData(":SYNC:TINT?")]
    [InlineData(":SYNC:HOLD:DUR?")]
    [InlineData(":GPS:REF:ADEL?")]
    [InlineData(":GPS:POS:SURV:PROG?")]
    [InlineData(":GPS:SAT:TRAC:COUN?")]
    [InlineData(":PTIM:LEAP:STAT?")]
    [InlineData(":LED:GPSL?")]
    [InlineData(":DIAG:ROSC:EFC:REL?")]
    [InlineData(":DIAG:LOG:READ:ALL?")]
    [InlineData(":STAT:OPER:COND?")]
    [InlineData(":STAT:QUES:PTR?")]
    [InlineData(":STAT:OPER:HOLD:EVEN?")]
    public void EveryListedSafeCommandIsCatalogedAsSafe(string mnemonic)
    {
        ScpiCommand? command = CommandCatalog.Find(mnemonic);

        Assert.NotNull(command);
        Assert.Equal(SafetyTier.Safe, command.Tier);
        Assert.Null(command.ConfirmationText);
    }

    /// <summary>
    /// The two recovery actions §8.2 singles out. They are actions rather than queries and are
    /// still Safe, because they move the unit toward lock and cannot damage anything — the one
    /// place in the model where "not a query" does not imply "needs confirming".
    /// </summary>
    [Theory]
    [InlineData(":SYNC:HOLD:REC:INIT")]
    [InlineData(":SYNC:HOLD:REC:LIM:IGN")]
    public void TheRecoveryActionsAreSafeDespiteNotBeingQueries(string mnemonic)
    {
        ScpiCommand? command = CommandCatalog.Find(mnemonic);

        Assert.NotNull(command);
        Assert.Equal(SafetyTier.Safe, command.Tier);
        Assert.False(command.IsQuery);
    }

    [Theory]
    [InlineData(":SYST:PRESet")]
    [InlineData(":SYST:COMM:SER1:BAUD")]
    [InlineData(":SYST:COMM:SER1:FDUPlex")]
    [InlineData(":SYST:COMM:SER1:PRESet")]
    [InlineData(":GPS:REF:ADELay")]
    [InlineData(":GPS:POSition")]
    [InlineData(":GPS:POSition LAST")]
    [InlineData(":GPS:POSition SURVey")]
    [InlineData(":GPS:POSition:SURVey:STATe ONCE")]
    [InlineData(":GPS:POS:SURV:STAT:POWerup")]
    [InlineData(":GPS:INIT:DATE")]
    [InlineData(":GPS:INIT:POSition")]
    [InlineData(":GPS:SAT:TRAC:EMANgle")]
    [InlineData(":GPS:SAT:TRAC:IGNore")]
    [InlineData(":GPS:SAT:TRAC:IGNore ALL")]
    [InlineData(":GPS:SAT:TRAC:INCLude NONE")]
    [InlineData(":SYNC:HOLDover:INITiate")]
    [InlineData(":SYNC:HOLD:DUR:THReshold")]
    [InlineData(":SYNC:IMMediate")]
    [InlineData(":PTIM:TZONe")]
    [InlineData(":DIAG:LOG:CLEar")]
    [InlineData(":STAT:PRESet:ALARm")]
    [InlineData(":STAT:QUES:COND:USER")]
    [InlineData(":STAT:OPER:ENABle")]
    [InlineData("*ESE")]
    [InlineData("*SRE")]
    [InlineData("*TST?")]
    [InlineData(":DIAG:TEST?")]
    public void EveryListedConfirmCommandIsCatalogedAsConfirm(string mnemonic)
    {
        ScpiCommand? command = CommandCatalog.Find(mnemonic);

        Assert.NotNull(command);
        Assert.Equal(SafetyTier.Confirm, command.Tier);
    }

    /// <summary>
    /// The structural half of the tier claim: a tier C entry without consequence text would put a
    /// dialog on screen with nothing in it, which is worse than no dialog because it trains the
    /// user to click through.
    /// </summary>
    [Fact]
    public void EveryConfirmCommandCarriesConsequenceText()
    {
        Assert.All(CommandCatalog.Confirm, command =>
        {
            Assert.Equal(SafetyTier.Confirm, command.Tier);
            Assert.False(string.IsNullOrWhiteSpace(command.ConfirmationText));
        });
    }

    /// <summary>The converse: a Safe command must not carry text implying it will ask first.</summary>
    [Fact]
    public void NoSafeCommandCarriesConsequenceText()
    {
        Assert.All(CommandCatalog.Safe, command => Assert.Null(command.ConfirmationText));
    }

    /// <summary>
    /// §9.7.4 names exactly four commands whose confirm button is gated behind a checkbox. All four
    /// lose lock, lose settings, or corrupt oscillator learning.
    /// </summary>
    [Fact]
    public void ExactlyTheFourStrongVariantsRequireAcknowledgement()
    {
        string[] expected =
        [
            ":GPS:SAT:TRAC:IGNore ALL",
            ":GPS:SAT:TRAC:INCLude NONE",
            ":SYNC:HOLDover:INITiate",
            ":SYST:PRESet",
        ];

        string[] actual = CommandCatalog.All
            .Where(c => c.RequiresAcknowledgement)
            .Select(c => c.Mnemonic)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    /// <summary>Confirmation text with a value in it must have somewhere to put the value.</summary>
    [Fact]
    public void ConfirmationTextWithAPlaceholderHasAParameterToFillIt()
    {
        Assert.All(
            CommandCatalog.Confirm.Where(c => c.ConfirmationText!.Contains("{0}", StringComparison.Ordinal)),
            command => Assert.NotEmpty(command.Parameters));
    }

    /// <summary>
    /// §9.11 gives every tier C command a success line, because every tier C command is
    /// consequential by definition — that is what put it in tier C. A missing one would leave the
    /// user having confirmed something destructive and then told nothing at all.
    /// </summary>
    [Fact]
    public void EveryConfirmCommandCarriesASuccessSentence()
    {
        Assert.All(
            CommandCatalog.Confirm,
            command => Assert.False(string.IsNullOrWhiteSpace(command.SuccessText)));
    }

    /// <summary>
    /// The converse, and the same reasoning as the confirmation text: §9.11 gives a routine success
    /// no UI at all, so a safe command with a success line is one that would produce a toast the
    /// specification says not to show.
    /// </summary>
    [Fact]
    public void NoSafeCommandCarriesASuccessSentence()
    {
        Assert.All(CommandCatalog.Safe, command => Assert.Null(command.SuccessText));
    }

    /// <summary>A success line with a value in it needs somewhere for the value to come from.</summary>
    [Fact]
    public void SuccessTextWithAPlaceholderHasAParameterToFillIt()
    {
        Assert.All(
            CommandCatalog.Confirm.Where(c => c.SuccessText!.Contains("{0}", StringComparison.Ordinal)),
            command => Assert.NotEmpty(command.Parameters));
    }

    /// <summary>
    /// §9.11: a verb keeps its identity end to end. The success line is past tense and the display
    /// name is imperative, so they cannot be compared word for word — but a success line that
    /// shares no significant word with its own button is one that drifted.
    /// </summary>
    [Fact]
    public void EverySuccessSentenceSharesAWordWithItsButton()
    {
        Assert.All(CommandCatalog.Confirm, command =>
        {
            HashSet<string> success = Significant(command.SuccessText!);
            Assert.True(
                Significant(command.DisplayName).Any(word => success.Contains(word)),
                $"{command.Mnemonic}: \"{command.DisplayName}\" and \"{command.SuccessText}\" share no word.");
        });

        // Stems rather than whole words, so "Clear" matches "Cleared" and "Adopt" matches "Adopted"
        // without a stemmer: four characters is enough to separate these verbs from each other and
        // short enough to survive an -ed or an -s.
        static HashSet<string> Significant(string text) =>
            text.Split([' ', ',', '.', '?', '—', '-'], StringSplitOptions.RemoveEmptyEntries)
                .Where(word => word.Length >= 4)
                .Select(word => word[..4].ToLowerInvariant())
                .ToHashSet(StringComparer.Ordinal);
    }

    // -------------------------------------------------------------------------------------------
    // §8.5 — opt-in queries
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// §8.5's list is fixed and query-only: "no free-text entry into it". A set form appearing here
    /// would be an undocumented write to a receiver, which §8.4 blocks outright.
    /// </summary>
    [Fact]
    public void EveryExperimentalCommandIsAQueryAndTheListIsExactlyTheSpecifiedSix()
    {
        Assert.Equal(6, CommandCatalog.Experimental.Count);

        Assert.All(CommandCatalog.Experimental, command =>
        {
            Assert.True(command.IsQuery);
            Assert.EndsWith("?", command.Mnemonic, StringComparison.Ordinal);
            Assert.Equal(SafetyTier.Safe, command.Tier);
            Assert.Empty(command.Parameters);
        });
    }

    /// <summary>Opt-in queries are not in the everyday list, or the opt-in would mean nothing.</summary>
    [Fact]
    public void ExperimentalQueriesAreNotPartOfTheSafeList()
    {
        Assert.DoesNotContain(CommandCatalog.Safe, command => command.IsExperimental);
        Assert.Equal(CommandCatalog.All.Count, CommandCatalog.Safe.Count + CommandCatalog.Confirm.Count + CommandCatalog.Experimental.Count);
    }

    // -------------------------------------------------------------------------------------------
    // The validator (§8.4)
    // -------------------------------------------------------------------------------------------

    /// <summary>
    /// The interaction §8.4 and §8.5 create between them: an undocumented node is permanently
    /// blocked in set form and may be readable in query form. Getting this backwards either bricks
    /// a receiver or removes a feature the specification asks for.
    /// </summary>
    [Fact]
    public void AnUndocumentedNodeIsBlockedAsASetterAndAllowedAsAQuery()
    {
        ScpiCommand query = Assert.Single(
            CommandCatalog.Experimental,
            c => c.Mnemonic.EndsWith(":TCOefficient?", StringComparison.OrdinalIgnoreCase));

        // The query form is catalogued, and the validator lets it through.
        Assert.False(CommandCatalog.IsBlocked(query.Mnemonic));

        // The same node without the question mark is a set form, and is refused.
        string setter = query.Mnemonic.TrimEnd('?');
        Assert.True(CommandCatalog.IsBlocked(setter));
        Assert.Null(CommandCatalog.Find(setter));
    }

    /// <summary>An excluded command with an argument is still excluded.</summary>
    [Fact]
    public void ParametersDoNotSmuggleAnExcludedCommandPastTheValidator()
    {
        ScpiCommand query = Assert.Single(
            CommandCatalog.Experimental,
            c => c.Mnemonic.EndsWith(":TCOefficient?", StringComparison.OrdinalIgnoreCase));

        string setter = query.Mnemonic.TrimEnd('?');

        Assert.True(CommandCatalog.IsBlocked($"  {setter}  "));
        Assert.True(CommandCatalog.IsBlocked($"{setter} 42"));
        Assert.True(CommandCatalog.IsBlocked(setter.ToLowerInvariant()));
    }

    [Fact]
    public void OrdinaryCommandsAreNotBlocked()
    {
        Assert.False(CommandCatalog.IsBlocked(":SYST:STAT?"));
        Assert.False(CommandCatalog.IsBlocked("*IDN?"));
        Assert.False(CommandCatalog.IsBlocked(null));
        Assert.False(CommandCatalog.IsBlocked(""));
        Assert.False(CommandCatalog.IsBlocked("   "));
    }

    // -------------------------------------------------------------------------------------------
    // Short forms
    // -------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(":GPS:SAT:TRAC:EMANgle", ":GPS:SAT:TRAC:EMAN")]
    [InlineData(":SYNC:HOLDover:INITiate", ":SYNC:HOLD:INIT")]
    [InlineData(":SYST:PRESet", ":SYST:PRES")]
    [InlineData(":PTIM:TZONe", ":PTIM:TZON")]
    [InlineData(":DIAG:LOG:CLEar", ":DIAG:LOG:CLE")]
    [InlineData(":SYST:STAT?", ":SYST:STAT?")]
    [InlineData("*IDN?", "*IDN?")]
    [InlineData(":GPS:POSition LAST", ":GPS:POS LAST")]
    public void ShortFormDropsTheLowerCaseTailOfEachNode(string mnemonic, string expected)
    {
        Assert.Equal(expected, ScpiCommand.ToShortForm(mnemonic));
    }

    /// <summary>
    /// Sanity on the catalog as a whole: §8.2 lists around ninety commands and §8.3 around forty
    /// once the grids are expanded, so a catalog that collapsed to a handful means a builder threw
    /// and was swallowed somewhere.
    /// </summary>
    [Fact]
    public void TheCatalogIsFullyPopulated()
    {
        Assert.True(CommandCatalog.Safe.Count >= 80, $"Only {CommandCatalog.Safe.Count} safe commands.");
        Assert.True(CommandCatalog.Confirm.Count >= 40, $"Only {CommandCatalog.Confirm.Count} confirm commands.");
    }
}
