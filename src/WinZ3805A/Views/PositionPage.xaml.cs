using System.Globalization;

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Windows.ApplicationModel.DataTransfer;

using WinZ3805A.Controls;
using WinZ3805A.Device.Commands;
using WinZ3805A.Device.Transport;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.6 Position page.
/// </summary>
public sealed partial class PositionPage : Page
{
    /// <summary>§8.3's four survey commands and the manual set, all tier C.</summary>
    private static readonly ScpiCommand StartSurvey = CommandConfirmation.Require(":GPS:POSition:SURVey:STATe ONCE");
    private static readonly ScpiCommand AdoptSurvey = CommandConfirmation.Require(":GPS:POSition SURVey");
    private static readonly ScpiCommand CancelSurvey = CommandConfirmation.Require(":GPS:POSition LAST");
    private static readonly ScpiCommand SurveyAtPowerUp = CommandConfirmation.Require(":GPS:POS:SURV:STAT:POWerup");
    private static readonly ScpiCommand SetPosition = CommandConfirmation.Require(":GPS:POSition");

    /// <summary>The §8.5-adjacent read behind the power-up checkbox — an ordinary tier S query.</summary>
    private static readonly ScpiCommand ReadPowerUp = CommandConfirmation.Require(":GPS:POS:SURV:STAT:POW?");

    private PositionViewModel? _model;
    private DeviceContext? _device;
    private CommandInvoker? _invoker;
    private readonly NumberFieldValidator[] _fields;
    private bool _busy;
    private bool _ready;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Creates the page.</summary>
    public PositionPage()
    {
        InitializeComponent();

        LatitudeSign.ItemsSource = new[] { "N", "S" };
        LongitudeSign.ItemsSource = new[] { "E", "W" };
        LatitudeSign.SelectedIndex = 0;
        LongitudeSign.SelectedIndex = 0;

        // §10.6's ranges, which match the 58503B manual's own table exactly. Assigned in code
        // rather than XAML: the parser reads a NumberBox.Value literal as a float and widens it.
        _fields =
        [
            new NumberFieldValidator(LatitudeDegrees, LatitudeDegreesError, 0, 90, "°"),
            new NumberFieldValidator(LatitudeMinutes, LatitudeMinutesError, 0, 59, "′"),
            new NumberFieldValidator(LatitudeSeconds, LatitudeSecondsError, 0, 59.999, "″"),
            new NumberFieldValidator(LongitudeDegrees, LongitudeDegreesError, 0, 180, "°"),
            new NumberFieldValidator(LongitudeMinutes, LongitudeMinutesError, 0, 59, "′"),
            new NumberFieldValidator(LongitudeSeconds, LongitudeSecondsError, 0, 59.999, "″"),
            new NumberFieldValidator(HeightBox, HeightError, -1000, 18000, "m"),
        ];

        foreach (NumberFieldValidator field in _fields)
        {
            field.ValidityChanged += (_, _) => Render();
        }

        _stalenessTicker.Tick += (_, _) => _model?.RaiseAll();
        Unloaded += (_, _) =>
        {
            _stalenessTicker.Stop();
            if (_device is DeviceContext device)
            {
                device.Session.StatusChanged -= OnStatusChanged;
            }
        };
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
        _model = new PositionViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        _ready = true;
        _stalenessTicker.Start();
        Render();

        _ = ReadPowerUpSettingAsync();
    }

    /// <summary>
    /// Asks the receiver whether it surveys at power-up.
    /// </summary>
    /// <remarks>
    /// On navigation rather than on a timer: it is a setting, not a reading, and §7.3's two
    /// cadences have no business carrying something that changes only when someone changes it. Left
    /// indeterminate if the read fails — a cleared checkbox would be a definite "off" the user
    /// could act on, and this does not know that.
    /// </remarks>
    private async Task ReadPowerUpSettingAsync()
    {
        if (_device is not DeviceContext device || device.Session.Status != ConnectionStatus.Connected)
        {
            return;
        }

        try
        {
            Transaction reply = await device.Session.ExecuteAsync(ReadPowerUp).ConfigureAwait(true);

            // The body is the test, not the prompt. §7.2: a rejected query answers with the prompt
            // and nothing else, so a query that returned a line returned an answer — whatever an
            // earlier poll happens to have left in the receiver's error queue (#173).
            if (reply.Succeeded && !reply.WasRejected && reply.FirstLine is string answer)
            {
                string text = answer.Trim();
                if (_model is PositionViewModel model)
                {
                    model.SurveyAtPowerUp = text is "1" or "ON" or "on";
                }
            }
        }
        catch (InvalidOperationException)
        {
            // The session went away. The checkbox stays indeterminate, which is the truth.
        }
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is PositionViewModel model)
            {
                model.Connection = e.Status;
            }
        });

    private void Render()
    {
        if (_model is not PositionViewModel model)
        {
            return;
        }

        // Position hold is the normal state for a stationary timing receiver, so it is neutral
        // rather than a success: nothing has gone right, this is simply where it is.
        ModePill.Severity = Severity.Neutral;
        ModePill.Text = model.ModeText;

        LatitudeText.Text = model.LatitudeText;
        LongitudeText.Text = model.LongitudeText;
        HeightText.Text = model.HeightText;

        CopyButton.IsEnabled = model.CopyText is not null;

        SurveyProgress.Visibility = model.SurveyPercentComplete is not null
            ? Visibility.Visible
            : Visibility.Collapsed;
        SurveyProgress.Value = model.SurveyPercentComplete ?? 0;

        // Collapsed rather than blanked: an empty TextBlock still occupies a line, which leaves a
        // gap in the card that reads as something failing to load.
        if (model.SurveyPercentComplete is double percent)
        {
            SurveyPercentText.Text =
                $"{ReadoutFormatter.Format(percent, decimalPlaces: 1)}{ReadoutFormatter.HairSpace}%";
            SurveyPercentText.Visibility = Visibility.Visible;
        }
        else
        {
            SurveyPercentText.Visibility = Visibility.Collapsed;
        }

        SurveyPill.Visibility = model.IsSurveySuspended ? Visibility.Visible : Visibility.Collapsed;
        SurveyPill.Severity = model.SurveySeverity;
        SurveyPill.Text = "Survey suspended";

        SurveyStatusText.Text = model.SurveyStatusText;

        StartSurveyButton.IsEnabled = !_busy && model.CanStartSurvey;
        AdoptSurveyButton.IsEnabled = !_busy && model.CanEndSurvey;
        CancelSurveyButton.IsEnabled = !_busy && model.CanEndSurvey;

        PowerUpSurveyCheck.IsEnabled = !_busy && model.CanSetPosition;
        PowerUpSurveyCheck.IsChecked = model.SurveyAtPowerUp;

        FillFromReceiverButton.IsEnabled = !_busy && model.HasPosition;
        ApplyPositionButton.IsEnabled =
            !_busy && model.CanSetPosition && Array.TrueForAll(_fields, field => field.IsValid);

        // §10.6 annotates the entry field WGS-84 while the receiver command is documented as
        // "height above mean sea level" (#114). Until that is settled the page says which datum
        // the receiver reported for the value it is showing, and says nothing it cannot support
        // about the one being typed.
        HeightDatumText.Text = model.HeightEntryNote;

        FooterText.Text = $"Position {model.AgeDescription}";
    }

    /// <remarks>
    /// The clipboard, and nothing else. This is the one command on the page that is not a device
    /// write, which is why it is here while the rest wait for §15 step 10.
    /// </remarks>
    private void OnCopyClicked(object sender, RoutedEventArgs e)
    {
        if (_model?.CopyText is not string text)
        {
            return;
        }

        DataPackage package = new();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // ---- §8.3's survey commands ------------------------------------------------------------

    private async void OnStartSurveyClicked(object sender, RoutedEventArgs e) =>
        await RunAsync(StartSurvey, SurveyOutcome);

    private async void OnAdoptSurveyClicked(object sender, RoutedEventArgs e) =>
        await RunAsync(AdoptSurvey, SurveyOutcome);

    private async void OnCancelSurveyClicked(object sender, RoutedEventArgs e) =>
        await RunAsync(CancelSurvey, SurveyOutcome);

    /// <summary>
    /// The power-up setting. A checkbox that sends a tier C command needs its own state put back
    /// when the user cancels the confirmation, because the click has already moved it.
    /// </summary>
    private async void OnPowerUpSurveyClicked(object sender, RoutedEventArgs e)
    {
        if (!_ready || _model is not PositionViewModel model || _busy)
        {
            return;
        }

        bool wanted = PowerUpSurveyCheck.IsChecked == true;
        string keyword = wanted ? "ON" : "OFF";

        CommandOutcome? outcome = await RunAsync(
            SurveyAtPowerUp, SurveyOutcome, argument: keyword, displayValue: keyword);

        if (outcome is { Succeeded: true })
        {
            model.SurveyAtPowerUp = wanted;
        }
        else
        {
            // Cancelled or refused: the receiver did not change, so neither does the box.
            Render();
        }
    }

    // ---- §10.6's manual position -----------------------------------------------------------

    /// <summary>
    /// Copies the receiver's own position into the entry fields.
    /// </summary>
    /// <remarks>
    /// The common case for this card is nudging a position the receiver already holds, not typing
    /// one from scratch — and re-keying eight fields to change one of them is where a digit gets
    /// lost.
    /// </remarks>
    private void OnFillFromReceiverClicked(object sender, RoutedEventArgs e)
    {
        if (_model?.ReceiverPosition is not { LatitudeDegrees: double latitude, LongitudeDegrees: double longitude })
        {
            return;
        }

        Fill(latitude, LatitudeSign, LatitudeDegrees, LatitudeMinutes, LatitudeSeconds, "N", "S");
        Fill(longitude, LongitudeSign, LongitudeDegrees, LongitudeMinutes, LongitudeSeconds, "E", "W");

        if (_model.ReceiverPosition.HeightMetres is double height)
        {
            HeightBox.Value = Math.Round(height, 2);
        }

        foreach (NumberFieldValidator field in _fields)
        {
            field.Revalidate();
        }

        static void Fill(
            double signed,
            ComboBox sign,
            NumberBox degrees,
            NumberBox minutes,
            NumberBox seconds,
            string positive,
            string negative)
        {
            sign.SelectedItem = signed >= 0 ? positive : negative;

            double absolute = Math.Abs(signed);
            double wholeDegrees = Math.Floor(absolute);
            double totalMinutes = (absolute - wholeDegrees) * 60;
            double wholeMinutes = Math.Floor(totalMinutes);

            degrees.Value = wholeDegrees;
            minutes.Value = wholeMinutes;

            // Rounded to the receiver's own 0.001 resolution, and carried up if that rounding
            // lands on 60 - otherwise 59.9996 becomes an out-of-range 60.000 the field rejects.
            double remainingSeconds = Math.Round((totalMinutes - wholeMinutes) * 60, 3);
            if (remainingSeconds >= 60)
            {
                remainingSeconds = 0;
                minutes.Value = wholeMinutes + 1 > 59 ? 0 : wholeMinutes + 1;
            }

            seconds.Value = remainingSeconds;
        }
    }

    /// <summary>
    /// §8.3's manual position. The wire format is the 58503B manual's, exactly:
    /// <c>N,&lt;deg&gt;,&lt;min&gt;,&lt;sec&gt;,E,&lt;deg&gt;,&lt;min&gt;,&lt;sec&gt;,&lt;height&gt;</c>.
    /// </summary>
    private async void OnApplyPositionClicked(object sender, RoutedEventArgs e)
    {
        if (_model is not PositionViewModel model || !model.CanSetPosition || _busy)
        {
            return;
        }

        if (!Array.TrueForAll(_fields, field => field.IsValid))
        {
            return;
        }

        string argument = string.Join(
            ',',
            LatitudeSign.SelectedItem as string ?? "N",
            Whole(LatitudeDegrees),
            Whole(LatitudeMinutes),
            Fractional(LatitudeSeconds),
            LongitudeSign.SelectedItem as string ?? "E",
            Whole(LongitudeDegrees),
            Whole(LongitudeMinutes),
            Fractional(LongitudeSeconds),
            HeightBox.Value.ToString("0.00", CultureInfo.InvariantCulture));

        string display =
            $"{LatitudeSign.SelectedItem} {Whole(LatitudeDegrees)}° {Whole(LatitudeMinutes)}′ {Fractional(LatitudeSeconds)}″, "
            + $"{LongitudeSign.SelectedItem} {Whole(LongitudeDegrees)}° {Whole(LongitudeMinutes)}′ {Fractional(LongitudeSeconds)}″, "
            + $"{HeightBox.Value.ToString("0.00", CultureInfo.CurrentCulture)} m";

        await RunAsync(SetPosition, PositionOutcome, argument, display);

        static string Whole(NumberBox box) => box.Value.ToString("0", CultureInfo.InvariantCulture);

        static string Fractional(NumberBox box) => box.Value.ToString("0.###", CultureInfo.InvariantCulture);
    }

    /// <summary>Runs one tier C command with the page quiet and the result on the given card.</summary>
    private async Task<CommandOutcome?> RunAsync(
        ScpiCommand command,
        CommandOutcomeBar bar,
        string? argument = null,
        string? displayValue = null)
    {
        if (_invoker is not CommandInvoker invoker)
        {
            return null;
        }

        _busy = true;
        bar.Clear();
        Render();

        try
        {
            CommandOutcome? outcome = await CommandConfirmation.RunAsync(
                XamlRoot, invoker, command, argument, displayValue);

            bar.Show(outcome);
            return outcome;
        }
        finally
        {
            _busy = false;
            Render();
        }
    }
}
