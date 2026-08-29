using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// §10.5's Manage dialog: which satellites the receiver may track (P1-3).
/// </summary>
/// <remarks>
/// <para>
/// <b>This dialog chooses; it never sends.</b> Each button records which catalogued command the user
/// asked for and closes, and the page then runs it through <see cref="CommandConfirmation.RunAsync"/>
/// and reopens this dialog. Nothing is staged while it is open, so closing it cannot lose a change
/// or make one.
/// </para>
/// <para>
/// <b>Choosing and sending are separated because they have to be.</b> Showing §8.3's confirmation
/// from inside this dialog nests one <c>ContentDialog</c> in another, which WinUI does not permit —
/// and the way it does not permit it is by killing the process from an <c>async void</c> handler,
/// with nothing in the log. Found by clicking the button. One dialog at a time is not a style
/// preference here; it is the only arrangement that runs.
/// </para>
/// <para>
/// <b>It opens showing the receiver's state, not the application's memory of it.</b> #51 requires
/// that, because the receiver may have been changed by something else or power-cycled since anyone
/// last looked. Two tier S queries answer it, and both are read every time it opens — including
/// after a command, so the grid shows what the receiver did rather than what was asked of it.
/// </para>
/// </remarks>
public sealed partial class SatelliteManagementDialog : ContentDialog
{
    /// <summary>The five §8.3 commands this dialog can offer, resolved from the driver's catalog.</summary>
    /// <remarks>
    /// Two of them — excluding every satellite, and tracking none — carry <c>acknowledge: true</c>
    /// in the catalog because both drive the receiver into holdover. That flag lives on the catalog
    /// entry rather than here, so §9.7.4's checkbox appears because the command says so and not
    /// because this file remembered to ask for it.
    /// </remarks>
    private readonly ScpiCommand _includeList;
    private readonly ScpiCommand _includeAll;
    private readonly ScpiCommand _includeNone;
    private readonly ScpiCommand _excludeAll;
    private readonly ScpiCommand _excludeNone;

    private readonly DeviceContext _device;
    private readonly List<SatelliteChoice> _choices = [];

    /// <summary>Creates the dialog over a device.</summary>
    public SatelliteManagementDialog(DeviceContext device)
    {
        ArgumentNullException.ThrowIfNull(device);

        InitializeComponent();

        _device = device;

        // Resolved through the device's driver in the constructor (#287): unlike a page, the
        // dialog cannot exist without a device, so the commands can be readonly instance state.
        _includeList = CommandConfirmation.Require(device.Driver, ":GPS:SAT:TRAC:INCLude");
        _includeAll = CommandConfirmation.Require(device.Driver, ":GPS:SAT:TRAC:INCLude ALL");
        _includeNone = CommandConfirmation.Require(device.Driver, ":GPS:SAT:TRAC:INCLude NONE");
        _excludeAll = CommandConfirmation.Require(device.Driver, ":GPS:SAT:TRAC:IGNore ALL");
        _excludeNone = CommandConfirmation.Require(device.Driver, ":GPS:SAT:TRAC:IGNore NONE");

        Opened += async (_, _) => await LoadAsync().ConfigureAwait(true);
    }

    /// <summary>Which command the user asked for, or null if they just closed it.</summary>
    public ScpiCommand? ChosenCommand { get; private set; }

    /// <summary>Its argument, for the one command that takes a PRN list.</summary>
    public string? ChosenArgument { get; private set; }

    /// <summary>
    /// Reads the two lists and builds the grid.
    /// </summary>
    /// <remarks>
    /// The formats are not what a reasonable guess would produce, and
    /// <see cref="SatelliteTrackingParser"/> carries the account of what this receiver actually
    /// sends: an empty list answers <c>+0</c>, and a full one arrives on the second line.
    /// </remarks>
    private async Task LoadAsync()
    {
        SummaryText.Text = "Reading the receiver's tracking lists…";

        IReadOnlySet<int> included = SatelliteTrackingParser.ParsePrnList(
            await AskAsync(":GPS:SAT:TRAC:INCL?").ConfigureAwait(true));
        IReadOnlySet<int> excluded = SatelliteTrackingParser.ParsePrnList(
            await AskAsync(":GPS:SAT:TRAC:IGN?").ConfigureAwait(true));

        _choices.Clear();
        foreach (int prn in SatelliteTrackingState.AllPrns)
        {
            SatelliteChoice choice = new(prn, included.Contains(prn), excluded.Contains(prn));
            choice.PropertyChanged += (_, _) => RenderSelection();
            _choices.Add(choice);
        }

        PrnRepeater.ItemsSource = _choices;

        SummaryText.Text = _device.Session.Status == ConnectionStatus.Connected
            ? $"The receiver has {included.Count} satellite(s) on its inclusion list "
              + $"and {excluded.Count} excluded."
            : "Not connected, so nothing could be read and nothing can be sent.";

        RenderSelection();
    }

    private void RenderSelection()
    {
        int selected = _choices.Count(choice => choice.IsSelected);
        bool connected = _device.Session.Status == ConnectionStatus.Connected;

        SelectionText.Text = selected == 0
            ? "Nothing selected. Use Track none if that is what you mean."
            : $"{selected} selected.";

        ApplyIncludeButton.IsEnabled = connected && selected > 0;
        IncludeAllButton.IsEnabled = connected;
        IncludeNoneButton.IsEnabled = connected;
        ExcludeAllButton.IsEnabled = connected;
        ExcludeNoneButton.IsEnabled = connected;
    }

    /// <summary>Asks one catalogued query and returns its lines, or null.</summary>
    /// <remarks>
    /// Through the catalog, never as text: §8.1 makes the catalog an allowlist and
    /// <c>ExecuteAsync</c> takes an <see cref="ScpiCommand"/> precisely so nothing routes around it.
    /// </remarks>
    private async Task<IReadOnlyList<string>?> AskAsync(string mnemonic)
    {
        if (_device.Driver.Find(mnemonic) is not ScpiCommand command)
        {
            return null;
        }

        try
        {
            Transaction transaction = await _device.Session
                .ExecuteAsync(command, origin: CommandOrigin.User)
                .ConfigureAwait(true);

            return transaction.Succeeded ? transaction.Lines : null;
        }
        catch (Exception exception) when (exception is InvalidOperationException or TransportException)
        {
            return null;
        }
    }

    // ===========================================================================================
    // Choosing. Nothing here sends.
    // ===========================================================================================

    private void Choose(ScpiCommand command, string? argument = null)
    {
        ChosenCommand = command;
        ChosenArgument = argument;
        Hide();
    }

    private void OnApplyIncludeClicked(object sender, RoutedEventArgs e)
    {
        string prns = SatelliteTrackingParser.FormatPrnList(
            _choices.Where(choice => choice.IsSelected).Select(choice => choice.Prn));

        if (prns.Length > 0)
        {
            Choose(_includeList, prns);
        }
    }

    private void OnIncludeAllClicked(object sender, RoutedEventArgs e) => Choose(_includeAll);

    private void OnIncludeNoneClicked(object sender, RoutedEventArgs e) => Choose(_includeNone);

    private void OnExcludeAllClicked(object sender, RoutedEventArgs e) => Choose(_excludeAll);

    private void OnExcludeNoneClicked(object sender, RoutedEventArgs e) => Choose(_excludeNone);
}
