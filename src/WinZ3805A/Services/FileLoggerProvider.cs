using System.Collections.Concurrent;
using System.Globalization;
using System.Text;

using Microsoft.Extensions.Logging;

namespace WinZ3805A.Services;

/// <summary>
/// The <c>ILoggerProvider</c> that finally gives §6.1's logging row somewhere to write (#127).
/// </summary>
/// <remarks>
/// <para>
/// <c>ILogger</c> has been injected into the transport, the session and the poller since §15 step 1,
/// and <c>TransportLog</c> holds real source-generated instrumentation — but nothing ever registered
/// a provider, so <c>ILoggerFactory</c> resolved to null and every line went to <c>NullLogger</c>.
/// The plumbing was real and the log was thrown away.
/// </para>
/// <para>
/// <b>Not <c>ApplicationData.Current.LocalFolder</c></b>, which is what §6.1 named until 15 Aug
/// 2026. Reading <c>ApplicationData.Current</c> terminates this process uncatchably — the window
/// simply never appears — which is why every preference store here is a plain file under
/// <see cref="Environment.SpecialFolder.LocalApplicationData"/>, and why this is too.
/// </para>
/// </remarks>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private readonly ConcurrentDictionary<string, FileLogger> _loggers = new(StringComparer.Ordinal);
    private readonly FileLogWriter _writer;
    private readonly TimeProvider _timeProvider;

    /// <summary>Creates a provider over a writer.</summary>
    /// <param name="writer">Where the lines go.</param>
    /// <param name="timeProvider">
    /// The clock stamping each line. Injected on §12's rule against reading the clock directly —
    /// binding on the Device library, followed here by choice — because a test asserting a
    /// timestamp against <c>DateTime.Now</c> is a test that passes for the minute it was written
    /// in.
    /// </param>
    public FileLoggerProvider(FileLogWriter writer, TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(writer);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _writer = writer;
        _timeProvider = timeProvider;
    }

    /// <summary>Where the log is being written, for the "show me the file" affordance.</summary>
    public string Path => _writer.Path;

    /// <summary>The default file location, beside the other stores this application keeps.</summary>
    public static string DefaultPath() => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "WinZ3805A",
        "logs",
        "app.log");

    /// <inheritdoc />
    public ILogger CreateLogger(string categoryName) =>
        _loggers.GetOrAdd(categoryName ?? string.Empty, name => new FileLogger(this, name));

    /// <summary>Flushes anything buffered.</summary>
    public void Flush() => _writer.Flush();

    /// <inheritdoc />
    public void Dispose()
    {
        _writer.Dispose();
        _loggers.Clear();
    }

    /// <summary>Formats one entry the way the file records it.</summary>
    /// <remarks>
    /// <para>
    /// A fixed-width level and a sortable timestamp, because the first thing anyone does with this
    /// file is open it in a text editor and scan a column. The category is shortened to its last
    /// segment: every line would otherwise start with the same twenty-odd characters of namespace,
    /// which is noise in a file whose whole value is being skimmable.
    /// </para>
    /// <para>
    /// The timestamp is local rather than UTC. It is read next to a wall clock and a lab notebook
    /// by someone working out when their antenna dropped out — the same reason §10.3 shows the
    /// receiver's time in the display zone. The offset is included so the file is still unambiguous
    /// across a daylight-saving change.
    /// </para>
    /// </remarks>
    internal static string Format(DateTimeOffset when, LogLevel level, string category, string message, Exception? exception)
    {
        StringBuilder text = new();

        text.Append(when.ToString("yyyy-MM-dd HH:mm:ss.fff zzz", CultureInfo.InvariantCulture))
            .Append("  ")
            .Append(Abbreviate(level))
            .Append("  ")
            .Append(LastSegment(category))
            .Append("  ")
            .Append(message);

        if (exception is not null)
        {
            // Indented onto following lines rather than flattened onto this one, so a stack trace
            // does not turn one entry into a four-hundred-character line nobody can scan past.
            text.Append(Environment.NewLine)
                .Append("    ")
                .Append(exception.ToString().Replace(Environment.NewLine, Environment.NewLine + "    ", StringComparison.Ordinal));
        }

        return text.ToString();
    }

    private static string Abbreviate(LogLevel level) => level switch
    {
        LogLevel.Trace => "TRCE",
        LogLevel.Debug => "DBUG",
        LogLevel.Information => "INFO",
        LogLevel.Warning => "WARN",
        LogLevel.Error => "FAIL",
        LogLevel.Critical => "CRIT",
        _ => "NONE",
    };

    private static string LastSegment(string category)
    {
        int dot = category.LastIndexOf('.');
        return dot >= 0 && dot < category.Length - 1 ? category[(dot + 1)..] : category;
    }

    private sealed class FileLogger(FileLoggerProvider provider, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel) || formatter is null)
            {
                return;
            }

            provider._writer.Write(Format(
                provider._timeProvider.GetLocalNow(),
                logLevel,
                category,
                formatter(state, exception),
                exception));
        }
    }
}
