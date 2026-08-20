using System.ComponentModel;
using System.Globalization;

namespace WinZ3805A.ViewModels;

/// <summary>
/// One satellite in §10.5's Manage dialog: what the receiver says about it, and what the user has
/// picked.
/// </summary>
/// <remarks>
/// <b>Reported state and chosen state are separate fields on purpose.</b> The dialog opens with the
/// selection matching what the receiver reported, and from then on the two can differ — which is
/// exactly what the user is doing when they change something. Collapsing them into one flag would
/// mean the dialog could no longer say "the receiver has 17 excluded and you have not changed that",
/// which is the sentence a user opening it most often wants.
/// </remarks>
public sealed class SatelliteChoice : INotifyPropertyChanged
{
    private bool _isSelected;

    /// <summary>Creates a row for one PRN.</summary>
    /// <param name="prn">1 to 32.</param>
    /// <param name="isIncluded">Whether the receiver reports it on the inclusion list.</param>
    /// <param name="isExcluded">Whether the receiver reports it on the exclusion list.</param>
    public SatelliteChoice(int prn, bool isIncluded, bool isExcluded)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(prn, SatelliteTrackingState.FirstPrn);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(prn, SatelliteTrackingState.LastPrn);

        Prn = prn;
        IsIncluded = isIncluded;
        IsExcluded = isExcluded;

        // The selection starts where the receiver is. A dialog that opened with everything
        // unticked would invite a user to "apply" their way into tracking nothing.
        _isSelected = isIncluded && !isExcluded;
    }

    /// <inheritdoc />
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>The satellite's PRN.</summary>
    public int Prn { get; }

    /// <summary>Whether the receiver reports it on the inclusion list.</summary>
    public bool IsIncluded { get; }

    /// <summary>Whether the receiver reports it on the exclusion list.</summary>
    public bool IsExcluded { get; }

    /// <summary>The PRN as it is shown.</summary>
    public string PrnText => Prn.ToString(CultureInfo.CurrentCulture);

    /// <summary>Whether the user has it picked.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(AutomationName)));
        }
    }

    /// <summary>
    /// A mark beside the PRN for a satellite the receiver excludes.
    /// </summary>
    /// <remarks>
    /// Exclusion is carried by a glyph and by the tooltip, not by the toggle's own state, because a
    /// toggle can only say two things and there are three: included, excluded, and on neither list.
    /// §9.4.3 and A11Y-12 rule out doing it with colour.
    /// </remarks>
    public string Marker => IsExcluded ? "✕" : string.Empty;

    /// <summary>What the receiver says about this satellite, in words.</summary>
    public string StateText => (IsIncluded, IsExcluded) switch
    {
        (_, true) => $"PRN {Prn} is excluded from tracking",
        (true, false) => $"PRN {Prn} is on the inclusion list",
        _ => $"PRN {Prn} is on neither list",
    };

    /// <summary>
    /// The whole row as one sentence, for assistive technology.
    /// </summary>
    /// <remarks>
    /// Carries both what the receiver reports and what the user has picked, because the toggle's own
    /// checked state announces only the second and the two can differ — which is the entire point of
    /// the dialog being editable.
    /// </remarks>
    public string AutomationName =>
        $"{StateText}. {(IsSelected ? "Selected" : "Not selected")}.";
}
