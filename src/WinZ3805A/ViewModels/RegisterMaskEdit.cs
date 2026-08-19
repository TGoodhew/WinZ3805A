namespace WinZ3805A.ViewModels;

/// <summary>Which of a status register's three writable masks a bit belongs to.</summary>
/// <remarks>
/// §10.10's table has five columns of marks. Condition and Event are what the receiver reports and
/// are not editable; these three are settings, which is why the wireframe draws them as boxes
/// rather than circles.
/// </remarks>
public enum RegisterMask
{
    /// <summary><c>:ENABle</c> — which bits are allowed to propagate to the summary byte.</summary>
    Enable,

    /// <summary><c>:PTRansition</c> — which bits latch an event when they go from 0 to 1.</summary>
    PositiveTransition,

    /// <summary><c>:NTRansition</c> — which bits latch an event when they go from 1 to 0.</summary>
    NegativeTransition,
}

/// <summary>
/// The pending edit to one status register's three writable masks (P1-4).
/// </summary>
/// <remarks>
/// <para>
/// Bit arithmetic and a dirty check, with no UI type in sight, so both are testable headlessly.
/// The part worth asserting is not that a checkbox toggles: it is that a mask the user did not
/// touch is never written. Each of these is a tier C command against a working instrument, and
/// sending three where the user changed one is three confirmations and two needless writes.
/// </para>
/// <para>
/// A mask the receiver did not answer for stays <see langword="null"/> and cannot be edited. §11.1's
/// rule is that unread is not zero — treating a missing mask as 0 and writing it back would clear
/// every bit the user could not see.
/// </para>
/// </remarks>
public sealed class RegisterMaskEdit
{
    private readonly Dictionary<RegisterMask, int> _read = [];
    private readonly Dictionary<RegisterMask, int> _edited = [];

    /// <summary>Starts an edit from what the receiver reported.</summary>
    /// <param name="enable">The <c>:ENABle</c> value, or <see langword="null"/> if unread.</param>
    /// <param name="positive">The <c>:PTRansition</c> value, or <see langword="null"/>.</param>
    /// <param name="negative">The <c>:NTRansition</c> value, or <see langword="null"/>.</param>
    public RegisterMaskEdit(int? enable, int? positive, int? negative)
    {
        Set(RegisterMask.Enable, enable);
        Set(RegisterMask.PositiveTransition, positive);
        Set(RegisterMask.NegativeTransition, negative);

        void Set(RegisterMask mask, int? value)
        {
            if (value is int number)
            {
                _read[mask] = number;
                _edited[mask] = number;
            }
        }
    }

    /// <summary>Whether this mask was read and can therefore be edited.</summary>
    public bool IsEditable(RegisterMask mask) => _read.ContainsKey(mask);

    /// <summary>The current, possibly edited, value of a mask, or <see langword="null"/> if unread.</summary>
    public int? Value(RegisterMask mask) => _edited.TryGetValue(mask, out int value) ? value : null;

    /// <summary>Whether a bit is set in a mask's current value.</summary>
    public bool IsSet(RegisterMask mask, int bit) =>
        Value(mask) is int value && (value & (1 << bit)) != 0;

    /// <summary>Sets or clears one bit of one mask.</summary>
    /// <returns><see langword="true"/> if anything changed.</returns>
    /// <remarks>
    /// A no-op on a mask that was never read, rather than an exception: the page disables those
    /// checkboxes, and a race between a refresh and a click should not throw.
    /// </remarks>
    public bool SetBit(RegisterMask mask, int bit, bool set)
    {
        if (!_edited.TryGetValue(mask, out int value) || bit is < 0 or > 15)
        {
            return false;
        }

        int updated = set ? value | (1 << bit) : value & ~(1 << bit);
        if (updated == value)
        {
            return false;
        }

        _edited[mask] = updated;
        return true;
    }

    /// <summary>Whether a mask differs from what was read.</summary>
    public bool IsChanged(RegisterMask mask) =>
        _read.TryGetValue(mask, out int read) && _edited[mask] != read;

    /// <summary>Whether anything at all has been changed.</summary>
    public bool IsDirty => Enum.GetValues<RegisterMask>().Any(IsChanged);

    /// <summary>The masks that changed, in the order they should be written, with their values.</summary>
    /// <remarks>
    /// Enable last. It is the one that lets a bit reach the summary byte and so the one that makes
    /// the others take effect; writing it first would arm the register against transition masks
    /// that have not been set yet, which can latch an event the user did not ask for.
    /// </remarks>
    public IReadOnlyList<(RegisterMask Mask, int Value)> PendingWrites =>
    [
        .. new[] { RegisterMask.PositiveTransition, RegisterMask.NegativeTransition, RegisterMask.Enable }
            .Where(IsChanged)
            .Select(mask => (mask, _edited[mask])),
    ];

    /// <summary>Throws away the edits and goes back to what was read.</summary>
    public void Revert()
    {
        foreach ((RegisterMask mask, int value) in _read)
        {
            _edited[mask] = value;
        }
    }

    /// <summary>Accepts a write as done, so the mask stops counting as changed.</summary>
    public void Accept(RegisterMask mask)
    {
        if (_edited.TryGetValue(mask, out int value))
        {
            _read[mask] = value;
        }
    }

    /// <summary>The SCPI field name for a mask, as the catalog spells it.</summary>
    public static string Field(RegisterMask mask) => mask switch
    {
        RegisterMask.Enable => "ENABle",
        RegisterMask.PositiveTransition => "PTRansition",
        RegisterMask.NegativeTransition => "NTRansition",
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };

    /// <summary>What the mask is called in the interface and in a confirmation dialog.</summary>
    public static string Label(RegisterMask mask) => mask switch
    {
        RegisterMask.Enable => "enable",
        RegisterMask.PositiveTransition => "positive transition",
        RegisterMask.NegativeTransition => "negative transition",
        _ => throw new ArgumentOutOfRangeException(nameof(mask)),
    };
}
