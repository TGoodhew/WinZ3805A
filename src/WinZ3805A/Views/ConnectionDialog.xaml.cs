using System.IO.Ports;
using System.ComponentModel;

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.12 connection dialog.
/// </summary>
/// <remarks>
/// <para>
/// Wiring only. Which port is selected after a refresh, whether Connect is offerable, what the
/// progress line reads and which of §9.11's rows a failure belongs to are all
/// <see cref="ConnectionViewModel"/>'s, where they are tested without a window.
/// </para>
/// <para>
/// Bound by hand rather than through <c>x:Bind</c>, as <see cref="MainPage"/> is: the pickers hold
/// value types, and a <c>SelectedItem</c> round trip through the WinRT projection compares boxes
/// rather than numbers. Selecting by index sidesteps the question entirely.
/// </para>
/// </remarks>
public sealed partial class ConnectionDialog : ContentDialog
{
    private readonly ConnectionViewModel _model;

    /// <summary>True once the constructor has finished, so handlers firing during it do nothing.</summary>
    private readonly bool _ready;

    /// <summary>True while the code is writing the controls, so its own writes do not echo back.</summary>
    private bool _updating;

    /// <summary>
    /// The one handler this page hands the dispatcher, reused for every notification (#399).
    /// </summary>
    /// <remarks>
    /// A field rather than a lambda or a method group because each of those allocates a fresh
    /// delegate, and a fresh delegate is a fresh COM wrapper the runtime can never reuse. See
    /// <see cref="MainPage"/> for the measurement.
    /// </remarks>
    private readonly DispatcherQueueHandler _render;

    /// <summary>Creates the dialog over a view model.</summary>
    public ConnectionDialog(ConnectionViewModel model)
    {
        ArgumentNullException.ThrowIfNull(model);

        InitializeComponent();

        _render = Render;

        _model = model;
        _model.PropertyChanged += OnModelChanged;

        BaudPicker.ItemsSource = _model.BaudRateOptions;
        DataBitsPicker.ItemsSource = _model.DataBitOptions;
        ParityPicker.ItemsSource = _model.ParityOptions;
        StopBitsPicker.ItemsSource = _model.StopBitOptions;

        _ready = true;

        Opened += async (_, _) => await RefreshAsync();
        Render();
    }

    /// <summary>Whether the receiver answered, once the dialog has closed.</summary>
    public bool Connected { get; private set; }

    private async Task RefreshAsync()
    {
        try
        {
            await _model.RefreshPortsAsync();
        }
        catch (OperationCanceledException)
        {
            // The dialog closed while the registry walk was in flight.
        }
    }

    private async void OnRefreshClicked(object sender, RoutedEventArgs e) => await RefreshAsync();

    private void OnPortSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_updating)
        {
            _model.SelectedPort = PortPicker.SelectedIndex >= 0
                && PortPicker.SelectedIndex < _model.AvailablePorts.Count
                    ? _model.AvailablePorts[PortPicker.SelectedIndex]
                    : null;
        }
    }

    private void OnModeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && !_updating)
        {
            _model.IsAutoDetect = ModeChoice.SelectedIndex == 0;
        }
    }

    private void OnSettingChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || _updating)
        {
            return;
        }

        if (Chosen(BaudPicker, _model.BaudRateOptions) is int baud)
        {
            _model.BaudRate = baud;
        }

        if (Chosen(DataBitsPicker, _model.DataBitOptions) is int bits)
        {
            _model.DataBits = bits;
        }

        if (Chosen(ParityPicker, _model.ParityOptions) is Parity parity)
        {
            _model.Parity = parity;
        }

        if (Chosen(StopBitsPicker, _model.StopBitOptions) is int stopBits)
        {
            _model.StopBitCount = stopBits;
        }
    }

    private void OnPreferenceToggled(object sender, RoutedEventArgs e)
    {
        if (_ready && !_updating)
        {
            _model.ReconnectAutomatically = ReconnectCheck.IsChecked == true;
            _model.ConnectOnLaunch = ConnectOnLaunchCheck.IsChecked == true;
        }
    }

    /// <remarks>
    /// The click is deferred so the attempt can run with the dialog still on screen, and the dialog
    /// is held open on failure: §9.11 puts the error where the control that caused it is, and a
    /// dialog that vanished would take the port picker with it.
    /// </remarks>
    private async void OnConnectClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        try
        {
            Connected = await _model.ConnectAsync();
            args.Cancel = !Connected;
        }
        finally
        {
            deferral.Complete();
        }
    }

    /// <remarks>
    /// While an attempt is running, Cancel stops the attempt rather than closing the dialog — §10.12
    /// requires the walk to be cancellable, and a user who has spotted the wrong port wants the
    /// picker back, not the window gone.
    /// </remarks>
    private void OnCancelClicked(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        if (_model.IsBusy)
        {
            args.Cancel = true;
            _model.Cancel();
        }
    }

    /// <summary>Pushes the view model onto the surface.</summary>
    /// <summary>Renders on a model notification (#388).</summary>
    /// <remarks>
    /// Named rather than a lambda although nothing leaks here: MainPage builds a fresh
    /// <c>ConnectionViewModel</c> for each dialog, so the two die together. The rule is universal
    /// because the exemption is a claim about a caller that a future caller can quietly break.
    /// </remarks>
    private void OnModelChanged(object? sender, PropertyChangedEventArgs e) =>
        DispatcherQueue.TryEnqueue(_render);

    private void Render()
    {
        _updating = true;
        try
        {
            PortPicker.ItemsSource = _model.AvailablePorts;
            PortPicker.SelectedIndex = _model.SelectedPort is null
                ? -1
                : IndexOf(_model.AvailablePorts, _model.SelectedPort);

            PortsMessageBar.Message = _model.PortsMessage ?? string.Empty;
            PortsMessageBar.IsOpen = _model.PortsMessage is not null;

            ModeChoice.SelectedIndex = _model.IsAutoDetect ? 0 : 1;

            BaudPicker.SelectedIndex = IndexOf(_model.BaudRateOptions, _model.BaudRate);
            DataBitsPicker.SelectedIndex = IndexOf(_model.DataBitOptions, _model.DataBits);
            ParityPicker.SelectedIndex = IndexOf(_model.ParityOptions, _model.Parity);
            StopBitsPicker.SelectedIndex = IndexOf(_model.StopBitOptions, _model.StopBitCount);

            ReconnectCheck.IsChecked = _model.ReconnectAutomatically;
            ConnectOnLaunchCheck.IsChecked = _model.ConnectOnLaunch;

            // A Grid is a Panel and has no IsEnabled of its own, so the four pickers are disabled
            // individually. Disabled rather than hidden: the settings an auto-detect is about to
            // overwrite stay readable, and the dialog does not change height when the radio moves.
            foreach (ComboBox picker in new[] { BaudPicker, DataBitsPicker, ParityPicker, StopBitsPicker })
            {
                picker.IsEnabled = _model.CanEditSettings;
            }

            ModeChoice.IsEnabled = !_model.IsBusy;
            PortPicker.IsEnabled = _model.CanChoosePort;
            RefreshButton.IsEnabled = _model.CanChoosePort;
            IsPrimaryButtonEnabled = _model.CanConnect;

            ProgressArea.Visibility = _model.IsBusy ? Visibility.Visible : Visibility.Collapsed;
            ProgressText.Text = _model.ProgressText ?? string.Empty;

            ErrorBar.Message = _model.ErrorMessage ?? string.Empty;
            ErrorBar.IsOpen = _model.ErrorMessage is not null;
        }
        finally
        {
            _updating = false;
        }
    }

    private static int IndexOf<T>(IReadOnlyList<T> options, T value)
    {
        for (int index = 0; index < options.Count; index++)
        {
            if (EqualityComparer<T>.Default.Equals(options[index], value))
            {
                return index;
            }
        }

        return -1;
    }

    private static object? Chosen<T>(Selector picker, IReadOnlyList<T> options) =>
        picker.SelectedIndex >= 0 && picker.SelectedIndex < options.Count
            ? options[picker.SelectedIndex]
            : null;
}
