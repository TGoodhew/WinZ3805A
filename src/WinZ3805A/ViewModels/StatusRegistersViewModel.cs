using System.ComponentModel;
using System.Globalization;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Models;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;

namespace WinZ3805A.ViewModels;

/// <summary>
/// The §10.10 Status Registers page.
/// </summary>
/// <remarks>
/// <para>
/// The first page that <i>asks</i> the receiver something rather than reading the store. The
/// registers are not on either §7.3 cadence, and they must not be: <b>reading the event field
/// clears it</b>, so a page that polled would consume the very latches a user opened it to see.
/// §10.10 gives it a Refresh button for that reason, and this reads only when asked.
/// </para>
/// <para>
/// It goes through <see cref="DeviceSessionService"/>, which is what §12 means by view models not
/// touching the port: the session owns the command channel and serialises this against whatever
/// the poller is doing.
/// </para>
/// </remarks>
public sealed class StatusRegistersViewModel : INotifyPropertyChanged
{
    private readonly DeviceSessionService _session;

    private StatusRegisterMap _register = StatusRegisterMaps.Operation;
    private StatusRegisterReading? _reading;
    private bool _isReading;
    private string? _error;

    /// <summary>The pending mask edit, rebuilt whenever a fresh reading arrives.</summary>
    private RegisterMaskEdit _edit = new(null, null, null);

    /// <summary>
    /// The rows, cached against the reading they came from.
    /// </summary>
    /// <remarks>
    /// <b>Not rebuilt per read.</b> The mask columns are two-way bound checkboxes and the page runs
    /// a staleness ticker, so handing out fresh row objects every time <c>Rows</c> is read would
    /// throw the user's half-finished edit away roughly once a second. The same mistake is on
    /// record against the satellite table, where it silently broke selection.
    /// </remarks>
    private IReadOnlyList<RegisterBitRow> _rows = [];
    private StatusRegisterReading? _rowsFor;

    /// <summary>Creates a view model over the shared session.</summary>
    public StatusRegistersViewModel(DeviceSessionService session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The registers §10.10's picker offers.</summary>
    public static IReadOnlyList<StatusRegisterMap> Registers => StatusRegisterMaps.All;

    /// <summary>Which register is being shown.</summary>
    public StatusRegisterMap Register
    {
        get => _register;
        set
        {
            ArgumentNullException.ThrowIfNull(value);

            if (!ReferenceEquals(_register, value))
            {
                _register = value;

                // The previous register's bits say nothing about this one, and leaving them on
                // screen under a new heading would be the most misleading thing this page could do.
                _reading = null;
                _error = null;
                _edit = new RegisterMaskEdit(null, null, null);
                RaiseAll();
            }
        }
    }

    /// <summary>The last reading, or <see langword="null"/> if none has been taken.</summary>
    public StatusRegisterReading? Reading => _reading;

    /// <summary>Whether a read is in flight.</summary>
    public bool IsReading => _isReading;

    /// <summary>What went wrong, if anything did.</summary>
    public string? Error => _error;

    /// <summary>Whether the page can ask the receiver anything.</summary>
    public bool CanRead => !_isReading && _session.Status == ConnectionStatus.Connected;

    /// <summary>The rows to show, cached so an edit in progress survives a re-render.</summary>
    public IReadOnlyList<RegisterBitRow> Rows
    {
        get
        {
            if (ReferenceEquals(_rowsFor, _reading))
            {
                return _rows;
            }

            _rowsFor = _reading;
            _rows = _reading is StatusRegisterReading reading
                ? [.. reading.Rows.Select(row => new RegisterBitRow(row, _edit, OnMaskEdited))]
                : [];

            return _rows;
        }
    }

    /// <summary>Whether the user has changed a mask that has not been written yet.</summary>
    public bool IsDirty => _edit.IsDirty;

    /// <summary>Whether Apply may be pressed.</summary>
    public bool CanApplyMasks => IsDirty && !_isReading && _session.Status == ConnectionStatus.Connected;

    /// <summary>The masks still to write, in the order they should go.</summary>
    public IReadOnlyList<(RegisterMask Mask, int Value)> PendingWrites => _edit.PendingWrites;

    /// <summary>One line naming what Apply would do, for the card's caption.</summary>
    public string PendingText
    {
        get
        {
            IReadOnlyList<(RegisterMask Mask, int Value)> pending = _edit.PendingWrites;
            if (pending.Count == 0)
            {
                return string.Empty;
            }

            // Each write is separately tier C (§8.3), so the count is what tells the user how many
            // confirmations they are about to be asked for. Saying so up front is the difference
            // between a considered "yes, three" and three surprises.
            string masks = string.Join(", ", pending.Select(write => RegisterMaskEdit.Label(write.Mask)));
            return pending.Count == 1
                ? $"The {masks} mask has changed. Applying it needs one confirmation."
                : $"The {masks} masks have changed. Applying them needs {pending.Count} confirmations, one each.";
        }
    }

    /// <summary>Accepts a mask the receiver has taken, so Apply stops offering it.</summary>
    public void AcceptWrite(RegisterMask mask)
    {
        _edit.Accept(mask);
        RefreshRows();
        RaiseAll();
    }

    /// <summary>Throws away the pending edit.</summary>
    public void RevertEdits()
    {
        _edit.Revert();
        RefreshRows();
        RaiseAll();
    }

    /// <summary>
    /// Tells every cached row to re-read its checkboxes.
    /// </summary>
    /// <remarks>
    /// <c>RaiseAll</c> is not enough on its own. It re-raises the view model's own properties, and
    /// the page re-reads <c>Rows</c> — but that returns the <i>same</i> cached list, deliberately,
    /// so nothing rebinds and the checkboxes keep whatever they were showing.
    /// </remarks>
    private void RefreshRows()
    {
        foreach (RegisterBitRow row in _rows)
        {
            row.Refresh();
        }
    }

    private void OnMaskEdited() => RaiseAll();

    /// <summary>The raw values line at the foot of the §10.10 table.</summary>
    public string RawText
    {
        get
        {
            if (_reading is not StatusRegisterReading reading)
            {
                return string.Empty;
            }

            return string.Join("   ",
                Raw("CONDition", reading.Condition),
                Raw("EVENt", reading.Events),
                Raw("ENABle", reading.Enable),
                Raw("PTR", reading.PositiveTransition),
                Raw("NTR", reading.NegativeTransition));

            static string Raw(string label, int? value) => value is int number
                ? $"{label} +{number.ToString(CultureInfo.InvariantCulture)}"
                : $"{label} —";
        }
    }

    /// <summary>
    /// Reads all five fields of the selected register.
    /// </summary>
    /// <remarks>
    /// The event field is read last. It is the destructive one — reading it clears the latches —
    /// so if the transaction sequence is going to fail partway, it should fail before rather than
    /// after the field that cannot be read twice.
    /// </remarks>
    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (!CanRead)
        {
            return;
        }

        _isReading = true;
        _error = null;
        RaiseAll();

        try
        {
            StatusRegisterMap register = _register;

            int? condition = await ReadFieldAsync(register, "COND", cancellationToken).ConfigureAwait(true);
            int? enable = await ReadFieldAsync(register, "ENAB", cancellationToken).ConfigureAwait(true);
            int? positive = await ReadFieldAsync(register, "PTR", cancellationToken).ConfigureAwait(true);
            int? negative = await ReadFieldAsync(register, "NTR", cancellationToken).ConfigureAwait(true);
            int? events = await ReadFieldAsync(register, "EVEN", cancellationToken).ConfigureAwait(true);

            _reading = new StatusRegisterReading
            {
                Register = register,
                Condition = condition,
                Events = events,
                Enable = enable,
                PositiveTransition = positive,
                NegativeTransition = negative,
            };

            // A fresh reading starts a fresh edit. Keeping pending changes across a refresh would
            // mean writing bits computed from a mask the receiver has since changed underneath us.
            _edit = new RegisterMaskEdit(enable, positive, negative);

            if (!_reading.HasAnyValue)
            {
                _error = "The receiver did not answer any of this register's fields.";
            }
        }
        catch (OperationCanceledException)
        {
            // The page was navigated away from mid-read. Nothing to report to a page nobody is on.
        }
        finally
        {
            _isReading = false;
            RaiseAll();
        }
    }

    private async Task<int?> ReadFieldAsync(
        StatusRegisterMap register,
        string field,
        CancellationToken cancellationToken)
    {
        string mnemonic = $":STAT:{register.Node}:{field}?";

        // Resolved from the catalog rather than sent as a string: §8.1 makes the catalog an
        // allowlist, and ExecuteAsync takes an ScpiCommand precisely so nothing can route around it.
        if (CommandCatalog.Find(mnemonic) is not ScpiCommand command)
        {
            _error = $"{mnemonic} is not in the command catalog.";
            return null;
        }

        Transaction transaction = await _session.ExecuteAsync(command, cancellationToken: cancellationToken)
            .ConfigureAwait(true);

        if (!transaction.Succeeded || transaction.Lines.Count == 0)
        {
            _error ??= transaction.PromptStatus is string status
                ? $"The receiver answered {status} to {mnemonic}."
                : $"No answer to {mnemonic}.";
            return null;
        }

        // Responses carry a leading space (#78) and the register masks are plain integers.
        return int.TryParse(
            transaction.Lines[0].Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out int value)
            ? value
            : null;
    }

    /// <summary>Raises <see cref="PropertyChanged"/> for every property.</summary>
    public void RaiseAll() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>
/// One §10.10 table row, with the marks it is drawn with.
/// </summary>
/// <remarks>
/// The glyphs live here rather than on <see cref="StatusBitReading"/> because which mark stands for
/// a set bit is a §9 decision, and the device library holds what the receiver said, not how it is
/// drawn. The wireframe's own marks are used: filled and hollow circles for the two live fields,
/// ticked and empty boxes for the three masks — which are settings rather than states.
/// </remarks>
public sealed class RegisterBitRow : INotifyPropertyChanged
{
    private readonly StatusBitReading _reading;
    private readonly RegisterMaskEdit _edit;
    private readonly Action _changed;

    /// <summary>Wraps a decoded bit for display and for editing its three masks.</summary>
    /// <param name="reading">What the receiver said about this bit.</param>
    /// <param name="edit">The pending mask edit the checkboxes read and write.</param>
    /// <param name="changed">Raised after a mask is toggled, so the page can re-render Apply.</param>
    public RegisterBitRow(StatusBitReading reading, RegisterMaskEdit edit, Action changed)
    {
        ArgumentNullException.ThrowIfNull(reading);
        ArgumentNullException.ThrowIfNull(edit);
        ArgumentNullException.ThrowIfNull(changed);

        _reading = reading;
        _edit = edit;
        _changed = changed;
    }

    /// <summary>Whether the enable mask may be edited on this row.</summary>
    /// <remarks>
    /// False when the receiver did not answer for that mask. §11.1: unread is not zero, and a
    /// checkbox offering to write a bit into a value nobody has seen is worse than a blank.
    /// </remarks>
    public bool CanEditEnable => _edit.IsEditable(RegisterMask.Enable);

    /// <summary>Whether the positive-transition mask may be edited on this row.</summary>
    public bool CanEditPositive => _edit.IsEditable(RegisterMask.PositiveTransition);

    /// <summary>Whether the negative-transition mask may be edited on this row.</summary>
    public bool CanEditNegative => _edit.IsEditable(RegisterMask.NegativeTransition);

    /// <summary>The enable mask's bit, two-way bound to its checkbox.</summary>
    public bool EnableBit
    {
        get => _edit.IsSet(RegisterMask.Enable, Bit);
        set => Toggle(RegisterMask.Enable, value);
    }

    /// <summary>The positive-transition mask's bit.</summary>
    public bool PositiveBit
    {
        get => _edit.IsSet(RegisterMask.PositiveTransition, Bit);
        set => Toggle(RegisterMask.PositiveTransition, value);
    }

    /// <summary>The negative-transition mask's bit.</summary>
    public bool NegativeBit
    {
        get => _edit.IsSet(RegisterMask.NegativeTransition, Bit);
        set => Toggle(RegisterMask.NegativeTransition, value);
    }

    private void Toggle(RegisterMask mask, bool value)
    {
        if (_edit.SetBit(mask, Bit, value))
        {
            _changed();
        }
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Re-reads every checkbox from the pending edit.
    /// </summary>
    /// <remarks>
    /// Needed because the edit can change without the row being touched — Discard puts every mask
    /// back at once, and a write that succeeds accepts one. Without this the row is a one-way
    /// mirror: the model reverts, <c>Apply</c> greys out, and the checkbox stays ticked, which is
    /// the worst of both because the screen and the instrument now disagree about what is pending.
    /// </remarks>
    public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));

    /// <summary>Which bit this is.</summary>
    public int Bit => _reading.Bit;

    /// <summary>What it means.</summary>
    public string MeaningText => _reading.MeaningText;

    /// <summary>Whether it is currently reporting a fault.</summary>
    public bool IsRaised => _reading.IsRaised;

    /// <summary>The condition mark.</summary>
    public string ConditionText => State(_reading.Condition);

    /// <summary>The event mark.</summary>
    public string EventText => State(_reading.Event);

    /// <summary>Names the enable checkbox for a screen reader.</summary>
    /// <remarks>
    /// A checkbox in a grid announces itself as "checkbox" and nothing else. The row carries a full
    /// sentence, but that does not say which of three columns has focus, so each says its own bit
    /// and mask.
    /// </remarks>
    public string EnableName => $"Bit {Bit} enable mask";

    /// <summary>Names the positive-transition checkbox.</summary>
    public string PositiveName => $"Bit {Bit} positive transition mask";

    /// <summary>Names the negative-transition checkbox.</summary>
    public string NegativeName => $"Bit {Bit} negative transition mask";

    /// <summary>The enable-mask mark.</summary>
    public string EnableText => Mask(_reading.Enable);

    /// <summary>The positive-transition mask mark.</summary>
    public string PositiveTransitionText => Mask(_reading.PositiveTransition);

    /// <summary>The negative-transition mask mark.</summary>
    public string NegativeTransitionText => Mask(_reading.NegativeTransition);

    /// <summary>
    /// One sentence naming every column, for the row's automation name.
    /// </summary>
    /// <remarks>
    /// A screen reader announcing six circles and boxes conveys nothing. §9.4.3's rule that nothing
    /// is carried by shape alone applies to a table of shapes more than anywhere else on this page.
    /// </remarks>
    public string Description
    {
        get
        {
            string state = _reading.Condition switch
            {
                true => "condition present",
                false => "condition clear",
                _ => "condition not read",
            };

            string latched = _reading.Event == true ? ", event latched" : string.Empty;
            string fault = IsRaised ? ", reporting a fault" : string.Empty;

            return $"Bit {Bit}, {MeaningText}: {state}{latched}{fault}";
        }
    }

    /// <summary>Filled when set, hollow when clear, a dash when the field was not read.</summary>
    private static string State(bool? value) => value switch
    {
        true => "●",
        false => "○",
        _ => ReadoutFormatter.NoValue,
    };

    /// <summary>Ticked when set, empty when clear, a dash when the field was not read.</summary>
    private static string Mask(bool? value) => value switch
    {
        true => "☑",
        false => "☐",
        _ => ReadoutFormatter.NoValue,
    };
}
