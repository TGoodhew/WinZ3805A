using System.Globalization;

using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// Reading the installer's records (#592) back for Diagnostics (#602). The lines are taken from
/// real records: Tony's clean-VM runs of 30 Sep 2026, trimmed to what the reader looks at.
/// </summary>
public sealed class InstallRecordTests
{
    private static readonly string[] Upgraded =
    [
        "16:00:44.714  WinZ3805A installer, run from C:\\Users\\tony_\\Desktop\\WinZ3805A-1.3.1.0-x64-Test\\WinZ3805A-1.3.1.0-x64",
        "16:00:44.735  download      WinZ3805A_1.3.1.0_x64.msixbundle, online, no .NET installer",
        "16:01:21.573  ok    It is running (process 2484).",
        "16:01:21.601  finished      started ok: True",
    ];

    /// <remarks>The first run of the upgrade test, which #589's parameter fix was for: the elevated
    /// half succeeded and the unelevated half then failed.</remarks>
    private static readonly string[] FalselyDeclined =
    [
        "15:33:33.443  download      WinZ3805A_1.3.1.0_x64.msixbundle, online, no .NET installer",
        "15:35:03.383  [elevated] certutil -addstore exited 0",
        "15:35:04.042  FAILED: The administrator prompt was declined, so nothing was changed. Run this installer again and agree to it.",
        "15:35:04.046    at line 497: throw 'The administrator prompt was declined, so nothing was changed. Run this installer again and agree to it.'",
    ];

    [Fact]
    public void AnUpgradeThatStartedReadsAsStarted()
    {
        InstallRecord record = InstallRecords.Parse("install-20260930-160044.log", Upgraded);

        Assert.Equal(InstallOutcome.Started, record.Outcome);
        Assert.Equal("1.3.1.0", record.Version);
        Assert.Equal(new DateTime(2026, 9, 30, 16, 0, 44), record.StartedAt);
        Assert.Null(record.Failure);
    }

    [Fact]
    public void AFailureCarriesTheInstallersOwnWords()
    {
        InstallRecord record = InstallRecords.Parse("install-20260930-153333.log", FalselyDeclined);

        Assert.Equal(InstallOutcome.Failed, record.Outcome);
        Assert.StartsWith("The administrator prompt was declined", record.Failure, StringComparison.Ordinal);
    }

    /// <remarks>The elevated half's own failure line is not the verdict: the person saw the
    /// unelevated half's, which follows it.</remarks>
    [Fact]
    public void AnElevatedFailureLineIsNotTheVerdict()
    {
        InstallRecord record = InstallRecords.Parse("install-20260930-170000.log",
        [
            "17:00:01.000  [elevated] FAILED: something only the elevated half saw",
            "17:00:02.000  finished      started ok: True",
        ]);

        Assert.Equal(InstallOutcome.Started, record.Outcome);
        Assert.Null(record.Failure);
    }

    [Theory]
    [InlineData("started ok: False", InstallOutcome.DidNotStart)]
    [InlineData("started ok: not checked", InstallOutcome.StartNotChecked)]
    public void TheStartChecksOtherVerdictsAreRead(string verdict, InstallOutcome expected)
    {
        InstallRecord record = InstallRecords.Parse("install-20260930-170000.log",
            [$"17:00:02.000  finished      {verdict}"]);

        Assert.Equal(expected, record.Outcome);
    }

    /// <remarks>A record with no verdict is not guessed at: the window was closed, or the machine
    /// went down.</remarks>
    [Fact]
    public void ARecordWithoutAnEndingIsUnfinished()
    {
        InstallRecord record = InstallRecords.Parse("install-20260930-170000.log", Upgraded[..3]);

        Assert.Equal(InstallOutcome.Unfinished, record.Outcome);
    }

    [Fact]
    public void AFileNameOutsideThePatternHasNoDate()
    {
        Assert.Null(InstallRecords.Parse("install-latest.log", Upgraded).StartedAt);
    }

    [Fact]
    public void TheNewestRecordIsReadFromTheFolder()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"wz602-{Guid.NewGuid():N}");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllLines(Path.Combine(folder, "install-20260930-153333.log"), FalselyDeclined);
            File.WriteAllLines(Path.Combine(folder, "install-20260930-160044.log"), Upgraded);

            InstallRecord? record = InstallRecords.ReadNewest(folder);

            Assert.NotNull(record);
            Assert.Equal("install-20260930-160044.log", record.FileName);
            Assert.Equal(InstallOutcome.Started, record.Outcome);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void NoFolderMeansNoRecordRatherThanAnError()
    {
        Assert.Null(InstallRecords.ReadNewest(Path.Combine(Path.GetTempPath(), $"wz602-missing-{Guid.NewGuid():N}")));
    }

    [Fact]
    public void TheSentenceSaysVersionDateAndResult()
    {
        string sentence = InstallRecords.Describe(
            InstallRecords.Parse("install-20260930-160044.log", Upgraded), CultureInfo.GetCultureInfo("en-GB"));

        Assert.Equal("Version 1.3.1.0, installed 30 September 2026 at 16:00. The installer started WinZ3805A and it got going.", sentence);
    }
}
