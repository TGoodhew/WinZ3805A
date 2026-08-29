using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;

using WinZ3805A.Controls;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Parsing;

namespace WinZ3805A.ViewModels;

/// <summary>
/// One row of the §10.9 self-test card: a subsystem, and what is known about it.
/// </summary>
/// <param name="Subsystem">Which subsystem the row names.</param>
/// <param name="Result">The last result, or null when it has not been tested this session.</param>
/// <param name="RanAt">When that result was obtained.</param>
public sealed record SelfTestRow(SelfTestSubsystem Subsystem, SelfTestResult? Result, DateTimeOffset? RanAt)
{
    /// <summary>The subsystem's name, for the row label.</summary>
    public string Name => Subsystem.DisplayName;

    /// <summary>
    /// The row's severity, which is deliberately never <c>Critical</c>.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A non-zero code is <b>not</b> proof of a fault. The manual says only "non-zero is test
    /// specific code" and does not decode them, and the one non-zero seen in practice —
    /// <c>+65536</c> from <c>GPS</c> — proved intermittent: the same command returned <c>+0</c> in
    /// 11.6 s and <c>+65536</c> in 24.0 s minutes apart, on a receiver tracking nine satellites at
    /// −0.1 ns. The code tracked the duration, not the hardware.
    /// </para>
    /// <para>
    /// So a non-zero code is <see cref="Severity.Caution"/> — "this did not report a pass, here is
    /// what it said" — rather than <see cref="Severity.Critical"/>, which would assert a hardware
    /// failure the receiver never claimed. §9.4.3's red is for a fault being asserted.
    /// </para>
    /// </remarks>
    public Severity Severity => Result?.Passed switch
    {
        true => Severity.Success,
        false => Severity.Caution,
        _ => Severity.Neutral,
    };

    /// <summary>When the result was obtained, or an empty string when it has not been.</summary>
    /// <remarks>
    /// UTC and spelled out, for the reason the sky-plot export caption gives: a result compared
    /// against another machine's later cannot be read if the reader has to know which zone this one
    /// was in. Empty rather than "—" because the pill beside it already says there is nothing here,
    /// and two placeholders on one row read as two missing things.
    /// </remarks>
    public string RanAtText => RanAt is DateTimeOffset at
        ? at.ToUniversalTime().ToString("yyyy-MM-dd HH:mm:ss 'UTC'", CultureInfo.InvariantCulture)
        : string.Empty;

    /// <summary>What the pill says. Never a bare colour (§9.4.3, §9.13 item 10).</summary>
    public string StatusText => Result?.Passed switch
    {
        true => "Passed",
        false => $"Code {Result!.Code}",
        _ => ReadoutFormatter.NoValue,
    };
}

/// <summary>
/// The §10.9 self-test card, and P1-5's per-subsystem selection (#53).
/// </summary>
/// <remarks>
/// <para>
/// <b>Only what was measured.</b> §10.9's wireframe shows eleven ticks side by side, and no single
/// query produces that — <c>:DIAG:TEST:RES?</c> reports one <c>&lt;code&gt;,&lt;subsystem&gt;</c>
/// pair for whichever test ran last. So a subsystem that has not been tested in this session shows
/// <c>—</c>, per §11.1, rather than a tick it never earned. Rendering eleven greens after one
/// <c>ALL</c> run would assert ten results the receiver never sent, which is #245's defect exactly.
/// </para>
/// <para>
/// Results are session-scoped and deliberately not persisted. A self-test is a statement about the
/// receiver at one moment, and a tick restored from disk after a power cycle would be a claim about
/// hardware nobody has tested since.
/// </para>
/// </remarks>
public sealed class SelfTestViewModel : INotifyPropertyChanged
{
    private readonly TimeProvider _time;
    private readonly Dictionary<string, (SelfTestResult Result, DateTimeOffset At)> _results = [];

    private SelfTestSubsystem _selected = SelfTestSubsystem.All;
    private bool _isRunning;

    /// <summary>Creates the view model.</summary>
    /// <param name="timeProvider">
    /// Supplies the run timestamp. Injected rather than read from <c>DateTime</c> so a test can pin
    /// it — the card's whole content is "what was true, and when".
    /// </param>
    public SelfTestViewModel(TimeProvider timeProvider)
    {
        _time = timeProvider;
        Rows = new ObservableCollection<SelfTestRow>(
            SelfTestSubsystem.Known.Select(s => new SelfTestRow(s, null, null)));
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Every subsystem, for the selector.</summary>
    public IReadOnlyList<SelfTestSubsystem> Subsystems => SelfTestSubsystem.Known;

    /// <summary>One row per subsystem, in §10.9's order.</summary>
    public ObservableCollection<SelfTestRow> Rows { get; }

    /// <summary>Which subsystem the run action will test.</summary>
    public SelfTestSubsystem Selected
    {
        get => _selected;
        set
        {
            _selected = value;
            Raise(nameof(Selected));
            Raise(nameof(RunLabel));
        }
    }

    /// <summary>Whether a test is in flight.</summary>
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            _isRunning = value;
            Raise(nameof(IsRunning));
        }
    }

    /// <summary>The run button's text, which names what will actually be tested.</summary>
    /// <remarks>
    /// Named rather than a bare "Run test", because this action costs the receiver its lock. A
    /// button whose label does not say what it is about to do is the wrong affordance for something
    /// that expensive.
    /// </remarks>
    public string RunLabel => Selected.Keyword == SelfTestSubsystem.All.Keyword
        ? "Run all tests"
        : $"Test {Selected.DisplayName}";

    /// <summary>How many subsystems have a result this session.</summary>
    public int TestedCount => _results.Count;

    /// <summary>
    /// A one-line summary of what is known, or an empty state that says why it is empty.
    /// </summary>
    public string Summary
    {
        get
        {
            if (_results.Count == 0)
            {
                return "No test has been run in this session. Results are not kept between runs of "
                    + "the application, because a self-test describes the receiver at one moment.";
            }

            int failed = _results.Values.Count(r => r.Result.Passed == false);

            return failed == 0
                ? $"{_results.Count} of {SelfTestSubsystem.Known.Count} tested, all reported a pass."
                : $"{_results.Count} of {SelfTestSubsystem.Known.Count} tested, {failed} did not report a pass.";
        }
    }

    /// <summary>Records a completed run.</summary>
    /// <param name="result">What the receiver reported.</param>
    /// <remarks>
    /// An <c>ALL</c> run records against <c>ALL</c> only. It genuinely is one test with one answer:
    /// the receiver does not break it down, and crediting eleven rows from it would be inventing
    /// ten readings.
    /// </remarks>
    public void Record(SelfTestResult result)
    {
        if (result.Subsystem is not SelfTestSubsystem subsystem)
        {
            return;
        }

        _results[subsystem.Keyword] = (result, _time.GetUtcNow());
        RefreshRows();
    }

    /// <summary>Marks a run as started or finished.</summary>
    public void SetRunning(bool running) => IsRunning = running;

    private void RefreshRows()
    {
        for (int i = 0; i < Rows.Count; i++)
        {
            SelfTestSubsystem subsystem = Rows[i].Subsystem;

            Rows[i] = _results.TryGetValue(subsystem.Keyword, out (SelfTestResult Result, DateTimeOffset At) held)
                ? new SelfTestRow(subsystem, held.Result, held.At)
                : new SelfTestRow(subsystem, null, null);
        }

        Raise(nameof(TestedCount));
        Raise(nameof(Summary));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
