using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Views; // For RelayCommand
using Avalonia.Media;
using Avalonia.Media.Imaging;
using System;
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels.Library;

public class AlbumNode : ILibraryNode, INotifyPropertyChanged
{
    private readonly DownloadManager? _downloadManager;
    private readonly ArtworkCacheService? _artworkCacheService;
    private readonly IEventBus? _eventBus;
    private ArtworkProxy? _artwork;
    
    public string? AlbumTitle { get; set; }
    public string? Artist { get; set; }
    public string? Title => AlbumTitle;
    public string? Album => AlbumTitle;
    public string? Duration => string.Empty;
    public string? Bitrate => string.Empty;
    public string? Status => string.Empty;
    public int SortOrder => 0;
    public int Popularity => 0;
    public string? Genres => string.Empty;
    public DateTime AddedAt => DateTime.MinValue;
    public string? ReleaseYear => Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.ReleaseYear))?.ReleaseYear ?? "";
    private string? _albumArtPath;
    public string? AlbumArtPath
    {
        get => _albumArtPath;
        set
        {
            if (_albumArtPath != value)
            {
                _albumArtPath = value;
                OnPropertyChanged();
            }
        }
    }

    public double Progress
    {
        get
        {
            if (Tracks == null || !Tracks.Any()) return 0;
            // Only count tracks that have started or are downloading
            var tracksWithProgress = Tracks.Where(t => t.Progress > 0).ToList();
            if (!tracksWithProgress.Any()) return 0;
            
            return tracksWithProgress.Average(t => t.Progress);
        }
    }

    public ObservableCollection<PlaylistTrackViewModel> Tracks { get; } = new();
    
    public ICommand DownloadAlbumCommand { get; }
    public ICommand DownloadMissingCommand { get; }
    public ICommand ForceRedownloadCommand { get; }
    public ICommand PlayAlbumCommand { get; } 

    public bool IsLoading => false; // Obsolete with proxy

    public ArtworkProxy? Artwork
    {
        get
        {
            if (_artwork == null && !string.IsNullOrEmpty(AlbumArtPath) && _artworkCacheService != null)
            {
                _artwork = new ArtworkProxy(_artworkCacheService, AlbumArtPath);
            }
            return _artwork;
        }
    }

    public Bitmap? ArtworkBitmap => Artwork?.Image; // Keep for XAML binding compatibility if needed, but we'll update XAML too

    public IBrush FallbackBrush => GenerateColorFromHash(AlbumTitle ?? "?");
    public string FallbackLetter => !string.IsNullOrEmpty(AlbumTitle) ? AlbumTitle.Substring(0, 1).ToUpper() : "?";

    // Track Count for UI binding
    public int TrackCount => Tracks.Count;

    // Helper for color generation
    private IBrush GenerateColorFromHash(string input)
    {
        int hash = input.GetHashCode();
        byte r = (byte)((hash & 0xFF0000) >> 16);
        byte g = (byte)((hash & 0x00FF00) >> 8);
        byte b = (byte)(hash & 0x0000FF);
        
        // Ensure color is not too dark
        if (r < 50) r += 50;
        if (g < 50) g += 50;
        if (b < 50) b += 50;

        return new SolidColorBrush(Color.FromRgb(r, g, b));
    }

    public AlbumNode(string? albumTitle, string? artist, DownloadManager? downloadManager = null, ArtworkCacheService? artworkCacheService = null, IEventBus? eventBus = null)
    {
        AlbumTitle = albumTitle;
        Artist = artist;
        _downloadManager = downloadManager;
        _artworkCacheService = artworkCacheService;
        _eventBus = eventBus;
        
        DownloadAlbumCommand = new RelayCommand(DownloadAlbum);
        DownloadMissingCommand = new RelayCommand(DownloadMissing);
        ForceRedownloadCommand = new RelayCommand(ForceRedownload);
        PlayAlbumCommand = new RelayCommand<object>(_ => PlayAlbum());
        
        Tracks.CollectionChanged += (s, e) => {
            if (e.NewItems != null)
            {
                foreach (PlaylistTrackViewModel item in e.NewItems)
                    item.PropertyChanged += OnTrackPropertyChanged;
            }
            if (e.OldItems != null)
            {
                foreach (PlaylistTrackViewModel item in e.OldItems)
                    item.PropertyChanged -= OnTrackPropertyChanged;
            }
            OnPropertyChanged(nameof(Progress));
            UpdateAlbumArt();
        };
    }

    private void DownloadAlbum()
    {
        DownloadMissing();
    }

    private void DownloadMissing()
    {
        if (_downloadManager == null || !Tracks.Any()) return;
        
        var tracksToDownload = Tracks
            .Where(t => t.Model.Status != TrackStatus.Downloaded && t.Model.Status != TrackStatus.OnHold)
            .Select(t => { 
                t.Model.Priority = 0; 
                return t.Model; 
            }).ToList();
            
        if (tracksToDownload.Any())
        {
            _downloadManager.QueueTracks(tracksToDownload);
        }
    }

    private void ForceRedownload()
    {
        if (_downloadManager == null || !Tracks.Any()) return;
        
        var tracksToDownload = Tracks.Select(t => t.Model).ToList();
        foreach (var t in tracksToDownload)
        {
            _downloadManager.CancelTrack(t.TrackUniqueHash);
            t.Priority = 0;
            t.Status = TrackStatus.Missing;
            t.IsClearedFromDownloadCenter = false;
        }
        
        _downloadManager.QueueTracks(tracksToDownload);
    }


    private void PlayAlbum()
    {
        if (!Tracks.Any()) return;

        var tracks = Tracks
            .Where(t => t.Model != null && !string.IsNullOrEmpty(t.Model.ResolvedFilePath))
            .Select(t => t.Model)
            .ToList();

        if (tracks.Count == 0) return;

        _eventBus?.Publish(new PlayAlbumRequestEvent(tracks));
    }

    private void UpdateAlbumArt()
    {
        // Use the art from the first track that has it
        var art = Tracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtPath))?.AlbumArtPath;
        if (art != AlbumArtPath)
        {
            AlbumArtPath = art;
            // Refresh proxy
            _artwork = null;
            if (!string.IsNullOrEmpty(AlbumArtPath) && _artworkCacheService != null)
            {
                _artwork = new ArtworkProxy(_artworkCacheService, AlbumArtPath);
            }
            OnPropertyChanged(nameof(Artwork));
            OnPropertyChanged(nameof(ArtworkBitmap));
        }
    }

    private void OnTrackPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(PlaylistTrackViewModel.Progress))
        {
            OnPropertyChanged(nameof(Progress));
        }
        else if (e.PropertyName == nameof(PlaylistTrackViewModel.AlbumArtPath))
        {
            UpdateAlbumArt();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
