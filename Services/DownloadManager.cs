using System;
using SLSKDONET.Views;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json; // Phase 2A: Checkpoint serialization
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Serilog.Context;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.InputParsers;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using SLSKDONET.Data.Essentia;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Repositories; // [NEW] Namespace
using SLSKDONET.Services.IO; // Added explicit using
using SLSKDONET.Events;


namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates the download process for projects and individual tracks.
/// "The Conductor" - manages the state machine and queue.
/// Delegates search to DownloadDiscoveryService and enrichment to MetadataEnrichmentOrchestrator.
/// </summary>
public class DownloadManager : INotifyPropertyChanged, IDisposable
{
    private readonly ILogger<DownloadManager> _logger;
    private readonly AppConfig _config;
    private readonly ConfigManager _configManager; // Persistence
    private readonly ISoulseekAdapter _soulseek;
    private readonly FileNameFormatter _fileNameFormatter;
    // Removed ITaggerService dependency (moved to Enricher)
    private readonly DatabaseService _databaseService;
    // Removed ISpotifyMetadataService dependency (moved to Enricher)
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    
    // NEW Services
    private readonly DownloadDiscoveryService _discoveryService;
    private readonly PathProviderService _pathProvider;
    private readonly IFileWriteService _fileWriteService; // Phase 1A
    private readonly CrashRecoveryJournal _crashJournal;
    private readonly PeerReliabilityService _peerReliability;
    private readonly INetworkHealthService _networkHealth;


    // Phase 2: Parallel Pre-Search Cache
    private readonly ConcurrentDictionary<string, Task<DownloadDiscoveryService.DiscoveryResult>> _preSearchTasks = new();
    private readonly ConcurrentDictionary<string, long> _lastBytesByUsername = new(StringComparer.OrdinalIgnoreCase);

    // Opt-P1: O(1) progress lookup — keyed by Soulseek username, updated on transfer start/end.
    // Eliminates the O(N) _downloads.FirstOrDefault scan inside OnDownloadProgressChanged
    // which was holding _collectionLock on every incoming data packet.
    private readonly ConcurrentDictionary<string, DownloadContext> _activeByUsername = new(StringComparer.OrdinalIgnoreCase);

    // Opt-P3: Thread-safe RNG for retry jitter (avoids Random allocation per call)
    private static readonly Random _jitterRandom = new();

    // Phase 2.5: Concurrency control with SemaphoreSlim throttling
    private readonly CancellationTokenSource _globalCts = new();
    private readonly SemaphoreSlim _downloadSemaphore; // Initialized in optimization
    private readonly SemaphoreSlim _searchSemaphore;
    private const int DEFAULT_DOWNLOAD_LANES = 3;
    private const int DEFAULT_SEARCH_LANES = 5;
    private int _currentSearchLanes = DEFAULT_SEARCH_LANES;
    private Task? _laneAutotuneTask;
    private Task? _processingTask;
    private readonly object _processingLock = new();
    public bool IsRunning => _processingTask != null && !_processingTask.IsCompleted;

    private bool _isPaused;
    public bool IsPaused
    {
        get => _isPaused;
        private set
        {
            if (_isPaused != value)
            {
                _isPaused = value;
                OnPropertyChanged();
            }
        }
    }

    private bool _isHydrated;
    public bool IsHydrated => _isHydrated;

    // STATE MACHINE:
    // WHY: Downloads are long-running stateful operations that can fail mid-flight
    // - App crash: resume from last checkpoint (CrashRecoveryJournal)
    // - Network drop: retry with exponential backoff
    // - User cancel: clean abort without orphaned files
    // 
    // DownloadContext tracks:
    // - Model: Track metadata (artist, title, preferences)
    // - State: Current phase (Queued -> Searching -> Downloading -> Complete)
    // - Progress: Bytes transferred (for UI progress bar)
    // Global State managed via Events
    private readonly List<DownloadContext> _downloads = new();
    private readonly object _collectionLock = new object();
    
    private const int LAZY_QUEUE_BUFFER_SIZE = 5000;
    private const int REFILL_THRESHOLD = 50;

    // Expose read-only copy for internal checks
    public IReadOnlyList<DownloadContext> ActiveDownloads 
    {
        get { lock(_collectionLock) { return _downloads.ToList(); } }
    }
    
    // Expose download directory from config
    public string? DownloadDirectory => _config.DownloadDirectory;

    public int ActiveWorkerSlots => _maxActiveDownloads - _downloadSemaphore.CurrentCount;
    public int TotalWorkerSlots => _maxActiveDownloads;
    public bool SoulseekConnected => _soulseek.IsLoggedIn;
    public bool IsBackingOff => CurrentBackoffSeconds > 0;
    public int CurrentBackoffSeconds { get; private set; }

    public DownloadManager(
        ILogger<DownloadManager> logger,
        AppConfig config,
        ConfigManager configManager, // Injected
        ISoulseekAdapter soulseek,
        FileNameFormatter fileNameFormatter,
        DatabaseService databaseService,
        ILibraryService libraryService,
        IEventBus eventBus,
        DownloadDiscoveryService discoveryService,
        PathProviderService pathProvider,
        IFileWriteService fileWriteService,
        CrashRecoveryJournal crashJournal,
        PeerReliabilityService peerReliability,
        INetworkHealthService networkHealth) // Phase 1: Engine Overhaul

    {
        _logger = logger;
        _config = config;
        _configManager = configManager;
        _soulseek = soulseek;
        _fileNameFormatter = fileNameFormatter;
        _databaseService = databaseService;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _discoveryService = discoveryService;
        _pathProvider = pathProvider;
        _fileWriteService = fileWriteService;
        _crashJournal = crashJournal; 
        _peerReliability = peerReliability;
        _networkHealth = networkHealth;


        // CONCURRENCY CONTROL ARCHITECTURE:
        // WHY: SemaphoreSlim instead of Task.WhenAll() or Parallel.ForEach():
        // 
        // Problem: P2P nodes have limited upload slots (typically 2-10 per user)
        // - If we launch 50 downloads at once, 45 will queue/timeout
        // - Network saturation kills ALL downloads (competing for bandwidth)
        // - No graceful cancellation - Task.WhenAll() is all-or-nothing
        // 
        // Solution: SemaphoreSlim throttling
        // - Limits concurrent downloads to user-defined value (default: 3)
        // - Queued downloads wait their turn (no timeout waste)
        // - Dynamic adjustment: user can change MaxActiveDownloads at runtime
        // - Hard cap at 50: prevents DOS if user enters 99999
        // 
        // Real-world impact:
        // - 20 concurrent = 60% success rate, ~1.5MB/s (contention overhead)
        
        // Golden Rule: Hard cap at 50 concurrent downloads
        int configLimit = _config.MaxConcurrentDownloads > 0 ? _config.MaxConcurrentDownloads : DEFAULT_DOWNLOAD_LANES;
        int initialLimit = Math.Min(50, configLimit); 
        int initialSearchLanes = _config.MaxConcurrentSearches > 0 ? _config.MaxConcurrentSearches : DEFAULT_SEARCH_LANES;
        initialSearchLanes = Math.Clamp(initialSearchLanes, _config.MinAdaptiveSearchLanes, _config.MaxAdaptiveSearchLanes);
        
        _maxActiveDownloads = initialLimit; 
        _downloadSemaphore = new SemaphoreSlim(initialLimit, 50); // Hard cap at 50 to prevent DOS
        _searchSemaphore = new SemaphoreSlim(initialSearchLanes, _config.MaxAdaptiveSearchLanes);
        _currentSearchLanes = initialSearchLanes;
        
        // Phase 8: Automation Subscriptions
        _eventBus.GetEvent<AutoDownloadTrackEvent>().Subscribe(OnAutoDownloadTrack);
        _eventBus.GetEvent<AutoDownloadUpgradeEvent>().Subscribe(OnAutoDownloadUpgrade);
        _eventBus.GetEvent<UpgradeAvailableEvent>().Subscribe(OnUpgradeAvailable);
        // Phase 6: Library Interactions
        _eventBus.GetEvent<DownloadAlbumRequestEvent>().Subscribe(OnDownloadAlbumRequest);
        _eventBus.GetEvent<ForceStartRequestEvent>().Subscribe(e => _ = ForceStartTrack(e.TrackGlobalId));

        // Phase 12: Adapter Event Subscriptions
        _soulseek.DownloadProgressChanged += OnDownloadProgressChanged;
        _soulseek.DownloadCompleted += OnDownloadCompleted;
    }

    /// <summary>
    /// Returns a snapshot of all current downloads for ViewModel hydration.
    /// </summary>
    public IReadOnlyList<(PlaylistTrack Model, PlaylistTrackState State)> GetAllDownloads()
    {
        lock (_collectionLock)
        {
            return _downloads.Select(ctx => (ctx.Model, ctx.State)).ToList();
        }
    }

    /// <summary>
    /// Handles requests to download an entire album (Project or AlbumNode).
    /// </summary>
    private void OnDownloadAlbumRequest(DownloadAlbumRequestEvent e)
    {
        try
        {
            if (e.Album is PlaylistJob job)
            {
                _logger.LogInformation("ðŸ“¢ Processing DownloadAlbumRequest for Project: {Title}", job.SourceTitle);
                
                // Ensure tracks are loaded
                 _ = Task.Run(async () => 
                 {
                     _logger.LogInformation("ðŸ” Loading tracks for project {Id}...", job.Id);
                     var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
                     
                     if (tracks.Any())
                     {
                         _logger.LogInformation("âœ… Found {Count} tracks, queuing...", tracks.Count);
                         QueueTracks(tracks);
                         _logger.LogInformation("ðŸš€ Queued {Count} tracks for project {Title}", tracks.Count, job.SourceTitle);
                     }
                     else
                     {
                         _logger.LogWarning("âš ï¸ No tracks found for project {Title} (ID: {Id}) - Database might be empty or tracks missing", job.SourceTitle, job.Id);
                     }
                 });
            }
            else if (e.Album is ViewModels.Library.AlbumNode node)
            {
                _logger.LogInformation("Processing DownloadAlbumRequest for AlbumNode: {Title}", node.AlbumTitle);
                var tracks = node.Tracks.Select(vm => vm.Model).ToList();
                if (tracks.Any())
                {
                    QueueTracks(tracks);
                    _logger.LogInformation("Queued {Count} tracks from AlbumNode {Title}", tracks.Count, node.AlbumTitle);
                }
            }
            else
            {
                _logger.LogWarning("Unknown payload type for DownloadAlbumRequestEvent: {Type}", e.Album?.GetType().Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle DownloadAlbumRequestEvent");
        }
    }

    /// <summary>
    /// Gets the number of actively downloading or queued tracks for a specific project.
    /// Used for real-time UI updates in the library sidebar.
    /// </summary>
    public int GetActiveDownloadsCountForProject(Guid projectId)
    {
        lock (_collectionLock)
        {
            return _downloads.Count(d => d.Model.PlaylistId == projectId && d.IsActive);
        }
    }

    /// <summary>
    /// Gets the name of the track currently being downloaded for a project.
    /// </summary>
    public string? GetCurrentlyDownloadingTrackName(Guid projectId)
    {
        lock (_collectionLock)
        {
            var active = _downloads.FirstOrDefault(d => 
                d.Model.PlaylistId == projectId && 
                d.State == PlaylistTrackState.Downloading);
            
            return active != null ? $"{active.Model.Artist} - {active.Model.Title}" : null;
        }
    }

    /// <summary>
    /// Checks if a track is already in the library or download queue.
    /// </summary>
    public bool IsTrackAlreadyQueued(string? spotifyTrackId, string artist, string title)
    {
        lock (_collectionLock)
        {
            if (!string.IsNullOrEmpty(spotifyTrackId))
            {
                if (_downloads.Any(d => d.Model.SpotifyTrackId == spotifyTrackId))
                    return true;
            }

            return _downloads.Any(d => 
                string.Equals(d.Model.Artist, artist, StringComparison.OrdinalIgnoreCase) && 
                string.Equals(d.Model.Title, title, StringComparison.OrdinalIgnoreCase));
        }
    }

    private int _maxActiveDownloads;
    public int MaxActiveDownloads 
    {
        get => _maxActiveDownloads;
        set
        {
            if (_maxActiveDownloads == value || value < 1 || value > 50) return;
            
            int diff = value - _maxActiveDownloads;
            _maxActiveDownloads = value;
            
            // Persist
            if (_config.MaxConcurrentDownloads != value)
            {
                _config.MaxConcurrentDownloads = value;
                _ = _configManager.SaveAsync(_config); // Fire and forget save
            }
            
            // Adjust semaphore count dynamically
            if (diff > 0)
            {
                try 
                {
                    _downloadSemaphore.Release(diff);
                    _logger.LogInformation("ðŸš€ Increased concurrent download limit to {Count}", value);
                }
                catch (SemaphoreFullException) 
                {
                     // Should not happen with max 50, but fail safe
                     _logger.LogWarning("Failed to increase concurrency limit - semaphore full");
                }
            }
            else
            {
                // Decrease limit: Acquire slots asynchronously to throttle future downloads
                // We don't cancel running downloads, just prevent new ones until count drops
                int reduceBy = Math.Abs(diff);
                _logger.LogInformation("ðŸ›‘ Decreasing concurrent download limit to {Count} (throttling {Reduce} slots)", value, reduceBy);
                
                Task.Run(async () => 
                {
                    for(int i=0; i < reduceBy; i++)
                    {
                        await _downloadSemaphore.WaitAsync();
                    }
                });
            }
        }
    }
    
    public bool EnableMp3Fallback
    {
        get => _config.EnableMp3Fallback;
        set
        {
            if (_config.EnableMp3Fallback != value)
            {
                _config.EnableMp3Fallback = value;
                _ = _configManager.SaveAsync(_config);
                OnPropertyChanged();
            }
        }
    }
    
    public async Task InitAsync()

    {
        if (_isHydrated) return;
        try
        {
            await _databaseService.InitAsync();
            
            // Phase 3C.5: Lazy Hydration - Only load active/history and a buffer of pending tracks
            
            // 1. Load ONLY actually active tracks (Downloading, Searching, Stalled) 
            // This prevents the "Global Leak" where 10,000 history items are hydrated on boot
            var activeTracks = await _databaseService.GetActiveTracksAsync();
            HydrateAndAddEntities(activeTracks);
            
            _logger.LogInformation("Hydrated {Count} active tracks", activeTracks.Count);

            // PERFORMANCE FIX: Defer queue refilling until after startup
            // The ProcessQueueLoop will call RefillQueueAsync when needed, 
            // which is now project-aware to prevent mass-activation.
            // await RefillQueueAsync(); // Removed global refill on init


            // Notify observers that we are ready and hydrated
            _isHydrated = true;
            OnPropertyChanged(nameof(IsHydrated));
            _eventBus.Publish(new DownloadManagerHydratedEvent(_downloads.Count));
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to init persistence layer");
        }
    }


    // Track updates now published via IEventBus (TrackUpdatedEvent)

    /// <summary>
    /// Queues a project (a PlaylistJob) for processing and persists the job header and tracks.
    /// This is the preferred entry point for importing new multi-track projects.
    /// </summary>
    public async Task QueueProject(PlaylistJob job)
    {
        // Add correlation context for all logs related to this job
        using (LogContext.PushProperty("PlaylistJobId", job.Id))
        using (LogContext.PushProperty("JobName", job.SourceTitle))
        {
            // Robustness: If the job comes from an import preview, it will have OriginalTracks
            // but no PlaylistTracks. We must convert them before proceeding.
            if (job.PlaylistTracks.Count == 0 && job.OriginalTracks.Count > 0)
            {
                _logger.LogInformation("Gap analysis: Checking for existing tracks in Job {JobId} to avoid duplicates", job.Id);

                // Phase 7.1: Robust Deduplication + Merge Missing
                // Load persisted tracks for this playlist so we can:
                // - skip already-present healthy tracks
                // - reset/requeue failed or on-hold tracks
                // - add truly new tracks
                var existingByHash = new Dictionary<string, PlaylistTrack>(StringComparer.OrdinalIgnoreCase);
                try 
                {
                    var existingJob = await _libraryService.FindPlaylistJobAsync(job.Id);
                    if (existingJob != null)
                    {
                        var persistedTracks = await _libraryService.LoadPlaylistTracksAsync(existingJob.Id);
                        foreach (var t in persistedTracks)
                        {
                            if (!string.IsNullOrEmpty(t.TrackUniqueHash))
                                existingByHash[t.TrackUniqueHash] = t;
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load existing tracks for gap analysis, proceeding cautiously");
                }

                // Build a local library index once per import to avoid per-track DB misses and enable tolerant matching.
                var allLibraryEntries = await _libraryService.LoadAllLibraryEntriesAsync();
                var libraryByHash = allLibraryEntries
                    .Where(e => !string.IsNullOrWhiteSpace(e.UniqueHash))
                    .GroupBy(e => e.UniqueHash, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AddedAt).First(), StringComparer.OrdinalIgnoreCase);

                var libraryBySpotifyId = allLibraryEntries
                    .Where(e => !string.IsNullOrWhiteSpace(e.SpotifyTrackId))
                    .GroupBy(e => e.SpotifyTrackId!, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AddedAt).First(), StringComparer.OrdinalIgnoreCase);

                var libraryByLooseIdentity = allLibraryEntries
                    .Where(e => !string.IsNullOrWhiteSpace(e.Artist) && !string.IsNullOrWhiteSpace(e.Title))
                    .GroupBy(e => BuildLooseIdentityKey(e.Artist, e.Title), StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.OrderByDescending(x => x.AddedAt).First(), StringComparer.Ordinal);

                _logger.LogInformation("Converting {OriginalTrackCount} OriginalTracks to PlaylistTracks (Existing: {ExistingCount})",
                    job.OriginalTracks.Count, existingByHash.Count);

                var playlistTracks = new List<PlaylistTrack>();
                var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int idx = existingByHash.Count + 1;
                int retried = 0;
                int skipped = 0;
                int added = 0;
                int linkedFromLibrary = 0;
                int reconciledExisting = 0;

                foreach (var track in job.OriginalTracks)
                {
                    var hash = track.UniqueHash;
                    if (string.IsNullOrWhiteSpace(hash) || !seenInBatch.Add(hash))
                    {
                        _logger.LogDebug("Skipping track '{Title}' - invalid/duplicate hash in current batch", track.Title);
                        continue;
                    }

                    if (existingByHash.TryGetValue(hash, out var existingTrack))
                    {
                        if (existingTrack.Status != TrackStatus.Downloaded && libraryByHash.TryGetValue(hash, out var existingEntryByHash))
                        {
                            existingTrack.Status = TrackStatus.Downloaded;
                            existingTrack.ResolvedFilePath = string.IsNullOrWhiteSpace(existingTrack.ResolvedFilePath)
                                ? existingEntryByHash.FilePath
                                : existingTrack.ResolvedFilePath;
                            existingTrack.TrackUniqueHash = existingEntryByHash.UniqueHash;
                            existingTrack.CompletedAt ??= DateTime.UtcNow;
                            existingTrack.SourcePlaylistId = job.Id;
                            existingTrack.SourcePlaylistName = job.SourceTitle;

                            // Persist status reconciliation for this existing row.
                            playlistTracks.Add(existingTrack);
                            reconciledExisting++;
                            continue;
                        }

                        if (existingTrack.Status == TrackStatus.Failed || existingTrack.Status == TrackStatus.OnHold)
                        {
                            existingTrack.Artist = track.Artist ?? existingTrack.Artist;
                            existingTrack.Title = track.Title ?? existingTrack.Title;
                            existingTrack.Album = track.Album ?? existingTrack.Album;
                            existingTrack.AlbumArtUrl = track.AlbumArtUrl ?? existingTrack.AlbumArtUrl;
                            existingTrack.SpotifyTrackId = track.SpotifyTrackId ?? existingTrack.SpotifyTrackId;
                            existingTrack.SpotifyAlbumId = track.SpotifyAlbumId ?? existingTrack.SpotifyAlbumId;
                            existingTrack.SpotifyArtistId = track.SpotifyArtistId ?? existingTrack.SpotifyArtistId;
                            existingTrack.Genres = track.Genres ?? existingTrack.Genres;
                            existingTrack.Popularity = track.Popularity != 0 ? track.Popularity : existingTrack.Popularity;
                            existingTrack.CanonicalDuration = track.CanonicalDuration > 0 ? track.CanonicalDuration : existingTrack.CanonicalDuration;
                            existingTrack.ReleaseDate = track.ReleaseDate ?? existingTrack.ReleaseDate;
                            
                            // Phase 7.1 Fix: Ensure playlist context is updated for merged tracks
                            existingTrack.SourcePlaylistId = job.Id;
                            existingTrack.SourcePlaylistName = job.SourceTitle;
                            
                            existingTrack.Status = TrackStatus.Missing;
                            existingTrack.SearchRetryCount = 0;
                            existingTrack.NotFoundRestartCount = 0;

                            playlistTracks.Add(existingTrack);
                            retried++;
                        }
                        else
                        {
                            // Even if already downloaded, update the source metadata so it shows up in the right group if viewed
                            existingTrack.SourcePlaylistId = job.Id;
                            existingTrack.SourcePlaylistName = job.SourceTitle;
                            skipped++;
                        }

                        continue;
                    }

                    var existingLibraryEntry = ResolveExistingLibraryEntryForImport(
                        track,
                        hash,
                        libraryByHash,
                        libraryBySpotifyId,
                        libraryByLooseIdentity);
                    var shouldMarkDownloaded = existingLibraryEntry != null;

                    playlistTracks.Add(new PlaylistTrack
                    {
                        Id = Guid.NewGuid(),
                        PlaylistId = job.Id,
                        Artist = track.Artist ?? string.Empty,
                        Title = track.Title ?? string.Empty,
                        Album = track.Album ?? string.Empty,
                        TrackUniqueHash = existingLibraryEntry?.UniqueHash ?? hash,
                        Status = shouldMarkDownloaded ? TrackStatus.Downloaded : TrackStatus.Missing,
                        ResolvedFilePath = shouldMarkDownloaded ? (existingLibraryEntry?.FilePath ?? string.Empty) : string.Empty,
                        TrackNumber = idx++,
                        AddedAt = DateTime.UtcNow,
                        CompletedAt = shouldMarkDownloaded ? DateTime.UtcNow : null,
                        Priority = 10, // Default: Bulk lane. Express (0) = VIP only, Standard (1-9) = user bumps
                        // Map Metadata if available from import
                        SourcePlaylistId = job.Id,
                        SourcePlaylistName = job.SourceTitle,
                        SpotifyTrackId = track.SpotifyTrackId,
                        SpotifyAlbumId = track.SpotifyAlbumId,
                        SpotifyArtistId = track.SpotifyArtistId,
                        AlbumArtUrl = track.AlbumArtUrl,
                        ArtistImageUrl = track.ArtistImageUrl,
                        Genres = track.Genres,
                        Popularity = track.Popularity,
                        CanonicalDuration = track.CanonicalDuration,
                        ReleaseDate = track.ReleaseDate

                    });
                    added++;
                    if (shouldMarkDownloaded)
                    {
                        linkedFromLibrary++;
                    }
                }

                _logger.LogInformation(
                    "Merge result for job {JobId}: {Added} new ({LinkedFromLibrary} linked from library), {Retried} retried, {ReconciledExisting} reconciled existing to downloaded, {Skipped} unchanged existing",
                    job.Id,
                    added,
                    linkedFromLibrary,
                    retried,
                    reconciledExisting,
                    skipped);

                job.PlaylistTracks = playlistTracks;
                job.TotalTracks = existingByHash.Count + added;
            }

            _logger.LogInformation("Queueing project with {TrackCount} tracks", job.PlaylistTracks.Count);

            // 0. Set Album Art for the Job from the first track if available
            if (string.IsNullOrEmpty(job.AlbumArtUrl))
            {
                 job.AlbumArtUrl = job.OriginalTracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtUrl))?.AlbumArtUrl 
                                   ?? job.PlaylistTracks.FirstOrDefault(t => !string.IsNullOrEmpty(t.AlbumArtUrl))?.AlbumArtUrl;
            }

            // 1. Persist the job header and all associated tracks via LibraryService
            try
            {
                await _libraryService.SavePlaylistJobWithTracksAsync(job);
                _logger.LogInformation("Saved PlaylistJob to database with {TrackCount} tracks", job.PlaylistTracks.Count);
                await _databaseService.LogPlaylistJobDiagnostic(job.Id);


            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to persist PlaylistJob and its tracks");
                throw; // CRITICAL: Propagate error so caller (ImportPreview) knows it failed
            }

            // 3. Queue the tracks using the internal method
            QueueTracks(job.PlaylistTracks);
            
            // 4. Fire event for Library UI to refresh
            // Duplicate event removed: LibraryService already publishes ProjectAddedEvent
            // _eventBus.Publish(new ProjectAddedEvent(job.Id));
        }
    }

    private static LibraryEntry? ResolveExistingLibraryEntryForImport(
        Track track,
        string trackHash,
        IReadOnlyDictionary<string, LibraryEntry> libraryByHash,
        IReadOnlyDictionary<string, LibraryEntry> libraryBySpotifyId,
        IReadOnlyDictionary<string, LibraryEntry> libraryByLooseIdentity)
    {
        // Fast path: exact unique-hash lookup.
        if (!string.IsNullOrWhiteSpace(trackHash) && libraryByHash.TryGetValue(trackHash, out var byHash))
        {
            return byHash;
        }

        var artist = track.Artist ?? string.Empty;
        var title = track.Title ?? string.Empty;
        if (string.IsNullOrWhiteSpace(artist) || string.IsNullOrWhiteSpace(title))
        {
            return null;
        }

        // Fallback: tolerant metadata match for cases where import formatting changed hash shape.
        if (!string.IsNullOrWhiteSpace(track.SpotifyTrackId))
        {
            if (libraryBySpotifyId.TryGetValue(track.SpotifyTrackId, out var bySpotifyId))
            {
                return bySpotifyId;
            }
        }

        var looseKey = BuildLooseIdentityKey(artist, title);
        if (!string.IsNullOrWhiteSpace(looseKey) && libraryByLooseIdentity.TryGetValue(looseKey, out var byLooseIdentity))
        {
            return byLooseIdentity;
        }

        return null;
    }

    private static string BuildLooseIdentityKey(string artist, string title)
    {
        var normalizedArtist = NormalizeForLooseIdentity(artist);
        var normalizedTitle = NormalizeForLooseIdentity(title);

        if (string.IsNullOrWhiteSpace(normalizedArtist) || string.IsNullOrWhiteSpace(normalizedTitle))
        {
            return string.Empty;
        }

        return $"{normalizedArtist}|{normalizedTitle}";
    }

    private static string NormalizeForLooseIdentity(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;

        var chars = value.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray();
        return new string(chars);
    }

    /// <summary>
    /// Internal method to queue a list of individual tracks for processing (e.g. from an existing project or ad-hoc).
    /// </summary>
    public void QueueTracks(List<PlaylistTrack> tracks)
    {
        _logger.LogInformation("Queueing project tracks with {Count} tracks", tracks.Count);
        
        int skipped = 0;
        int queued = 0;
        
        // O(N) Optimization: Create lookup maps OUTSIDE the lock to prevent UI freeze on large imports
        var currentDownloads = ActiveDownloads; 
        
        var existingMap = currentDownloads
            .Where(d => !string.IsNullOrEmpty(d.Model.TrackUniqueHash))
            .GroupBy(d => d.Model.TrackUniqueHash)
            .ToDictionary(g => g.Key!, g => g.First());
        
        var existingIds = currentDownloads
            .GroupBy(d => d.Model.Id)
            .ToDictionary(g => g.Key, g => g.First());

        lock (_collectionLock)
        {

            foreach (var track in tracks)
            {
                DownloadContext? existingCtx = null;
                
                if (existingIds.TryGetValue(track.Id, out var byId)) existingCtx = byId;
                else if (existingMap.TryGetValue(track.TrackUniqueHash, out var byHash)) existingCtx = byHash;

                if (existingCtx != null)
                {
                    // Fix: Smart Retry if in a terminal/failure state
                    if (existingCtx.State == PlaylistTrackState.Failed || 
                        existingCtx.State == PlaylistTrackState.Cancelled || 
                        existingCtx.State == PlaylistTrackState.Deferred ||
                        existingCtx.State == PlaylistTrackState.Pending)
                    {
                        _logger.LogInformation("Retrying existing track {Title} (State: {State}) - Bumping to Priority 10 (Standard)", track.Title, existingCtx.State);
                        
                        existingCtx.Model.Priority = 10;
                        _ = UpdateStateAsync(existingCtx, PlaylistTrackState.Pending);
                        
                        existingCtx.RetryCount = 0;
                        existingCtx.NextRetryTime = null;
                        existingCtx.FailureReason = null;
                        

                        queued++; 
                        continue;
                    }

                    skipped++;
                    _logger.LogDebug("Skipping track {Artist} - {Title}: already active/completed (State: {State})", 
                        track.Artist, track.Title, existingCtx.State);
                    continue;
                }
                
                if (_downloads.Count(d => d.State == PlaylistTrackState.Pending) >= LAZY_QUEUE_BUFFER_SIZE 
                    && track.Priority > 0)
                {
                     skipped++; 
                     continue; 
                }

                var ctx = new DownloadContext(track);
                _downloads.Add(ctx);
                
                existingMap[track.TrackUniqueHash] = ctx;
                existingIds[track.Id] = ctx;
                
                queued++;
                
                _eventBus.Publish(new TrackAddedEvent(track));
                _ = SyncDbAsync(ctx);
            }
        }
        
        if (skipped > 0)
        {
            _logger.LogInformation("Queued {Queued} new tracks, skipped {Skipped} already queued tracks", queued, skipped);
        }
        
        if (queued > 0)
        {
             // Phase 21: Targeted Refill - Only refill for projects that were just queued
             var projectIds = tracks.Select(t => t.PlaylistId).Distinct().ToList();
             foreach(var pid in projectIds)
             {
                 _ = RefillQueueAsync(pid);
             }
             
             // Auto-start engine if not running and we have tracks to process
             if (!IsRunning)
             {
                 _logger.LogInformation("Auto-starting download engine for {Count} queued tracks", queued);
                 _ = StartAsync();
             }
        }
    }

    private void HydrateAndAddEntities(List<PlaylistTrackEntity> entities)
    {
        lock (_collectionLock)
        {
            var batchBuffer = new List<(PlaylistTrack, PlaylistTrackState?)>();
            
            foreach (var t in entities)
            {
                // Map PlaylistTrackEntity -> PlaylistTrack Model
                var model = new PlaylistTrack 
                { 
                    Id = t.Id,
                    PlaylistId = t.PlaylistId,
                    Artist = t.Artist, 
                    Title = t.Title,
                    Album = t.Album,
                    TrackUniqueHash = t.TrackUniqueHash,
                    Status = t.Status,
                    ResolvedFilePath = t.ResolvedFilePath,
                    SpotifyTrackId = t.SpotifyTrackId,
                    AlbumArtUrl = t.AlbumArtUrl,
                    Format = t.Format,
                    Bitrate = t.Bitrate,
                    Priority = t.Priority,
                    AddedAt = t.AddedAt,
                    CompletedAt = t.CompletedAt,
                    IsClearedFromDownloadCenter = t.IsClearedFromDownloadCenter,
                    IsUserPaused = t.IsUserPaused,
                    SourcePlaylistId = t.SourcePlaylistId,
                    SourcePlaylistName = t.SourcePlaylistName,
                    SearchRetryCount = 0, // Reset in-session counter on start
                    NotFoundRestartCount = t.NotFoundRestartCount
                };
                
                // Map status to download state
                var ctx = new DownloadContext(model);
                
                // Phase 3 Hardening: Accurate State Restoration
                // 1. If user explicitly paused it, honor that state
                if (model.IsUserPaused)
                {
                    ctx.State = PlaylistTrackState.Paused;
                }
                else
                {
                    // 2. Otherwise map based on last known Status
                    ctx.State = t.Status switch
                    {
                        TrackStatus.Downloaded => PlaylistTrackState.Completed,
                        TrackStatus.Failed => PlaylistTrackState.Failed,
                        TrackStatus.Skipped => PlaylistTrackState.Cancelled,
                        TrackStatus.OnHold => IsMp3FallbackAllowed(model) ? PlaylistTrackState.Paused : PlaylistTrackState.Pending,
                        // If it was pending/downloading/searching, we make it Pending to restart
                        _ => PlaylistTrackState.Pending 
                    };
                }

                // Phase 21: Auto-escalate to OnHold if restart limit reached
                if (model.NotFoundRestartCount >= 3 && model.Status != TrackStatus.Downloaded && IsMp3FallbackAllowed(model))
                {
                    model.Status = TrackStatus.OnHold;
                    ctx.State = PlaylistTrackState.Paused;
                }

                _downloads.Add(ctx);
                
                // Issue #4: Batch track events instead of firing per-track to prevent UI freeze
                batchBuffer.Add((model, ctx.State));
            }
            
            // Issue #4: Publish all tracks in single batch event for efficient UI update
            if (batchBuffer.Count > 0)
            {
                _eventBus.Publish(new BatchTracksAddedEvent(batchBuffer.AsReadOnly()));
            }
        }
    }

    /// <summary>
    /// Phase 3C.5: "The Waiting Room" - Fetches pending tracks from DB if buffer is low.
    /// Manages memory pressure by ensuring we don't hydrate 50,000 pending tracks.
    /// Refactoring: Added optional projectId to support targeted project activation.
    /// </summary>
    private async Task RefillQueueAsync(Guid? projectId = null)
    {
        try
        {
            List<Guid> excludeIds;
            int needed;

            lock (_collectionLock)
            {
                // If we are refilling for a specific project, we check the buffer for that project specifically
                int pendingCount = projectId.HasValue 
                    ? _downloads.Count(d => d.Model.PlaylistId == projectId && d.State == PlaylistTrackState.Pending)
                    : _downloads.Count(d => d.State == PlaylistTrackState.Pending);
                
                if (pendingCount >= LAZY_QUEUE_BUFFER_SIZE) return; // Buffer full enough

                needed = LAZY_QUEUE_BUFFER_SIZE - pendingCount;
                excludeIds = _downloads.Select(d => d.Model.Id).ToList();
            }

            if (needed <= 0) return;

            // Fetch next batch from "Waiting Room" (DB)
            // If projectId is provided, we ONLY fetch for that project to prevent global mass-initialization
            var newTracks = projectId.HasValue
                ? await _databaseService.GetPendingTracksForProjectAsync(projectId.Value, needed, excludeIds)
                : await _databaseService.GetPendingPriorityTracksAsync(needed, excludeIds);
            
            if (newTracks.Any())
            {
                _logger.LogDebug("Refilling queue with {Count} tracks for {Scope}", 
                    newTracks.Count, projectId.HasValue ? $"Project {projectId}" : "Global Queue");
                HydrateAndAddEntities(newTracks);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refill queue from database");
        }
    }    
        // Processing loop picks this up automatically

    // Updated Delete to take GlobalId instead of VM
    public async Task DeleteTrackFromDiskAndHistoryAsync(string globalId)
    {
        DownloadContext? ctx;
        lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx == null) return;
        
        using (LogContext.PushProperty("TrackHash", globalId))
        {
            _logger.LogInformation("Deleting track from disk and history");

            // 1. Cancel active download
            ctx.CancellationTokenSource?.Cancel();

            // 2. Delete Physical Files
            DeleteLocalFiles(ctx.Model.ResolvedFilePath);

            // 3. Remove from Global History (DB)
            await _databaseService.RemoveTrackAsync(globalId);

            // 4. Update references in Playlists (DB)
            await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(globalId, TrackStatus.Missing, string.Empty);

            // 5. Remove from Memory
            lock (_collectionLock) _downloads.Remove(ctx);
            _eventBus.Publish(new TrackRemovedEvent(globalId));
        }
    }

    /// <summary>
    /// Phase 6: Reset a track for re-download. 
    /// Deletes local files and sets status back to Missing across all playlists.
    /// </summary>
    public async Task ResetTrackToPendingAsync(string globalId)
    {
        DownloadContext? ctx;
        lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        using (LogContext.PushProperty("TrackHash", globalId))
        {
            _logger.LogInformation("Resetting track for re-download: {Hash}", globalId);

            // 1. Cancel active download if any
            if (ctx != null)
            {
                ctx.CancellationTokenSource?.Cancel();
                lock (_collectionLock) _downloads.Remove(ctx);
            }

            // 2. Delete Physical Files from Library Entry
            var entry = await _libraryService.FindLibraryEntryAsync(globalId);
            if (entry != null && !string.IsNullOrEmpty(entry.FilePath))
            {
                DeleteLocalFiles(entry.FilePath);
                // Also remove from library index entirely
                await _libraryService.RemoveTrackFromLibraryAsync(globalId);
            }

            // 3. Reset references in Playlists to Missing
            await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(globalId, TrackStatus.Missing, string.Empty);
            
            // 4. Reset global track queue state
            var track = await _databaseService.FindTrackAsync(globalId);
            if (track != null)
            {
                track.State = "Pending";
                track.Filename = string.Empty;
                track.StalledReason = null;
                await _databaseService.SaveTrackAsync(track);
            }

            // 5. Notify UI
            _eventBus.Publish(new TrackStateChangedEvent(globalId, Guid.Empty, PlaylistTrackState.Pending, DownloadFailureReason.None, "Fraud Purge / Reset"));
            
            _logger.LogInformation("Track {Hash} reset to Pending state. Ready for Discovery.", globalId);
        }
    }
    
    private void DeleteLocalFiles(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                _logger.LogInformation("Deleted file: {Path}", path);
            }
            
            var partPath = path + ".part";
            if (File.Exists(partPath))
            {
                File.Delete(partPath);
                _logger.LogInformation("Deleted partial file: {Path}", partPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete file(s) for path {Path}", path);
        }
    }

    // Removed OnTrackPropertyChanged - Service no longer listens to VM property changes
    
    // Helper to update state and publish event (Overload: Structured Failure Reason)
    public async Task UpdateStateAsync(DownloadContext ctx, PlaylistTrackState newState, DownloadFailureReason failureReason)
    {
        // Store structured failure data
        ctx.FailureReason = failureReason;
        
        // Generate detailed message from enum + search attempts
        var displayMessage = failureReason.ToDisplayMessage();
        var suggestion = failureReason.ToActionableSuggestion();
        
        // Only append search diagnostics if the failure is search-related (i.e. we couldn't find a valid track)
        // If we passed the search phase and failed later (TransferFailed), irrelevant rejections shouldn't be shown.
        var isSearchFailure = failureReason == DownloadFailureReason.NoSearchResults ||
                              failureReason == DownloadFailureReason.AllResultsRejectedQuality ||
                              failureReason == DownloadFailureReason.AllResultsRejectedFormat ||
                              failureReason == DownloadFailureReason.AllResultsBlacklisted ||
                              failureReason == DownloadFailureReason.DiscoveryTimeout;

        // If we have search attempt logs, add the best rejection details
        if (isSearchFailure && ctx.SearchAttempts.Any())
        {
            var lastAttempt = ctx.SearchAttempts.Last();
            if (lastAttempt.Top3RejectedResults.Any())
            {
                var bestRejection = lastAttempt.Top3RejectedResults[0]; // Focus on #1
                displayMessage += $" ({bestRejection.ShortReason})";
            }
        }
        
        // Store detailed message for persistence
        ctx.DetailedFailureMessage = $"{displayMessage}. {suggestion}";
        
        // Call original method with generated error message
        await UpdateStateAsync(ctx, newState, ctx.DetailedFailureMessage);
    }
    
    // Helper to update state and publish event (Original: String-based)
    public async Task UpdateStateAsync(DownloadContext ctx, PlaylistTrackState newState, string? error = null)
    {
        if (ctx.State == newState && ctx.ErrorMessage == error) return;
        
        if (newState == PlaylistTrackState.Searching && ctx.SearchStartedAt == null)
        {
            ctx.SearchStartedAt = DateTime.UtcNow;
            ctx.Model.SearchStartedAt = ctx.SearchStartedAt;
        }

        if (newState == PlaylistTrackState.Searching && ctx.IsVip)
        {
            _logger.LogInformation("🚀 VIP Track bypassing worker slot: {Title} ({TrackId})", ctx.Model.Title, ctx.GlobalId);
        }

        ctx.State = newState;
        ctx.ErrorMessage = error; // Update context

        // Update model and timestamp for terminal states
        if (newState == PlaylistTrackState.Completed || newState == PlaylistTrackState.Failed || newState == PlaylistTrackState.Cancelled)
        {
            ctx.Model.CompletedAt = DateTime.UtcNow;

            if (newState == PlaylistTrackState.Completed)
            {
                _networkHealth.RecordTransferOutcome(null);
            }
            else if (newState == PlaylistTrackState.Cancelled)
            {
                _networkHealth.RecordTransferOutcome(DownloadFailureReason.TransferCancelled);
            }
            else
            {
                _networkHealth.RecordTransferOutcome(ctx.FailureReason ?? DownloadFailureReason.TransferFailed);
            }
        }
        
        // Publish with ProjectId for targeted updates
        // Phase 0.5: Include best search log for diagnostics
        var bestSearchLog = ctx.SearchAttempts.OrderByDescending(x => x.ResultsCount).FirstOrDefault();
        _eventBus.Publish(new TrackStateChangedEvent(ctx.GlobalId, ctx.Model.PlaylistId, newState, ctx.FailureReason ?? DownloadFailureReason.None, error, bestSearchLog, ctx.CurrentUsername));
        
        // DB Persistence (Consolidated)
        // Phase 3D: High-Efficiency Core - Master and Playlist updates now happen in a SINGLE transaction
        await SyncDbAsync(ctx);
        
        // Phase 6: VIP/Bypass Sync
        if (newState == PlaylistTrackState.Downloading && ctx.IsVip)
        {
             _logger.LogInformation("🚀 VIP Track Active: {Title}", ctx.Model.Title);
        }

        // Phase 6 Fix: Real-time population of "All Tracks" (LibraryEntry)
        if (newState == PlaylistTrackState.Completed && !string.IsNullOrEmpty(ctx.Model.ResolvedFilePath))
        {
            await _libraryService.AddTrackToLibraryIndexAsync(ctx.Model, ctx.Model.ResolvedFilePath);

            // Phase 6: Reciprocal Sharing Growth
            // As new downloads land in the library/download folders, refresh the outgoing share count
            // so Soulseek peers immediately see updated availability.
            try
            {
                await _soulseek.RefreshShareStateAsync();
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Share state refresh skipped/failed after completion for {Track}", ctx.Model.Title);
            }
        }
    }
    
    /// <summary>
    /// Consolidated DB Sync: Updates Master Record, Playlist Status, and Recalculates Job progress 
    /// in a single transaction. Reduces DB overhead by 50% per state change.
    /// </summary>
    private async Task SyncDbAsync(DownloadContext ctx)
    {
        try
        {
            var dbStatus = ctx.State switch
            {
                PlaylistTrackState.Completed => TrackStatus.Downloaded,
                PlaylistTrackState.Failed => TrackStatus.Failed,
                PlaylistTrackState.Cancelled => TrackStatus.Skipped,
                _ => ctx.Model.Status
            };

            var updatedJobIds = await _databaseService.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(
                ctx.GlobalId, 
                dbStatus, 
                ctx.Model.ResolvedFilePath,
                ctx.Model.SearchRetryCount,
                ctx.Model.NotFoundRestartCount,
                state: ctx.State.ToString(),
                error: ctx.ErrorMessage,
                completedAt: ctx.Model.CompletedAt,
                stalledReason: ctx.Model.StalledReason
            );

            // Notify UI to refresh Project Headers
            if (updatedJobIds.Any())
            {
                foreach (var jobId in updatedJobIds)
                {
                    _eventBus.Publish(new ProjectUpdatedEvent(jobId));
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync DB record for {Id}", ctx.GlobalId);
        }
    }



    /// <summary>
    /// Phase 2.5: Enhanced pause with immediate cancellation and IsUserPaused tracking.
    /// </summary>
    public void PromoteTrackToExpress(string globalId)
    {
        DownloadContext? ctx;
        lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx != null)
        {
            ctx.Model.Priority = 0;
            _logger.LogInformation("Creating VIP Pass for {Title} (Priority 0)", ctx.Model.Title);
            // In a real implementation, we would persist this to PlaylistTrackEntity here.
        }
    }

    public async Task PauseTrackAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        
        if (ctx == null)
        {
            _logger.LogWarning("Cannot pause track {Id}: not found", globalId);
            return;
        }
        
        // CRITICAL: Cancel the CancellationTokenSource immediately
        // This ensures the download stops mid-transfer and preserves the .part file
        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource(); // Reset for resume
        
        await UpdateStateAsync(ctx, PlaylistTrackState.Paused);
        
        // Mark as user-paused in DB so hydration knows not to auto-resume
        try
        {
            var job = await _libraryService.FindPlaylistJobAsync(ctx.Model.PlaylistId);
            if (job != null)
            {
                job.IsUserPaused = true;
                // Update via LibraryService (uses Save internally)
                await _libraryService.SavePlaylistJobAsync(job);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mark job as user-paused in DB (non-fatal)");
        }
        
        _logger.LogInformation("â¸ï¸ Paused track: {Artist} - {Title} (user-initiated)", ctx.Model.Artist, ctx.Model.Title);
    }

    /// <summary>
    /// Pauses all active downloads.
    /// </summary>
    public async Task PauseAllAsync() 
    {
        List<DownloadContext> active;
        lock (_collectionLock)
        {
             active = _downloads.Where(d => d.IsActive).ToList();
        }

        if (active.Any())
        {
            _logger.LogInformation("⏸ Pausing all {Count} active downloads...", active.Count);
            foreach(var d in active) 
            {
                 await PauseTrackAsync(d.GlobalId);
            }
        }
    }

    /// <summary>
    /// Resumes all paused downloads and ensures engine is running.
    /// </summary>
    public async Task ResumeAllAsync()
    {
        if (!IsRunning)
        {
            await StartAsync();
        }

        List<DownloadContext> paused;
        lock (_collectionLock)
        {
            paused = _downloads.Where(d => d.State == PlaylistTrackState.Paused).ToList();
        }

        if (paused.Any())
        {
            _logger.LogInformation("▶ Resuming all {Count} paused downloads...", paused.Count);
            foreach (var d in paused)
            {
                await ResumeTrackAsync(d.GlobalId);
            }
        }
        
        IsPaused = false; // Globally unpause if it was
    }

    public async Task StopAsync()
    {
        _logger.LogInformation("Stopping DownloadManager engine (soft stop)...");
        _ = PauseAllAsync(); // Fire and forget pause of active tracks
        _globalCts.Cancel();
        try
        {
            if (_processingTask != null)
                await _processingTask;
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while stopping processing loop");
        }
        
        _logger.LogInformation("DownloadManager engine stopped.");
        OnPropertyChanged(nameof(IsRunning));
    }

    public async Task TogglePauseEngineAsync()
    {
        IsPaused = !IsPaused;
        _logger.LogInformation("DownloadManager engine {Status}", IsPaused ? "PAUSED" : "RESUMED");
    }

    public async Task ResumeTrackAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        if (ctx != null)
        {
            _ = UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        }
    }


    public void CancelTrack(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        if (ctx == null) return;

        _logger.LogInformation("Cancelling track: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);

        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource(); // Reset
        
        _ = UpdateStateAsync(ctx, PlaylistTrackState.Cancelled);

        // Delete the final file + its .part sibling (handles the case where ResolvedFilePath is set)
        DeleteLocalFiles(ctx.Model.ResolvedFilePath);

        // Also delete the .part file at the download staging path, which is where the
        // download loop writes bytes BEFORE the atomic rename to the final path.
        // This path may be set even when ResolvedFilePath is still null (mid-download cancellation).
        try
        {
            var stagingPartPath = _pathProvider.GetTrackPath(
                ctx.Model.Artist,
                ctx.Model.Album ?? "Unknown",
                ctx.Model.Title,
                ctx.Model.Format ?? "mp3") + ".part";

            if (File.Exists(stagingPartPath))
            {
                File.Delete(stagingPartPath);
                _logger.LogInformation("Deleted staging .part file on cancel: {Path}", stagingPartPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete staging .part file for cancelled track {Title}", ctx.Model.Title);
        }
    }


    /// <summary>
    /// Phase 3B: Health Monitor Intervention
    /// Cancels a stalled download, blacklists the peer, and re-queues it for discovery.
    /// Non-destructive: Does NOT delete the .part file (optimistic resume if new peer has same file).
    /// </summary>
    public async Task AutoRetryStalledDownloadAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(t => t.GlobalId == globalId);
        
        if (ctx == null) return;

        var stalledUser = ctx.CurrentUsername;
        if (!string.IsNullOrEmpty(stalledUser))
        {
            ctx.BlacklistedUsers.Add(stalledUser);
            _logger.LogWarning("⚠️ Health Monitor: Blacklisting peer {User} for {Track}", stalledUser, ctx.Model.Title);
            
            // Notify UI
            _eventBus.Publish(new NotificationEvent(
                "Auto-Retry Triggered",
                $"Stalled download '{ctx.Model.Title}' switched from peer {stalledUser}",
                NotificationType.Warning));
        }

        // 1. Cancel active transfer (stops Soulseek)
        ctx.CancellationTokenSource?.Cancel();
        
        // 2. IMPORTANT: Don't delete files! We want to try to resume from another peer if possible.
        // Wait, Soulseek resume requires same file hash. DiscoveryService might find a different file hash.
        // If different file hash, Resume logic (based on file size match?) might be risky.
        // DownloadFileAsync logic checks .part file size.
        // If new file is different size, it might think it's truncated or ghost.
        // Safe bet: For now, we trust the resume logic to handle mismatches (it checks sizes).

        // 3. Reset state to Pending so ProcessQueueLoop picks it up
        await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Auto-retrying after stall");
        
        // Reset CTS for next attempt
        ctx.CancellationTokenSource = new CancellationTokenSource();
    }

    public async Task ForceStartTrack(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx == null) return;

        _logger.LogInformation("🚀 Force Start (VIP) triggered for {Title}", ctx.Model.Title);
        
        // 1. Mark as VIP and bump priority so it is selected first by queue order
        ctx.IsVip = true;
        ctx.Model.Priority = 0; // Bump to top of swimlanes
        
        // 2. Keep strict slot accounting: queue loop will start it when a worker slot is available.
        if (ctx.State != PlaylistTrackState.Downloading && ctx.State != PlaylistTrackState.Searching)
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        }
    }

    public async Task ForceDownloadIgnoreGuardsAsync(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx == null) return;

        _logger.LogInformation("🛡️ Bypassing Guards for {Title}. Force downloading ignoring quality filters.", ctx.Model.Title);
        
        ctx.Model.IgnoreSafetyGuards = true;
        ctx.IsVip = true; 
        ctx.Model.Priority = 0;
        
        await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        
        // Reset failures and logs to give it a fresh start
        ctx.ErrorMessage = null;
        ctx.SearchAttempts.Clear();

        // Keep strict slot accounting: queue loop starts this within configured concurrency.
    }

    public async Task ForceDownloadSpecificCandidateAsync(string globalId, string username, string filename, int? bitrate = null, string? format = null)
    {
        if (string.IsNullOrWhiteSpace(globalId) || string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(filename))
            return;

        DownloadContext? ctx;
        lock (_collectionLock)
        {
            ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        }

        if (ctx == null)
            return;

        _logger.LogInformation("🎛️ Manual force download for {Title}: @{User} -> {File}", ctx.Model.Title, username, filename);

        ctx.Model.IgnoreSafetyGuards = true;
        ctx.IsVip = true;
        ctx.Model.Priority = 0;
        ctx.ErrorMessage = null;
        ctx.SearchAttempts.Clear();

        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource?.Dispose();
        ctx.CancellationTokenSource = new CancellationTokenSource();

        if (!string.IsNullOrWhiteSpace(ctx.CurrentUsername))
        {
            _activeByUsername.TryRemove(ctx.CurrentUsername, out _);
            _lastBytesByUsername.TryRemove(ctx.CurrentUsername, out _);
        }

        var resolvedFormat = !string.IsNullOrWhiteSpace(format)
            ? format.Trim().TrimStart('.').ToLowerInvariant()
            : Path.GetExtension(filename)?.TrimStart('.').ToLowerInvariant();

        var candidate = new Track
        {
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            Album = ctx.Model.Album,
            Username = username,
            Filename = filename,
            Bitrate = bitrate ?? (ctx.Model.Bitrate ?? 0),
            Format = string.IsNullOrWhiteSpace(resolvedFormat) ? null : resolvedFormat,
            MatchReason = "🛠 Manual force from row details"
        };

        _eventBus.Publish(new Events.TrackDetailedStatusEvent(globalId,
            $"🛠 Manual force: starting direct download from {username}.",
            false,
            ctx.CorrelationId));

        await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Manual force candidate selected");

        // Keep strict slot accounting: queue loop starts this within configured concurrency.
        // Preserve manual candidate intent by recording it for the next discovery/download cycle.
        ctx.OverrideCandidate = candidate;
    }
    
    public async Task UpdateTrackFiltersAsync(string globalId, string formats, int minBitrate)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        
        if (ctx != null)
        {
            ctx.Model.PreferredFormats = formats;
            ctx.Model.MinBitrateOverride = minBitrate;
            
            // Persist to DB immediately
            await SyncDbAsync(ctx);
            
            // If it's a playlist track, update that entity too
            try 
            {
                using var context = new Data.AppDbContext();
                var pt = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == ctx.Model.Id);
                if (pt != null)
                {
                    pt.PreferredFormats = formats;
                    pt.MinBitrateOverride = minBitrate;
                    await context.SaveChangesAsync();
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to update PlaylistTrack filters in DB for {Id}", globalId);
            }
        }
    }
    
    public bool EnqueueTrack(Track track)
    {
        if (!TryValidateTrackForQueue(track, out var reason))
        {
            _logger.LogWarning("Skipping queue add for track {Artist} - {Title}: {Reason}", track.Artist, track.Title, reason);
            _eventBus.Publish(new GlobalStatusEvent($"Queue rejected: {reason}", IsActive: false, IsError: true));
            return false;
        }

        var playlistTrack = new PlaylistTrack
        {
             Id = Guid.NewGuid(),
             Artist = track.Artist ?? "Unknown",
             Title = track.Title ?? "Unknown",
             Album = track.Album ?? "Unknown",
             Status = TrackStatus.Missing,
             ResolvedFilePath = Path.Combine(_config.DownloadDirectory!, _fileNameFormatter.Format(_config.NameFormat ?? "{artist} - {title}", track) + "." + track.GetExtension()),
             TrackUniqueHash = track.UniqueHash
        };
        
        QueueTracks(new List<PlaylistTrack> { playlistTrack });
        return true;
    }

    private bool TryValidateTrackForQueue(Track track, out string reason, int? minBitrateOverride = null, bool allowFallbackFormat = false)
    {
        reason = string.Empty;

        var preferredFormats = _config.PreferredFormats?
            .Where(f => !string.IsNullOrWhiteSpace(f))
            .Select(f => f.Trim().ToLowerInvariant())
            .ToHashSet() ?? new HashSet<string>();

        var extension = (track.Format ?? track.GetExtension())?.Trim().TrimStart('.').ToLowerInvariant() ?? string.Empty;
        var allowMp3FormatFallback = allowFallbackFormat &&
                                     _config.EnableMp3Fallback &&
                                     string.Equals(extension, "mp3", StringComparison.OrdinalIgnoreCase);

        if (preferredFormats.Count > 0 && !string.IsNullOrEmpty(extension) && !preferredFormats.Contains(extension) && !allowMp3FormatFallback)
        {
            reason = $"format '{extension}' not allowed";
            return false;
        }

        var requiredMinBitrate = minBitrateOverride ?? _config.PreferredMinBitrate;
        if (requiredMinBitrate > 0 && track.Bitrate > 0 && track.Bitrate < requiredMinBitrate)
        {
            reason = $"bitrate {track.Bitrate}kbps below required {requiredMinBitrate}kbps";
            return false;
        }

        if (_config.PreferredMaxSampleRate > 0 && track.SampleRate.HasValue && track.SampleRate.Value > _config.PreferredMaxSampleRate)
        {
            reason = $"sample-rate {track.SampleRate.Value}Hz exceeds {_config.PreferredMaxSampleRate}Hz";
            return false;
        }

        return true;
    }

    private bool IsMp3FallbackAllowed(PlaylistTrack track)
    {
        if (_config.EnableMp3Fallback)
            return true;

        // Legacy profile compatibility path
        var formats = _config.PreferredFormats ?? new List<string>();

        return formats.Any(f => string.Equals(f?.Trim(), "mp3", StringComparison.OrdinalIgnoreCase));
    }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (IsRunning) return;

        _logger.LogInformation("DownloadManager Orchestrator started.");

        
        await InitAsync();

        // Phase 13: Non-blocking Journal Recovery
        // We run this in background to avoid blocking the UI/Splash Screen
        // while it reconciles potentially thousands of checks.
        // Run recovery in background
        _ = Task.Run(async () => 
        {
            try 
            {
                await HydrateFromCrashAsync();

                // Phase 2.5: Zombie Cleanup - Delete orphaned .part files older than 24 hours
                // Moved from InitAsync to prevent blocking Engine startup over slow directories
                var activePartPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                lock (_collectionLock)
                {
                    foreach (var ctx in _downloads.Where(t => t.State == PlaylistTrackState.Pending || t.State == PlaylistTrackState.Downloading))
                    {
                        var partPath = _pathProvider.GetTrackPath(ctx.Model.Artist, ctx.Model.Album ?? "Unknown", ctx.Model.Title, "mp3") + ".part";
                        activePartPaths.Add(partPath);
                    }
                }
                await _pathProvider.CleanupOrphanedPartFilesAsync(activePartPaths);

                // Startup Orphan Sweep: catch tracks the crash journal didn't handle
                // (e.g., tracks stuck in Searching/Downloading without journal entries)
                List<DownloadContext> orphans;
                lock (_collectionLock)
                {
                    orphans = _downloads.Where(d =>
                        d.State == PlaylistTrackState.Searching ||
                        d.State == PlaylistTrackState.Downloading ||
                        d.State == PlaylistTrackState.Stalled).ToList();
                }

                if (orphans.Any())
                {
                    _logger.LogWarning("🧹 Orphan Sweep: Found {Count} tracks stuck in active states from previous session.", orphans.Count);
                    foreach (var orphan in orphans)
                    {
                        await UpdateStateAsync(orphan, PlaylistTrackState.Failed,
                            DownloadFailureReason.Interrupted);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to recover journaled downloads or cleanup orphaned files");
            }
        }, ct);

        _processingTask = Task.Run(() => ProcessQueueLoop(_globalCts.Token), _globalCts.Token);
        if (_laneAutotuneTask == null || _laneAutotuneTask.IsCompleted)
        {
            _laneAutotuneTask = Task.Run(() => LaneAutotuneLoop(_globalCts.Token), _globalCts.Token);
        }
        OnPropertyChanged(nameof(IsRunning));

        await Task.CompletedTask;
    }

    private async Task LaneAutotuneLoop(CancellationToken token)
    {
        while (!token.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), token);

                if (!_config.EnableAdaptiveLanes)
                {
                    continue;
                }

                var snapshot = _peerReliability.GetGlobalSnapshot();
                if (snapshot.TotalStarts < 6)
                {
                    continue;
                }

                var targetDownloadLanes = MaxActiveDownloads;
                var targetSearchLanes = _currentSearchLanes;

                if (snapshot.CompletionRatio < 0.45 || snapshot.StallRatio > 0.45)
                {
                    targetSearchLanes = Math.Max(_config.MinAdaptiveSearchLanes, _currentSearchLanes - 1);
                }
                else if (snapshot.CompletionRatio > 0.80 && snapshot.StallRatio < 0.20)
                {
                    targetSearchLanes = Math.Min(_config.MaxAdaptiveSearchLanes, _currentSearchLanes + 1);
                }

                // STRICT CONCURRENCY: keep download worker slots pinned to user slider.
                _ = targetDownloadLanes;

                ResizeSearchLanes(targetSearchLanes);

                _logger.LogDebug(
                    "Adaptive lanes: completion={Completion:P0}, stalls={Stall:P0}, download={DownloadLanes}, search={SearchLanes}",
                    snapshot.CompletionRatio,
                    snapshot.StallRatio,
                    MaxActiveDownloads,
                    _currentSearchLanes);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Adaptive lane autotuner cycle failed");
            }
        }
    }

    private void ResizeSearchLanes(int target)
    {
        target = Math.Clamp(target, _config.MinAdaptiveSearchLanes, _config.MaxAdaptiveSearchLanes);
        if (target == _currentSearchLanes)
        {
            return;
        }

        var diff = target - _currentSearchLanes;
        _currentSearchLanes = target;

        if (diff > 0)
        {
            try
            {
                _searchSemaphore.Release(diff);
            }
            catch (SemaphoreFullException)
            {
                _logger.LogDebug("Search lane semaphore already at max while resizing to {Target}", target);
            }
            return;
        }

        var reduceBy = Math.Abs(diff);
        _ = Task.Run(async () =>
        {
            for (int i = 0; i < reduceBy; i++)
            {
                await _searchSemaphore.WaitAsync(_globalCts.Token);
            }
        }, _globalCts.Token);
    }

    /// <summary>
    /// Phase 13: Crash Recovery Journal Integration
    /// Reconciles state between SQLite WAL Journal and Disk.
    /// Handles:
    /// 1. Truncation Guard (fixing over-written .part files)
    /// 2. Ghost/Zombie Cleanup (removing stale checkpoints)
    /// 3. Priority Resumption (jumping queue for interrupted downloads)
    /// </summary>
    private async Task HydrateFromCrashAsync()
    {
        try
        {
            var pendingCheckpoints = await _crashJournal.GetPendingCheckpointsAsync();
            if (!pendingCheckpoints.Any())
            {
                _logger.LogDebug("Journal Check: Clean state (no pending checkpoints)");
                return;
            }

            _logger.LogInformation("Journal Check: Found {Count} pending download sessions", pendingCheckpoints.Count);

            int recovered = 0;
            int zombies = 0;

            // Phase 13 Optimization: "Batch Zombie Check"
            // Instead of querying DB one-by-one, we fetch all relevant tracks in one go.
            var uniqueHashList = pendingCheckpoints
                .Select(c => JsonSerializer.Deserialize<DownloadCheckpointState>(c.StateJson)?.TrackGlobalId)
                .Where(id => !string.IsNullOrEmpty(id))
                .Distinct()
                .ToList();

            var knownTracks = new HashSet<string>();
            try 
            {
                // Assuming DatabaseService has a method to check existence by list, or we add one.
                // For now, sticking to existing public surface area to avoid expanding scope too much
                // in this specific 'replace_file_content' operation.
                // If ID is in Hydrated downloads, we know it exists.
                lock (_collectionLock) 
                {
                    foreach(var d in _downloads) knownTracks.Add(d.GlobalId);
                }
            }
            catch (Exception ex)
            {
                 _logger.LogWarning(ex, "Failed to optimize zombie check");
            }

            foreach (var checkpoint in pendingCheckpoints)
            {
                if (checkpoint.OperationType != OperationType.Download) continue;

                DownloadCheckpointState? state = null;
                try 
                {
                    state = JsonSerializer.Deserialize<DownloadCheckpointState>(checkpoint.StateJson);
                }
                catch 
                {
                    _logger.LogWarning("Corrupt checkpoint state for {Id}, marking dead letter.", checkpoint.Id);
                    await _crashJournal.MarkAsDeadLetterAsync(checkpoint.Id);
                    continue;
                }

                if (state == null) continue;

                // 2. CORRELATE: Find the DownloadContext
                DownloadContext? ctx;
                lock(_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == state.TrackGlobalId);

                if (ctx == null)
                {
                    // Track exists in DB but not in memory.
                    // Zombie check: If file completely missing AND track gone from DB?
                    if (!File.Exists(state.PartFilePath) && !knownTracks.Contains(state.TrackGlobalId))
                    {
                        // Check DB individually if not in cache (fallback)
                        var dbTrack = await _databaseService.FindTrackAsync(state.TrackGlobalId);
                        if (dbTrack == null)
                        {
                            _logger.LogWarning("👻 Zombie Checkpoint: {Track} (File & Record missing). Cleaning up.", state.Title);
                            await _crashJournal.CompleteCheckpointAsync(checkpoint.Id); 
                            zombies++;
                            continue;
                        }
                    }
                    else
                    {
                         // Track likely exists but wasn't hydrated (Lazy buffer full?)
                         // We leave the checkpoint alone. The "RefillQueueAsync" will pick up the track later.
                         _logger.LogDebug("Deferred Recovery: {Track} valid but not in active memory.", state.Title);
                    }
                    continue;
                }

                // 3. TRUNCATION GUARD (The "Industrial" Fix)
                if (File.Exists(state.PartFilePath))
                {
                    var info = new FileInfo(state.PartFilePath);
                    if (info.Length > state.BytesDownloaded)
                    {
                        try 
                        {
                            _logger.LogWarning("⚠️ Truncation Guard: Truncating {Track} from {Disk} to {Journal} bytes.", 
                                state.Title, info.Length, state.BytesDownloaded);
                                
                            await using (var fs = new FileStream(state.PartFilePath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                            {
                                fs.SetLength(state.BytesDownloaded);
                                await fs.FlushAsync();
                            }
                        }
                        catch (IOException ioEx)
                        {
                             _logger.LogError("Locked file {Path} prevented truncation. Skipping recovery for this session. ({Msg})", state.PartFilePath, ioEx.Message);
                             continue; // Skip this track until next restart or manual retry
                        }
                        catch (Exception ex)
                        {
                             _logger.LogError(ex, "Failed to truncate file: {Path}", state.PartFilePath);
                        }
                    }
                }

                // 4. UPDATE MEMORY STATE
                ctx.BytesReceived = state.BytesDownloaded;
                ctx.TotalBytes = state.ExpectedSize;
                ctx.IsResuming = true;
                
                // 5. PRIORITIZE
                ctx.NextRetryTime = DateTime.MinValue;
                ctx.RetryCount = 0; 
                
                if (ctx.State == PlaylistTrackState.Downloading || ctx.State == PlaylistTrackState.Searching || ctx.State == PlaylistTrackState.Stalled)
                {
                    // If user manually paused it, honor that
                    if (ctx.Model.IsUserPaused)
                    {
                        ctx.State = PlaylistTrackState.Paused;
                    }
                    else
                    {
                        // Otherwise, reset to pending for retry
                        ctx.State = PlaylistTrackState.Pending;
                    }
                }
                
                // Also catch any tracks left in 'Paused' state that SHOULD be paused
                if (ctx.Model.IsUserPaused && ctx.State != PlaylistTrackState.Paused)
                {
                    ctx.State = PlaylistTrackState.Paused;
                }
                
                // If it's not paused, and was in a failed/cancelled state, reset to pending
                if (!ctx.Model.IsUserPaused && (ctx.State == PlaylistTrackState.Failed || ctx.State == PlaylistTrackState.Cancelled))
                {
                    ctx.State = PlaylistTrackState.Pending;
                }
                
                await UpdateStateAsync(ctx, ctx.State, "Recovered from Crash Journal");
                
                recovered++;
                _logger.LogInformation("✅ Recovered Session: {Artist} - {Title} ({Percent}%)", 
                    state.Artist, state.Title, (state.BytesDownloaded * 100.0 / Math.Max(1, state.ExpectedSize)).ToString("F0"));
            }

            // Clean up stale entries while we are here
            await _crashJournal.ClearStaleCheckpointsAsync();
            
            _logger.LogInformation("Recovery Summary: {Recovered} Resumed, {Zombies} Zombies squashed.", recovered, zombies);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Critical Error in RecoverJournaledDownloadsAsync");
        }
    }

    private async Task ProcessQueueLoop(CancellationToken token)
    {
        int disconnectBackoff = 0;

        while (!token.IsCancellationRequested)
        {
            try
            {
                // Global Engine Pause
                if (_isPaused)
                {
                    await Task.Delay(1000, token);
                    continue;
                }
                // Phase 0.9: Circuit Breaker
                if (!_soulseek.IsLoggedIn)
                {
                    if (disconnectBackoff == 0)
                    {
                        _logger.LogWarning("🔌 Circuit Breaker: Queue processing PAUSED due to disconnection.");
                        _eventBus.Publish(new GlobalStatusEvent("Disconnected: Waiting for Soulseek...", true, true));
                        
                        // Transition active downloads to waiting state
                        lock (_collectionLock)
                        {
                            var activeDownloading = _downloads.Where(d => d.State == PlaylistTrackState.Downloading).ToList();
                            foreach (var d in activeDownloading)
                            {
                                _ = UpdateStateAsync(d, PlaylistTrackState.WaitingForConnection);
                            }
                        }
                    }
                    
                    // Exponential Backoff: 2, 4, 8, 16... max 60s
                    int delaySeconds = Math.Min(60, (int)Math.Pow(2, Math.Min(6, disconnectBackoff + 1))); 
                    disconnectBackoff++;
                    CurrentBackoffSeconds = delaySeconds;
                    OnPropertyChanged(nameof(CurrentBackoffSeconds));
                    OnPropertyChanged(nameof(IsBackingOff));
                    
                    if (disconnectBackoff % 5 == 0) // Log occasionally
                    {
                        _logger.LogInformation("Circuit Breaker: Waiting for connection... (Next check in {Seconds}s)", delaySeconds);
                        _eventBus.Publish(new GlobalStatusEvent($"Disconnected: Retrying in {delaySeconds}s...", true, true));
                    }

                    await Task.Delay(delaySeconds * 1000, token);
                    continue;
                }

                // Reset backoff if connected
                if (disconnectBackoff > 0)
                {
                    _logger.LogInformation("✅ Circuit Breaker: Connection restored! Resuming queue processing.");
                    _eventBus.Publish(new GlobalStatusEvent("Connection Restored", false));
                    disconnectBackoff = 0;
                    CurrentBackoffSeconds = 0;
                    OnPropertyChanged(nameof(CurrentBackoffSeconds));
                    OnPropertyChanged(nameof(IsBackingOff));
                    
                    // Transition waiting downloads back to pending
                    lock (_collectionLock)
                    {
                        var waiting = _downloads.Where(d => d.State == PlaylistTrackState.WaitingForConnection).ToList();
                        foreach (var d in waiting)
                        {
                            _ = UpdateStateAsync(d, PlaylistTrackState.Pending);
                        }
                    }
                }

                DownloadContext? nextContext = null;
                lock (_collectionLock)
                {
                    // Phase 3D: High-Efficiency Core - Single Pass Selection
                    // Collect eligible tracks and current lane occupancy in one loop to minimize lock time
                    var eligibleTracks = new List<DownloadContext>();
                    var activeByPriority = new Dictionary<int, int>();

                    foreach (var ctx in _downloads)
                    {
                        if (ctx.State == PlaylistTrackState.Searching || ctx.State == PlaylistTrackState.Downloading)
                        {
                            activeByPriority[ctx.Model.Priority] = activeByPriority.GetValueOrDefault(ctx.Model.Priority) + 1;
                        }
                        else if (ctx.State == PlaylistTrackState.Pending && 
                            (!ctx.NextRetryTime.HasValue || ctx.NextRetryTime.Value <= DateTime.UtcNow))
                        {
                            eligibleTracks.Add(ctx);
                        }
                    }

                    if (eligibleTracks.Any())
                    {
                        // Priority 0 tracks should always be evaluated first
                        var sortedEligible = eligibleTracks.OrderBy(t => t.Model.Priority).ToList();
                        nextContext = SelectNextTrackWithLaneAllocation(sortedEligible, activeByPriority);
                    }
                    
                    // Phase 3C.5: Check if we need to release the hounds (Refill)
                    var pendingCount = _downloads.Count(d => d.State == PlaylistTrackState.Pending);
                    if (pendingCount < REFILL_THRESHOLD)
                    {
                         // Trigger background refill
                         _ = Task.Run(() => RefillQueueAsync());
                    }

                    // Pre-search is intentionally disabled in strict-concurrency mode.
                }

                if (nextContext == null)
                {
                    // No tracks ready or all lanes saturated (Except VIPs which are handled proactively)
                    await Task.Delay(500, token);
                    continue;
                }

                _logger.LogDebug("Spinning up search/download for {Title} (Priority: {Prio}, VIP: {Vip})", 
                    nextContext.Model.Title, nextContext.Model.Priority, nextContext.IsVip);

                bool tookSemaphoreSlot = false;
                // STRICT CONCURRENCY: every track lifecycle (search -> match -> download) consumes exactly one worker slot.
                // No VIP bypass here; slider value is authoritative for "no more, no less" active tracks.
                if (!_downloadSemaphore.Wait(0))
                {
                    await Task.Delay(200, token);
                    continue;
                }
                tookSemaphoreSlot = true;

                OnPropertyChanged(nameof(ActiveWorkerSlots));

                // Phase 3C Hardening: Race Condition Check
                // After waiting, the world may have changed (e.g., lane filled by stealth/high prio).
                // We MUST re-confirm this track is still the best choice and valid.
                DownloadContext? confirmedContext = null;
                lock (_collectionLock)
                {
                    // Update Active map with new reality
                    var activeByPriority = GetActiveDownloadsByPriority();
                    
                    // Check if our pre-selected 'nextContext' is still valid and optimal
                    // Or simply re-run selection to be safe
                    var eligibleTracks = _downloads.Where(t => 
                        t.State == PlaylistTrackState.Pending && 
                        (!t.NextRetryTime.HasValue || t.NextRetryTime.Value <= DateTime.UtcNow))
                        .ToList();

                    confirmedContext = SelectNextTrackWithLaneAllocation(eligibleTracks, activeByPriority);
                }

                if (confirmedContext == null)
                {
                    // False alarm or lane filled up while waiting
                    _logger.LogDebug("Race Condition: Slot acquired but no eligible track found after wait. Releasing.");
                    if (tookSemaphoreSlot) _downloadSemaphore.Release();
                    await Task.Delay(100, token); // Backoff
                    continue;
                }

                // If we switched tracks (e.g. a higher priority one came in), use the new one.
                // If confirmedContext matches nextContext, great. If not, confirmedContext is better.
                nextContext = confirmedContext;

                // Transition state via update method
                await UpdateStateAsync(nextContext, PlaylistTrackState.Searching);

                // Fire-and-forget pattern with guaranteed semaphore release
                bool finalTookSlot = tookSemaphoreSlot; // Capture for lambda
                _ = Task.Run(async () =>
                {
                    try
                    {
                        await ProcessTrackAsync(nextContext, token);
                    }
                    finally
                    {
                        // ALWAYS release the semaphore, but ONLY if we actually took a slot (not VIP)
                        if (finalTookSlot)
                        {
                            _downloadSemaphore.Release();
                            _logger.LogDebug("Released semaphore slot. Available slots: {Available}/{Total}", 
                                _downloadSemaphore.CurrentCount, _maxActiveDownloads);
                        }
                        OnPropertyChanged(nameof(ActiveWorkerSlots));
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "DownloadManager processing loop exception");
                await Task.Delay(1000, token); // Prevent hot loop on error
            }
        }
    }

    private void EnqueuePreSearch(DownloadContext track)
    {
        // STRICT CONCURRENCY MODE:
        // Pre-search can create extra parallel network searches outside worker slots.
        // Keep discovery inside ProcessTrackAsync so total active network work equals slider value.
        _ = track;
    }

    private void PrimePipelineSearchForNextTrack(string currentTrackGlobalId)
    {
        _ = currentTrackGlobalId;
    }

    private async Task ProcessTrackAsync(DownloadContext ctx, CancellationToken ct)
    {
        ctx.CancellationTokenSource = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var trackCt = ctx.CancellationTokenSource.Token;

        using (LogContext.PushProperty("TrackHash", ctx.GlobalId))
        using (LogContext.PushProperty("CorrelationId", ctx.CorrelationId))
        {
            try
            {
                // Pre-check: File already exists at resolved destination path (global filesystem dedup)
                if (!string.IsNullOrWhiteSpace(ctx.Model.ResolvedFilePath) && File.Exists(ctx.Model.ResolvedFilePath))
                {
                    _logger.LogInformation("Track already exists on disk, reusing local file: {Artist} - {Title} => {Path}",
                        ctx.Model.Artist, ctx.Model.Title, ctx.Model.ResolvedFilePath);

                    ctx.Model.Status = TrackStatus.Downloaded;
                    await _libraryService.UpdatePlaylistTrackAsync(ctx.Model);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                    return;
                }

                // Pre-check: Already downloaded in this project
                if (ctx.Model.Status == TrackStatus.Downloaded && File.Exists(ctx.Model.ResolvedFilePath))
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                    return;
                }

                // Phase 0: Check if file already exists in global library (cross-project deduplication)
                var existingEntry = string.IsNullOrWhiteSpace(ctx.Model.TrackUniqueHash)
                    ? null
                    : await _libraryService.FindLibraryEntryAsync(ctx.Model.TrackUniqueHash);
                if (existingEntry != null && File.Exists(existingEntry.FilePath))
                {
                    _logger.LogInformation("â™»ï¸ Track already in library: {Artist} - {Title}, reusing file: {Path}", 
                        ctx.Model.Artist, ctx.Model.Title, existingEntry.FilePath);
                    
                    // Reuse existing file instead of downloading
                    ctx.Model.ResolvedFilePath = existingEntry.FilePath;
                    ctx.Model.Status = TrackStatus.Downloaded;
                    await _libraryService.UpdatePlaylistTrackAsync(ctx.Model);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                    return;
                }

                // Phase 3.1: Use Detection Service (Searching State)
                // Refactor Note: DiscoveryService now takes PlaylistTrack (Decoupled).
                // Phase 3B: Pass Blacklisted users for Health Monitor retries
                // Phase 2: Parallel Pre-Search Integration
                // Check if we already have a running search task for this track
                DownloadDiscoveryService.DiscoveryResult discoveryResult;
                Track? bestMatch;

                if (ctx.OverrideCandidate != null)
                {
                    bestMatch = ctx.OverrideCandidate;
                    ctx.OverrideCandidate = null;
                    discoveryResult = new DownloadDiscoveryService.DiscoveryResult(bestMatch, null, null);
                    _logger.LogInformation("Using manual override candidate for {Title}", ctx.Model.Title);
                }
                else if (_preSearchTasks.TryRemove(ctx.GlobalId, out var existingTask))
                {
                    _logger.LogDebug("⚡ Using Pre-Search result for {Title}", ctx.Model.Title);
                    discoveryResult = await existingTask; // Await the already running task
                    bestMatch = discoveryResult.BestMatch;
                }
                else
                {
                    // Fallback to normal execution if not pre-searched
                    discoveryResult = await _discoveryService.FindBestMatchAsync(ctx.Model, trackCt, ctx.BlacklistedUsers, ctx.CorrelationId);
                    bestMatch = discoveryResult.BestMatch;
                }
                ctx.HedgeAttempted = false;
                ctx.HedgeMatch = null;

                if (_config.EnableHedgedDownloadFailover && discoveryResult.RunnerUpMatch != null)
                {
                    var candidate = discoveryResult.RunnerUpMatch;
                    if (!string.IsNullOrWhiteSpace(candidate.Username) &&
                        !string.IsNullOrWhiteSpace(candidate.Filename) &&
                        !string.Equals(candidate.Username, bestMatch?.Username, StringComparison.OrdinalIgnoreCase))
                    {
                        ctx.HedgeMatch = candidate;
                    }
                }

                // Capture search diagnostics
                if (discoveryResult.Log != null)
                {
                    if (string.IsNullOrWhiteSpace(discoveryResult.Log.CorrelationId))
                    {
                        discoveryResult.Log.CorrelationId = ctx.CorrelationId;
                    }
                    ctx.SearchAttempts.Add(discoveryResult.Log);
                }

                if (bestMatch == null)
                {
                    // Determine specific failure reason based on search history
                    var failureReason = DownloadFailureReason.NoSearchResults; // Default

                    // Phase 21: Progressive Retry Logic (3x Session, 3x Restart)
                    ctx.Model.SearchRetryCount++;
                    
                    // Determine specific failure reason based on search history for better logs/UI
                    if (ctx.SearchAttempts.Any())
                    {
                        var lastAttempt = ctx.SearchAttempts.Last();
                        
                        // Check for discovery timeout first (90s global limit)
                        if (lastAttempt.TimedOut)
                        {
                            failureReason = DownloadFailureReason.DiscoveryTimeout;
                        }
                        else if (lastAttempt.ResultsCount > 0)
                        {
                            // Results were found but rejected - determine why
                            if (lastAttempt.RejectedByQuality > 0)
                                failureReason = DownloadFailureReason.AllResultsRejectedQuality;
                            else if (lastAttempt.RejectedByFormat > 0)
                                failureReason = DownloadFailureReason.AllResultsRejectedFormat;
                            else if (lastAttempt.RejectedByBlacklist > 0)
                                failureReason = DownloadFailureReason.AllResultsBlacklisted;
                        }
                    }

                    if (ctx.Model.SearchRetryCount < _config.MaxSearchAttempts)
                    {

                        // In-session retry: put at the end of the queue
                        ctx.Model.Priority = 20; // Lower priority for retries

                        // Opt-P3: Add per-track jitter (±5 min) to the 20-minute base delay.
                        // Without jitter, all failed tracks retry simultaneously after a network
                        // outage, creating a search spike that can trigger Soulseek throttling.
                        var jitterSeconds = _jitterRandom.Next(-300, 300); // ±5 minutes in seconds
                        var delayMinutes = 20;
                        ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(delayMinutes).AddSeconds(jitterSeconds);

                        _logger.LogWarning("🔍 No match for {Title}. In-session retry {Count}/{MaxAttempts}. Next try at {Time} (20m ±jitter | Reason: {Reason}).", 
                            ctx.Model.Title, ctx.Model.SearchRetryCount, _config.MaxSearchAttempts, ctx.NextRetryTime, failureReason);


                        await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in ~{delayMinutes}m ({failureReason})");
                        return;
                    }
                    else
                    {
                        // Exceeded session retries: increment restart count and mark Failed/OnHold
                        ctx.Model.SearchRetryCount = 0; // Reset for next session
                        ctx.Model.NotFoundRestartCount++;

                        // If already in OnHold (MP3 lane) and still failing → truly terminal
                        if (ctx.Model.Status == TrackStatus.OnHold && ctx.Model.NotFoundRestartCount >= _config.MaxSearchAttempts)

                        {
                            _logger.LogError("🚫 TERMINAL: {Title} failed all FLAC AND MP3 attempts. Marking as permanently Failed.", ctx.Model.Title);
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(ctx.GlobalId, "🚫 Not found on network — tried FLAC + MP3 across multiple sessions.", false, ctx.CorrelationId));
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, "Terminal: Not found (FLAC + MP3 exhausted)");
                            return;
                        }

                        // 3 sessions x 3 retries = 9 total (FLAC lane)
                        if (ctx.Model.NotFoundRestartCount >= _config.MaxSearchAttempts && IsMp3FallbackAllowed(ctx.Model))

                        {
                            _logger.LogWarning("🎵 MP3 FALLBACK: {Title} exhausted all FLAC attempts. Auto-escalating to MP3 search lane.", ctx.Model.Title);
                            ctx.Model.Status = TrackStatus.OnHold;
                            ctx.Model.NotFoundRestartCount = 0; // Reset so MP3 lane gets fresh attempts
                            ctx.Model.SearchRetryCount = 0;
                            ctx.Model.Priority = 15; // Slightly lower priority in the queue
                            ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(5); // Small delay before MP3 attempt
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(ctx.GlobalId, $"🎵 FLAC not found after {_config.MaxSearchAttempts * _config.MaxSearchAttempts} attempts — switching to MP3 fallback lane automatically.", false, ctx.CorrelationId));
                            await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"FLAC failed ({_config.MaxSearchAttempts * _config.MaxSearchAttempts}x) → Auto MP3 Fallback queued");

                        }
                        else if (ctx.Model.NotFoundRestartCount >= _config.MaxSearchAttempts)

                        {
                            _logger.LogWarning("🛡️ Lossless-only profile active for {Title}; skipping MP3 fallback escalation after FLAC exhaustion.", ctx.Model.Title);
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(ctx.GlobalId, "🛡️ Lossless-only profile active. MP3 fallback skipped.", false, ctx.CorrelationId));
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, $"Lossless-only search exhausted ({failureReason}). MP3 fallback disabled by profile.");
                        }
                        else
                        {
                            _logger.LogWarning("⚠️ Session Failure for {Title}: No results found after 3 attempts. Deferring to next app restart (Session {Count}/3 | Reason: {Reason}).", 
                                ctx.Model.Title, ctx.Model.NotFoundRestartCount, failureReason);
                            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, $"Search failed 3x in this session ({failureReason}). Deferring to next restart.");
                        }
                        return;
                    }
                }

                // Phase 3.1: Download Logic (Downloading State)
                if (!TryValidateTrackForQueue(
                    bestMatch,
                    out var validationReason,
                    ctx.Model.MinBitrateOverride,
                    allowFallbackFormat: IsMp3FallbackAllowed(ctx.Model)))
                {
                    _logger.LogWarning("Rejected candidate before download for {Title}: {Reason}. Candidate: {Filename}",
                        ctx.Model.Title, validationReason, bestMatch.Filename);

                    ctx.Model.SearchRetryCount++;
                    ctx.Model.Priority = 20;
                    ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(10);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Candidate rejected: {validationReason}. Retrying later.");
                    return;
                }

                await DownloadFileAsync(ctx, bestMatch, trackCt);
            }
            catch (OperationCanceledException)
            {
                // Enhanced cancellation diagnostics
                var cancellationReason = "Unknown";
                
                // Fix #3: Preemption-aware cancellation handling
                if (ctx.Model.Priority >= 10 && ctx.State == PlaylistTrackState.Downloading)
                {
                    cancellationReason = "Preempted for high-priority download";
                    _logger.LogInformation("â¸ Download preempted for high-priority work: {Title} - deferring to queue", ctx.Model.Title);
                    await UpdateStateAsync(ctx, PlaylistTrackState.Deferred, "Deferred for high-priority downloads");
                    return;
                }
                
                // Check if it was user-initiated pause
                if (ctx.State == PlaylistTrackState.Paused)
                {
                    cancellationReason = "User paused download";
                    _logger.LogInformation("â¸ Download paused by user: {Title}", ctx.Model.Title);
                    return;
                }
                
                // Check if it was explicit cancellation
                if (ctx.State == PlaylistTrackState.Cancelled)
                {
                    cancellationReason = "User cancelled download";
                    _logger.LogInformation("âŒ Download cancelled by user: {Title}", ctx.Model.Title);
                    return;
                }
                
                // Check if it's a global shutdown
                if (_globalCts.Token.IsCancellationRequested)
                {
                    _logger.LogInformation("Shutdown detected. Preserving state for {Title} (Current State: {State})", 
                        ctx.Model.Title, ctx.State);
                    return;
                }

                // Otherwise it's an unexpected cancellation (health monitor, timeout, etc.)
                cancellationReason = "System/timeout cancellation";
                _logger.LogWarning(
                    "Unexpected cancellation for {Title} in state {State}. reason={Reason} correlationId={CorrelationId}",
                    ctx.Model.Title,
                    ctx.State,
                    cancellationReason,
                    ctx.CorrelationId ?? "-");
                await UpdateStateAsync(ctx, PlaylistTrackState.Cancelled);
            }
            catch (TimeoutException tex)
            {
                // Phase 3: Stalled Detection
                _logger.LogWarning(
                    "Download stall detected for {Title}. reason={Reason} correlationId={CorrelationId}",
                    ctx.Model.Title,
                    tex.Message,
                    ctx.CorrelationId ?? "-");

                if (await TryRunHedgeFailoverAsync(ctx, trackCt, "primary transfer stalled"))
                {
                    return;
                }

                if (!string.IsNullOrEmpty(ctx.CurrentUsername))
                {
                    lock (ctx.BlacklistedUsers)
                    {
                        ctx.BlacklistedUsers.Add(ctx.CurrentUsername);
                    }
                }

                ctx.Model.Priority = 10;
                ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(1);
                await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Stall detected (>10s no throughput). Re-searching with new peer.");
            }
            catch (DownloadDiscoveryService.DiscoveryConnectionUnavailableException ex)
            {
                // Transient connectivity issue: do NOT consume "not found" attempt budget.
                // Keep the track hot in queue with a short retry delay.
                _logger.LogInformation("Discovery paused for {Title} due to temporary connection loss: {Message}", ctx.Model.Title, ex.Message);
                ctx.NextRetryTime = DateTime.UtcNow.AddSeconds(15);
                await UpdateStateAsync(ctx, PlaylistTrackState.Pending, "Waiting for Soulseek connection. Retrying shortly.");
            }
            catch (SearchRejectedException srex)
            {
                _logger.LogWarning(
                    "Search rejected for {Title}. reason={Reason} correlationId={CorrelationId}",
                    ctx.Model.Title,
                    srex.Message,
                    ctx.CorrelationId ?? "-");
                
                // 1. Capture Diagnostics
                if (srex.SearchLog != null)
                {
                    ctx.SearchAttempts.Add(srex.SearchLog);
                }

                // 2. Exponential Backoff for "No Results" (Retry Logic)
                ctx.RetryCount++;
                if (ctx.RetryCount < _config.MaxDownloadRetries)
                {
                    var delayMinutes = Math.Pow(2, ctx.RetryCount); // 2, 4, 8, 16...
                    ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(delayMinutes);
                    ctx.Model.Priority = 20; // Low priority
                    
                    // Important: Set state to Pending so it stays in the queue, but with a status message explaining the delay
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in {delayMinutes}m: Search Rejected");
                    _logger.LogInformation("Scheduled retry #{Count} for {GlobalId} at {Time} due to search rejection", ctx.RetryCount, ctx.GlobalId, ctx.NextRetryTime);
                }
                else
                {
                    // Terminal Failure
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, DownloadFailureReason.NoSearchResults);
                }
            }
            catch (Exception ex)
            {
                var transferDisposition = ClassifyTransferFailure(ex);

                if (transferDisposition.AllowHedgeFailover)
                {
                    _logger.LogWarning(
                        "Peer transfer failure for {Title}. reason={Reason} correlationId={CorrelationId}",
                        ctx.Model.Title,
                        ex.Message,
                        ctx.CorrelationId ?? "-");

                    if (await TryRunHedgeFailoverAsync(ctx, trackCt, "primary transfer was rejected"))
                    {
                        return;
                    }
                }
                else
                {
                    _logger.LogError(
                        ex,
                        "ProcessTrackAsync error for {GlobalId}. reason={Reason} correlationId={CorrelationId}",
                        ctx.GlobalId,
                        ex.Message,
                        ctx.CorrelationId ?? "-");
                }

                // FIX: Blacklist the failing peer so we don't pick them again instantly
                if (!string.IsNullOrEmpty(ctx.CurrentUsername))
                {
                    lock (ctx.BlacklistedUsers) // HashSet isn't thread-safe
                    {
                        if (ctx.BlacklistedUsers.Add(ctx.CurrentUsername))
                        {
                            _logger.LogWarning(
                                "Blacklisted peer {User} for {Track}. reason={Reason} correlationId={CorrelationId}",
                                ctx.CurrentUsername,
                                ctx.Model.Title,
                                transferDisposition.OperatorMessage,
                                ctx.CorrelationId ?? "-");
                        }
                    }
                }

                if (!transferDisposition.RetryFailureReason.ShouldAutoRetry())
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, transferDisposition.RetryFailureReason);
                    return;
                }
                
                // Exponential / classified backoff logic
                ctx.RetryCount++;
                if (ctx.RetryCount < _config.MaxDownloadRetries)
                {
                    var delayMinutes = transferDisposition.DelayMinutes ?? Math.Pow(2, ctx.RetryCount); // 2, 4, 8, 16...
                    ctx.NextRetryTime = DateTime.UtcNow.AddMinutes(delayMinutes);
                    ctx.Model.Priority = 20; // LOW PRIORITY: Send retries to back of queue (fresh downloads = priority 10)
                    await UpdateStateAsync(ctx, PlaylistTrackState.Pending, $"Retrying in {delayMinutes}m: {transferDisposition.OperatorMessage}");
                    _logger.LogInformation("Scheduled retry #{Count} for {GlobalId} at {Time} (low priority)", ctx.RetryCount, ctx.GlobalId, ctx.NextRetryTime);
                }
                else
                {
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, transferDisposition.RetryFailureReason);
                }
            }
        }
    }

    internal sealed record TransferFailureDisposition(
        DownloadFailureReason RetryFailureReason,
        bool AllowHedgeFailover,
        double? DelayMinutes,
        string OperatorMessage);

    internal static TransferFailureDisposition ClassifyTransferFailure(Exception ex)
    {
        var message = ex.Message ?? string.Empty;

        if (message.Contains("banned", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("not authorized", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("access denied", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferFailureDisposition(
                DownloadFailureReason.RemoteAccessDenied,
                AllowHedgeFailover: false,
                DelayMinutes: null,
                OperatorMessage: "peer denied transfer access");
        }

        if (ex.GetType().Name.Contains("TransferRejectedException", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Too many files", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("queue", StringComparison.OrdinalIgnoreCase))
        {
            return new TransferFailureDisposition(
                DownloadFailureReason.RemoteQueueDenied,
                AllowHedgeFailover: true,
                DelayMinutes: 2,
                OperatorMessage: "peer queue rejected transfer");
        }

        if (message.Contains("refused", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("aborted", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("Unable to read", StringComparison.OrdinalIgnoreCase) ||
            ex is IOException)
        {
            return new TransferFailureDisposition(
                DownloadFailureReason.NetworkError,
                AllowHedgeFailover: true,
                DelayMinutes: 2,
                OperatorMessage: "network transfer error");
        }

        if (ex is TimeoutException)
        {
            return new TransferFailureDisposition(
                DownloadFailureReason.Timeout,
                AllowHedgeFailover: true,
                DelayMinutes: 1,
                OperatorMessage: "transfer timeout");
        }

        return new TransferFailureDisposition(
            DownloadFailureReason.PeerRejected,
            AllowHedgeFailover: false,
            DelayMinutes: null,
            OperatorMessage: string.IsNullOrWhiteSpace(message) ? "peer rejected transfer" : message);
    }

    internal static string? ResolveDiscoveryReason(string? sourceProvenance, string? matchReason, string? scoreBreakdown)
    {
        var preferredDiscoveryReason = !string.IsNullOrWhiteSpace(matchReason)
            ? matchReason
            : scoreBreakdown;

        if (string.Equals(sourceProvenance, "ShieldSanitized", StringComparison.OrdinalIgnoreCase))
        {
            return string.IsNullOrWhiteSpace(preferredDiscoveryReason)
                ? "🛡 Shield sanitized search"
                : $"🛡 Shield sanitized · {preferredDiscoveryReason}";
        }

        return string.IsNullOrWhiteSpace(preferredDiscoveryReason)
            ? null
            : preferredDiscoveryReason;
    }

    private async Task DownloadFileAsync(DownloadContext ctx, Track bestMatch, CancellationToken ct)
    {
        await UpdateStateAsync(ctx, PlaylistTrackState.Downloading);
        _peerReliability.RecordDownloadStarted(bestMatch.Username);

        static string FormatBytes(long value)
        {
            if (value >= 1024L * 1024L * 1024L) return $"{value / 1024d / 1024d / 1024d:F2} GB";
            if (value >= 1024L * 1024L) return $"{value / 1024d / 1024d:F2} MB";
            if (value >= 1024L) return $"{value / 1024d:F1} KB";
            return $"{value} B";
        }

        _eventBus.Publish(new Events.TrackDetailedStatusEvent(
            ctx.GlobalId,
            $"📥 Transfer started | user:{bestMatch.Username} | file:{bestMatch.Filename} | bitrate:{bestMatch.Bitrate}kbps | format:{bestMatch.Format ?? "unknown"}",
            false,
            ctx.CorrelationId));

        ctx.Model.Bitrate = bestMatch.Bitrate;
        if (!string.IsNullOrWhiteSpace(bestMatch.Filename))
        {
            var extension = Path.GetExtension(bestMatch.Filename);
            if (!string.IsNullOrWhiteSpace(extension))
            {
                ctx.Model.Format = extension.TrimStart('.').ToLowerInvariant();
            }
        }
        ctx.Model.QualityDetails = $"{bestMatch.Bitrate}kbps|{bestMatch.BitDepth ?? 0}bit|{bestMatch.SampleRate ?? 0}Hz";
        ctx.Model.DiscoveryReason = ResolveDiscoveryReason(
            ctx.Model.SourceProvenance,
            bestMatch.MatchReason,
            bestMatch.ScoreBreakdown);

        PrimePipelineSearchForNextTrack(ctx.GlobalId);
        
        // Phase 3B: Track current peer for Health Monitor blacklisting
        ctx.CurrentUsername = bestMatch.Username;

        // Opt-P1: Register in O(1) active-username index so OnDownloadProgressChanged
        // doesn't have to scan all downloads on every progress packet.
        if (!string.IsNullOrEmpty(bestMatch.Username))
            _activeByUsername[bestMatch.Username] = ctx;
        
        // Phase 0.3: Reset Health Metrics for new attempt
        ctx.StallCount = 0;
        ctx.CurrentSpeed = 0;

        // Phase 2.5: Use PathProviderService for consistent folder structure
        // Create a temporary track object to combine DB (enriched) metadata with search result (technical) info
        var namingTrack = new Track
        {
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            Album = ctx.Model.Album,
            Bitrate = bestMatch.Bitrate,
            BPM = ctx.Model.BPM,
            MusicalKey = ctx.Model.MusicalKey,
            Energy = ctx.Model.Energy,
            Filename = bestMatch.Filename, // For {filename} variable
            Username = bestMatch.Username, // For {user} variable
            Length = ctx.Model.CanonicalDuration // For {length} variable
        };
        var finalPath = _pathProvider.GetTrackPath(namingTrack);
        ctx.Model.ResolvedFilePath = finalPath; // Set early so UI can probe the path (will look for .part)

        var partPath = finalPath + ".part";
        long startPosition = 0;

        // STEP 1: Check if final file already exists and is complete
        if (File.Exists(finalPath))
        {
            var existingFileInfo = new FileInfo(finalPath);
            if (existingFileInfo.Length == bestMatch.Size)
            {
                _logger.LogInformation("File already exists and is complete: {Path}", finalPath);
                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);
                return;
            }
            else
            {
                // File exists but is incomplete (corrupted?) - delete and restart
                _logger.LogWarning("Final file exists but size mismatch (expected {Expected}, got {Actual}). Deleting and restarting.", 
                    bestMatch.Size, existingFileInfo.Length);
                File.Delete(finalPath);
            }
        }

        // STEP 2: Check for existing .part file to resume
        if (File.Exists(partPath))
        {
            var diskBytes = new FileInfo(partPath).Length;
            
            // Phase 3A: Atomic Handshake - Trust Journal, Truncate Disk
            var confirmedBytes = await _crashJournal.GetConfirmedBytesAsync(ctx.GlobalId);
            long expectedSize = bestMatch.Size ?? 0;

            // Fix: Ghost File Race Condition Check
            // If file is fully downloaded on disk but journal says 99% (crash during finalization),
            // TRUST THE DISK. Do not truncate. Verification step will validate integrity.
            if (expectedSize > 0 && diskBytes >= expectedSize)
            {
                startPosition = diskBytes;
                _logger.LogInformation("ðŸ‘» Ghost File Detected: Disk ({Disk}) >= Expected ({Expected}). Skipping truncation despite Journal ({Journal}).", 
                    diskBytes, expectedSize, confirmedBytes);
            }
            else if (confirmedBytes > 0 && diskBytes > confirmedBytes)
            {
                // Case 1: Disk has more data than journal (unconfirmed tail)
                // Truncate to confirmed bytes to ensure no corrupt/torn data is kept
                _logger.LogWarning("âš ï¸ Atomic Resume: Truncating {Diff} bytes of unconfirmed data for {Track}", 
                    diskBytes - confirmedBytes, ctx.Model.Title);
                    
                await using (var fs = new FileStream(partPath, FileMode.Open, FileAccess.Write, FileShare.None, 4096, useAsync: true))
                {
                    fs.SetLength(confirmedBytes);
                    await fs.FlushAsync();
                }
                startPosition = confirmedBytes;
            }
            else
            {
                // Case 2: Disk <= Journal, or no Journal entry (clean shutdown/new)
                // Resume from what we physically have
                startPosition = diskBytes;
            }

            ctx.IsResuming = true;
            ctx.BytesReceived = startPosition;
            
            _logger.LogInformation("Resuming download from byte {Position} for {Track} (Journal Confirmed: {Confirmed})", 
                startPosition, ctx.Model.Title, confirmedBytes);

            _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                ctx.GlobalId,
                $"♻️ Resuming transfer | user:{bestMatch.Username} | file:{bestMatch.Filename} | offset:{FormatBytes(startPosition)}",
                false,
                ctx.CorrelationId));
        }
        else
        {
            ctx.IsResuming = false;
            ctx.BytesReceived = 0;
        }

        // STEP 3: Set total bytes for progress tracking
        ctx.TotalBytes = bestMatch.Size ?? 0;  // Handle nullable size

        // Phase 2A: CHECKPOINT LOGGING - Log before download starts
        var checkpointState = new DownloadCheckpointState
        {
            TrackGlobalId = ctx.GlobalId,
            Artist = ctx.Model.Artist,
            Title = ctx.Model.Title,
            SoulseekUsername = bestMatch.Username!,
            SoulseekFilename = bestMatch.Filename!,
            ExpectedSize = bestMatch.Size ?? 0,
            PartFilePath = partPath,
            FinalPath = finalPath,
            BytesDownloaded = startPosition // Start with existing progress if resuming
        };

        var checkpoint = new RecoveryCheckpoint
        {
            Id = ctx.GlobalId, // CRITICAL: Use TrackGlobalId to prevent duplicates on retry
            OperationType = OperationType.Download,
            TargetPath = finalPath,
            StateJson = JsonSerializer.Serialize(checkpointState),
            Priority = 10 // High priority - active user download
        };

        string? checkpointId = await _crashJournal.LogCheckpointAsync(checkpoint);
        _logger.LogDebug("âœ… Download checkpoint logged: {Id} - {Artist} - {Title}", 
            checkpointId, ctx.Model.Artist, ctx.Model.Title);

        // Phase 2A: PERIODIC HEARTBEAT with stall detection
        var heartbeatIntervalSeconds = Math.Max(1, Math.Min(5, _config.StallTimeoutSeconds));
        var stallThresholdTicks = Math.Max(1, (int)Math.Ceiling(_config.StallTimeoutSeconds / (double)heartbeatIntervalSeconds));
        var throughputFloorBytesPerSecond = Math.Max(1, _config.MinThroughputFloorKbps) * 1024.0;

        using var heartbeatTimer = new PeriodicTimer(TimeSpan.FromSeconds(heartbeatIntervalSeconds));
        using var heartbeatCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var stallMonitorCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        
        int stallCount = 0;
        long lastHeartbeatBytes = startPosition;
        long lastThroughputBytes = startPosition;
        bool stalledByMonitor = false;
        
        var heartbeatTask = Task.Run(async () =>
        {
            try
            {
                while (await heartbeatTimer.WaitForNextTickAsync(heartbeatCts.Token))
                {
                    // Phase 3A: Finalization Guard - Stop heartbeat immediately if completion logic started
                    if (ctx.IsFinalizing) return;

                    var currentBytes = ctx.BytesReceived; // Thread-safe Interlocked read
                    
                    // STALL DETECTION: throughput below floor for configured timeout window.
                    var bytesDelta = currentBytes - lastThroughputBytes;
                    var minimumBytesExpected = throughputFloorBytesPerSecond * heartbeatIntervalSeconds;

                    if (bytesDelta < minimumBytesExpected)
                    {
                        stallCount++;
                        if (stallCount >= stallThresholdTicks && !stalledByMonitor)
                        {
                            var currentKbps = bytesDelta > 0
                                ? (bytesDelta / 1024.0) / heartbeatIntervalSeconds
                                : 0;

                            _logger.LogWarning("âš ï¸ Download stalled for >{StallTimeout}s: {Artist} - {Title} ({Current}/{Total} bytes, {Kbps:0.0} KB/s)",
                                _config.StallTimeoutSeconds,
                                ctx.Model.Artist, ctx.Model.Title, currentBytes, checkpointState.ExpectedSize, currentKbps);
                            
                            // [NEW] Overhaul Phase: Set machine-readable reason
                            ctx.Model.StalledReason = $"Throughput below {_config.MinThroughputFloorKbps} KB/s for {_config.StallTimeoutSeconds}s";
                            stalledByMonitor = true;
                            stallMonitorCts.Cancel();
                            return;
                        }
                    }
                    else
                    {
                        stallCount = 0; // Reset on progress
                    }

                    lastThroughputBytes = currentBytes;

                    // PERFORMANCE: Only update if progress > 1KB to reduce SQLite overhead
                    if (currentBytes > 0 && currentBytes > lastHeartbeatBytes + 1024)
                    {
                        checkpointState.BytesDownloaded = currentBytes;
                        
                        // SSD OPTIMIZATION: Skip if no meaningful progress (built into UpdateHeartbeatAsync)
                        await _crashJournal.UpdateHeartbeatAsync(
                            checkpointId!,
                            JsonSerializer.Serialize(checkpointState), // Serialize in heartbeat thread
                            lastHeartbeatBytes,
                            currentBytes);
                        
                        lastHeartbeatBytes = currentBytes;
                        
                        _logger.LogTrace("Heartbeat: {Current}/{Total} bytes ({Percent}%)",
                            currentBytes, checkpointState.ExpectedSize, 
                            (currentBytes * 100.0 / checkpointState.ExpectedSize));
                    }
                }
            }
            catch (OperationCanceledException)
            {
                // Expected on download completion or cancellation
                _logger.LogDebug("Heartbeat cancelled for {GlobalId}", ctx.GlobalId);
            }
            catch (Exception ex)
            {
                _logger.LogError(
                    ex,
                    "Heartbeat error for {GlobalId}. reason={Reason} correlationId={CorrelationId}",
                    ctx.GlobalId,
                    ex.Message,
                    ctx.CorrelationId ?? "-");
            }
        }, heartbeatCts.Token);

        try
        {
            // STEP 4: Progress tracking with 100ms throttling
        var lastNotificationTime = DateTime.MinValue;
        var lastDetailedProgressTime = DateTime.MinValue;
        var totalFileSize = bestMatch.Size ?? 1;  // Avoid division by zero
        var progress = new Progress<double>(p =>
        {
            ctx.Progress = p * 100;
            ctx.BytesReceived = (long)((bestMatch.Size ?? 0) * p);

            // Throttle to 2 updates/sec (500ms) to prevent UI stuttering under heavy load
            if ((DateTime.Now - lastNotificationTime).TotalMilliseconds > 500)
            {
                _eventBus.Publish(new TrackProgressChangedEvent(
                    ctx.GlobalId, 
                    ctx.Progress,
                    ctx.BytesReceived,
                    ctx.TotalBytes,
                    ctx.CorrelationId
                ));
                
                lastNotificationTime = DateTime.Now;
            }

            if ((DateTime.Now - lastDetailedProgressTime).TotalSeconds >= 3)
            {
                var speedBytesPerSecond = Math.Max(0, (long)ctx.CurrentSpeed);
                _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                    ctx.GlobalId,
                    $"↔ Transfer progress | user:{bestMatch.Username} | file:{bestMatch.Filename} | speed:{FormatBytes(speedBytesPerSecond)}/s | received:{FormatBytes(ctx.BytesReceived)} | total:{FormatBytes(ctx.TotalBytes)}",
                    false,
                    ctx.CorrelationId));
                lastDetailedProgressTime = DateTime.Now;
            }
        });

        // STEP 5: Download to .part file with resume support
        bool success;
        try
        {
            success = await _soulseek.DownloadAsync(
                bestMatch.Username!,
                bestMatch.Filename!,
                partPath,          // Download to .part file
                bestMatch.Size,
                progress,
                lifecycleUpdate: update =>
                {
                    switch (update.Phase)
                    {
                        case TransferLifecyclePhase.RemoteQueued:
                            _ = UpdateStateAsync(ctx, PlaylistTrackState.Queued, update.Detail ?? "Queued remotely by peer");
                            _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                                ctx.GlobalId,
                                $"⏳ Remote queue: {bestMatch.Username} is holding the transfer in queue.",
                                false,
                                ctx.CorrelationId));
                            break;
                        case TransferLifecyclePhase.Transferring:
                            _ = UpdateStateAsync(ctx, PlaylistTrackState.Downloading, update.Detail);
                            break;
                    }
                },
                ct: stallMonitorCts.Token,
                startOffset: startPosition      // Resume from existing bytes
            );
        }
        catch (OperationCanceledException) when (stalledByMonitor)
        {
            throw new TimeoutException("Local stall monitor triggered (10s with no throughput). Dropping peer.");
        }

        if (success)
        {
            // STEP 6: Atomic Rename - Only if download completed successfully
            try
            {
                // Brief pause to ensure all file handles are released
                await Task.Delay(100, ct);

                // Verify .part file exists and has correct size
                if (!File.Exists(partPath))
                {
                    throw new FileNotFoundException($"Part file disappeared: {partPath}");
                }

                var finalPartSize = new FileInfo(partPath).Length;
                // Fix: Allow file to be slightly larger (metadata padding)
                // We rely on VerifyAudioFormatAsync later for actual integrity
                if (finalPartSize < bestMatch.Size)
                {
                    throw new InvalidDataException(
                        $"Downloaded file truncated. Expected {bestMatch.Size}, got {finalPartSize}");
                }

                // Clean up old final file if it exists (race condition edge case)
                if (File.Exists(finalPath))
                {
                    _logger.LogWarning("Final file already exists, overwriting: {Path}", finalPath);
                    // File.Delete is handled by MoveAtomicAsync logic (via WriteAtomicAsync)
                }

                // ATOMIC OPERATION: Use SafeWrite to move .part to .mp3
                var moveSuccess = await _fileWriteService.MoveAtomicAsync(partPath, finalPath);
                
                if (!moveSuccess)
                {
                     // If move failed (e.g. disk full during copy phase), throw execution to trigger retry/fail logic
                     throw new IOException($"Failed to atomically move file from {partPath} to {finalPath}");
                }
                
                _logger.LogInformation("Atomic move complete: {Part} â†’ {Final}", 
                    Path.GetFileName(partPath), Path.GetFileName(finalPath));

                _eventBus.Publish(new Events.TrackDetailedStatusEvent(
                    ctx.GlobalId,
                    $"✅ Transfer finalized | user:{bestMatch.Username} | file:{Path.GetFileName(finalPath)} | total:{FormatBytes(finalPartSize)}",
                    false,
                    ctx.CorrelationId));

                // Phase 1A: POST-DOWNLOAD VERIFICATION
                // Verify the downloaded file is valid before adding to library
                try
                {
                    _logger.LogDebug("Verifying downloaded file: {Path}", finalPath);
                    
                    // STEP 1: Verify audio format (ensures file can be opened and has valid properties)
                    var isValidAudio = await SLSKDONET.Services.IO.FileVerificationHelper.VerifyAudioFormatAsync(finalPath);
                    if (!isValidAudio)
                    {
                        _logger.LogWarning("Downloaded file failed audio format verification: {Path}", finalPath);
                        
                        // Delete corrupt file
                        File.Delete(finalPath);
                        
                        // Mark as failed with specific error
                        await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                            DownloadFailureReason.FileVerificationFailed);
                        return;
                    }
                    
                    // STEP 2: Verify minimum file size (prevents 0-byte or tiny corrupt files)
                    var isValidSize = await SLSKDONET.Services.IO.FileVerificationHelper.VerifyFileSizeAsync(finalPath, 10 * 1024); // 10KB minimum
                    if (!isValidSize)
                    {
                        _logger.LogWarning("Downloaded file too small (< 10KB): {Path}", finalPath);
                        
                        // Delete invalid file
                        File.Delete(finalPath);
                        
                        // Mark as failed
                        await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                            DownloadFailureReason.FileVerificationFailed);
                        return;
                    }
                    
                    _logger.LogInformation("âœ… File verification passed: {Path}", finalPath);
                }
                catch (Exception verifyEx)
                {
                    _logger.LogError(verifyEx, "File verification error for {Path}", finalPath);
                    
                    // If verification crashes, treat as corrupt and clean up
                    try { File.Delete(finalPath); } catch { }
                    
                    await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                        DownloadFailureReason.FileVerificationFailed);
                    return;
                }

                ctx.Model.ResolvedFilePath = finalPath;
                ctx.Progress = 100;
                ctx.BytesReceived = bestMatch.Size ?? 0;  // Handle nullable size
                await UpdateStateAsync(ctx, PlaylistTrackState.Completed);

                // Phase 2A: Complete checkpoint on success
                if (checkpointId != null)
                {
                    // Phase 3A: Sentinel Flag - Prevent heartbeat from re-creating checkpoint
                    ctx.IsFinalizing = true;
                    
                    await _crashJournal.CompleteCheckpointAsync(checkpointId);
                    _logger.LogDebug("âœ… Download checkpoint completed: {Id}", checkpointId);
                }

                // CRITICAL: Create LibraryEntry for global index (enables All Tracks view + cross-project deduplication)
                var libraryEntry = new LibraryEntry
                {
                    UniqueHash = ctx.Model.TrackUniqueHash,
                    Artist = ctx.Model.Artist,
                    Title = ctx.Model.Title,
                    Album = ctx.Model.Album ?? "Unknown",
                    FilePath = finalPath,
                    Format = Path.GetExtension(finalPath).TrimStart('.'),
                    Bitrate = bestMatch.Bitrate
                };
                await _libraryService.SaveOrUpdateLibraryEntryAsync(libraryEntry);
                _logger.LogInformation("ðŸ“š Added to library: {Artist} - {Title}", ctx.Model.Artist, ctx.Model.Title);
            }
            catch (Exception renameEx)
            {
                _logger.LogError(renameEx, "Failed to perform atomic rename for {Track}", ctx.Model.Title);
                await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                    DownloadFailureReason.AtomicRenameFailed);
            }
        }
        else
        {
            await UpdateStateAsync(ctx, PlaylistTrackState.Failed, 
                DownloadFailureReason.TransferFailed);
        }
    }

    finally
    {
        // Opt-P1: Ensure username index is cleaned up even on failures / cancellation.
        if (!string.IsNullOrEmpty(bestMatch.Username))
        {
            _activeByUsername.TryRemove(bestMatch.Username, out _);
            _lastBytesByUsername.TryRemove(bestMatch.Username, out _);
        }

        // Phase 2A: CRITICAL CLEANUP - Stop heartbeat timer
        heartbeatCts.Cancel(); // Signal heartbeat to stop
        heartbeatTimer.Dispose();
        
        try
        {
            await heartbeatTask; // Wait for heartbeat task to complete
        }
        catch (OperationCanceledException)
        {
            // Expected when heartbeat is cancelled
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error waiting for heartbeat task cleanup");
        }
    }
}

    private async Task<bool> TryRunHedgeFailoverAsync(DownloadContext ctx, CancellationToken ct, string reason)
    {
        if (!_config.EnableHedgedDownloadFailover || ctx.HedgeAttempted || ctx.HedgeMatch == null)
        {
            return false;
        }

        var hedgeMatch = ctx.HedgeMatch;
        if (string.IsNullOrWhiteSpace(hedgeMatch.Username) || string.IsNullOrWhiteSpace(hedgeMatch.Filename))
        {
            return false;
        }

        ctx.HedgeAttempted = true;

        if (!string.IsNullOrEmpty(ctx.CurrentUsername))
        {
            lock (ctx.BlacklistedUsers)
            {
                ctx.BlacklistedUsers.Add(ctx.CurrentUsername);
            }
        }

        _logger.LogWarning("Hedge failover for {Title}: switching to runner-up peer {User}. reason={Reason} correlationId={CorrelationId}",
            ctx.Model.Title,
            hedgeMatch.Username,
            reason,
            ctx.CorrelationId ?? "-");

        _eventBus.Publish(new Events.TrackDetailedStatusEvent(
            ctx.GlobalId,
            $"⚡ Switching to runner-up peer {hedgeMatch.Username} ({reason}).",
            false,
            ctx.CorrelationId));

        try
        {
            await DownloadFileAsync(ctx, hedgeMatch, ct);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Hedge failover attempt failed for {Title}. reason={Reason} correlationId={CorrelationId}",
                ctx.Model.Title,
                ex.Message,
                ctx.CorrelationId ?? "-");
            return false;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void OnAutoDownloadTrack(AutoDownloadTrackEvent e)
    {
        _logger.LogInformation("Auto-Download triggered for {TrackId}", e.TrackGlobalId);
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
        if (ctx == null) return;

        if (!string.IsNullOrWhiteSpace(e.CorrelationId))
        {
            ctx.CorrelationId = e.CorrelationId!;
        }

        _ = Task.Run(async () => 
        {
            // Phase 3C Hardening: Enforce Priority 0 (Express Lane) and persistence
            ctx.Model.Priority = 0;
            // Persist valid priority for restart resilience
            await _databaseService.UpdatePlaylistTrackPriorityAsync(ctx.Model.Id, 0); 
            
            // Allow loop to pick it up naturally (respecting semaphore)
            await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
            
            // Check if we need to preempt immediately (wake up loop)
            // The loop runs every 500ms when idle, so latent pickup is fast.
        });
    }

    private void OnAutoDownloadUpgrade(AutoDownloadUpgradeEvent e)
    {
        _logger.LogInformation("Auto-Upgrade triggered for {TrackId}", e.TrackGlobalId);
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == e.TrackGlobalId);
        if (ctx == null) return;

        if (!string.IsNullOrWhiteSpace(e.CorrelationId))
        {
            ctx.CorrelationId = e.CorrelationId!;
        }

        _ = Task.Run(async () => 
        {
            // 1. Delete old file first to avoid confusion
            if (!string.IsNullOrEmpty(ctx.Model.ResolvedFilePath))
            {
                DeleteLocalFiles(ctx.Model.ResolvedFilePath);
            }

            // 2. Clear old quality metrics
            ctx.Model.Bitrate = null;
            ctx.Model.SpectralHash = null;
            ctx.Model.IsTrustworthy = null;

            // 3. Set High Priority and Queue
            ctx.Model.Priority = 0;
            await _databaseService.UpdatePlaylistTrackPriorityAsync(ctx.Model.Id, 0); 

            await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        });
    }

    private void OnUpgradeAvailable(UpgradeAvailableEvent e)
    {
        // For now just log, could trigger a notification in future
        _logger.LogInformation("Upgrade Available (Manual Approval Needed): {TrackId} - {BestMatch}", 
            e.TrackGlobalId, e.BestMatch.Filename);
    }

    // ========================================
    // Phase 3C: Multi-Lane Priority Engine
    // ========================================

    /// <summary>
    /// Gets count of active downloads grouped by priority level.
    /// Returns dictionary: Priority -> Count
    /// </summary>
    private Dictionary<int, int> GetActiveDownloadsByPriority()
    {
        var activeDownloads = _downloads
            .Where(d => d.State == PlaylistTrackState.Searching || d.State == PlaylistTrackState.Downloading)
            .GroupBy(d => d.Model.Priority)
            .ToDictionary(g => g.Key, g => g.Count());

        return activeDownloads;
    }

    /// <summary>
    /// Selects next track by queue priority only.
    /// Concurrency is enforced exclusively by the global worker-slot semaphore.
    /// </summary>
    private DownloadContext? SelectNextTrackWithLaneAllocation(
        List<DownloadContext> eligibleTracks,
        Dictionary<int, int>? activeByPriority) // Made optional
    {
        if (!eligibleTracks.Any()) return null;

        _ = activeByPriority;
        return eligibleTracks
            .OrderBy(t => t.Model.Priority)
            .ThenBy(t => t.Model.AddedAt)
            .FirstOrDefault();
    }

    public void BumpTrackToTop(string globalId)
    {
        DownloadContext? ctx;
        lock (_collectionLock) ctx = _downloads.FirstOrDefault(d => d.GlobalId == globalId);
        if (ctx != null)
        {
            ctx.Model.Priority = 0;
            ctx.Model.AddedAt = DateTime.MinValue; // Absolute top
            
            if (ctx.State == PlaylistTrackState.Pending || ctx.State == PlaylistTrackState.Stalled || ctx.State == PlaylistTrackState.Paused)
            {
                _ = UpdateStateAsync(ctx, PlaylistTrackState.Pending);
            }
            _ = RefillQueueAsync();
        }
    }

    /// <summary>
    /// Prioritizes all tracks from a specific project by bumping to Priority 0 (High).
    /// Phase 3C: The "VIP Pass" - allows user to jump queue with specific playlist.
    /// Hardening Fix #1: Now persists to database for crash resilience.
    /// </summary>
    public async Task PrioritizeProjectAsync(Guid playlistId)
    {
        _logger.LogInformation("ðŸš€ Prioritizing project: {PlaylistId}", playlistId);

        // Fix #1: Persist to database FIRST for crash resilience
        await _databaseService.UpdatePlaylistTracksPriorityAsync(playlistId, 0);
        
        // Update in-memory contexts
        int updatedCount = 0;
        lock (_collectionLock)
        {
            foreach (var download in _downloads.Where(d => d.Model.PlaylistId == playlistId && d.State == PlaylistTrackState.Pending))
            {
                download.Model.Priority = 0;
                updatedCount++;
            }
        }

        _logger.LogInformation("âœ… Prioritized {Count} tracks from project {PlaylistId} (database + in-memory)",
            updatedCount, playlistId);
    }

    /// <summary>
    /// Pauses the lowest priority active download to free a slot for high-priority track.
    /// Phase 3C: Preemption support.
    /// </summary>
    private async Task PauseLowestPriorityDownloadAsync()
    {
        DownloadContext? lowestPriority = null;

        lock (_collectionLock)
        {
            lowestPriority = _downloads
                .Where(d => d.State == PlaylistTrackState.Downloading || d.State == PlaylistTrackState.Searching)
                .OrderByDescending(d => d.Model.Priority) // Highest priority value = lowest priority
                .ThenBy(d => d.Model.AddedAt)
                .FirstOrDefault();
        }

        if (lowestPriority != null && lowestPriority.Model.Priority > 0) // Preempt anything lower than High Priority (0)
        {
            _logger.LogInformation("â¸ Preempting lower priority download (Priority {Prio}): {Title}", 
                lowestPriority.Model.Priority, lowestPriority.Model.Title);
            await PauseTrackAsync(lowestPriority.Model.TrackUniqueHash);
        }
    }


    private void OnDownloadProgressChanged(object? sender, DownloadProgressEventArgs e)
    {
        // Find context by username (reliable enough for active transfers)
        DownloadContext? ctx;
        lock (_collectionLock)
        {
            ctx = _downloads.FirstOrDefault(d => 
                d.State == PlaylistTrackState.Downloading && 
                d.CurrentUsername == e.Username);
        }

        if (ctx != null)
        {
            ctx.Progress = e.Progress * 100;
            ctx.BytesReceived = e.BytesReceived;
            ctx.TotalBytes = e.TotalBytes;

            var previous = _lastBytesByUsername.AddOrUpdate(e.Username, e.BytesReceived, (_, old) => e.BytesReceived);
            var delta = e.BytesReceived - previous;
            if (delta > 0)
            {
                _peerReliability.RecordProgress(e.Username, delta);
            }

            // Phase 9: Stall Detection
            var now = DateTime.Now;
            if (ctx.LastSpeedUpdate.HasValue)
            {
                var timeDelta = (now - ctx.LastSpeedUpdate.Value).TotalSeconds;
                if (timeDelta > 0)
                {
                    ctx.CurrentSpeed = (long)(delta / timeDelta);
                    const long stallThreshold = 15 * 1024; // 15 KB/s
                    if (ctx.CurrentSpeed < stallThreshold)
                    {
                        if (!ctx.StallStartTime.HasValue)
                        {
                            ctx.StallStartTime = now;
                        }
                        else if ((now - ctx.StallStartTime.Value).TotalSeconds > 15 && !ctx.IsStalled)
                        {
                            ctx.IsStalled = true;
                            _eventBus.Publish(new TrackStalledEvent(ctx.GlobalId, e.Username));
                            _logger.LogWarning("🚧 Track stalled: {Title} from {User}, triggering hedge", ctx.Model.Title, e.Username);
                            // Trigger hedge failover
                            _ = Task.Run(() => TryRunHedgeFailoverAsync(ctx, _globalCts.Token, "stalled"));
                        }
                    }
                    else
                    {
                        // Reset stall state if speed recovered
                        ctx.StallStartTime = null;
                        ctx.IsStalled = false;
                    }
                }
            }
            ctx.LastSpeedUpdate = now;
        }
    }

    private void OnDownloadCompleted(object? sender, DownloadCompletedEventArgs e)
    {
        _logger.LogDebug("Adapter download completed event: {File} ({Success})", e.Filename, e.Success);
        _lastBytesByUsername.TryRemove(e.Username, out _);

        if (e.Success)
        {
            _peerReliability.RecordDownloadCompleted(e.Username);
            return;
        }

        var isStall = !string.IsNullOrWhiteSpace(e.Error) &&
                      (e.Error.Contains("Timeout", StringComparison.OrdinalIgnoreCase) ||
                       e.Error.Contains("stalled", StringComparison.OrdinalIgnoreCase));
        _peerReliability.RecordDownloadFailed(e.Username, stalled: isStall);
    }

    public void Dispose()
    {
        _soulseek.DownloadProgressChanged -= OnDownloadProgressChanged;
        _soulseek.DownloadCompleted -= OnDownloadCompleted;
        _globalCts.Cancel();
        _globalCts.Dispose();
        _processingTask?.Wait(1000);
        _downloadSemaphore.Dispose();
    }

    private DownloadContext? FindContextByGlobalId(string globalId)
    {
        if (string.IsNullOrWhiteSpace(globalId))
        {
            return null;
        }

        var candidate = globalId.Trim();
        lock (_collectionLock)
        {
            var direct = _downloads.FirstOrDefault(d =>
                string.Equals(d.GlobalId, candidate, StringComparison.OrdinalIgnoreCase));
            if (direct != null)
            {
                return direct;
            }

            if (Guid.TryParse(candidate, out var parsed))
            {
                var idN = parsed.ToString("N");
                var idD = parsed.ToString("D");

                return _downloads.FirstOrDefault(d =>
                    d.Model.Id == parsed ||
                    string.Equals(d.GlobalId, idN, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(d.GlobalId, idD, StringComparison.OrdinalIgnoreCase));
            }

            return null;
        }
    }

    public async Task HardRetryTrack(string globalId)
    {
        var ctx = FindContextByGlobalId(globalId);

        if (ctx == null) return;

        _logger.LogInformation("🔄 Hard Retry triggered for {Title}", ctx.Model.Title);

        // 1. Reset State
        ctx.CancellationTokenSource?.Cancel();
        ctx.CancellationTokenSource = new CancellationTokenSource();
        ctx.RetryCount = 0;
        ctx.NextRetryTime = null;
        ctx.FailureReason = null; // Clear error message
        ctx.ErrorMessage = null;
        ctx.Model.IsUserPaused = false; // Reset user pause
        ctx.Model.Status = TrackStatus.Pending;
        ctx.Model.CompletedAt = null;
        ctx.Model.StalledReason = null;
        ctx.IsFinalizing = false; // Reset critical flags
        ctx.SearchAttempts.Clear(); // Clear bad search history
        
        lock (ctx.BlacklistedUsers)
        {
             ctx.BlacklistedUsers.Clear(); // Give peers a second chance
        }

        // 2. Clean Disk (Nuanced: Only delete .part if it's failed/stalled, keep .mp3 if it was somehow completed?)
        // Actually "Hard Retry" implies "Delete everything and start over".
        try 
        {
            // Fix: Use the component-based overload of GetTrackPath as Models.PlaylistTrack is not Models.Track
            var partPath = _pathProvider.GetTrackPath(ctx.Model.Artist, ctx.Model.Album, ctx.Model.Title, ctx.Model.Format ?? "mp3") + ".part";
            
            if (File.Exists(partPath)) 
            {
                 File.Delete(partPath);
                 _logger.LogInformation("Deleted stale .part file for retry: {Path}", partPath);
            }
            
            // If we have a resolved file path that is invalid, nuke it
            if (!string.IsNullOrEmpty(ctx.Model.ResolvedFilePath) && File.Exists(ctx.Model.ResolvedFilePath))
            {
                 // Check if it's actually valid? No, hard retry = force redownload.
                 File.Delete(ctx.Model.ResolvedFilePath);
                 ctx.Model.ResolvedFilePath = string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(
                ex,
                "Failed to clean up files during hard retry for {Title}. reason={Reason} correlationId={CorrelationId}",
                ctx.Model.Title,
                ex.Message,
                ctx.CorrelationId ?? "-");
        }

        // 3. Reset to Pending to trigger immediate pickup
        ctx.Model.Priority = 0; // VIP Pass
        await UpdateStateAsync(ctx, PlaylistTrackState.Pending);
        
        // 4. Force immediate queue refill and check
        _ = RefillQueueAsync();
        
        // 5. Force save to DB to ensure 'IsUserPaused=false' and Priority persists
        await _databaseService.UpdatePlaylistTrackUserPausedAsync(ctx.Model.Id, false);
        // We'll trust the SaveTrackToDb call within UpdateStateAsync to persist the Priority, 
        // or explicitly save here if needed.
    }

}
