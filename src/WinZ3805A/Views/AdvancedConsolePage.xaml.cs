using System.Globalization;
using System.Text;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.11 Advanced Console: a picker over the catalog, and a transcript of the wire.
/// </summary>
/// <remarks>
/// <para>
/// <b>There is no path from this page to a command the catalog does not hold.</b> The picker's
/// items come from <see cref="ConsoleCatalog"/>, which projects the driver's allowlist; the
/// session takes an <see cref="ScpiCommand"/> and has no overload accepting text; and the one free
/// text field on the page filters that list rather than feeding it. The §8.4 exclusions are not in
/// the catalog, so they are not here — absent, not filtered out.
/// </para>
/// <para>
/// <b>Tier C is not relaxed here.</b> A confirm-tier command selected in the console raises the
/// same §8.3 dialog, with the same consequence sentence and the same acknowledgement checkbox, as
/// it does from the page that owns it. "Advanced" describes who is looking, not what the rules are.
/// </para>
/// </remarks>
public sealed partial class AdvancedConsolePage : Page, ICsvExportSource
{
    private DeviceContext? _device;

    /// <summary>The picker's list, projected from the device's driver in <c>OnNavigatedTo</c> (#287).</summary>
    private ConsoleCatalog? _catalog;
    private CommandInvoker? _invoker;
    private CommandTranscript? _transcript;

    private ConsoleCommand? _selected;
    private ConsoleArgument.Result _argument;
    /// <summary>The width every parameter editor shares, so a nine-field form lines up.</summary>
    private const int FieldWidth = 320;

    /// <summary>How to read each editor, in the order the receiver wants the values.</summary>
    private readonly List<Func<string?>> _readers = [];

    private bool _busy;

    /// <summary>False until the picker has been populated, so its own events are ignored.</summary>
    private bool _ready;

    /// <summary>Creates the page.</summary>
    public AdvancedConsolePage()
    {
        InitializeComponent();

        // §9.7.4's right-click layer, on the transcript this page exports.
        CopyMenu.AttachCsv(TranscriptCard, this);

        Unloaded += (_, _) => Detach();
    }

    /// <summary>Undoes everything <see cref="OnNavigatedTo"/> subscribed to (#388).</summary>
    /// <remarks>
    /// Idempotent: both <c>Unloaded</c> and <see cref="OnNavigatedFrom"/> call it, and neither is
    /// reliable alone. Disposing the model is the half that matters - it is what lets go of the
    /// store, which outlives every page and was keeping this one alive after it left the screen.
    /// </remarks>
    private void Detach()
    {
        if (_device is DeviceContext device)
        {
            device.Session.StatusChanged -= OnStatusChanged;
        }

    }

    /// <inheritdoc />
    /// <remarks>
    /// <b>The Frame's hook, not Unloaded (#388).</b> Everything this page subscribed to in
    /// <see cref="OnNavigatedTo"/> is undone here, and the model is disposed so it lets go of the
    /// store. Unloaded was doing half the job and could not do the other half: the store outlives
    /// every page, so store -> model -> page kept the page alive and rendering on every reading
    /// after it left the screen, once per visit. Four visits to Overview left four of them.
    /// </remarks>
    protected override void OnNavigatedFrom(NavigationEventArgs e)
    {
        base.OnNavigatedFrom(e);
        Detach();
    }

    /// <inheritdoc />
    protected override void OnNavigatedTo(NavigationEventArgs e)
    {
        base.OnNavigatedTo(e);

        if (e?.Parameter is not DeviceContext device)
        {
            return;
        }

        _device = device;
        _invoker = new CommandInvoker(device.Session);
        _transcript = device.Transcript;
        _transcript.Changed += OnTranscriptChanged;

        // _ready first: BindDriver selects the first command, and the picker's own
        // SelectionChanged is what builds the parameter editor for it.
        _ready = true;
        BindDriver();

        device.Session.StatusChanged += OnStatusChanged;

        RenderTranscript();
        Render();
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (e?.Status == ConnectionStatus.Connected)
            {
                // The receiver on the port can have been swapped while the link was down, so the
                // session re-selects a driver on every connect (#287) and this page's answer to
                // "what may I offer" has to be asked again rather than kept from navigation (#304).
                BindDriver();
            }

            Render();
        });

    /// <summary>
    /// Rebuilds §10.11's picker from the connected receiver's driver (#304).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This page <b>is</b> §8.1's allowlist made visible — there is no free-text box (#55), so the
    /// picker's contents are the whole of what a user can send. A list kept from navigation would
    /// therefore be the one place in the application where a reconnect to a different family offers
    /// commands that family has never heard of, and the failure would be a receiver error rather
    /// than anything the interface said.
    /// </para>
    /// <para>
    /// The filter and the selection are reset rather than carried across, because a
    /// <see cref="ConsoleCommand"/> from the old catalog is not an entry in the new one even when
    /// the two spell the same mnemonic: <c>Matching</c> would keep it, <c>Send</c> would run it, and
    /// the value it carried would be validated against the wrong parameter spec.
    /// </para>
    /// </remarks>
    private void BindDriver()
    {
        if (_device is not DeviceContext device)
        {
            return;
        }

        _catalog = new ConsoleCatalog(device.Driver);

        FilterBox.Text = string.Empty;
        CommandPicker.ItemsSource = _catalog.All;
        CommandPicker.SelectedIndex = _catalog.All.Count > 0 ? 0 : -1;
    }

    // ===========================================================================================
    // The command
    // ===========================================================================================

    private void OnFilterChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (!_ready || _catalog is not ConsoleCatalog catalog || args?.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
        {
            return;
        }

        IReadOnlyList<ConsoleCommand> matches = catalog.Matching(FilterBox.Text);

        // The selection is kept when it survives the filter, because a filter that silently
        // reselected something else would change what Send does without the user touching it.
        ConsoleCommand? keep = _selected is not null && matches.Contains(_selected) ? _selected : null;

        CommandPicker.ItemsSource = matches;
        CommandPicker.SelectedItem = keep ?? (matches.Count > 0 ? matches[0] : null);
    }

    private void OnCommandChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready)
        {
            return;
        }

        _selected = CommandPicker.SelectedItem as ConsoleCommand;
        BuildParameterEditor();
        Render();
    }

    /// <summary>
    /// Shows the one editor this command's parameter needs, and hides the others.
    /// </summary>
    /// <remarks>
    /// Three editors and no text box for a value. A number goes through <c>NumberBox</c> with the
    /// catalog's own bounds; a keyword is chosen from <see cref="ParameterSpec.Choices"/>, so the
    /// spelling that reaches the wire is the catalog's; a PRN list is parsed to integers and
    /// re-rendered, which is what makes a semicolon — SCPI's command separator — unable to survive
    /// the trip rather than merely discouraged.
    /// </remarks>
    private void BuildParameterEditor()
    {
        ParameterFields.Children.Clear();
        _readers.Clear();
        ParameterError.Visibility = Visibility.Collapsed;

        if (_selected is not ConsoleCommand selected || selected.Parameters.Count == 0)
        {
            ParameterPanel.Visibility = Visibility.Collapsed;
            _argument = new ConsoleArgument.Result(null, null);
            return;
        }

        ParameterPanel.Visibility = Visibility.Visible;

        foreach (ParameterSpec parameter in selected.Parameters)
        {
            _readers.Add(AddEditor(parameter));
        }

        Revalidate();
    }

    /// <summary>
    /// Adds one editor for one parameter and returns how to read what it holds.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A closure rather than a list of controls to be re-inspected by kind later. The kind is
    /// already known here, where the control is made; deciding it a second time when reading is how
    /// the reader and the editor come to disagree about which control holds the value.
    /// </para>
    /// <para>
    /// Every editor is typed. A number gets a NumberBox carrying the catalog&apos;s range, a keyword a
    /// ComboBox over the catalog&apos;s own list, and a PRN list the only text field there is - which is
    /// parsed to integers and re-rendered, so a semicolon, SCPI&apos;s command separator, cannot survive
    /// the trip.
    /// </para>
    /// </remarks>
    private Func<string?> AddEditor(ParameterSpec parameter)
    {
        string label = parameter.Unit is null ? parameter.Name : $"{parameter.Name} ({parameter.Unit})";

        switch (parameter.Kind)
        {
            case ParameterKind.Keyword:
                ComboBox keyword = new()
                {
                    Header = label,
                    Width = FieldWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    ItemsSource = parameter.Choices,
                    SelectedIndex = parameter.Choices is { Count: > 0 } ? 0 : -1,
                };

                keyword.SelectionChanged += (_, _) => Revalidate();
                ParameterFields.Children.Add(keyword);

                return () => keyword.SelectedItem as string;

            case ParameterKind.PrnList:
                TextBox prn = new()
                {
                    Header = $"{label} - one or more, comma separated",
                    MaxWidth = FieldWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    PlaceholderText = "e.g. 3,17,28",
                };

                prn.TextChanged += (_, _) => Revalidate();
                ParameterFields.Children.Add(prn);

                return () => prn.Text;

            default:
                NumberBox number = new()
                {
                    Header = RangeLabel(parameter, label),
                    MaxWidth = FieldWidth,
                    HorizontalAlignment = HorizontalAlignment.Left,
                    SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,

                    // Bounds are enforced by the validator rather than by Minimum and Maximum on
                    // the control. §9.11 wants an out-of-range entry explained, not silently
                    // replaced - and the XAML parser widens a literal bound anyway, which is how a
                    // maximum of 0.99 once became 0.9900000095 on the Timing page.
                    Value = parameter.Minimum ?? 0,
                };

                number.ValueChanged += (_, _) => Revalidate();
                ParameterFields.Children.Add(number);

                return () => double.IsNaN(number.Value)
                    ? string.Empty
                    : number.Value.ToString("0.###########", CultureInfo.InvariantCulture);
        }
    }

    private static string RangeLabel(ParameterSpec parameter, string label) =>
        (parameter.Minimum, parameter.Maximum) switch
        {
            (double low, double high) =>
                $"{label} ({low.ToString("0.###", CultureInfo.CurrentCulture)}"
                + $" – {high.ToString("0.###", CultureInfo.CurrentCulture)})",
            _ => label,
        };

    private void Revalidate()
    {
        if (_selected is not ConsoleCommand selected)
        {
            _argument = new ConsoleArgument.Result(null, null);
            Render();
            return;
        }

        string?[] values = Array.ConvertAll(_readers.ToArray(), read => read());

        _argument = ConsoleArgument.For(selected.Parameters, values);

        ParameterError.Text = _argument.Error ?? string.Empty;
        ParameterError.Visibility = _argument.Error is null ? Visibility.Collapsed : Visibility.Visible;

        Render();
    }

    private void Render()
    {
        bool connected = _device?.Session.Status == ConnectionStatus.Connected;

        DescriptionText.Text = _selected?.Description ?? string.Empty;

        // The preview is what the session will write, produced by the session's own method. A
        // second expression here that agreed with it today is exactly the thing §10.11's preview
        // exists to rule out.
        PreviewText.Text = _selected is null
            ? ReadoutFormatter.NoValue
            : DeviceSessionService.TextFor(_selected.Command, _argument.Text);

        SendButton.IsEnabled = connected && _selected is not null && _argument.IsValid && !_busy;
        SendButton.Content = _selected?.NeedsConfirmation == true ? "Send…" : "Send";
    }

    private async void OnSendClicked(object sender, RoutedEventArgs e)
    {
        if (_device is not DeviceContext device
            || _selected is not ConsoleCommand selected
            || !_argument.IsValid
            || _busy)
        {
            return;
        }

        _busy = true;
        SendOutcome.Clear();
        Render();

        try
        {
            if (selected.NeedsConfirmation)
            {
                // The same dialog, the same sentence, the same checkbox. Routed through
                // CommandConfirmation rather than reimplemented, so a change to §8.3's ceremony
                // reaches this page without anyone remembering it exists.
                SendOutcome.Show(await CommandConfirmation.RunAsync(
                    XamlRoot,
                    _invoker!,
                    selected.Command,
                    _argument.Text,
                    _argument.Text));
            }
            else
            {
                // Tier S runs on click, which is what tier S means (§8.2). The reply lands in the
                // transcript below rather than in a message here: this page's answer to "what did
                // it say" is the transcript, and duplicating it would let the two disagree.
                await device.Session
                    .ExecuteAsync(selected.Command, _argument.Text, CommandOrigin.User)
                    .ConfigureAwait(true);
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException or TransportException)
        {
            SendOutcome.Show(null, exception.Message);
        }
        finally
        {
            _busy = false;
            Render();
        }
    }

    // ===========================================================================================
    // The transcript
    // ===========================================================================================

    private void OnTranscriptChanged(object? sender, EventArgs e) =>
        DispatcherQueue.TryEnqueue(RenderTranscript);

    private void OnShowPollsChanged(object sender, RoutedEventArgs e) => RenderTranscript();

    private void OnClearClicked(object sender, RoutedEventArgs e) => _transcript?.Clear();

    private void OnExportClicked(object sender, RoutedEventArgs e) =>
        DetailsWindow.ExportFrom(this, XamlRoot);

    private bool ShowPolls => ShowPollsCheck.IsChecked == true;

    /// <summary>
    /// Redraws the transcript.
    /// </summary>
    /// <remarks>
    /// One <c>TextBlock</c> rather than a <c>ListView</c> of entries. The transcript is read as a
    /// conversation and copied out whole, which a selectable block does for free and a virtualised
    /// list makes awkward; and at <see cref="CommandTranscript.Capacity"/> entries the cost of
    /// rebuilding the string is a few milliseconds against a redraw that happens once a second.
    /// </remarks>
    private void RenderTranscript()
    {
        if (_transcript is not CommandTranscript transcript)
        {
            return;
        }

        IReadOnlyList<TranscriptEntry> entries = transcript.Snapshot(ShowPolls);

        StringBuilder builder = new();
        foreach (TranscriptEntry entry in entries)
        {
            builder.Append("> ").AppendLine(entry.Sent);

            foreach (string line in entry.Received)
            {
                builder.Append("< ").AppendLine(line);
            }

            // A timeout produces no lines at all, and a transcript that showed nothing after the
            // ">" would read as a command still in flight rather than one that never answered.
            if (entry.Outcome != TransactionOutcome.Completed)
            {
                builder.Append("! ").AppendLine(entry.Outcome == TransactionOutcome.TimedOut
                    ? "no response within the timeout"
                    : "the link faulted");
            }
            else if (entry.PromptStatus is string status)
            {
                builder.Append("! ").AppendLine(status);
            }
        }

        TranscriptText.Text = builder.ToString();

        TranscriptFooter.Text = entries.Count == 0
            ? "Nothing recorded yet."
            : $"{entries.Count:N0} of {transcript.Count:N0} transaction(s)"
              + (ShowPolls ? string.Empty : ", poll traffic hidden")
              + $". The last {CommandTranscript.Capacity:N0} are kept.";

        ExportAvailabilityChanged?.Invoke(this, EventArgs.Empty);

        // Following the tail is what a transcript is for; scrolling up to read history and being
        // yanked back a second later is not. Only auto-scroll when already at the bottom.
        if (TranscriptScroller.VerticalOffset >= TranscriptScroller.ScrollableHeight - 4)
        {
            TranscriptScroller.UpdateLayout();
            TranscriptScroller.ChangeView(null, TranscriptScroller.ScrollableHeight, null, disableAnimation: true);
        }
    }

    // ===========================================================================================
    // §9.7.5's export
    // ===========================================================================================

    /// <inheritdoc />
    public event EventHandler? ExportAvailabilityChanged;

    /// <inheritdoc />
    public bool CanExport => _transcript?.Count > 0;

    /// <inheritdoc />
    public string SuggestedFileName =>
        $"receiver-transcript-{(_device?.TimeProvider ?? TimeProvider.System).GetLocalNow():yyyy-MM-dd-HHmm}";

    /// <inheritdoc />
    /// <remarks>
    /// Exports what is on screen, filter included, for the same reason the log export does: §9.7.5
    /// calls the command "Export current view", and a file holding poll traffic the user had hidden
    /// would not be the view they were looking at.
    /// </remarks>
    public CsvDocument? BuildCsv()
    {
        if (_transcript is not CommandTranscript transcript)
        {
            return null;
        }

        IReadOnlyList<TranscriptEntry> entries = transcript.Snapshot(ShowPolls);
        if (entries.Count == 0)
        {
            return null;
        }

        CsvDocument document = new("Timestamp", "Origin", "Sent", "Received", "Outcome", "ElapsedMs", "Status");

        foreach (TranscriptEntry entry in entries)
        {
            document.AddRow(
                CsvDocument.PreciseTimestamp(new DateTime(entry.Ticks, DateTimeKind.Utc)),
                entry.Origin.ToString(),

                // Device-literal, both columns. Multi-line responses keep their line breaks inside
                // one quoted field rather than becoming several rows — the status screen is one
                // answer to one question, and splitting it would misrepresent the exchange.
                entry.Sent,
                string.Join("\n", entry.Received),
                entry.Outcome.ToString(),
                CsvDocument.Number(entry.Elapsed.TotalMilliseconds, 1),
                entry.PromptStatus);
        }

        return document;
    }
}
