using System.Text;
using Microsoft.Extensions.Time.Testing;
using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>
/// Replays a captured status screen through the transport, which is what §15 step 1 means by
/// proving the transaction loop against fixtures.
/// </summary>
/// <remarks>
/// These assertions are about delivery, not meaning: that 1875 bytes of real device output survive
/// a trip through the protocol with every column position and trailing space where it started.
/// `StatusScreenParser` (§15 step 2, P0-4) asserts what the screen says.
/// </remarks>
public class FixtureReplayTests
{
    private static readonly TimeSpan s_testTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The receiver on the bench does not echo, so the fixture replay does not either.</summary>
    private const bool DeviceEchoes = false;

    [Fact]
    public async Task ACapturedStatusScreenSurvivesTheTransactionLoopIntact()
    {
        string[] expected = ReadFixtureLines("locked-stabilizing.txt");

        await using FakeTransport transport = new(_ => string.Join("\r\n", expected))
        {
            EchoCommands = DeviceEchoes,
            ChunkSize = 64,
        };

        LineProtocol protocol = new(transport, new FakeTimeProvider());
        await transport.OpenAsync();

        Transaction transaction = await protocol.ExecuteAsync(":SYST:STAT?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.False(transaction.EchoDiscarded);
        Assert.Equal(expected, transaction.Lines);
    }

    /// <summary>
    /// The header row arrives with its spacing intact, because §11.1 derives every satellite column
    /// from where the tokens sit in it.
    /// </summary>
    [Fact]
    public async Task ColumnPositionsInTheSatelliteHeaderAreUnchanged()
    {
        string[] expected = ReadFixtureLines("locked-stabilizing.txt");

        await using FakeTransport transport = new(_ => string.Join("\r\n", expected))
        {
            EchoCommands = DeviceEchoes,
            ChunkSize = 7,
        };

        LineProtocol protocol = new(transport, new FakeTimeProvider());
        await transport.OpenAsync();

        Transaction transaction = await protocol.ExecuteAsync(":SYST:STAT?").WaitAsync(s_testTimeout);

        string header = Assert.Single(transaction.Lines, line => line.Contains("PRN", StringComparison.Ordinal));
        Assert.Equal(expected.Single(line => line.Contains("PRN", StringComparison.Ordinal)), header);

        // Two side-by-side PRN groups, which is the §11.1 case the parser has to detect by counting.
        Assert.Equal(2, header.Split("PRN", StringSplitOptions.None).Length - 1);
    }

    /// <summary>Trailing spaces are content here: they are what makes a column empty rather than absent.</summary>
    [Fact]
    public async Task TrailingSpacesOnAResponseLineAreNotTrimmed()
    {
        string[] expected = ReadFixtureLines("locked-stabilizing.txt");
        Assert.Contains(expected, line => line.EndsWith(' '));

        await using FakeTransport transport = new(_ => string.Join("\r\n", expected)) { EchoCommands = DeviceEchoes };
        LineProtocol protocol = new(transport, new FakeTimeProvider());
        await transport.OpenAsync();

        Transaction transaction = await protocol.ExecuteAsync(":SYST:STAT?").WaitAsync(s_testTimeout);

        Assert.Equal(
            expected.Count(line => line.EndsWith(' ')),
            transaction.Lines.Count(line => line.EndsWith(' ')));
    }

    /// <summary>
    /// A command the receiver rejects answers with the error prompt and nothing else. Observed with
    /// <c>:SYST:COMM:SER2:BAUD?</c>, which a Z3805A does not implement (OQ-2).
    /// </summary>
    [Fact]
    public async Task ARejectedCommandReportsTheErrorFromThePromptWithNoResponseLines()
    {
        await using FakeTransport transport = new(_ => null)
        {
            EchoCommands = DeviceEchoes,
            Prompt = "E-113> ",
        };

        LineProtocol protocol = new(transport, new FakeTimeProvider());
        await transport.OpenAsync();

        Transaction transaction = await protocol.ExecuteAsync(":SYST:COMM:SER2:BAUD?").WaitAsync(s_testTimeout);

        Assert.Equal(TransactionOutcome.Completed, transaction.Outcome);
        Assert.True(transaction.HasDeviceError);
        Assert.Equal("E-113", transaction.PromptStatus);
        Assert.Empty(transaction.Lines);
    }

    /// <summary>An ordinary prompt reports no error, so nothing downstream has to special-case the normal path.</summary>
    [Fact]
    public async Task AnOrdinaryPromptReportsNoDeviceError()
    {
        await using FakeTransport transport = new(_ => "LOCK") { EchoCommands = DeviceEchoes };
        LineProtocol protocol = new(transport, new FakeTimeProvider());
        await transport.OpenAsync();

        Transaction transaction = await protocol.ExecuteAsync(":SYNC:STAT?").WaitAsync(s_testTimeout);

        Assert.False(transaction.HasDeviceError);
        Assert.Null(transaction.PromptStatus);
    }

    /// <summary>
    /// Reads a fixture as the device wrote it. Latin-1 because it never substitutes, and an explicit
    /// CRLF split because the file is committed with <c>-text</c> and must not depend on the
    /// platform's idea of a line.
    /// </summary>
    private static string[] ReadFixtureLines(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", name);
        string text = Encoding.Latin1.GetString(File.ReadAllBytes(path));
        return text.TrimEnd('\r', '\n').Split("\r\n");
    }
}
