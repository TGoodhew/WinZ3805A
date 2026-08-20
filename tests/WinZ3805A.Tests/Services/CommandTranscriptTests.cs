using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>§10.11's transcript: bounded, filterable, and never the thing that sends anything.</summary>
public sealed class CommandTranscriptTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), Path.GetRandomFileName());

    public void Dispose()
    {
        if (Directory.Exists(_folder))
        {
            Directory.Delete(_folder, recursive: true);
        }
    }

    private static TranscriptEntry Entry(
        string sent,
        CommandOrigin origin = CommandOrigin.User,
        TransactionOutcome outcome = TransactionOutcome.Completed,
        params string[] received) =>
        new(0, origin, sent, received, outcome, TimeSpan.FromMilliseconds(12), null);

    [Fact]
    public void ItStartsEmpty() => Assert.Equal(0, new CommandTranscript().Count);

    [Fact]
    public void EntriesComeBackOldestFirst()
    {
        CommandTranscript transcript = new();
        transcript.Add(Entry("*IDN?"));
        transcript.Add(Entry(":SYNC:STAT?"));

        Assert.Equal(["*IDN?", ":SYNC:STAT?"], transcript.Snapshot().Select(entry => entry.Sent));
    }

    /// <summary>
    /// §10.11's toggle. The connect sequence is deliberately not filtered with the polls: it is a
    /// handful of lines and it is what a user investigating a connection opened the page for.
    /// </summary>
    [Fact]
    public void PollTrafficCanBeHiddenAndNothingElseIs()
    {
        CommandTranscript transcript = new();
        transcript.Add(Entry("*IDN?", CommandOrigin.Session));
        transcript.Add(Entry(":SYNC:STAT?", CommandOrigin.Poll));
        transcript.Add(Entry(":SYST:STAT?", CommandOrigin.Poll));
        transcript.Add(Entry(":GPS:SAT:TRAC:EMANgle 10", CommandOrigin.User));

        Assert.Equal(4, transcript.Snapshot().Count);
        Assert.Equal(
            ["*IDN?", ":GPS:SAT:TRAC:EMANgle 10"],
            transcript.Snapshot(includePolls: false).Select(entry => entry.Sent));
    }

    /// <summary>Hiding polls filters the view, not the record — Count still counts them.</summary>
    [Fact]
    public void HidingPollTrafficDoesNotDiscardIt()
    {
        CommandTranscript transcript = new();
        transcript.Add(Entry(":SYNC:STAT?", CommandOrigin.Poll));

        Assert.Empty(transcript.Snapshot(includePolls: false));
        Assert.Equal(1, transcript.Count);
    }

    [Fact]
    public void ItKeepsTheMostRecentAndDropsTheOldest()
    {
        CommandTranscript transcript = new();

        for (int index = 0; index < CommandTranscript.Capacity + 50; index++)
        {
            transcript.Add(Entry($":CMD{index}"));
        }

        IReadOnlyList<TranscriptEntry> kept = transcript.Snapshot();

        Assert.Equal(CommandTranscript.Capacity, kept.Count);
        Assert.Equal(":CMD50", kept[0].Sent);
        Assert.Equal($":CMD{CommandTranscript.Capacity + 49}", kept[^1].Sent);
    }

    [Fact]
    public void ClearDiscardsEverything()
    {
        CommandTranscript transcript = new();
        transcript.Add(Entry("*IDN?"));

        transcript.Clear();

        Assert.Equal(0, transcript.Count);
        Assert.Empty(transcript.Snapshot());
    }

    [Fact]
    public void EveryChangeIsAnnounced()
    {
        CommandTranscript transcript = new();
        int changes = 0;
        transcript.Changed += (_, _) => changes++;

        transcript.Add(Entry("*IDN?"));
        transcript.Clear();

        Assert.Equal(2, changes);
    }

    /// <summary>A snapshot is a copy, so a page iterating it cannot be tripped by a poll landing.</summary>
    [Fact]
    public void ASnapshotIsNotTheLiveBuffer()
    {
        CommandTranscript transcript = new();
        transcript.Add(Entry("*IDN?"));

        IReadOnlyList<TranscriptEntry> taken = transcript.Snapshot();
        transcript.Add(Entry(":SYNC:STAT?"));

        Assert.Single(taken);
        Assert.Equal(2, transcript.Count);
    }

    /// <summary>
    /// A transaction that never answered is recorded, and visibly so. §10.11 says the transcript
    /// shows all traffic, and the failures are what someone opens it to see.
    /// </summary>
    [Fact]
    public void ATimeoutIsRecordedWithNoResponseLines()
    {
        CommandTranscript transcript = new();
        transcript.Add(Entry(":SYST:STAT?", outcome: TransactionOutcome.TimedOut));

        TranscriptEntry entry = Assert.Single(transcript.Snapshot());

        Assert.Equal(TransactionOutcome.TimedOut, entry.Outcome);
        Assert.Empty(entry.Received);
    }

    // ------------------------------------------------------------------- the Advanced opt-in

    [Fact]
    public void TheConsoleIsOffOnAFreshInstall() =>
        Assert.False(AdvancedPreferences.Default.IsConsoleEnabled);

    [Fact]
    public void TheOptInSurvivesARoundTrip()
    {
        string path = Path.Combine(_folder, "advanced.json");

        new LocalAdvancedPreferenceStore(path).Save(new AdvancedPreferences { IsConsoleEnabled = true });

        Assert.True(new LocalAdvancedPreferenceStore(path).Load().IsConsoleEnabled);
    }

    /// <summary>
    /// A corrupt file falls back to <b>off</b>, which is the safe direction. A store that failed
    /// open would enable an advanced surface because a disk went wrong.
    /// </summary>
    [Fact]
    public void ACorruptFileLeavesTheConsoleOff()
    {
        string path = Path.Combine(_folder, "advanced.json");
        Directory.CreateDirectory(_folder);
        File.WriteAllText(path, "{ not json at all");

        Assert.False(new LocalAdvancedPreferenceStore(path).Load().IsConsoleEnabled);
    }

    [Fact]
    public void AMissingFileLeavesTheConsoleOff() =>
        Assert.False(new LocalAdvancedPreferenceStore(Path.Combine(_folder, "absent.json"))
            .Load().IsConsoleEnabled);

    [Fact]
    public void TheOptInSitsBesideTheOtherStores() =>
        Assert.Equal(
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "WinZ3805A",
                "advanced.json"),
            LocalAdvancedPreferenceStore.DefaultPath());
}
