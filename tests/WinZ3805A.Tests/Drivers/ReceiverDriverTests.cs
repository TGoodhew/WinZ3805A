using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Drivers;
using WinZ3805A.Device.Drivers.Nmea;
using WinZ3805A.Device.Models;

namespace WinZ3805A.Tests.Drivers;

/// <summary>
/// The driver seam (#122, completed by #287) — what a second receiver family has to satisfy.
/// </summary>
/// <remarks>
/// <para>
/// These are the contract's tests rather than the SmartClock implementation's. Everything the
/// SmartClock driver delegates to is already covered where it lives; what is checked here is that
/// the seam preserves the properties a new driver could otherwise break — above all §8.4's, where
/// the wrong abstraction is a safety defect and not a missing feature.
/// </para>
/// <para>
/// <b>Since #287 the family-agnostic half runs against every driver</b>, including
/// <see cref="FakeReceiverDriver"/> — a second family that exists so the contract is exercised
/// against something that is not the SmartClock. A rule that only one implementation has ever met
/// is a description, not a contract.
/// </para>
/// </remarks>
public class ReceiverDriverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 29, 4, 0, 0, TimeSpan.Zero);

    private static SmartClockDriver Driver() => new(new FakeTimeProvider(Now));

    /// <summary>Every driver the contract binds: the two real families and the test-only one.</summary>
    public static TheoryData<IReceiverDriver> AllDrivers => new()
    {
        new SmartClockDriver(new FakeTimeProvider(Now)),
        new FakeReceiverDriver(),
        new NmeaDriver(new FakeTimeProvider(Now)),
    };

    [Fact]
    public void TheDriverNamesItsFamily() => Assert.Equal("SmartClock", Driver().Family);

    // ---- §8.4, the part where a wrong abstraction is a safety bug -----------------------------

    /// <summary>
    /// The interface exposes a verdict and never the patterns.
    /// </summary>
    /// <remarks>
    /// §8.4 requires that excluded commands do not exist as data a view can enumerate. A driver
    /// returning a list would put them back. This is asserted against the <b>interface</b> by
    /// reflection rather than by reading the source — and honestly: reflection cannot prove a
    /// semantic property, so this is a tripwire against the likely shapes of the mistake (a
    /// member named after blocking, a member whose type involves <see cref="System.Text.RegularExpressions.Regex"/>,
    /// a non-bool verdict), not a proof that no member could smuggle the list. The review that
    /// adds a member to this interface is where the real §8.4 judgement happens; this test makes
    /// sure that review is prompted.
    /// </remarks>
    [Fact]
    public void TheDriverContractCannotExposeTheExclusionsAsData()
    {
        Type contract = typeof(IReceiverDriver);

        Assert.DoesNotContain(
            contract.GetProperties(),
            p => p.Name.Contains("Block", StringComparison.OrdinalIgnoreCase));

        System.Reflection.MethodInfo blocked = contract.GetMethod(nameof(IReceiverDriver.IsBlocked))!;
        Assert.Equal(typeof(bool), blocked.ReturnType);

        // No member of the contract traffics in regular expressions at all — the patterns' own
        // type appearing anywhere in a signature would be the clearest smuggling route.
        foreach (System.Reflection.MethodInfo method in contract.GetMethods())
        {
            Assert.DoesNotContain("Regex", method.ReturnType.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.All(
                method.GetParameters(),
                parameter => Assert.DoesNotContain(
                    "Regex", parameter.ParameterType.ToString(), StringComparison.OrdinalIgnoreCase));
        }
    }

    [Fact]
    public void AnExcludedCommandIsRejectedThroughTheDriver()
    {
        // The token is taken from the catalog's own validator rather than written here: §8.4 forbids
        // naming these anywhere, and a test fixture is a where.
        string? excluded = FindAnExcludedHeader();
        Assert.NotNull(excluded);

        Assert.True(Driver().IsBlocked(excluded));
    }

    /// <summary>
    /// The exclusions are each driver's own, not a shared list (#287).
    /// </summary>
    /// <remarks>
    /// The interface documentation says inheriting another family's exclusions is not a
    /// conservative default, and this is the proof by counterexample: the same header gets opposite
    /// verdicts from the two drivers, in both directions. The SmartClock's excluded header is
    /// discovered rather than written, per §8.4; the Acme one is fictional and names no real
    /// command.
    /// </remarks>
    [Fact]
    public void EachDriverAnswersForItsOwnExclusions()
    {
        string? smartClockExcluded = FindAnExcludedHeader();
        Assert.NotNull(smartClockExcluded);

        FakeReceiverDriver acme = new();

        Assert.False(acme.IsBlocked(smartClockExcluded));
        Assert.True(acme.IsBlocked(":ACME:ZAP"));
        Assert.False(Driver().IsBlocked(":ACME:ZAP"));
    }

    [Theory]
    [InlineData(":SYST:STAT?")]
    [InlineData(":PTIM:TCOD?")]
    [InlineData("*IDN?")]
    [InlineData(":GPS:SAT:TRAC:COUN?")]
    public void OrdinaryCommandsAreNotBlocked(string header) => Assert.False(Driver().IsBlocked(header));

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void ABlankHeaderIsNotBlockedAndDoesNotThrow(IReceiverDriver driver)
    {
        Assert.False(driver.IsBlocked(""));
        Assert.False(driver.IsBlocked("   "));
        Assert.False(driver.IsBlocked(null));
    }

    // ---- The allowlist ------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void TheCommandsAreAnAllowlistWithContent(IReceiverDriver driver)
    {
        Assert.NotEmpty(driver.Commands);
        Assert.All(driver.Commands, c => Assert.False(string.IsNullOrWhiteSpace(c.Mnemonic)));
    }

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void NoCommandInTheAllowlistIsAlsoExcluded(IReceiverDriver driver) =>
        // The two rules must not contradict each other. A command both offered and blocked would
        // mean the catalog and §8.4 disagree about the same receiver.
        Assert.All(driver.Commands, c => Assert.False(driver.IsBlocked(c.Mnemonic)));

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void AnUnknownMnemonicIsNotFound(IReceiverDriver driver) =>
        Assert.Null(driver.Find(":NO:SUCH:COMMAND?"));

    /// <summary>
    /// Every driver's catalog carries IEEE 488.2's error query.
    /// </summary>
    /// <remarks>
    /// <c>CommandInvoker</c> reads <c>:SYST:ERR?</c> after every tier C command (§7.2) and throws
    /// if the driver cannot supply it, so this is a contract requirement rather than a SmartClock
    /// habit — <b>for a family that can be sent a tier C command at all.</b> #310's talker cannot
    /// be sent anything: its link is broadcast, its catalog is reads only, and the invoker never
    /// runs for it. The requirement therefore binds query/response families, which is what the
    /// invoker serves; a broadcast family is exempt by construction rather than by exception.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void TheErrorQueueQueryIsCatalogued(IReceiverDriver driver)
    {
        if (driver.Link == LinkStyle.Broadcast)
        {
            Assert.DoesNotContain(driver.Commands, command => command.Tier == SafetyTier.Confirm);
            return;
        }

        Assert.NotNull(driver.Find(":SYST:ERR?"));
    }

    // ---- #310: the members a broadcast family adds -------------------------------------------

    /// <summary>Hearing nothing claims nothing, for every driver.</summary>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void OverhearingNothingClaimsNothing(IReceiverDriver driver) =>
        Assert.Null(driver.Overhear([]));

    /// <summary>What a wrong baud rate sounds like must not throw, or claim.</summary>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void OverhearingNoiseNeverThrowsOrClaims(IReceiverDriver driver)
    {
        string[] noise = ["\0ÿþ$", "$", "$GP", "$GPGGA,*ZZ", "scpi > ", "SYMMETRICOM,Z3805A,3625A02931,1.01.03-A", "   ", "*IDN?"];

        Assert.Null(driver.Overhear(noise));
        foreach (string line in noise)
        {
            Assert.Null(driver.ClassifyLine(line));
        }
    }

    /// <summary>A query/response family does not claim a talker — the SmartClock has no business with NMEA.</summary>
    /// <remarks>
    /// The line is built by the codec rather than typed. It was typed once, with the checksum
    /// worked out by hand, and the checksum was wrong — the sentence this test fed a driver was
    /// one no talker could have sent. It passed anyway, because a query/response driver refuses
    /// every line whatever its checksum, so the defect could only ever be found by reading it
    /// (#319, from the #316 audit). That is the tutorial's finding 3, one file over.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void AQueryResponseDriverHearsNoOne(IReceiverDriver driver)
    {
        if (driver.Link != LinkStyle.QueryResponse)
        {
            return;
        }

        string rmc = NmeaSentence.Format(
            "GP", "RMC", "120000.00", "A", "4737.2300", "N", "12220.9580", "W", "0.0", "0.0", "290826", null, null);

        Assert.Null(driver.Overhear([rmc]));
        Assert.Null(driver.ClassifyLine(rmc));
    }

    /// <summary>
    /// A broadcast family's cycle boundary is its first fast-tier entry, and the whole-cycle key
    /// must be catalogued if the plan names it, because the session's allowlist check does not
    /// know it is special.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void ABroadcastPlanIsShapedForTheListener(IReceiverDriver driver)
    {
        if (driver.Link != LinkStyle.Broadcast)
        {
            return;
        }

        Assert.NotEmpty(driver.Plan.FastTier);
        Assert.Null(driver.Plan.RefusableIndex);
        Assert.NotNull(driver.Find(driver.Plan.FullStatus));
    }

    /// <summary>
    /// Every mnemonic the pages resolve at a click is in the SmartClock catalog.
    /// </summary>
    /// <remarks>
    /// #287 replaced the pages' static readonly command fields — which failed at
    /// type-initialisation — with lookups at the point of use, which fail at the click. This test
    /// is where the early loudness went: a mnemonic typo'd in a page, or a command dropped from the
    /// catalog while a page still needs it, fails the test run instead of the running application.
    /// The list must be kept in step with the pages' <c>CommandConfirmation.Require</c> calls.
    /// </remarks>
    [Theory]
    [InlineData(":DIAG:LOG:CLEar")]
    [InlineData(":DIAG:TEST?")]
    [InlineData(":SYNC:HOLDover:INITiate")]
    [InlineData(":SYNC:HOLD:REC:INIT")]
    [InlineData(":SYNC:HOLD:REC:LIM:IGN")]
    [InlineData(":SYNC:HOLD:DUR:THReshold")]
    [InlineData("*TST?")]
    [InlineData(":GPS:POSition:SURVey:STATe ONCE")]
    [InlineData(":GPS:POSition SURVey")]
    [InlineData(":GPS:POSition LAST")]
    [InlineData(":GPS:POS:SURV:STAT:POWerup")]
    [InlineData(":GPS:POSition")]
    [InlineData(":GPS:POS:SURV:STAT:POW?")]
    [InlineData(":GPS:SAT:TRAC:INCLude")]
    [InlineData(":GPS:SAT:TRAC:INCLude ALL")]
    [InlineData(":GPS:SAT:TRAC:INCLude NONE")]
    [InlineData(":GPS:SAT:TRAC:IGNore ALL")]
    [InlineData(":GPS:SAT:TRAC:IGNore NONE")]
    [InlineData(":GPS:SAT:TRAC:EMANgle")]
    [InlineData(":GPS:REF:ADELay")]
    [InlineData(":SYST:ERR?")]
    public void EveryMnemonicThePagesRequireIsCatalogued(string mnemonic) =>
        Assert.NotNull(Driver().Find(mnemonic));

    /// <summary>
    /// The three commands whose parameter specs feed field validators actually have one.
    /// </summary>
    /// <remarks>
    /// Holdover's threshold, the elevation mask and the antenna delay each build a
    /// <c>NumberFieldValidator</c> from <c>Parameters[0]</c> in <c>OnNavigatedTo</c>. An entry that
    /// lost its parameter would turn a navigation into an <c>IndexOutOfRangeException</c>.
    /// </remarks>
    [Theory]
    [InlineData(":SYNC:HOLD:DUR:THReshold")]
    [InlineData(":GPS:SAT:TRAC:EMANgle")]
    [InlineData(":GPS:REF:ADELay")]
    public void TheValidatorBackedCommandsCarryAParameterSpec(string mnemonic) =>
        Assert.NotEmpty(Driver().Find(mnemonic)!.Parameters);

    // ---- Timeouts and cadence -----------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void EveryTimeoutIsPositiveAndBounded(IReceiverDriver driver)
    {
        foreach (ScpiCommand command in driver.Commands)
        {
            TimeSpan timeout = driver.TimeoutFor(command.Mnemonic);

            Assert.True(timeout > TimeSpan.Zero, $"{command.Mnemonic} has a non-positive timeout");
            Assert.True(timeout <= TimeSpan.FromMinutes(2), $"{command.Mnemonic} waits over two minutes");
        }
    }

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void AnUnknownMnemonicStillGetsATimeout(IReceiverDriver driver)
    {
        // The poll loop must never be handed TimeSpan.Zero, which would fail every transaction
        // instantly rather than waiting for one.
        Assert.True(driver.TimeoutFor(":NO:SUCH:COMMAND?") > TimeSpan.Zero);
        Assert.True(driver.TimeoutFor(null) > TimeSpan.Zero);
    }

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void TheFullSweepIsSlowerThanTheFastOne(IReceiverDriver driver)
    {
        // The full status screen takes 3521 ms of wire time on the bench receiver. A cadence with
        // the two the same way round would queue sweeps behind each other forever.
        Assert.True(driver.Cadence.Fast > TimeSpan.Zero);
        Assert.True(driver.Cadence.Full > driver.Cadence.Fast);
    }

    // ---- The poll plan (#287) -----------------------------------------------------------------

    /// <summary>
    /// The plan sweeps something, and everything it names is in the driver's own catalog.
    /// </summary>
    /// <remarks>
    /// The poller warns and skips when a plan entry does not resolve, so a driver whose plan and
    /// catalog disagree polls nothing while looking configured. This is the test the poller's
    /// warning comment points at.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void ThePlanResolvesThroughTheDriversOwnCatalog(IReceiverDriver driver)
    {
        Assert.NotEmpty(driver.Plan.FastTier);
        Assert.All(driver.Plan.FastTier, mnemonic => Assert.NotNull(driver.Find(mnemonic)));
        Assert.NotNull(driver.Find(driver.Plan.FullStatus));
    }

    /// <summary>
    /// The refusable index, when there is one, points inside the sweep and never at the
    /// discriminator.
    /// </summary>
    /// <remarks>
    /// The first entry is read unconditionally — its answer is what the suppression keys on — so a
    /// refusable index of zero would suppress the one query the mechanism cannot work without.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void TheRefusableIndexPointsInsideTheSweep(IReceiverDriver driver)
    {
        if (driver.Plan.RefusableIndex is int index)
        {
            Assert.InRange(index, 1, driver.Plan.FastTier.Count - 1);
        }
    }

    /// <summary>
    /// Interpreting a sweep never throws, whatever shape the answers arrive in.
    /// </summary>
    /// <remarks>
    /// §11.1's rule applied to the fast tier: the poll loop calls this once a second, and a driver
    /// that throws on a torn sweep takes the loop down. Every shape here occurs in practice — a
    /// dropped link mid-sweep hands the driver a list of nulls, and a reconnect under a shorter
    /// plan is how a list can be the wrong length.
    /// </remarks>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void InterpretingAnySweepShapeNeverThrows(IReceiverDriver driver)
    {
        int width = driver.Plan.FastTier.Count;

        foreach (IReadOnlyList<string?> answers in new IReadOnlyList<string?>[]
        {
            [],
            new string?[width],
            new string?[width + 3],
            ["\0[2J", "not a number", null, "", "   ", "E-350"],
        })
        {
            SweepInterpretation sweep = driver.InterpretSweep(answers);

            Assert.NotNull(sweep);
            Assert.NotNull(sweep.Readings);
        }
    }

    /// <summary>
    /// A sweep that is not the driver's own comes back rejected, with a reason and with what was
    /// read still attached.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void AForeignSweepIsRejectedWithAReason(IReceiverDriver driver)
    {
        SweepInterpretation sweep = driver.InterpretSweep(["this is nobody's state token"]);

        Assert.NotNull(sweep.Rejection);
        Assert.NotNull(sweep.Readings);
    }

    // ---- The SmartClock sweep, specifically ---------------------------------------------------

    /// <summary>
    /// §7.3's six answers, read into the six fields, in the order the specification lists them.
    /// </summary>
    [Fact]
    public void AGoodSmartClockSweepReadsEveryField()
    {
        // Leading spaces faithful to the unit: §7.2 says responses carry one.
        SweepInterpretation sweep = Driver().InterpretSweep(
            [" LOCK", " +3", " 0", " -5.4E-009", " +1.2", " 7"]);

        Assert.Null(sweep.Rejection);
        Assert.Equal("LOCK", sweep.Readings.SyncState);
        Assert.Equal(3, sweep.Readings.Tfom);
        Assert.Equal(0, sweep.Readings.Ffom);
        Assert.NotNull(sweep.Readings.TimeIntervalNanoseconds);
        Assert.Equal(-5.4, sweep.Readings.TimeIntervalNanoseconds!.Value, precision: 6);
        Assert.Equal(1.2, sweep.Readings.EfcPercent!.Value, precision: 6);
        Assert.Equal(7, sweep.Readings.SatellitesTracked);
    }

    /// <summary>
    /// A short sweep leaves the unanswered fields absent rather than zero.
    /// </summary>
    [Fact]
    public void AShortSmartClockSweepLeavesTheRestAbsent()
    {
        SweepInterpretation sweep = Driver().InterpretSweep([" HOLD", " +9"]);

        Assert.Null(sweep.Rejection);
        Assert.Equal("HOLD", sweep.Readings.SyncState);
        Assert.Equal(9, sweep.Readings.Tfom);
        Assert.Null(sweep.Readings.Ffom);
        Assert.Null(sweep.Readings.TimeIntervalNanoseconds);
        Assert.Null(sweep.Readings.SatellitesTracked);
    }

    /// <summary>
    /// Every token the sweep accepts names a mode, and every mode it names is drawn.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The closed set used to exist twice — the driver's rejection rule here, and
    /// <c>ReceiverModes.FromSyncState</c> in the app's Controls — because the library must not
    /// reference the app, and this test was the agreement between them. #304 removed the second
    /// copy instead: <see cref="IReceiverDriver.InterpretSyncState"/> is now both the mapping and
    /// the sweep's acceptance test, so those two cannot drift by construction.
    /// </para>
    /// <para>
    /// What remains worth asserting is the other seam. The driver names a mode; §9 draws it; and
    /// the six tokens listed here are §10.3's table, so a token that stopped naming a mode would
    /// silently start rejecting sweeps.
    /// </para>
    /// </remarks>
    [Theory]
    [InlineData("LOCK")]
    [InlineData("REC")]
    [InlineData("WAIT")]
    [InlineData("HOLD")]
    [InlineData("POW")]
    [InlineData("OFF")]
    public void TheDriverAndTheUiAgreeOnTheSyncVocabulary(string token)
    {
        ReceiverMode mode = Driver().InterpretSyncState(token);

        Assert.Null(Driver().InterpretSweep([token]).Rejection);
        Assert.NotEqual(ReceiverMode.Disconnected, mode);

        // The other half of the seam: the driver names a mode, §9 draws it. A mode with no label
        // would reach the medallion as an empty string rather than as a compile error.
        Assert.False(string.IsNullOrWhiteSpace(ReceiverModes.TextOf(mode)));
        Assert.False(string.IsNullOrWhiteSpace(ReceiverModes.GlyphOf(mode)));
    }

    /// <summary>
    /// A family whose receiver has never heard of <c>LOCK</c> still lights the medallion (#304).
    /// </summary>
    /// <remarks>
    /// The defect this closed, stated as a test. <see cref="FakeReceiverDriver"/>'s receiver says
    /// <c>RUN</c> and <c>IDLE</c>; while the mapping was a static table over the SmartClock's six
    /// tokens, every reading it produced rendered as <see cref="ReceiverMode.Disconnected"/> on the
    /// medallion, the tray icon and the taskbar badge — with the sweep behind it stored and trended
    /// perfectly well, which is what made it hard to see.
    /// </remarks>
    [Fact]
    public void AFamilyWithItsOwnVocabularyGetsItsOwnModes()
    {
        FakeReceiverDriver driver = new();

        Assert.Equal(ReceiverMode.Locked, driver.InterpretSyncState("RUN"));
        Assert.Equal(ReceiverMode.PowerUp, driver.InterpretSyncState("IDLE"));
        Assert.Equal(ReceiverMode.Disconnected, driver.InterpretSyncState("LOCK"));
    }

    [Fact]
    public void ATokenNeitherSideKnowsIsRejectedByBoth()
    {
        Assert.NotNull(Driver().InterpretSweep(["TRACKING"]).Rejection);
        Assert.Equal(ReceiverMode.Disconnected, Driver().InterpretSyncState("TRACKING"));
    }

    // ---- Parsing ------------------------------------------------------------------------------

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void ParsingNeverThrowsAndSaysWhyItFailed(IReceiverDriver driver)
    {
        // §11.1, and the reason the contract states it: a driver that throws takes down the poll
        // loop, which is the one failure the parser contract exists to prevent.
        foreach (string? response in new[] { null, "", "   ", "not a status screen at all", "\0[2J" })
        {
            ReceiverStatus status = driver.Parse(response);

            Assert.NotNull(status);
            Assert.NotEmpty(status.ParseWarnings);
        }
    }

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void AnUnreadableScreenLeavesFieldsAbsentRatherThanZero(IReceiverDriver driver)
    {
        // Null is the contract for "this receiver did not say", and the UI renders it as an em dash.
        // Zero would be a reading nobody took -- a 1 PPS offset of 0 ns reads as a perfect lock.
        ReceiverStatus status = driver.Parse("nonsense");

        Assert.Null(status.OnePpsTiNanoseconds);
        Assert.Null(status.Tfom);
        Assert.Null(status.DeviceDateTime);
    }

    // ---- Recognition --------------------------------------------------------------------------

    [Fact]
    public void TheFamilyIsRecognisedFromAKnownIdentity() =>
        Assert.True(Driver().Recognises(DeviceIdentity.Parse("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A")));

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void AnUnreadIdentityIsClaimedByNoDriver(IReceiverDriver driver)
    {
        // The reverse of the pre-#287 rule, deliberately. The probe phase belongs to no driver now
        // — the session reads the identity neutrally and falls back to the first registered driver
        // itself — so a driver claiming null would be claiming every receiver whose identity could
        // not be read, which is the over-claim the interface warns against.
        Assert.False(driver.Recognises(null));
    }

    [Fact]
    public void AnUnrelatedReceiverIsNotClaimed() =>
        Assert.False(Driver().Recognises(DeviceIdentity.Parse("TRIMBLE,THUNDERBOLT,0001,1.0")));

    /// <summary>
    /// The two families claim disjoint identities.
    /// </summary>
    [Fact]
    public void TheTwoFamiliesDoNotClaimEachOthersReceivers()
    {
        DeviceIdentity smartClock = DeviceIdentity.Parse("SYMMETRICOM,Z3805A,3625A02931,1.01.03-A")!;
        DeviceIdentity acme = DeviceIdentity.Parse("ACME,ONE,0001,1.0")!;

        FakeReceiverDriver fake = new();

        Assert.True(fake.Recognises(acme));
        Assert.False(fake.Recognises(smartClock));
        Assert.False(Driver().Recognises(acme));
    }

    [Theory]
    [MemberData(nameof(AllDrivers))]
    public void TheAutoDetectSequenceIsOfferedThroughTheDriver(IReceiverDriver driver) =>
        Assert.NotEmpty(driver.AutoDetectSequence);

    /// <summary>
    /// Finds one header the validator rejects, without naming any in this file.
    /// </summary>
    /// <remarks>
    /// §8.4's rule extends to test fixtures, so the token is discovered by asking the validator
    /// rather than written down. Returns null if none is found, which fails the calling test rather
    /// than passing it vacuously.
    /// </remarks>
    private static string? FindAnExcludedHeader()
    {
        foreach (string candidate in ExcludedCandidates())
        {
            if (CommandCatalog.IsBlocked(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>Headers built from the catalog's own node vocabulary, not from a written list.</summary>
    private static IEnumerable<string> ExcludedCandidates()
    {
        // Every two-node combination of the node names the PERMITTED catalog already uses. One of
        // them collides with an exclusion, which is enough to prove the predicate works without
        // this file containing the name.
        string[] nodes = CommandCatalog.All
            .SelectMany(c => c.Mnemonic.Split(':', StringSplitOptions.RemoveEmptyEntries))
            .Select(n => n.TrimEnd('?'))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();

        foreach (string first in nodes)
        {
            foreach (string second in nodes)
            {
                yield return $":{first}:{second}";
            }
        }
    }
}
