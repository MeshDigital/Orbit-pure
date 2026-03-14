using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;

using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// ViewModel for previewing imported tracks before adding to library.
/// Displays tracks in a grid view with selection and album grouping.
/// </summary>
public class ImportPreviewViewModel : INotifyPropertyChanged
{
    private readonly ILogger<ImportPreviewViewModel> _logger;
    private readonly DownloadManager _downloadManager;
    private readonly ILibraryService? _libraryService;
    private readonly INavigationService _navigationService;
    private readonly ISpotifyMetadataService? _metadataService;
    
    private string _sourceTitle = "Import Preview";
    private string _sourceType = "";
    private ObservableCollection<SelectableTrack> _importedTracks = new();
    private ObservableCollection<AlbumGroupViewModel> _albumGroups = new();
    private bool _isLoading;
    private string _statusMessage = "Ready to import";
    private int _selectedCount;

    public string SourceTitle
    {
        get => _sourceTitle;
        set { _sourceTitle = value; OnPropertyChanged(); }
    }

    public string SourceType
    {
        get => _sourceType;
        set { _sourceType = value; OnPropertyChanged(); }
    }

    public ObservableCollection<SelectableTrack> ImportedTracks
    {
        get => _importedTracks;
        set { _importedTracks = value; OnPropertyChanged(); }
    }

    public ObservableCollection<AlbumGroupViewModel> AlbumGroups
    {
        get => _albumGroups;
        set { _albumGroups = value; OnPropertyChanged(); }
    }

    public bool IsLoading
    {
        get => _isLoading;
        set
        {
            if (_isLoading != value)
            {
                _isLoading = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanAddToLibrary));
            }
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        set { _statusMessage = value; OnPropertyChanged(); }
    }

    public int SelectedCount
    {
        get => _selectedCount;
        set { _selectedCount = value; OnPropertyChanged(); }
    }

    public int TrackCount => ImportedTracks.Count;
    public bool CanAddToLibrary => !IsLoading && SelectedCount > 0;

    public ICommand AddToLibraryCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand SelectMissingCommand { get; }
    public ICommand CancelCommand { get; }


    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler<PlaylistJob>? AddedToLibrary;
    public event EventHandler? Cancelled;

    public ImportPreviewViewModel(
        ILogger<ImportPreviewViewModel> logger,
        DownloadManager downloadManager,
        INavigationService navigationService,
        ILibraryService? libraryService = null,
        ISpotifyMetadataService? metadataService = null)
    {
        _logger = logger;
        _downloadManager = downloadManager;
        _libraryService = libraryService;
        _navigationService = navigationService;
        _metadataService = metadataService;

        AddToLibraryCommand = new AsyncRelayCommand(AddToLibraryAsync, () => CanAddToLibrary);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        SelectMissingCommand = new RelayCommand(SelectMissing);
        CancelCommand = new RelayCommand(Cancel);
        
        MergeCommand = new AsyncRelayCommand(MergeAsync);
        CreateNewCommand = new AsyncRelayCommand(CreateNewAsync);
    }

    /// <summary>
    /// Initialize preview with imported tracks from Spotify/CSV/etc
    /// </summary>
    public async Task InitializePreviewAsync(string sourceTitle, string sourceType, IEnumerable<SearchQuery> queries)
    {
        try
        {
            _logger.LogInformation("InitializePreviewAsync ENTRY: {SourceTitle}, {SourceType}, Query count: {Count}",
                sourceTitle, sourceType, queries?.Count() ?? 0);

            SourceTitle = sourceTitle;
            SourceType = sourceType;

            int trackNum = 1;
            var tempTracks = new List<Track>();
            foreach (var query in queries ?? Enumerable.Empty<SearchQuery>())
            {
                var track = new Track
                {
                    Title = query.Title,
                    Artist = query.Artist,
                    Album = query.Album,
                    Length = query.Length,
                    // Phase 0: Map Spotify Metadata
                    SpotifyTrackId = query.SpotifyTrackId,
                    SpotifyAlbumId = query.SpotifyAlbumId,
                    SpotifyArtistId = query.SpotifyArtistId,
                    AlbumArtUrl = query.AlbumArtUrl,
                    ArtistImageUrl = query.ArtistImageUrl,
                    Genres = query.Genres,
                    Popularity = query.Popularity,
                    CanonicalDuration = query.CanonicalDuration,
                    ReleaseDate = query.ReleaseDate
                };
                tempTracks.Add(track);
                trackNum++;
            }

            _logger.LogInformation("Created {Count} Track objects from queries", tempTracks.Count);

            // Check for duplicates asynchronously
            if (_libraryService != null)
            {
                try
                {
                    foreach (var track in tempTracks)
                    {
                        var entry = await _libraryService.FindLibraryEntryAsync(track.UniqueHash);
                        track.IsInLibrary = entry != null;
                    }
                }
                catch (Exception ex)
                {
                    // Database might not be initialized yet on first run - this is non-critical
                    _logger.LogDebug(ex, "Could not check library for duplicates (database may not be initialized)");
                }
            }

            // Create SelectableTrack wrappers
            var selectableTracks = new List<SelectableTrack>();
            foreach (var track in tempTracks)
            {
                var selectableTrack = new SelectableTrack(track);
                
                // Wire up notification so the ViewModel knows when selection changes
                selectableTrack.OnSelectionChanged = () => 
                {
                     UpdateSelectedCount();
                     // Re-evaluate command can-execute
                     ((AsyncRelayCommand)AddToLibraryCommand).RaiseCanExecuteChanged();
                };
                
                selectableTracks.Add(selectableTrack);
            }

            _logger.LogInformation("Created {Count} SelectableTrack wrappers", selectableTracks.Count);

            // CRITICAL FIX: Replace entire collection instead of modifying it
            // This triggers OnPropertyChanged and forces UI to rebind
            // Pattern from main branch, adapted for SelectableTrack
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                ImportedTracks = new ObservableCollection<SelectableTrack>(selectableTracks);
                AlbumGroups.Clear(); // Clear album groups before regrouping
            });

            _logger.LogInformation("Replaced ImportedTracks collection with {Count} items", ImportedTracks.Count);

            // Group by album for display
            GroupByAlbum();
            
            // Phase 22: Default to selecting missing tracks
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
            {
                SelectMissing();
                StatusMessage = $"Loaded {ImportedTracks.Count} tracks";
            });
            
            _logger.LogInformation("Import preview initialized with {Count} tracks from {Source}. First track: {Artist} - {Title}", 
                ImportedTracks.Count, sourceTitle, 
                ImportedTracks.FirstOrDefault()?.Model.Artist ?? "None", 
                ImportedTracks.FirstOrDefault()?.Model.Title ?? "None");
            
            // Start background metadata enrichment
            // REMOVED: Enrichment should only happen AFTER user confirms import (Add to Library).
            // This prevents premature API calls and "busy" UI during preview.
            /*
            if (_metadataService != null)
            {
                _enrichmentCts?.Cancel();
                _enrichmentCts = new CancellationTokenSource();
                var token = _enrichmentCts.Token;
                
                _ = Task.Run(() => EnrichTracksInBackgroundAsync(ImportedTracks.Select(st => st.Model).ToList(), token), token);
            }
            */
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "InitializePreviewAsync FAILED for {SourceTitle}", sourceTitle);
            StatusMessage = $"Error loading preview: {ex.Message}";
            throw;
        }
    }

    /// <summary>
    /// Initialize preview mode for streaming - clears existing data and sets headers immediately.
    /// </summary>
    private bool _isDuplicate;
    private int _existingTrackCount;
    private PlaylistJob? _existingJob;
    private Guid _targetJobId;
    private string? _sourceUrl;

    public bool IsDuplicate
    {
        get => _isDuplicate;
        set { _isDuplicate = value; OnPropertyChanged(); }
    }

    public int ExistingTrackCount
    {
        get => _existingTrackCount;
        set { _existingTrackCount = value; OnPropertyChanged(); }
    }

    public ICommand MergeCommand { get; }
    public ICommand CreateNewCommand { get; }

    /// <summary>
    /// Initialize preview mode for streaming - clears existing data and sets headers immediately.
    /// accepts an optional existing job for deduplication scenarios.
    /// </summary>
    public void InitializeStreamingPreview(string sourceTitle, string sourceType, Guid newJobId, string inputUrl, PlaylistJob? existingJob = null)
    {
        SourceTitle = sourceTitle;
        SourceType = sourceType;
        StatusMessage = "Starting import stream...";
        IsLoading = true;
        
        ImportedTracks.Clear();
        AlbumGroups.Clear();
        SelectedCount = 0;

        _sourceUrl = inputUrl;
        
        // Deduplication Logic
        _existingJob = existingJob;
        if (_existingJob != null)
        {
            IsDuplicate = true;
            ExistingTrackCount = _existingJob.PlaylistTracks.Count; // Assuming Tracks loaded, otherwise TotalTracks
            if (ExistingTrackCount == 0 && _existingJob.TotalTracks > 0) ExistingTrackCount = _existingJob.TotalTracks;
            
            // Default assumes MERGE, so we target the EXISTING ID
            _targetJobId = _existingJob.Id;
            StatusMessage = $"Found existing playlist '{_existingJob.SourceTitle}' ({ExistingTrackCount} tracks).";
        }
        else
        {
             IsDuplicate = false;
             ExistingTrackCount = 0;
             _targetJobId = newJobId;
        }
    }
    
    private async Task MergeAsync()
    {
        // 1. Deselect everything first
        DeselectAll();

        // 2. Select only missing tracks
        SelectMissing();

        // 3. Add to library (this will use the existing _targetJobId)
        await AddToLibraryAsync();
    }

    private async Task CreateNewAsync()
    {
        // Force a new random ID to avoid collision
        _targetJobId = Guid.NewGuid();
        await AddToLibraryAsync();
    }

    /// <summary>
    /// Appends a batch of streamed tracks to the preview.
    /// </summary>
    public async Task AddTracksToPreviewAsync(IEnumerable<SearchQuery> queries)
    {
         if (queries == null || !queries.Any()) return;

         var enrichedTracks = new List<SelectableTrack>();
         foreach (var query in queries)
         {
             var track = new Track
             {
                Title = query.Title,
                Artist = query.Artist,
                Album = query.Album,
                Length = query.Length,
                SpotifyTrackId = query.SpotifyTrackId,
                SpotifyAlbumId = query.SpotifyAlbumId,
                SpotifyArtistId = query.SpotifyArtistId,
                AlbumArtUrl = query.AlbumArtUrl,
                ArtistImageUrl = query.ArtistImageUrl,
                Genres = query.Genres,
                Popularity = query.Popularity,
                CanonicalDuration = query.CanonicalDuration,
                ReleaseDate = query.ReleaseDate
             };

             // Check library status for deduplication feedback (OFF UI THREAD)
             if (_libraryService != null)
             {
                 try 
                 {
                     var entry = await _libraryService.FindLibraryEntryAsync(track.UniqueHash);
                     track.IsInLibrary = entry != null;
                 }
                 catch { /* Ignore DB errors during stream */ }
             }

             var selectable = new SelectableTrack(track);
             enrichedTracks.Add(selectable);
         }

         await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
         {
             foreach (var selectable in enrichedTracks)
             {
                 selectable.OnSelectionChanged = () => 
                 {
                     UpdateSelectedCount();
                     if (AddToLibraryCommand is AsyncRelayCommand arc)
                        arc.RaiseCanExecuteChanged();
                 };
                 ImportedTracks.Add(selectable);
             }

             // Efficiently update groups
             UpdateAlbumGroupsIncremental(enrichedTracks);

             // Auto-select missing tracks if they are not already in library
             foreach (var selectable in enrichedTracks)
             {
                 if (!selectable.IsInLibrary)
                     selectable.IsSelected = true;
             }

             StatusMessage = $"Loaded {ImportedTracks.Count} tracks...";
             IsLoading = false; 
             UpdateSelectedCount();
         });
    }

    private void UpdateAlbumGroupsIncremental(List<SelectableTrack> newTracks)
    {
        foreach (var track in newTracks)
        {
            var albumName = track.Model.Album ?? "[Unknown Album]";
            var group = AlbumGroups.FirstOrDefault(g => g.Album == albumName);
            if (group == null)
            {
                group = new AlbumGroupViewModel { Album = albumName };
                AlbumGroups.Add(group);
            }
            group.Tracks.Add(track);
        }
    }
    private async Task EnrichTracksInBackgroundAsync(List<Track> tracks, CancellationToken cancellationToken)
    {
        try
        {
            // Skip enrichment if metadata service is not available or not authenticated
            if (_metadataService == null)
            {
                _logger.LogDebug("Skipping enrichment: Metadata service not available");
                return;
            }

            _logger.LogInformation("Starting background metadata enrichment for {Count} tracks", tracks.Count);
            
            // Initial delay to let UI settle
            await Task.Delay(500, cancellationToken);
            
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => StatusMessage = "Fetching metadata...");
            
            int enriched = 0;
            foreach (var track in tracks)
            {
                if (cancellationToken.IsCancellationRequested) break;

                try
                {
                    // Convert Track to PlaylistTrack for enrichment
                    var playlistTrack = new PlaylistTrack
                    {
                        Artist = track.Artist ?? string.Empty,
                        Title = track.Title ?? string.Empty,
                        Album = track.Album ?? string.Empty
                    };
                    
                    if (await _metadataService!.EnrichTrackAsync(playlistTrack))
                    {
                        // Copy metadata back to Track
                        track.SpotifyTrackId = playlistTrack.SpotifyTrackId;
                        track.SpotifyAlbumId = playlistTrack.SpotifyAlbumId;
                        track.SpotifyArtistId = playlistTrack.SpotifyArtistId;
                        track.AlbumArtUrl = playlistTrack.AlbumArtUrl;
                        track.ArtistImageUrl = playlistTrack.ArtistImageUrl;
                        track.Genres = playlistTrack.Genres;
                        track.Popularity = playlistTrack.Popularity;
                        track.CanonicalDuration = playlistTrack.CanonicalDuration;
                        track.ReleaseDate = playlistTrack.ReleaseDate;
                        track.BPM = playlistTrack.BPM;
                        track.MusicalKey = playlistTrack.MusicalKey;
                        track.Energy = playlistTrack.Energy;
                        track.Danceability = playlistTrack.Danceability;
                        track.Valence = playlistTrack.Valence;
                        
                        enriched++;
                        
                        // Update UI on main thread
                        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                        {
                            StatusMessage = $"Enriched {enriched}/{tracks.Count} tracks";
                        });
                    }
                    
                    // Increased delay to respect rate limits (250ms = ~4 req/sec max)
                    await Task.Delay(250, cancellationToken);
                }
                catch (TaskCanceledException) { throw; }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to enrich track: {Artist} - {Title}", track.Artist, track.Title);
                }
            }
            
            if (!cancellationToken.IsCancellationRequested)
            {
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                {
                    StatusMessage = $"Ready - {enriched}/{tracks.Count} tracks enriched";
                });
                
                _logger.LogInformation("Background enrichment complete: {Enriched}/{Total} tracks", enriched, tracks.Count);
            }
        }
        catch (TaskCanceledException)
        {
            _logger.LogInformation("Background metadata enrichment was cancelled");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Background metadata enrichment failed");
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                StatusMessage = "Metadata enrichment failed";
            });
        }
    }

    /// <summary>
    /// Group tracks by album for display in grid
    /// </summary>
    private void GroupByAlbum()
    {
        AlbumGroups.Clear();

        var groupedByAlbum = ImportedTracks
            .GroupBy(t => t.Model.Album ?? "[Unknown Album]")
            .OrderBy(g => g.Key)
            .ToList();

        foreach (var group in groupedByAlbum)
        {
            var albumGroup = new AlbumGroupViewModel
            {
                Album = group.Key,
                Tracks = new ObservableCollection<SelectableTrack>(group.ToList())
            };
            AlbumGroups.Add(albumGroup);
        }
    }

    /// <summary>
    /// Add selected tracks to library as a new PlaylistJob
    /// </summary>
    private async Task AddToLibraryAsync()
    {
        var selectedTracks = ImportedTracks.Where(t => t.IsSelected).Select(t => t.Model).ToList();

        if (!selectedTracks.Any())
        {
            StatusMessage = "No tracks selected";
            return;
        }

        IsLoading = true;
        StatusMessage = "Adding to library...";

        // Create PlaylistJob to group all tracks
        var job = new PlaylistJob
        {
            Id = _targetJobId, // Use the deterministic or existing ID
            SourceTitle = SourceTitle,
            SourceType = SourceType,
            CreatedAt = _existingJob?.CreatedAt ?? DateTime.UtcNow, // Keep original date if merging
            DestinationFolder = _existingJob?.DestinationFolder ?? "", // Keep original folder
            SourceUrl = _sourceUrl
        };

        try
        {
            await Task.Delay(100); // Simulate async work
            _logger.LogInformation(
                "Adding {Count} tracks to library. JobId: {JobId}, Thread: {ThreadId}",
                selectedTracks.Count,
                job.Id,
                Thread.CurrentThread.ManagedThreadId);

            // Convert tracks to PlaylistTracks
            foreach (var track in selectedTracks)
            {
                // Note: PlaylistJob.OriginalTracks is ObservableCollection<Track>, not PlaylistTrack
                job.OriginalTracks.Add(track);
            }

            _logger.LogInformation("[IMPORT TRACE] Step 1: Calling HandlePlaylistJobAddedAsync for job {JobId}", job.Id);
        
            // Queue the project to DownloadManager and navigate to Library
            await HandlePlaylistJobAddedAsync(job);
            
            _logger.LogInformation("[IMPORT TRACE] Step 2: HandlePlaylistJobAddedAsync completed, firing AddedToLibrary event");
            
            // Notify that tracks have been added
            _logger.LogInformation("Firing AddedToLibrary event for job {JobId}", job.Id);
            AddedToLibrary?.Invoke(this, job);

            _logger.LogInformation(
                "Successfully added {Count} tracks to library. JobId: {JobId}, Thread: {ThreadId}",
                selectedTracks.Count,
                job.Id,
                Thread.CurrentThread.ManagedThreadId);
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error: {ex.Message}";
            _logger.LogError(ex, "Failed to add tracks to library for JobId: {JobId}", job.Id);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void SelectAll()
    {
        foreach (var track in ImportedTracks)
        {
            track.IsSelected = true;
        }
        UpdateSelectedCount();
    }

    private void DeselectAll()
    {
        foreach (var track in ImportedTracks)
        {
            track.IsSelected = false;
        }
        UpdateSelectedCount();
    }

    private void SelectMissing()
    {
        foreach (var track in ImportedTracks)
        {
            track.IsSelected = !track.IsInLibrary;
        }
        UpdateSelectedCount();
    }

    private void Cancel()
    {
        _logger.LogInformation("Import preview cancelled");
        StatusMessage = "Preview cancelled";
        Cancelled?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateSelectedCount()
    {
        var newCount = ImportedTracks.Count(t => t.IsSelected);
        if (SelectedCount != newCount)
        {
            SelectedCount = newCount;
            OnPropertyChanged(nameof(CanAddToLibrary));
        }
    }

    /// <summary>
    /// Handles the logic when a playlist job is confirmed and added from the preview.
    /// This was moved from MainViewModel to make this component more self-contained.
    /// </summary>
    public async Task HandlePlaylistJobAddedAsync(PlaylistJob job)
    {
        try
        {
            _logger.LogInformation("[IMPORT TRACE] HandlePlaylistJobAddedAsync ENTRY: {Title} with {Count} tracks",
                job.SourceTitle, job.OriginalTracks.Count);

            _logger.LogInformation("[IMPORT TRACE] Calling DownloadManager.QueueProject for job {JobId}", job.Id);
        
            // Queue project through DownloadManager to persist and add to Library.
            await _downloadManager.QueueProject(job);
            
            _logger.LogInformation("QueueProject completed for {JobId}. Job saved to database.", job.Id);

            // Small delay to ensure ProjectAdded event handler completes
            // This allows LibraryViewModel.OnProjectAdded to finish adding to AllProjects
            await Task.Delay(200);
            
            _logger.LogInformation("Navigating to Library page to show job {JobId}...", job.Id);

            // _navigationService.NavigateTo("Library"); // Handled by ImportOrchestrator via AddedToLibrary event

            _logger.LogInformation("Navigation to Library completed.");
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error adding to library: {ex.Message}";
            _logger.LogError(ex, "Failed to handle PlaylistJob addition");
        }
    }

    /// <summary>
    /// Handles the logic when the import is cancelled.
    /// This was moved from MainViewModel.
    /// </summary>
    public void HandleCancellation()
    {
        // Navigate back to the search page on cancellation.
        _navigationService.NavigateTo("Search");
        _logger.LogInformation("Import cancelled, navigating back to Search page.");
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

public class AlbumGroupViewModel
{
    public string Album { get; set; } = "[Unknown]";
    public ObservableCollection<SelectableTrack> Tracks { get; set; } = new();
}
