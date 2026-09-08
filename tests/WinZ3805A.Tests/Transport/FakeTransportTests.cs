using System.IO.Pipelines;
using System.Text;

using WinZ3805A.Device.Transport;

namespace WinZ3805A.Tests.Transport;

/// <summary>
/// The test double's own contract, where getting it wrong looks like a bug in something else.
/// </summary>
public sealed class FakeTransportTests
{
    private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

    /// <summary>
    /// The reader and the writer get the same pipe even when they first ask for it at once.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#324.</b> The pipe is built on first use, because <c>WaitForReaderToConsume</c> is
    /// init-only and so is not known while the field initialisers run. That was written as
    /// <c>_pipe ??= new Pipe(…)</c>, which is two operations and not one: two threads can both find
    /// the field null, and each then goes on using the pipe <i>it</i> constructed. The writer writes
    /// into a pipe nobody reads and the reader waits on a pipe nobody writes to.
    /// </para>
    /// <para>
    /// It is not a theoretical arrangement. <c>BroadcastListener.Start</c> takes
    /// <c>ITransport.Input</c> on a thread-pool loop while the test writes from its own thread, so
    /// the two first uses are genuinely concurrent — which is why
    /// <c>BroadcastListenerTests.NoiseIsCountedAndDiscarded</c> crashed the test host every time it
    /// was run alone, and the whole suite about one run in six.
    /// </para>
    /// <para>
    /// <b>Why a loop.</b> A race cannot be asserted in one attempt: the unfixed code lost this one
    /// about three times in fifteen, so a single pass proves nothing. Forty attempts against those
    /// odds is a certainty, and costs milliseconds because each one is a pipe and seven bytes.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task TheReaderAndTheWriterShareOnePipeWhenTheyRaceForIt()
    {
        for (int attempt = 0; attempt < 40; attempt++)
        {
            await using FakeTransport transport = new()
            {
                Silent = true,
                EchoCommands = false,
                EmitPrompt = false,

                // The setting that turns losing the race into a deadlock rather than a lost write:
                // the writer pauses at one byte and waits for a reader that is watching elsewhere.
                WaitForReaderToConsume = true,
            };

            await transport.OpenAsync();

            // Both first uses, started together and on different threads.
            Task<PipeReader> takingTheReader = Task.Run(() => transport.Input);
            Task writing = Task.Run(async () => await transport.EmitAsync("hello\r\n"));

            PipeReader reader = await takingTheReader.WaitAsync(Patience);

            // Reading before awaiting the write, because the writer is paused until it is drained.
            // If the two got different pipes there is nothing to read and this is where it fails.
            using CancellationTokenSource giveUp = new(Patience);
            ReadResult result = await reader.ReadAsync(giveUp.Token);

            Assert.False(result.Buffer.IsEmpty, $"attempt {attempt}: the writer's bytes never reached the reader");
            reader.AdvanceTo(result.Buffer.End);

            await writing.WaitAsync(Patience);
        }
    }

    /// <summary>
    /// Disposing does not wait forever for a pump that is paused with nobody left to drain it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#381.</b> <see cref="FakeTransport.WaitForReaderToConsume"/> builds the pipe with a
    /// one-byte <c>pauseWriterThreshold</c>, so a response being pumped stops after its first chunk
    /// and waits to be read. <c>DisposeAsync</c> then awaited that pump <i>before</i> completing the
    /// pipe — with no cancellation and no timeout — so a transport disposed while the thing reading
    /// it had already stopped never came back. Not a failure: a hang, inside <c>await using</c>,
    /// which is why the test that hit it produced no output at all.
    /// </para>
    /// <para>
    /// The order in a test is what decides it. Sessions and listeners are usually declared after
    /// the transport and so dispose first, which is exactly the state this reproduces: the reader
    /// is gone, one chunk sits unread, and the writer is paused behind it.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task DisposingDoesNotWaitForeverForAPumpNobodyIsDraining()
    {
        FakeTransport transport = new(_ => "an answer long enough to need a second chunk\r\n")
        {
            WaitForReaderToConsume = true,
            ChunkSize = 4,
        };

        await transport.OpenAsync();

        // The pipe exists and has a reader — and then nothing ever reads from it, which is what a
        // disposed session leaves behind.
        _ = transport.Input;

        // A command starts the response pump, which pauses at the one-byte threshold.
        await transport.WriteAsync(Encoding.Latin1.GetBytes("SYST:ERR?\r\n"));

        await transport.DisposeAsync().AsTask().WaitAsync(Patience);
    }

    /// <summary>
    /// An emit nobody drains gives up and says so, instead of waiting for the life of the process.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>#449, and this is the deliberate violation the bound is worth nothing without.</b>
    /// <see cref="FakeTransport.WaitForReaderToConsume"/> gives the pipe a one-byte
    /// <c>pauseWriterThreshold</c>, so an emit does not return until something drains it. Here
    /// nothing ever does — which is what a faulted session, a stopped poller or an abandoned
    /// transaction leaves behind.
    /// </para>
    /// <para>
    /// Unbounded, this test would not fail. It would hang, take the test host with it after five
    /// minutes of silence, and report a green suite plus a guess at which test was responsible.
    /// That is exactly how the defect reached CI three times.
    /// </para>
    /// </remarks>
    [Fact]
    public async Task AnEmitNobodyDrainsFailsRatherThanHanging()
    {
        await using FakeTransport transport = new()
        {
            WaitForReaderToConsume = true,
            PausedWriteTimeout = TimeSpan.FromMilliseconds(250),
        };

        await transport.OpenAsync();

        // A reader exists but never reads, so the writer pauses at the threshold and stays there.
        _ = transport.Input;

        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(
            async () => await transport.EmitAsync("nobody is listening to this\r\n").AsTask().WaitAsync(Patience));

        // The message has to name the mechanism, because the next person to meet this will be
        // reading it instead of a stack of paused pipe internals.
        Assert.Contains("WaitForReaderToConsume", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Waiting for a command that never comes fails rather than hanging (#449).
    /// </summary>
    /// <remarks>
    /// <b>Every call site in the suite awaits <c>ReadCommandAsync</c> with no timeout of its own</b>
    /// — seven of them in <c>ResynchronisationTests</c> alone, the file whose test kept taking the
    /// host down. A command that never reaches the wire, because the protocol is blocked before the
    /// write rather than slow at it, stops the test dead instead of failing it.
    /// </remarks>
    [Fact]
    public async Task WaitingForACommandThatNeverComesFailsRatherThanHanging()
    {
        await using FakeTransport transport = new()
        {
            PausedWriteTimeout = TimeSpan.FromMilliseconds(250),
        };

        await transport.OpenAsync();

        // Nothing ever writes to this transport.
        TimeoutException thrown = await Assert.ThrowsAsync<TimeoutException>(
            async () => await transport.ReadCommandAsync().AsTask().WaitAsync(Patience));

        Assert.Contains("blocked before the write", thrown.Message, StringComparison.Ordinal);
    }

    /// <summary>A command that does arrive is returned, and the budget does not interfere.</summary>
    [Fact]
    public async Task ACommandThatArrivesIsStillReturned()
    {
        await using FakeTransport transport = new() { EchoCommands = false, EmitPrompt = false };
        await transport.OpenAsync();

        await transport.WriteAsync(Encoding.Latin1.GetBytes("SYNC:STAT?\r\n"));

        Assert.Equal("SYNC:STAT?", await transport.ReadCommandAsync().AsTask().WaitAsync(Patience));
    }

    /// <summary>Without the pause there is no deadlock to guard against, and no budget applied.</summary>
    [Fact]
    public async Task AnUnpausedEmitIsNotBounded()
    {
        await using FakeTransport transport = new()
        {
            PausedWriteTimeout = TimeSpan.FromMilliseconds(1),
        };

        await transport.OpenAsync();
        _ = transport.Input;

        // Would throw if the budget were applied regardless of the pause.
        await transport.EmitAsync("this completes at once\r\n").AsTask().WaitAsync(Patience);
    }
}
