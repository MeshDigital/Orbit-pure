using System;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Enhanced ViewModel wrapper for playlist cards in the library with forensic health metrics.
/// Provides UI-specific properties including the Health Ring calculation.
/// </summary>
public class LibraryPlaylistCardViewModel : ReactiveObject
{
    private readonly PlaylistJob _playlist;
    private readonly ArtworkCacheService? _artworkCacheService;
    private readonly PlaylistMosaicService? _mosaicService;

    private Bitmap? _coverBitmap;

    /// <summary>
    /// The cover bitmap to display for this playlist card.
    /// When the playlist has a dedicated cover URL it is loaded directly.
    /// When the playlist has no cover (e.g. it contains random tracks from many albums),
    /// a 2×2 mosaic is generated from up to four distinct track album-art images.
    /// This property is populated asynchronously; the UI updates via PropertyChanged.
    /// </summary>
    public Bitmap? CoverBitmap
    {
        get => _coverBitmap;
        private set => this.RaiseAndSetIfChanged(ref _coverBitmap, value);
    }

    public Guid Id => _playlist.Id;
    public string Name => _playlist.SourceTitle;
    public string? CoverImageUrl => _playlist.DisplayArtUrl;
    public int TrackCount => _playlist.TotalTracks;

    public string TrackCountText => $"{TrackCount} track{(TrackCount != 1 ? "s" : "")}";

    public int DownloadedCount => _playlist.SuccessfulCount;

    public double DownloadedRatio => TrackCount > 0
        ? (double)DownloadedCount / TrackCount
        : 0.0;

    private bool HasForensicCoverage =>
        _playlist.PlaylistTracks != null &&
        _playlist.PlaylistTracks.Any(t =>
            t.Integrity == SLSKDONET.Data.IntegrityLevel.Verified ||
            t.Integrity == SLSKDONET.Data.IntegrityLevel.Gold ||
            t.Integrity == SLSKDONET.Data.IntegrityLevel.Suspicious ||
            t.FrequencyCutoff.HasValue);

    public double PrimaryRatio => HasForensicCoverage ? LosslessRatio : DownloadedRatio;

    public string PrimaryRatioText => $"{PrimaryRatio:P0}";

    public string PrimaryBadgeText => HasForensicCoverage
        ? $"Lossless {LosslessRatio:P0}"
        : $"Downloaded {DownloadedRatio:P0}";

    public string SecondaryBadgeText => $"{DownloadedCount}/{TrackCount} downloaded";

    /// <summary>
    /// Health Ring: Ratio of verified lossless tracks (0.0 to 1.0)
    /// Calculated as (Verified + Gold) / TotalTracks
    /// </summary>
    public double LosslessRatio
    {
        get
        {
            if (_playlist.PlaylistTracks == null || _playlist.TotalTracks == 0)
                return 0.0;

            var verifiedCount = _playlist.PlaylistTracks.Count(t =>
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Verified ||
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Gold);

            return (double)verifiedCount / _playlist.TotalTracks;
        }
    }

    /// <summary>
    /// Health Ring color based on lossless ratio
    /// </summary>
    public string HealthRingColor => LosslessRatio switch
    {
        >= 0.9 => "#00FF88", // Green - Excellent
        >= 0.7 => "#FFCC00", // Yellow - Good
        >= 0.5 => "#FF9D00", // Orange - Fair
        _ => "#FF4444"       // Red - Poor
    };

    public string PrimaryRingColor => HasForensicCoverage
        ? HealthRingColor
        : DownloadedRatio switch
        {
            >= 0.95 => "#29D391",
            >= 0.7 => "#2DB5FF",
            >= 0.4 => "#F3A73B",
            _ => "#E56767"
        };

    /// <summary>
    /// Health status text for tooltip
    /// </summary>
    public string HealthStatusText
    {
        get
        {
            if (_playlist.PlaylistTracks == null)
                return $"No forensic data yet. {DownloadedCount}/{TrackCount} downloaded.";

            var verified = _playlist.PlaylistTracks.Count(t =>
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Verified ||
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Gold);

            var suspicious = _playlist.PlaylistTracks.Count(t =>
                t.Integrity == SLSKDONET.Data.IntegrityLevel.Suspicious);

            return $"{verified} verified lossless, {suspicious} suspicious";
        }
    }

    public string ForensicFlyoutText
    {
        get
        {
            if (_playlist.PlaylistTracks == null || _playlist.PlaylistTracks.Count == 0)
                return "No forensic data available yet.";

            var analyzed = _playlist.PlaylistTracks.Where(t => t.FrequencyCutoff.HasValue).ToList();
            if (!analyzed.Any())
                return $"{HealthStatusText} • Spectral analysis pending.";

            var avgCutoff = analyzed.Average(t => t.FrequencyCutoff!.Value) / 1000.0;
            var transcoded = _playlist.PlaylistTracks.Count(t => t.IsTranscoded);

            return $"{HealthStatusText}\nAvg spectral cutoff: {avgCutoff:F1} kHz\nFlagged transcodes: {transcoded}";
        }
    }

    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set => this.RaiseAndSetIfChanged(ref _isHovered, value);
    }

    public LibraryPlaylistCardViewModel(
        PlaylistJob playlist,
        ArtworkCacheService? artworkCacheService = null,
        PlaylistMosaicService? mosaicService = null)
    {
        _playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        _artworkCacheService = artworkCacheService;
        _mosaicService = mosaicService;
        _playlist.PropertyChanged += OnPlaylistPropertyChanged;

        // Kick off async cover-art load without blocking the constructor
        _ = LoadCoverBitmapAsync();
    }

    /// <summary>
    /// Asynchronously loads the cover bitmap.
    /// Priority: playlist's own cover URL → 2×2 mosaic from track art → nothing.
    /// </summary>
    private async Task LoadCoverBitmapAsync()
    {
        try
        {
            Bitmap? bmp = null;

            var coverUrl = _playlist.AlbumArtUrl;

            if (!string.IsNullOrWhiteSpace(coverUrl) && _artworkCacheService != null)
            {
                // Playlist has its own cover image — load it
                bmp = await _artworkCacheService.GetBitmapAsync(coverUrl);
            }
            else if (_mosaicService != null && _playlist.PlaylistTracks?.Count > 0)
            {
                // No dedicated cover: build a collage from track album-art URLs
                var trackArtUrls = _playlist.PlaylistTracks
                    .Select(t => t.AlbumArtUrl)
                    .Where(u => !string.IsNullOrWhiteSpace(u))
                    .Distinct()
                    .Take(4);

                bmp = await _mosaicService.GenerateMosaicAsync(trackArtUrls);
            }

            if (bmp != null)
            {
                await Dispatcher.UIThread.InvokeAsync(() => CoverBitmap = bmp);
            }
        }
        catch
        {
            // Non-fatal — placeholder will remain visible
        }
    }

    private void OnPlaylistPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        this.RaisePropertyChanged(nameof(Name));
        this.RaisePropertyChanged(nameof(CoverImageUrl));
        this.RaisePropertyChanged(nameof(CoverBitmap));
        this.RaisePropertyChanged(nameof(TrackCount));
        this.RaisePropertyChanged(nameof(TrackCountText));
        this.RaisePropertyChanged(nameof(DownloadedCount));
        this.RaisePropertyChanged(nameof(DownloadedRatio));
        this.RaisePropertyChanged(nameof(PrimaryRatio));
        this.RaisePropertyChanged(nameof(PrimaryRatioText));
        this.RaisePropertyChanged(nameof(PrimaryBadgeText));
        this.RaisePropertyChanged(nameof(SecondaryBadgeText));
        this.RaisePropertyChanged(nameof(LosslessRatio));
        this.RaisePropertyChanged(nameof(HealthRingColor));
        this.RaisePropertyChanged(nameof(PrimaryRingColor));
        this.RaisePropertyChanged(nameof(HealthStatusText));
        this.RaisePropertyChanged(nameof(ForensicFlyoutText));

        // Only reload cover art when the properties that affect it actually change,
        // to avoid redundant mosaic generation when other model fields update.
        if (e.PropertyName is nameof(PlaylistJob.AlbumArtUrl) or nameof(PlaylistJob.PlaylistTracks))
            _ = LoadCoverBitmapAsync();
    }

    // Explicit access to the underlying model if needed for commands
    public PlaylistJob Model => _playlist;
}