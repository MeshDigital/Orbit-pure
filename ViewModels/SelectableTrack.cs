using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Wrapper for Track model to handle UI selection state and notifications.
/// </summary>
public class SelectableTrack : INotifyPropertyChanged
{
    public Track Model { get; }
    public Track Track => Model; // Alias for compatibility
    
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected != value)
            {
                _isSelected = value;
                Model.IsSelected = value; // Sync with model
                OnPropertyChanged();
                
                // Notify listener (ViewModel)
                OnSelectionChanged?.Invoke();
            }
        }
    }

    public Action? OnSelectionChanged { get; set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    // Proxied properties for compatibility
    public string? Artist => Model.Artist;
    public string? Title => Model.Title;
    public string? Album => Model.Album;
    public bool IsInLibrary => Model.IsInLibrary;
    
    private int _trackNumber;
    public int TrackNumber
    {
        get => _trackNumber;
        set
        {
            if (_trackNumber != value)
            {
                _trackNumber = value;
                OnPropertyChanged();
            }
        }
    }

    public SelectableTrack(Track track, bool isSelected = false)
    {
        Model = track;
        _isSelected = isSelected;
        Model.IsSelected = isSelected;
    }

    // Constructor for SpotifyImportViewModel compatibility
    public SelectableTrack(Track track, int trackNumber) : this(track, false)
    {
        TrackNumber = trackNumber;
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
