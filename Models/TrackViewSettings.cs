using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace SLSKDONET.Models;
 
public enum TrackViewMode
{
    List,
    Cards,
    Compact,
    Pro
}

/// <summary>
/// User preferences for track metadata display in Library view.
/// Controls visibility of various UI elements in StandardTrackRow.
/// </summary>
public class TrackViewSettings : INotifyPropertyChanged
{
    private bool _showQualityBar = true;
    private bool _showStatusInfo = true;
    private bool _showBadges = true;
    private bool _showTechnicalDetails = true;
    private bool _showDuration = true;
    private bool _showFileSize = true;
    private bool _showBpmKey = true;
    private bool _showReleaseYear = true;
    private bool _showVibePills = true;
    private TrackViewMode _viewMode = TrackViewMode.List;

    public bool ShowQualityBar
    {
        get => _showQualityBar;
        set => SetProperty(ref _showQualityBar, value);
    }

    public bool ShowStatusInfo
    {
        get => _showStatusInfo;
        set => SetProperty(ref _showStatusInfo, value);
    }

    public bool ShowBadges
    {
        get => _showBadges;
        set => SetProperty(ref _showBadges, value);
    }

    public bool ShowTechnicalDetails
    {
        get => _showTechnicalDetails;
        set => SetProperty(ref _showTechnicalDetails, value);
    }

    public bool ShowDuration
    {
        get => _showDuration;
        set => SetProperty(ref _showDuration, value);
    }

    public bool ShowFileSize
    {
        get => _showFileSize;
        set => SetProperty(ref _showFileSize, value);
    }

    public bool ShowBpmKey
    {
        get => _showBpmKey;
        set => SetProperty(ref _showBpmKey, value);
    }

    public bool ShowReleaseYear
    {
        get => _showReleaseYear;
        set => SetProperty(ref _showReleaseYear, value);
    }

    public bool ShowVibePills
    {
        get => _showVibePills;
        set => SetProperty(ref _showVibePills, value);
    }
    
    public TrackViewMode ViewMode
    {
        get => _viewMode;
        set => SetProperty(ref _viewMode, value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }

    protected virtual void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
