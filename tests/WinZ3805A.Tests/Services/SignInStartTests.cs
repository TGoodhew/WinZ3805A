using WinZ3805A.Services;

namespace WinZ3805A.Tests.Services;

/// <summary>
/// #548 — what the start-at-sign-in control shows, and which setting a launch follows.
/// </summary>
/// <remarks>
/// The startup task itself is WinRT and cannot be reached from here; these pin everything around it.
/// The state is Windows', read fresh each time, so every one of its values is a case the control
/// will meet — including the ones this application cannot change.
/// </remarks>
public sealed class SignInStartTests
{
    // ------------------------------------------------------------------ what the control shows

    [Theory]
    [InlineData(true, SignInStartChoice.NotificationArea)]
    [InlineData(false, SignInStartChoice.Window)]
    public void AnEnabledTaskShowsHowItStarts(bool hidden, SignInStartChoice expected)
    {
        SignInStartView view = SignInStartPolicy.For(SignInTaskState.Enabled, hidden);

        Assert.Equal(expected, view.Choice);
        Assert.True(view.CanChange);
        Assert.Null(view.Note);
    }

    [Fact]
    public void ADisabledTaskShowsOffAndCanBeTurnedOn() =>
        Assert.Equal(
            new SignInStartView(SignInStartChoice.Off, CanChange: true, Note: null),
            SignInStartPolicy.For(SignInTaskState.Disabled, startHidden: true));

    /// <summary>
    /// <b>Switched off in Windows, it cannot be switched back on from here</b>, so the control says so
    /// rather than accepting a choice that would quietly snap back to Off.
    /// </summary>
    [Fact]
    public void ATaskTheUserTurnedOffInWindowsSaysWhereToTurnItOn()
    {
        SignInStartView view = SignInStartPolicy.For(SignInTaskState.DisabledByUser, startHidden: true);

        Assert.Equal(SignInStartChoice.Off, view.Choice);
        Assert.False(view.CanChange);
        Assert.Contains("Startup", view.Note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(SignInTaskState.DisabledByPolicy, SignInStartChoice.Off)]
    [InlineData(SignInTaskState.EnabledByPolicy, SignInStartChoice.NotificationArea)]
    public void APolicyDecisionIsShownAndCannotBeChanged(SignInTaskState state, SignInStartChoice expected)
    {
        SignInStartView view = SignInStartPolicy.For(state, startHidden: true);

        Assert.Equal(expected, view.Choice);
        Assert.False(view.CanChange);
        Assert.Contains("organisation", view.Note, StringComparison.Ordinal);
    }

    /// <remarks>
    /// Outside a package, or when the call fails. Off and unchangeable is the honest reading: the
    /// application does not know, and a control that looked usable would fail on first touch.
    /// </remarks>
    [Fact]
    public void AnUnreadableTaskShowsOffAndCannotBeChanged()
    {
        SignInStartView view = SignInStartPolicy.For(SignInTaskState.Unavailable, startHidden: false);

        Assert.Equal(SignInStartChoice.Off, view.Choice);
        Assert.False(view.CanChange);
        Assert.NotNull(view.Note);
    }

    /// <summary>Every state Windows can report has an answer, including any added later.</summary>
    [Fact]
    public void EveryStateHasAView()
    {
        foreach (SignInTaskState state in Enum.GetValues<SignInTaskState>())
        {
            SignInStartView view = SignInStartPolicy.For(state, startHidden: true);

            Assert.True(view.CanChange || view.Note is not null, $"{state} is locked without a reason");
        }
    }

    /// <summary>The control's three items are in the enum's order, which the page relies on.</summary>
    [Fact]
    public void TheChoicesAreInTheControlsOrder()
    {
        Assert.Equal(0, (int)SignInStartChoice.Off);
        Assert.Equal(1, (int)SignInStartChoice.NotificationArea);
        Assert.Equal(2, (int)SignInStartChoice.Window);
    }

    // ------------------------------------------------------------------ which setting a launch follows

    /// <summary>
    /// <b>The point of having two settings.</b> Quiet at sign-in must not make a start by hand
    /// windowless, which looks exactly like a failed start (§10.13).
    /// </summary>
    [Theory]
    [InlineData(true, true, false, true)]    // sign-in, hidden at sign-in, not by hand -> hidden
    [InlineData(true, false, true, false)]   // sign-in, window at sign-in, hidden by hand -> window
    [InlineData(false, true, false, false)]  // by hand, hidden at sign-in, not by hand -> window
    [InlineData(false, false, true, true)]   // by hand, window at sign-in, hidden by hand -> hidden
    public void ALaunchFollowsItsOwnSetting(bool signIn, bool hiddenAtSignIn, bool hiddenByHand, bool expected)
    {
        AdvancedPreferences preferences = new() { StartAtSignInHidden = hiddenAtSignIn, StartMinimised = hiddenByHand };

        Assert.Equal(expected, SignInStartPolicy.StartsHidden(new LaunchContext(signIn), preferences));
    }

    [Fact]
    public void ALaunchByHandIsNotASignInStart() =>
        Assert.False(LaunchContext.ByHand.IsSignInStart);

    // ------------------------------------------------------------------ the stored preference

    [Fact]
    public void ASignInStartIsHiddenByDefault() =>
        Assert.True(AdvancedPreferences.Default.StartAtSignInHidden);

    /// <summary>A file written before #548 has no such field, and reads as the default.</summary>
    [Fact]
    public void AFileFromBeforeTheSettingReadsTheDefault()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"wz-signin-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "advanced.json");

        try
        {
            Directory.CreateDirectory(folder);
            File.WriteAllText(path, """{ "KeepRunningWhenClosed": true, "StartMinimised": true }""");

            AdvancedPreferences read = new LocalAdvancedPreferenceStore(path).Load();

            Assert.True(read.StartAtSignInHidden);
            Assert.True(read.StartMinimised);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheChoiceSurvivesARestart()
    {
        string folder = Path.Combine(Path.GetTempPath(), $"wz-signin-{Guid.NewGuid():N}");
        string path = Path.Combine(folder, "advanced.json");

        try
        {
            new LocalAdvancedPreferenceStore(path).Save(new AdvancedPreferences { StartAtSignInHidden = false });

            Assert.False(new LocalAdvancedPreferenceStore(path).Load().StartAtSignInHidden);
        }
        finally
        {
            Directory.Delete(folder, recursive: true);
        }
    }
}
