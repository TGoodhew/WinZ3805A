using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Device.Drivers.Uccm;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Uccm;

/// <summary>
/// The variant comes from the prompt, because asking does not work (#513).
/// </summary>
/// <remarks>
/// <para>
/// §418 section 7 proposed settling the variant by asking the three queries Lady Heather describes
/// as UCCM-P-only and seeing which were answered. <b>Put to a real UCCM-P on 12 and 13 Sep 2026,
/// it answered none of them</b> — <c>:GPS:POS:SURV:STAT?</c> and <c>:GPS:POS:SURV:PROG?</c> both
/// returned <c>Undefined header</c>, the same reply a deliberately nonsensical header gets, and
/// <c>:ROSC:HOLD:DUR?</c> returned <c>Command error</c>. A probe would have called that module a
/// plain UCCM, which is the exact inversion §418 exists to prevent.
/// </para>
/// <para>
/// The prompt says it instead, on every transaction and before any question is asked. Only the
/// UCCM-P half is measured: no plain UCCM has been on a bench here, so <c>UCCM &gt;</c> is Heather's
/// claim and is marked as such below.
/// </para>
/// </remarks>
public sealed class UccmVariantFromPromptTests
{
    private static UccmDriver Driver() =>
        new(new FakeTimeProvider(new DateTimeOffset(2026, 9, 13, 0, 0, 0, TimeSpan.Zero)));

    [Fact]
    public void TheMeasuredUccmPPromptSettlesTheVariant()
    {
        UccmDriver driver = Driver();
        Assert.Equal(UccmVariant.Unknown, driver.Profile.Variant);

        driver.NotePrompt("UCCM-P");

        Assert.Equal(UccmVariant.UccmP, driver.Profile.Variant);
    }

    [Fact]
    public void APlainUccmPromptSettlesItTheOtherWay()
    {
        // HEATHER'S CLAIM, NOT A MEASUREMENT. No plain UCCM has been on the bench. It is the safe
        // direction to be wrong in: getting this wrong mislabels a variant, where the probe this
        // replaced would have mislabelled the one module we do have.
        UccmDriver driver = Driver();

        driver.NotePrompt("UCCM");

        Assert.Equal(UccmVariant.Uccm, driver.Profile.Variant);
    }

    [Fact]
    public void AnErrorPromptLeavesTheVariantAlone()
    {
        UccmDriver driver = Driver();
        driver.NotePrompt("UCCM-P");

        // Null is what an error prompt gives; it must not undo what is already known.
        driver.NotePrompt(null);

        Assert.Equal(UccmVariant.UccmP, driver.Profile.Variant);
    }

    [Fact]
    public void APromptThisFamilyDoesNotKnowLeavesTheVariantUnknown()
    {
        UccmDriver driver = Driver();

        driver.NotePrompt("scpi");

        Assert.Equal(UccmVariant.Unknown, driver.Profile.Variant);
    }

    [Fact]
    public void TheVendorIsNotTouchedByTheVariant()
    {
        // §418: the two axes are orthogonal, and Heather's own source is the cautionary example -
        // it infers a variant from a vendor signature.
        UccmDriver driver = Driver();

        driver.NotePrompt("UCCM-P");

        Assert.Equal(UccmVendor.Unknown, driver.Profile.Vendor);
    }

    // ---- the grammar itself ----------------------------------------------------------------------

    [Fact]
    public void APlainUccmCanConnectAtAll()
    {
        // Until 13 Sep 2026 the grammar held only "UCCM-P", so a plain UCCM's prompt matched
        // nothing: every transaction would have run to its timeout with the answer already read, and
        // auto-detect would have reported that no receiver answered - the failure #470 found for the
        // UCCM-P itself.
        Assert.True(Driver().Prompt.TryMatch("UCCM > ", out _, out _, out string? word));
        Assert.Equal("UCCM", word);
    }

    [Fact]
    public void TheLongerPromptWinsEvenThoughTheShorterOneAlsoMatches()
    {
        // THE TRAP. "UCCM" is a prefix of "UCCM-P", so a first-match rule consumes "UCCM" out of
        // "UCCM-P >" and then fails on the hyphen - reporting NO prompt on a receiver that had just
        // printed one, which reads as a dead link rather than as a parsing bug.
        Assert.True(Driver().Prompt.TryMatch("UCCM-P > ", out _, out _, out string? word));
        Assert.Equal("UCCM-P", word);
    }

    [Theory]
    [InlineData("UCCM-P >", "UCCM-P")]
    [InlineData("UCCM-P > ", "UCCM-P")]
    [InlineData("  UCCM-P >", "UCCM-P")]
    [InlineData("UCCM >", "UCCM")]
    [InlineData("  UCCM > ", "UCCM")]
    public void BothPromptsMatchInEverySpacing(string tail, string expected)
    {
        Assert.True(Driver().Prompt.TryMatch(tail, out _, out _, out string? word));
        Assert.Equal(expected, word);
    }

    [Fact]
    public void OrderingInTheWordListCannotBreakIt()
    {
        // Union preserves registration order, so a driver registered later could put a prefix word
        // ahead of a longer one. Length is compared rather than order, so it cannot matter.
        PromptGrammar awkward = new() { Words = ["UCCM", "UCCM-P"] };

        Assert.True(awkward.TryMatch("UCCM-P > ", out _, out _, out string? word));
        Assert.Equal("UCCM-P", word);
    }

    [Fact]
    public void TheSmartClockLearnsNothingFromItsOwnPrompt()
    {
        // The default is to ignore it, and the SmartClock says "scpi" whatever model it is.
        Assert.True(PromptGrammar.SmartClock.TryMatch("scpi > ", out _, out _, out string? word));
        Assert.Equal("scpi", word);
    }

    [Fact]
    public void AnErrorPromptReportsNoWordButStillMatches()
    {
        Assert.True(PromptGrammar.SmartClock.TryMatch("E-113 > ", out _, out string? status, out string? word));
        Assert.Equal("E-113", status);
        Assert.Null(word);
    }

    // ---- end to end, through the real transport ---------------------------------------------------

    [Theory]
    [InlineData("UCCM-P > ", "UCCM-P")]
    [InlineData("UCCM > ", "UCCM")]
    public async Task TheTransportCarriesTheMatchedPromptOntoTheTransaction(string prompt, string expected)
    {
        // The whole path in one test: the grammar matches, LineProtocol keeps which word it was, and
        // the Transaction carries it to where DeviceSessionService hands it to the driver. A unit
        // test of NotePrompt alone would pass with nothing ever calling it.
        await using FakeTransport transport = new(_ => "1") { Prompt = prompt };
        await transport.OpenAsync();

        LineProtocol protocol = new(
            transport,
            new FakeTimeProvider(),
            prompt: new UccmDriver(TimeProvider.System).Prompt);

        Transaction transaction = await protocol
            .ExecuteAsync(UccmCommands.LockLed)
            .WaitAsync(TimeSpan.FromSeconds(5));

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.Equal(expected, transaction.PromptWord);
    }

    [Fact]
    public async Task AUccmPIsIdentifiedFromOneOrdinaryTransaction()
    {
        // What actually happens on a connected receiver: a poll goes out, the prompt comes back, and
        // the variant is settled without anything having been asked about it.
        await using FakeTransport transport = new(_ => "1") { Prompt = "UCCM-P > " };
        await transport.OpenAsync();

        UccmDriver driver = Driver();
        LineProtocol protocol = new(transport, new FakeTimeProvider(), prompt: driver.Prompt);

        Transaction transaction = await protocol
            .ExecuteAsync(UccmCommands.LockLed)
            .WaitAsync(TimeSpan.FromSeconds(5));

        driver.NotePrompt(transaction.PromptWord);

        Assert.Equal(UccmVariant.UccmP, driver.Profile.Variant);
    }
}
