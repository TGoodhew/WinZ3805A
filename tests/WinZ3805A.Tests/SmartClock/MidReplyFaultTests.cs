using System.IO.Pipelines;
using System.IO.Pipes;

using Microsoft.Extensions.Logging;

using WinZ3805A.Device.Transport;
using WinZ3805A.Simulation.SmartClock;

namespace WinZ3805A.Tests.SmartClock;

/// <summary>
/// The simulator's mid-reply fault against the application's own protocol (#707).
/// </summary>
/// <remarks>
/// <para>
/// The QA pass found #707 by chance: the app restarted while the simulated receiver was partway
/// through a status screen, and the connect failed with the receiver answering every command
/// correctly. #708's fix is proved by <c>ResynchronisationTests</c> against a scripted wire; this
/// proves it against the simulator's real link - paced at 9600 baud, in real time, over a named pipe
/// as a VM's serial port is - so the fault the QA pass arms is known to put the connect on the path
/// the fix is for.
/// </para>
/// <para>
/// <b>Checked against the defect:</b> with #708's realignment removed from <c>ClearStatusAsync</c>,
/// the identity read a bare prompt and this test failed, as the connect on QA-Win10 did.
/// </para>
/// </remarks>
public sealed class MidReplyFaultTests
{
    [Fact]
    public async Task APortOpenedMidReplyIsRealignedBeforeTheIdentityIsAsked()
    {
        SimulatedReceiver receiver = new(TimeProvider.System);
        receiver.StartLocked();
        ScpiEngine engine = new(receiver, TimeProvider.System);
        SimulatorLink link = new(engine, TimeProvider.System);

        string name = "winz-test-" + Guid.NewGuid().ToString("N");
        using CancellationTokenSource done = new(TimeSpan.FromSeconds(60));
        await using NamedPipeServerStream server = new(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, System.IO.Pipes.PipeOptions.Asynchronous);
        await using NamedPipeClientStream client = new(".", name, PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
        Task connecting = server.WaitForConnectionAsync(done.Token);
        await client.ConnectAsync(done.Token);
        await connecting;
        Task serving = link.RunAsync(server, done.Token);

        // The reply already going out when the application opens its port, as after a restart mid-poll.
        Assert.Equal("ok: a reply goes out with no prompt until the next command", ControlCommands.Apply("fault mid-reply", receiver, engine, link));
        await Task.Delay(TimeSpan.FromSeconds(1), done.Token);

        List<string> log = [];
        await using StreamTransport transport = new(client);
        LineProtocol protocol = new(transport, TimeProvider.System, new RecordingLogger(log));
        await transport.OpenAsync(done.Token);

        Transaction heard = await protocol.SynchroniseAsync(TransactionTimeouts.AutoDetectProbe, done.Token);
        Transaction identity = await protocol.ExecuteAsync("*IDN?", TransactionTimeouts.AutoDetectProbe, done.Token);

        // The listen heard the reply under way and no prompt: the state the fault exists to produce.
        Assert.Equal(TransactionOutcome.TimedOut, heard.Outcome);
        Assert.NotEmpty(heard.Lines);

        // The defect itself first, so a regression reports what the user saw: a bare prompt for *IDN?.
        Assert.True(identity.Succeeded, $"*IDN? {identity.Outcome}: [{string.Join(" | ", identity.Lines)}]");
        Assert.Equal(receiver.Identity, Assert.Single(identity.Lines));
        Assert.Contains(log, line => line.StartsWith("The port opened mid-reply", StringComparison.Ordinal));

        await done.CancelAsync();
        try
        {
            await serving;
        }
        catch (OperationCanceledException)
        {
            // The link stops with the test.
        }
    }

    [Fact]
    public void TheFaultIsOneShotAndShownInTheStatus()
    {
        SimulatedReceiver receiver = new(TimeProvider.System);
        ScpiEngine engine = new(receiver, TimeProvider.System);
        SimulatorLink link = new(engine, TimeProvider.System);

        ControlCommands.Apply("fault mid-reply", receiver, engine, link);
        Assert.Contains("mid-reply", link.Faults.ToString(), StringComparison.Ordinal);

        ControlCommands.Apply("fault none", receiver, engine, link);
        Assert.False(link.Faults.MidReplyRequested);
    }

    /// <summary>An <see cref="ITransport"/> over a duplex stream, its input pumped as a port's is.</summary>
    private sealed class StreamTransport(Stream stream) : ITransport
    {
        private readonly Pipe _pipe = new();
        private readonly CancellationTokenSource _stop = new();
        private Task _pump = Task.CompletedTask;

        public string Description => "a named pipe";

        public bool IsOpen { get; private set; }

        public PipeReader Input => _pipe.Reader;

        public ValueTask OpenAsync(CancellationToken cancellationToken = default)
        {
            IsOpen = true;
            _pump = PumpAsync();
            return ValueTask.CompletedTask;
        }

        public async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            await stream.WriteAsync(buffer, cancellationToken);
            await stream.FlushAsync(cancellationToken);
        }

        // A port's driver buffer, which this would purge, is the pump's pipe here, and the protocol
        // drains that itself before each command.
        public void DiscardInput()
        {
        }

        public async ValueTask DisposeAsync()
        {
            IsOpen = false;
            await _stop.CancelAsync();
            try
            {
                await _pump;
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                // Stopped.
            }

            _stop.Dispose();
        }

        private async Task PumpAsync()
        {
            byte[] buffer = new byte[256];
            try
            {
                while (true)
                {
                    int read = await stream.ReadAsync(buffer, _stop.Token);
                    if (read == 0)
                    {
                        return;
                    }

                    await _pipe.Writer.WriteAsync(buffer.AsMemory(0, read), _stop.Token);
                }
            }
            finally
            {
                await _pipe.Writer.CompleteAsync();
            }
        }
    }

    private sealed class RecordingLogger(List<string> lines) : ILogger<LineProtocol>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (lines)
            {
                lines.Add(formatter(state, exception));
            }
        }
    }
}
