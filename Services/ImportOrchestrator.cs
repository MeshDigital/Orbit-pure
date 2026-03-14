using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.ViewModels;

namespace SLSKDONET.Services;

/// <summary>
/// Centralized orchestrator for all import operations.
/// Handles the entire import pipeline from source parsing to library persistence.
/// </summary>
public class ImportOrchestrator
{
    private readonly ILogger<ImportOrchestrator> _logger;
    private readonly ImportPreviewViewModel _previewViewModel;
    private readonly DownloadManager _downloadManager;
    private readonly INavigationService _navigationService;
    private readonly Views.INotificationService _notificationService;
    private readonly ILibraryService _libraryService;

    // Track current import to avoid duplicate event subscriptions in older logic
    // private bool _isHandlingImport; // REMOVED: Unused

    public ImportOrchestrator(
        ILogger<ImportOrchestrator> logger,
        ImportPreviewViewModel previewViewModel,
        DownloadManager downloadManager,
        INavigationService navigationService,
        Views.INotificationService notificationService,
        ILibraryService libraryService)
    {
        _logger = logger;
        _previewViewModel = previewViewModel;
        _downloadManager = downloadManager;
        _navigationService = navigationService;
        _notificationService = notificationService;
        _libraryService = libraryService;
    }

    /// <summary>
    /// Import with preview screen - allows user to select tracks.
    /// </summary>
    /// <summary>
    /// Import with preview screen - allows user to select tracks.
    /// Supports streaming for immediate UI feedback.
    /// </summary>
    /// <summary>
    /// Unified Import Method: Streams into Preview, then Hands off to DownloadManager.
    /// Replaces all legacy blocking/split logic.
    /// </summary>
    public async Task StartImportWithPreviewAsync(IImportProvider provider, string input)
    {
        try
        {
            _logger.LogInformation("Starting unified import from {Provider}: {Input}", provider.Name, input);

            if (provider is IStreamingImportProvider streamProvider)
            {
                try
                {
                     // Phase 7: Deterministic ID / Deduplication
                     // WHY: Prevents duplicate imports of same playlist:
                     // - User re-pastes Spotify URL -> should update existing, not create duplicate
                     // - Algorithm: Hash(normalized URL) = consistent GUID for same source
                     // - Fallback: Check by URL string match if hash fails (legacy imports)
                     // 
                     // BENEFITS:
                     // - Idempotent imports (safe to retry)
                     // - "Refresh" feature (re-import updates metadata)
                     // - Storage efficiency (no duplicate playlist entries)
                     var newJobId = Utils.GuidGenerator.CreateFromUrl(input);
                     _logger.LogInformation("Generated Job ID: {Id} for input: {Input}", newJobId, input);
                     
                     // Retrieve existing job if any (Deduplication)
                     var existingJob = await _libraryService.FindPlaylistJobAsync(newJobId);

                     if (existingJob != null)
                     {
                         _logger.LogInformation("Found duplicate playlist by ID match: {Title} ({Id})", existingJob.SourceTitle, existingJob.Id);
                     }
                     else
                     {
                         _logger.LogInformation("No duplicate found by ID match for {Id}. Checking URL fallback...", newJobId);
                     }

                     // Fallback: Check by normalized URL if strict ID match failed
                     if (existingJob == null)
                     {
                         existingJob = await _libraryService.FindPlaylistJobBySourceUrlAsync(input);
                         if (existingJob != null)
                         {
                             _logger.LogInformation("Found duplicate playlist by URL match: {Title}", existingJob.SourceTitle);
                         }
                         else
                         {
                             _logger.LogInformation("No duplicate found by URL match for input: {Input}", input);
                         }
                     }
                     
                     // Initialize UI
                     _previewViewModel.InitializeStreamingPreview(provider.Name, provider.Name, newJobId, input, existingJob);
                     
                     // Clean/Setup Callbacks
                     SetupPreviewCallbacks();
    
                     // Navigate
                     _navigationService.NavigateTo("ImportPreview");
                     _logger.LogInformation("Navigated to ImportPreview");
                     
                     // Start Streaming
                     _ = Task.Run(async () => await StreamPreviewAsync(streamProvider, input));
                }
                catch (Exception navEx)
                {
                    _logger.LogError(navEx, "Critical error during Import Setup/Navigation");
                    _notificationService.Show("Import Error", $"Navigation failed: {navEx.Message}", Views.NotificationType.Error);
                    throw; // Rethrow to let caller know
                }
            }
            else
            {
                throw new InvalidOperationException($"Provider {provider.Name} must implement IStreamingImportProvider");
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to start import from {Provider}", provider.Name);
            _notificationService.Show("Import Error", $"Failed to import: {ex.Message}", Views.NotificationType.Error);
        }
    }

    /// <summary>
    /// Import directly to library/downloader without preview UI.
    /// Useful for background syncing or "Import All" scenarios.
    /// </summary>
    public async Task SilentImportAsync(IImportProvider provider, string input)
    {
        try
        {
            _logger.LogInformation("Starting silent/background import from {Provider}: {Input}", provider.Name, input);

            if (provider is IStreamingImportProvider streamProvider)
            {
                var newJobId = Utils.GuidGenerator.CreateFromUrl(input);
                var existingJob = await _libraryService.FindPlaylistJobAsync(newJobId);
                
                string sourceTitle = provider.Name;
                var allTracks = new System.Collections.Generic.List<PlaylistTrack>();

                await foreach (var batch in streamProvider.ImportStreamAsync(input))
                {
                    if (!string.IsNullOrEmpty(batch.SourceTitle) && sourceTitle == provider.Name)
                        sourceTitle = batch.SourceTitle;
                    
                    var convertedTracks = batch.Tracks.Select(t => new PlaylistTrack 
                    {
                        Id = Guid.NewGuid(),
                        Artist = t.Artist ?? string.Empty,
                        Title = t.Title ?? string.Empty,
                        Album = t.Album ?? string.Empty,
                        TrackUniqueHash = t.TrackHash ?? string.Empty,
                        SpotifyTrackId = t.SpotifyTrackId,
                        SpotifyAlbumId = t.SpotifyAlbumId,
                        SpotifyArtistId = t.SpotifyArtistId,
                        AlbumArtUrl = t.AlbumArtUrl,
                        ArtistImageUrl = t.ArtistImageUrl,
                        Genres = t.Genres,
                        Popularity = t.Popularity,
                        CanonicalDuration = t.CanonicalDuration,
                        ReleaseDate = t.ReleaseDate,
                        SourcePlaylistId = newJobId,
                        SourcePlaylistName = sourceTitle,
                        Status = TrackStatus.Missing,

                        AddedAt = DateTime.UtcNow
                    }).ToList();

                    allTracks.AddRange(convertedTracks);
                }

                if (allTracks.Count == 0)
                {
                    _logger.LogWarning("Silent import for {Input} yielded 0 tracks. Skipping.", input);
                    return;
                }

                // Create and start job
                var job = new PlaylistJob
                {
                    Id = newJobId,
                    SourceUrl = input,
                    SourceTitle = sourceTitle,
                    SourceType = provider.Name,
                    PlaylistTracks = allTracks,
                    CreatedAt = DateTime.UtcNow
                };

                await _downloadManager.QueueProject(job);
                _notificationService.Show("Import Complete", $"Successfully synced {allTracks.Count} tracks from '{sourceTitle}'", Views.NotificationType.Success);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to perform silent import for {Input}", input);
            _notificationService.Show("Import Error", $"Silent import failed: {ex.Message}", Views.NotificationType.Error);
        }
    }

    private async Task StreamPreviewAsync(IStreamingImportProvider provider, string input)
    {
        try
        {
            await foreach (var batch in provider.ImportStreamAsync(input))
            {
                 // Update Title from first batch if generic
                 if (!string.IsNullOrEmpty(batch.SourceTitle) && _previewViewModel.SourceTitle == provider.Name)
                 {
                     await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                     {
                         _previewViewModel.SourceTitle = batch.SourceTitle;
                     });
                 }
                 
                 await _previewViewModel.AddTracksToPreviewAsync(batch.Tracks);
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Error during streaming preview");
             await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _previewViewModel.StatusMessage = "Stream error: " + ex.Message);
        }
        finally
        {
             await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => _previewViewModel.IsLoading = false);
        }
    }

    /// <summary>
    /// Set up event handlers for preview screen callbacks.
    /// </summary>
    private void SetupPreviewCallbacks()
    {
        // Always clean up any existing subscriptions first to avoid doubles
        _logger.LogInformation("Setting up ImportPreviewViewModel event callbacks");
        _previewViewModel.AddedToLibrary -= OnPreviewConfirmed;
        _previewViewModel.Cancelled -= OnPreviewCancelled;

        // Subscribe
        _previewViewModel.AddedToLibrary += OnPreviewConfirmed;
        _previewViewModel.Cancelled += OnPreviewCancelled;
    }

    /// <summary>
    /// Handle when user confirms tracks in preview screen.
    /// </summary>
    private void OnPreviewConfirmed(object? sender, PlaylistJob job)
    {
        try
        {
            _logger.LogInformation("Preview confirmed: {Title} with {Count} tracks",
                job.SourceTitle, job.OriginalTracks.Count);

            // Navigate to library
            _navigationService.NavigateTo("Library");

            _logger.LogInformation("Import completed and navigated to Library");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle preview confirmation");
        }
        finally
        {
            CleanupCallbacks();
        }
    }

    /// <summary>
    /// Handle when user cancels preview.
    /// </summary>
    private void OnPreviewCancelled(object? sender, EventArgs e)
    {
        _logger.LogInformation("Import preview cancelled");
        _navigationService.GoBack();
        CleanupCallbacks();
    }

    /// <summary>
    /// Remove event handlers after import completes.
    /// </summary>
    private void CleanupCallbacks()
    {
        _previewViewModel.AddedToLibrary -= OnPreviewConfirmed;
        _previewViewModel.Cancelled -= OnPreviewCancelled;
    }
}
