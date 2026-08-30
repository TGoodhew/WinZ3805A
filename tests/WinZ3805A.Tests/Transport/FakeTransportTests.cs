using System.IO.Pipelines;

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
}
