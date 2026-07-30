using Avalonia.Media.Imaging;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel wrapper for playlist cards on the Mission Control dashboard.
/// Provides UI-specific properties and formatting.
/// </summary>
public class PlaylistCardViewModel : ReactiveObject
{
    private readonly PlaylistJob _playlist;
    private readonly ArtworkCacheService? _artworkCacheService;
    private readonly PlaylistMosaicService? _mosaicService;

    public Guid Id => _playlist.Id;
    public string Name => _playlist.SourceTitle;
    public string? CoverImageUrl => _playlist.DisplayArtUrl;
    public int TrackCount => _playlist.TotalTracks;

    private Bitmap? _coverBitmap;
    /// <summary>
    /// The cover bitmap to display for this card. Loaded asynchronously: the playlist's own
    /// cover URL if it has one, otherwise a 2x2 mosaic generated from up to four distinct
    /// track album-art images. Mirrors <see cref="LibraryPlaylistCardViewModel"/>'s loading logic.
    /// </summary>
    public Bitmap? CoverBitmap
    {
        get => _coverBitmap;
        private set => this.RaiseAndSetIfChanged(ref _coverBitmap, value);
    }

    // Calculated match percentage from database context usually,
    // but we'll allow it to be passed or updated.
    private double _matchPercentage;
    public double MatchPercentage
    {
        get => _matchPercentage;
        set => this.RaiseAndSetIfChanged(ref _matchPercentage, value);
    }

    public string TrackCountText => $"{TrackCount} track{(TrackCount != 1 ? "s" : "")}";
    public string MatchText => $"{MatchPercentage:0}% Match";

    private bool _isHovered;
    public bool IsHovered
    {
        get => _isHovered;
        set => this.RaiseAndSetIfChanged(ref _isHovered, value);
    }

    public PlaylistCardViewModel(PlaylistJob playlist, ArtworkCacheService? artworkCacheService = null, PlaylistMosaicService? mosaicService = null)
    {
        _playlist = playlist ?? throw new ArgumentNullException(nameof(playlist));
        _artworkCacheService = artworkCacheService;
        _mosaicService = mosaicService;

        // Initial match percentage calculation (if not already set in model)
        // Note: Models don't have MatchPercentage yet, we'll calculate it in DashboardService
        _matchPercentage = _playlist.TotalTracks > 0
            ? (double)_playlist.SuccessfulCount / _playlist.TotalTracks * 100
            : 0;

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
                bmp = await _artworkCacheService.GetBitmapAsync(coverUrl);
            }
            else if (_mosaicService != null && _playlist.PlaylistTracks?.Count > 0)
            {
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

    // Explicit access to the underlying model if needed for commands
    public PlaylistJob Model => _playlist;
}
