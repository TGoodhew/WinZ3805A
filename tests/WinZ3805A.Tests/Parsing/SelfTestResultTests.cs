using Microsoft.Extensions.Time.Testing;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.Parsing;

/// <summary>
/// P1-5's self-test reading (#53), against what the receiver actually says.
/// </summary>
/// <remarks>
/// The replies here are transcribed from a probe of the live Z3805A on 28 Aug 2026, not invented:
/// <c>+0,ALL</c>, <c>+65536,GPS</c>, and <c>-224,"Illegal parameter value"</c> for a keyword that
/// does not exist. Neither manual documents any of it.
/// </remarks>
public class SelfTestResultTests
{
    private static readonly DateTimeOffset Ran = new(2026, 8, 28, 18, 20, 0, TimeSpan.Zero);

    [Fact]
    public void A_pass_is_read_from_the_receivers_own_reply()
    {
        SelfTestResult result = SelfTestResult.Parse("+0,ALL");

        Assert.Equal(0, result.Code);
        Assert.True(result.Passed);
        Assert.Equal("All subsystems", result.Subsystem!.DisplayName);
    }

    [Fact]
    public void A_non_zero_code_is_not_a_pass_and_keeps_its_value()
    {
        // The code is kept rather than reduced to a boolean, because the manual does not decode it
        // and the number is the only thing anybody could act on.
        SelfTestResult result = SelfTestResult.Parse("+65536,GPS");

        Assert.Equal(65536, result.Code);
        Assert.False(result.Passed);
        Assert.Equal("GPS", result.Subsystem!.DisplayName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("something unexpected")]
    [InlineData(null)]
    public void An_unreadable_reply_is_unknown_rather_than_failed(string? reply)
    {
        // §11.1: the parser never throws, and an unparseable field is null. Null is NOT false --
        // reporting "did not pass" because the reply was garbled would invent a finding.
        SelfTestResult result = SelfTestResult.Parse(reply);

        Assert.Null(result.Passed);
        Assert.Null(result.Code);
    }

    [Fact]
    public void The_run_reply_reads_only_the_first_of_its_three_integers()
    {
        // ":DIAG:TEST? GPS" answers "+65536,+0,+0". What the second and third mean is unknown, and
        // a number shown on a diagnostics page is read as meaningful, so they are not surfaced.
        SelfTestResult result = SelfTestResult.ParseRun("+65536,+0,+0", SelfTestSubsystem.ByKeyword("GPS")!);

        Assert.Equal(65536, result.Code);
        Assert.Equal("GPS", result.Subsystem!.Keyword);
    }

    [Fact]
    public void Every_probed_keyword_is_known()
    {
        // All twelve were accepted by the live receiver. An invalid keyword returned
        // -224 "Illegal parameter value" immediately, which is what made the positives mean
        // something rather than being a list nobody checked.
        string[] probed = ["ALL", "DISP", "PROC", "RAM", "EEPR", "UART", "QSPI", "FPGA", "INT", "IREF", "GPS", "POW"];

        Assert.All(probed, k => Assert.NotNull(SelfTestSubsystem.ByKeyword(k)));
        Assert.Equal(probed.Length, SelfTestSubsystem.Known.Count);
    }

    [Fact]
    public void An_unknown_keyword_resolves_to_nothing()
    {
        Assert.Null(SelfTestSubsystem.ByKeyword("ZZNOSUCH"));
        Assert.Null(SelfTestSubsystem.ByKeyword(""));
        Assert.Null(SelfTestSubsystem.ByKeyword(null));
    }

    [Fact]
    public void Keyword_matching_ignores_case()
    {
        // The receiver's echo is not guaranteed to match the case sent.
        Assert.Equal("GPS", SelfTestSubsystem.ByKeyword("gps")!.Keyword);
    }

    // ---- The card ---------------------------------------------------------------------------

    private static SelfTestViewModel Card() => new(new FakeTimeProvider(Ran));

    [Fact]
    public void Nothing_is_claimed_before_anything_is_tested()
    {
        SelfTestViewModel card = Card();

        Assert.Equal(0, card.TestedCount);
        Assert.All(card.Rows, r => Assert.Equal(ReadoutFormatter.NoValue, r.StatusText));
        Assert.All(card.Rows, r => Assert.Equal(Severity.Neutral, r.Severity));
    }

    [Fact]
    public void Running_ALL_credits_only_ALL()
    {
        // The heart of it. §10.9's wireframe shows eleven ticks and no query produces them: the
        // receiver reports one result for the test that ran. Crediting the other ten from an ALL
        // run would assert readings it never sent -- #245's defect, in a different field.
        SelfTestViewModel card = Card();

        card.Record(SelfTestResult.Parse("+0,ALL"));

        Assert.Equal(1, card.TestedCount);
        Assert.Equal("Passed", card.Rows.Single(r => r.Subsystem.Keyword == "ALL").StatusText);
        Assert.All(
            card.Rows.Where(r => r.Subsystem.Keyword != "ALL"),
            r => Assert.Equal(ReadoutFormatter.NoValue, r.StatusText));
    }

    [Fact]
    public void A_failing_code_is_caution_and_never_critical()
    {
        // +65536 from GPS proved intermittent -- +0 in 11.6 s and +65536 in 24.0 s minutes apart on
        // a receiver tracking nine satellites. Red asserts a fault; the receiver asserted no such
        // thing, and the manual does not decode the code at all.
        SelfTestViewModel card = Card();

        card.Record(SelfTestResult.Parse("+65536,GPS"));

        SelfTestRow gps = card.Rows.Single(r => r.Subsystem.Keyword == "GPS");
        Assert.Equal(Severity.Caution, gps.Severity);
        Assert.NotEqual(Severity.Critical, gps.Severity);
        Assert.Contains("65536", gps.StatusText, StringComparison.Ordinal);
    }

    [Fact]
    public void A_result_carries_the_time_it_was_obtained()
    {
        SelfTestViewModel card = Card();

        card.Record(SelfTestResult.Parse("+0,POW"));

        Assert.Equal(Ran, card.Rows.Single(r => r.Subsystem.Keyword == "POW").RanAt);
    }

    [Fact]
    public void A_later_run_replaces_the_earlier_result_for_that_subsystem()
    {
        // GPS is intermittent, so re-running it is the expected thing to do and the newer answer
        // is the one that counts.
        SelfTestViewModel card = Card();

        card.Record(SelfTestResult.Parse("+65536,GPS"));
        card.Record(SelfTestResult.Parse("+0,GPS"));

        Assert.Equal(1, card.TestedCount);
        Assert.True(card.Rows.Single(r => r.Subsystem.Keyword == "GPS").Result!.Passed);
    }

    [Fact]
    public void An_unrecognised_subsystem_is_not_recorded()
    {
        SelfTestViewModel card = Card();

        card.Record(SelfTestResult.Parse("+0,ZZNOSUCH"));

        Assert.Equal(0, card.TestedCount);
    }

    [Fact]
    public void The_empty_summary_says_why_it_is_empty()
    {
        // §9.11: an empty state says what will appear there. "No results" alone reads as a failure
        // to read them.
        Assert.Contains("No test has been run", Card().Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_summary_counts_what_did_not_pass()
    {
        SelfTestViewModel card = Card();

        card.Record(SelfTestResult.Parse("+0,POW"));
        card.Record(SelfTestResult.Parse("+65536,GPS"));

        Assert.Contains("2 of 12 tested", card.Summary, StringComparison.Ordinal);
        Assert.Contains("1 did not report a pass", card.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void The_run_label_names_what_will_be_tested()
    {
        // The action costs the receiver its lock, so a bare "Run test" is the wrong affordance.
        SelfTestViewModel card = Card();
        Assert.Equal("Run all tests", card.RunLabel);

        card.Selected = SelfTestSubsystem.ByKeyword("IREF")!;
        Assert.Equal("Test Internal reference", card.RunLabel);
    }
}
