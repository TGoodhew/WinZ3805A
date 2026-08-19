using WinZ3805A.ViewModels;

namespace WinZ3805A.Tests.ViewModels;

/// <summary>
/// P1-4's mask editing (#52). §8.3 makes every mask write tier C, so what matters here is not that
/// a bit toggles but that nothing is written which the user did not change.
/// </summary>
public sealed class RegisterMaskEditTests
{
    private static RegisterMaskEdit Edit(int? enable = 0, int? positive = 0, int? negative = 0) =>
        new(enable, positive, negative);

    [Fact]
    public void AFreshEditIsNotDirtyAndHasNothingToWrite()
    {
        RegisterMaskEdit edit = Edit(enable: 7, positive: 7, negative: 4);

        Assert.False(edit.IsDirty);
        Assert.Empty(edit.PendingWrites);
    }

    [Fact]
    public void SettingABitTurnsItOnAndMarksOnlyThatMaskChanged()
    {
        RegisterMaskEdit edit = Edit();

        Assert.True(edit.SetBit(RegisterMask.Enable, 3, true));

        Assert.True(edit.IsSet(RegisterMask.Enable, 3));
        Assert.Equal(8, edit.Value(RegisterMask.Enable));
        Assert.True(edit.IsChanged(RegisterMask.Enable));
        Assert.False(edit.IsChanged(RegisterMask.PositiveTransition));
        Assert.False(edit.IsChanged(RegisterMask.NegativeTransition));
    }

    [Fact]
    public void ClearingABitLeavesTheOthersAlone()
    {
        RegisterMaskEdit edit = Edit(enable: 0b1011);

        Assert.True(edit.SetBit(RegisterMask.Enable, 1, false));

        Assert.Equal(0b1001, edit.Value(RegisterMask.Enable));
    }

    [Fact]
    public void SettingABitThatIsAlreadySetChangesNothing()
    {
        RegisterMaskEdit edit = Edit(enable: 0b100);

        Assert.False(edit.SetBit(RegisterMask.Enable, 2, true));
        Assert.False(edit.IsDirty);
    }

    /// <summary>
    /// Toggling out and back is not a change, so Apply must not offer to write an identical value.
    /// </summary>
    [Fact]
    public void ReturningAMaskToItsReadValueClearsTheDirtyFlag()
    {
        RegisterMaskEdit edit = Edit(enable: 5);

        edit.SetBit(RegisterMask.Enable, 1, true);
        Assert.True(edit.IsDirty);

        edit.SetBit(RegisterMask.Enable, 1, false);

        Assert.False(edit.IsDirty);
        Assert.Empty(edit.PendingWrites);
    }

    /// <summary>
    /// §11.1: unread is not zero. A mask the receiver never answered for cannot be edited, because
    /// writing a computed 0 back would clear every bit the user was unable to see.
    /// </summary>
    [Fact]
    public void AMaskThatWasNeverReadIsNotEditable()
    {
        RegisterMaskEdit edit = new(enable: null, positive: 3, negative: null);

        Assert.False(edit.IsEditable(RegisterMask.Enable));
        Assert.True(edit.IsEditable(RegisterMask.PositiveTransition));
        Assert.False(edit.IsEditable(RegisterMask.NegativeTransition));

        Assert.False(edit.SetBit(RegisterMask.Enable, 0, true));
        Assert.Null(edit.Value(RegisterMask.Enable));
        Assert.False(edit.IsDirty);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(16)]
    [InlineData(31)]
    public void ABitOutsideTheRegisterIsRefused(int bit)
    {
        RegisterMaskEdit edit = Edit();

        Assert.False(edit.SetBit(RegisterMask.Enable, bit, true));
        Assert.False(edit.IsDirty);
    }

    [Fact]
    public void OnlyTheChangedMasksAreWritten()
    {
        RegisterMaskEdit edit = Edit(enable: 1, positive: 2, negative: 3);

        edit.SetBit(RegisterMask.NegativeTransition, 4, true);

        (RegisterMask mask, int value) = Assert.Single(edit.PendingWrites);
        Assert.Equal(RegisterMask.NegativeTransition, mask);
        Assert.Equal(3 | 16, value);
    }

    /// <summary>
    /// Enable is written last on purpose. It is what lets a bit reach the summary byte, so writing
    /// it first arms the register against transition masks that have not been set yet and can latch
    /// an event nobody asked for.
    /// </summary>
    [Fact]
    public void EnableIsWrittenAfterTheTransitionMasks()
    {
        RegisterMaskEdit edit = Edit();

        edit.SetBit(RegisterMask.Enable, 0, true);
        edit.SetBit(RegisterMask.PositiveTransition, 1, true);
        edit.SetBit(RegisterMask.NegativeTransition, 2, true);

        Assert.Equal(
            [RegisterMask.PositiveTransition, RegisterMask.NegativeTransition, RegisterMask.Enable],
            edit.PendingWrites.Select(write => write.Mask));
    }

    [Fact]
    public void RevertingGoesBackToWhatWasRead()
    {
        RegisterMaskEdit edit = Edit(enable: 9, positive: 9, negative: 9);

        edit.SetBit(RegisterMask.Enable, 2, true);
        edit.SetBit(RegisterMask.PositiveTransition, 2, true);

        edit.Revert();

        Assert.False(edit.IsDirty);
        Assert.Equal(9, edit.Value(RegisterMask.Enable));
        Assert.Equal(9, edit.Value(RegisterMask.PositiveTransition));
    }

    /// <summary>
    /// A write that succeeded stops being pending, so a second Apply does not re-send it. The other
    /// masks keep their edits, because one confirmation being accepted says nothing about another.
    /// </summary>
    [Fact]
    public void AcceptingOneWriteLeavesTheOtherMasksPending()
    {
        RegisterMaskEdit edit = Edit();

        edit.SetBit(RegisterMask.Enable, 0, true);
        edit.SetBit(RegisterMask.PositiveTransition, 0, true);

        edit.Accept(RegisterMask.PositiveTransition);

        Assert.False(edit.IsChanged(RegisterMask.PositiveTransition));
        Assert.True(edit.IsChanged(RegisterMask.Enable));
        Assert.True(edit.IsDirty);

        (RegisterMask mask, _) = Assert.Single(edit.PendingWrites);
        Assert.Equal(RegisterMask.Enable, mask);
    }

    /// <summary>The field names have to match what the catalog registers, or nothing resolves.</summary>
    [Theory]
    [InlineData(RegisterMask.Enable, "ENABle")]
    [InlineData(RegisterMask.PositiveTransition, "PTRansition")]
    [InlineData(RegisterMask.NegativeTransition, "NTRansition")]
    public void TheFieldNamesAreTheCatalogSpellings(RegisterMask mask, string expected) =>
        Assert.Equal(expected, RegisterMaskEdit.Field(mask));

    [Fact]
    public void EveryMaskHasALabelForTheConfirmationDialog() =>
        Assert.All(
            Enum.GetValues<RegisterMask>(),
            mask => Assert.False(string.IsNullOrWhiteSpace(RegisterMaskEdit.Label(mask))));

    /// <summary>Bit 15 is the top of a 16-bit register and is where a sign error would show.</summary>
    [Fact]
    public void TheTopBitBehavesLikeTheRest()
    {
        RegisterMaskEdit edit = Edit();

        Assert.True(edit.SetBit(RegisterMask.Enable, 15, true));
        Assert.Equal(32768, edit.Value(RegisterMask.Enable));
        Assert.True(edit.IsSet(RegisterMask.Enable, 15));
    }
}
