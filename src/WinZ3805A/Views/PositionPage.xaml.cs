using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using Windows.ApplicationModel.DataTransfer;

using WinZ3805A.Controls;
using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The §10.6 Position page, read-only.
/// </summary>
public sealed partial class PositionPage : Page
{
    private PositionViewModel? _model;
    private DeviceContext? _device;
    private readonly DispatcherTimer _stalenessTicker = new() { Interval = TimeSpan.FromSeconds(1) };

    /// <summary>Creates the page.</summary>
    public PositionPage()
    {
        InitializeComponent();

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
        _model = new PositionViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        _stalenessTicker.Start();
        Render();
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
}
