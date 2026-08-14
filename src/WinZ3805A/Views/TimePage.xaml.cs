using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;

using WinZ3805A.Services;
using WinZ3805A.ViewModels;

namespace WinZ3805A.Views;

/// <summary>
/// The Time page — the §10.3 clock with its workings shown.
/// </summary>
public sealed partial class TimePage : Page
{
    private TimeViewModel? _model;
    private DeviceContext? _device;
    private readonly DispatcherTimer _ticker = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _ready;

    /// <summary>Creates the page.</summary>
    public TimePage()
    {
        InitializeComponent();

        _ticker.Tick += (_, _) => _model?.RaiseAll();
        Unloaded += (_, _) =>
        {
            _ticker.Stop();
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
        _model = new TimeViewModel(device.Store) { Connection = device.Session.Status };
        _model.PropertyChanged += (_, _) => DispatcherQueue.TryEnqueue(Render);
        device.Session.StatusChanged += OnStatusChanged;

        LoadZones();

        _ready = true;
        _ticker.Start();
        Render();
    }

    /// <remarks>
    /// Populated on arrival rather than in the constructor: enumerating every system zone costs
    /// more than a page nobody has opened should pay.
    /// </remarks>
    private void LoadZones()
    {
        if (ZonePicker.Items.Count > 0 || _model is null)
        {
            return;
        }

        foreach (TimeZoneInfo zone in TimeZoneInfo.GetSystemTimeZones())
        {
            ZonePicker.Items.Add(zone);
        }

        ZonePicker.DisplayMemberPath = nameof(TimeZoneInfo.DisplayName);
        ZonePicker.SelectedItem = TimeZoneInfo.GetSystemTimeZones()
            .FirstOrDefault(zone => zone.Id == _model.DisplayZone.Id);
    }

    private void OnStatusChanged(object? sender, ConnectionStatusChanged e) =>
        DispatcherQueue.TryEnqueue(() =>
        {
            if (_model is TimeViewModel model)
            {
                model.Connection = e.Status;
            }
        });

    private void OnZoneSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_ready && _model is TimeViewModel model && ZonePicker.SelectedItem is TimeZoneInfo zone)
        {
            model.DisplayZone = zone;
        }
    }

    private void Render()
    {
        if (_model is not TimeViewModel model)
        {
            return;
        }

        ShownTimeText.Text = model.ShownTimeText;
        TimeScaleText.Text = model.TimeScaleText;
        DeviceTimeText.Text = model.DeviceTimeText;

        TimeScaleNoteText.Text = model.TimeScaleNote ?? string.Empty;
        TimeScaleNoteText.Visibility = model.TimeScaleNote is null
            ? Visibility.Collapsed
            : Visibility.Visible;

        RolloverPill.Severity = model.RolloverSeverity;
        RolloverPill.Text = model.IsDateCorrected ? "Date corrected" : "No correction";
        RolloverText.Text = model.RolloverText;

        LeapPill.Severity = model.LeapSeverity;
        LeapPill.Text = model.LeapPendingText;

        FooterText.Text = model.AgeDescription;
    }
}
