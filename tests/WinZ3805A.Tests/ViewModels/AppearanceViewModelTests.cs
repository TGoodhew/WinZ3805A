using WinZ3805A.Controls;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>§9.4.2's opt-in, decided without a resource dictionary in sight.</summary>
public sealed class AppearanceViewModelTests
{
    /// <summary>Windows' "Gold", which collides with dark-theme caution at 11.8.</summary>
    private static AccentRamp Gold { get; } = new(
        new Rgb(0x30, 0x1A, 0x00),
        new Rgb(0x60, 0x35, 0x00),
        new Rgb(0xC4, 0x6D, 0x00),
        new Rgb(0xFF, 0x8C, 0x00),
        new Rgb(0xFF, 0xA5, 0x33),
        new Rgb(0xFF, 0xBE, 0x66),
        new Rgb(0xFF, 0xD7, 0x99));

    /// <summary>Windows' default blue, clear of every semantic colour by at least 46.</summary>
    private static AccentRamp Blue { get; } = new(
        new Rgb(0x00, 0x30, 0x54),
        new Rgb(0x00, 0x48, 0x7E),
        new Rgb(0x00, 0x60, 0xA9),
        new Rgb(0x00, 0x78, 0xD4),
        new Rgb(0x33, 0x93, 0xDD),
        new Rgb(0x66, 0xAE, 0xE6),
        new Rgb(0x99, 0xC9, 0xEF));

    // ---------------------------------------------------------------------------- which ramp wins

    [Fact]
    public void TheBrandRampIsTheDefault() =>
        Assert.Equal(
            AccentRamp.Brand,
            AppearanceViewModel.Resolve(AppearancePreferences.Default, Blue));

    [Fact]
    public void OptingInUsesTheSystemRamp() =>
        Assert.Equal(
            Blue,
            AppearanceViewModel.Resolve(new AppearancePreferences { UseSystemAccent = true }, Blue));

    /// <summary>An unreadable system accent falls back rather than failing.</summary>
    [Fact]
    public void TheBrandRampIsUsedWhenTheSystemAccentCannotBeRead() =>
        Assert.Equal(
            AccentRamp.Brand,
            AppearanceViewModel.Resolve(new AppearancePreferences { UseSystemAccent = true }, null));

    // ------------------------------------------------------------------- when the warning is owed

    /// <summary>The brand accent never warns, because it cannot collide.</summary>
    [Fact]
    public void NotOptingInNeverWarns() =>
        Assert.Null(AppearanceViewModel.WarningFor(AppearancePreferences.Default, Gold));

    [Fact]
    public void OptingInToAClearAccentDoesNotWarn() =>
        Assert.Null(
            AppearanceViewModel.WarningFor(new AppearancePreferences { UseSystemAccent = true }, Blue));

    [Fact]
    public void OptingInToACollidingAccentWarns()
    {
        AccentCollision? warning = AppearanceViewModel.WarningFor(
            new AppearancePreferences { UseSystemAccent = true },
            Gold);

        Assert.NotNull(warning);
        Assert.Equal("caution", warning!.Value.Colour.Name);
    }

    // ------------------------------------------------------------- what the acknowledgement means

    /// <summary>Dismissing the warning silences it for that accent.</summary>
    [Fact]
    public void AnAcknowledgedAccentIsNotWarnedAboutAgain()
    {
        AppearancePreferences opted = new() { UseSystemAccent = true };
        AppearancePreferences after = AppearanceViewModel.Acknowledge(opted, Gold);

        Assert.Equal("#FF8C00", after.AcknowledgedAccent);
        Assert.Null(AppearanceViewModel.WarningFor(after, Gold));
    }

    /// <summary>
    /// But it silences it only for <i>that</i> accent — the point of storing the colour.
    /// </summary>
    /// <remarks>
    /// The failure this prevents: a user dismisses the tip for one warm accent, later picks a red
    /// one in Windows, and is never told that "selected" and "critical" have become the same
    /// colour. A boolean alone would produce exactly that, and would look correct in every test
    /// that only ever used one accent.
    /// </remarks>
    [Fact]
    public void ChangingToADifferentCollidingAccentWarnsAgain()
    {
        AppearancePreferences after = AppearanceViewModel.Acknowledge(
            new AppearancePreferences { UseSystemAccent = true },
            Gold);

        AccentRamp red = Gold with { Base = new Rgb(0xE7, 0x48, 0x56) };

        Assert.NotNull(AppearanceViewModel.WarningFor(after, red));
    }

    /// <summary>Reverting is not a dismissal, and leaves nothing behind.</summary>
    [Fact]
    public void RevertingClearsTheOptInAndTheAcknowledgement()
    {
        AppearancePreferences reverted = AppearanceViewModel.Revert(
            AppearanceViewModel.Acknowledge(new AppearancePreferences { UseSystemAccent = true }, Gold));

        Assert.False(reverted.UseSystemAccent);
        Assert.False(reverted.HasAcknowledgedCollision);
        Assert.Null(reverted.AcknowledgedAccent);
        Assert.Equal(AccentRamp.Brand, AppearanceViewModel.Resolve(reverted, Gold));

        // And opting in again owes the warning a second time.
        Assert.NotNull(
            AppearanceViewModel.WarningFor(reverted with { UseSystemAccent = true }, Gold));
    }
}
