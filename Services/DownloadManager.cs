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
using SLSKDONET.Services.AutoDownload;
using SLSKDONET.Utils;
using SLSKDONET.Services.Models;
using SLSKDONET.Services.Downloads;
using SLSKDONET.Data.Essentia;
using Microsoft.EntityFrameworkCore;
using SLSKDONET.Services.Repositories; // [NEW] Namespace
using SLSKDONET.Services.IO; // Added explicit using
using SLSKDONET.Events;


namespace SLSKDONET.Services;

/// <summary>
/// Orchestrates the download process for projects and individual tracks.
/// "The Conductor" - manages the state machine and queue.
/// Delegates search to DownloadDiscoveryService. Metadata enrichment of finished downloads is not
/// done here: Enrichment.MetadataEnrichmentOrchestrator picks them up from FileIngestionCompletedEvent.
/// </summary>
public partial class DownloadManager : INotifyPropertyChanged, IDisposable
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
    private readonly AutoSearchService _autoSearchService;
    private readonly PathProviderService _pathProvider;
    private readonly IFileWriteService _fileWriteService; // Phase 1A
    private readonly CrashRecoveryJournal _crashJournal;
    private readonly PeerReliabilityService _peerReliability;
    private readonly PrefetchVerifier _prefetchVerifier;
    private readonly INetworkHealthService _networkHealth;
    private readonly SLSKDONET.Services.Diagnostics.ITrackAuditLogger _auditLogger;
    private readonly SLSKDONET.Services.Library.AudioContentHasher? _contentHasher;
    private readonly SLSKDONET.Services.Library.Files.QualityUpgradeService? _qualityUpgradeService;


    private readonly List<IDisposable> _eventBusSubs = new();

    // Phase 2: Parallel Pre-Search Cache
    private readonly ConcurrentDictionary<string, Task<DownloadDiscoveryService.DiscoveryResult>> _preSearchTasks = new();
    // Issue #233: per-(peer, remote path) registry so two tracks from the same peer never share
    // progress/stall bookkeeping. A queued track is never registered.
    private readonly ActiveTransferRegistry _activeTransfers = new();

    // Epic 4.1: Per-peer connection gate — limits simultaneous TCP connections to any one
    // Soulseek peer to exactly 1.  Without this, 3 concurrent downloads from the same user
    // can trigger a remote-queue rejection chain and eventually a soft-ban.
    // Semaphores are created lazily and are never removed (peers are finite in number).
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _perPeerGates =
        new(StringComparer.OrdinalIgnoreCase);

    private SemaphoreSlim GetOrCreatePeerGate(string username) =>
        _perPeerGates.GetOrAdd(username, _ => new SemaphoreSlim(1, 1));

    // Opt-P3: Thread-safe RNG for retry jitter (avoids Random allocation per call)
    private static readonly Random _jitterRandom = new();

    // Playlist-level priority cache — loaded at startup, updated on SetJobPriorityAsync / ToggleFocusModeAsync.
    // Key = PlaylistJob.Id (same as PlaylistTrack.PlaylistId / SourcePlaylistId).
    // Value = effective priority int (0=Critical … 3=Low), already accounting for Focus Mode override.
    private readonly ConcurrentDictionary<Guid, int> _jobEffectivePriority = new();
    // Stores the base (non-focus) priority so toggling focus off restores it correctly.
    private readonly ConcurrentDictionary<Guid, int> _jobBasePriority = new();
    // Focus Mode state per job (true = temporarily forced to Critical).
    private readonly ConcurrentDictionary<Guid, bool> _jobFocused = new();
    // Manual drag-drop sort order per job.
    private readonly ConcurrentDictionary<Guid, int> _jobManualSortOrder = new();

    // Phase 2.5: Concurrency control with SemaphoreSlim throttling
    private CancellationTokenSource _globalCts = new();
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
    // Lock-ordering & deadlock audit (#316):
    // _collectionLock is the SOLE monitor guarding `_downloads` mutations and reads.
    // Rules enforced across all DownloadManager partial files:
    //   1. Never acquire another monitor while holding _collectionLock (per-track peer blacklists
    //      use DownloadContext's lock-free ConcurrentDictionary, not a secondary HashSet lock).
    //   2. Never publish to _eventBus, start background tasks, or invoke async/DB methods while
    //      holding _collectionLock — collect work items under the lock and dispatch after release.
    //   3. Keep critical sections minimal (in-memory list lookups/mutations only).
    private readonly object _collectionLock = new object();

    private const int LAZY_QUEUE_BUFFER_SIZE = 5000;
    private const int REFILL_THRESHOLD = 50;

    // Monotonically increasing counter incremented every time a track is dispatched to
    // Searching.  Failed tracks record RetryAfterSlot = _schedulerSlot + pendingCount so
    // the scheduler skips them until all currently-queued tracks have had at least one attempt.
    private long _schedulerSlot = 0;

    // Cap on how far a retry's RetryAfterSlot can be pushed into the future. Without this, a
    // retry deferred by "however many tracks are currently pending" scales disastrously with
    // library size: at 500+ pending tracks (a large multi-playlist download queue is completely
    // normal here), a single failed search could defer its retry by 500+ scheduler slots — and
    // since most of a big batch fails its first attempt at roughly the same time, nearly the
    // whole queue ends up gated behind the same huge future slot simultaneously, starving the
    // engine down to whatever tiny handful of never-yet-attempted tracks remain (confirmed live:
    // "524 tracks waiting, only 1 ever searching" with dozens of distinct tracks all logging
    // "eligible after slot 520" while the scheduler slot climbed roughly 1 per dispatch). The
    // intent — "don't let a just-failed track immediately re-compete with fresh candidates" — only
    // needs a modest push, not literally the entire queue's length.
    private const long MAX_RETRY_SLOT_DEFERRAL = 25;

    // Expose read-only copy for internal checks
    public IReadOnlyList<DownloadContext> ActiveDownloads
    {
        get { lock(_collectionLock) { return _downloads.ToList(); } }
    }

    // Expose download directory from config
    public string? DownloadDirectory => _config.DownloadDirectory;

    /// <summary>The deterministic LF3 staging .part path for a context — survives a restart (#450).</summary>
    private string StagingPartPathFor(DownloadContext ctx) =>
        DownloadStagingPath.GetPartPath(DownloadDirectory ?? "Downloads", ctx.Model.TrackUniqueHash ?? ctx.GlobalId);

    /// <summary>Whether a transfer is currently in flight against this peer — used to gate features that should only react to peers we're actively downloading from (e.g. auto-solving their "reply to unlock" gate-bot challenges).</summary>
    public bool HasActiveTransferWithPeer(string username) => _activeTransfers.HasAnyForUser(username);

    public int ActiveWorkerSlots => _maxActiveDownloads - _downloadSemaphore.CurrentCount;
    public int TotalWorkerSlots => _maxActiveDownloads;
    public bool SoulseekConnected => _soulseek.IsLoggedIn;

    /// <summary>
    /// True when the queue has nothing in it at all — not pending, searching, or downloading.
    /// <see cref="_downloads"/> already represents the full pipeline (see the Pending-state check
    /// in <c>QueueTracks</c>), so an empty list is a genuine "nothing going on" signal, not just
    /// "no active transfers." Used to gate background work (e.g. <c>GhostAcquisitionOrchestrator</c>)
    /// so it doesn't compete with the user's own in-progress searches/downloads for search bandwidth.
    /// </summary>
    public bool IsQueueIdle { get { lock (_collectionLock) { return _downloads.Count == 0; } } }
    public bool IsBackingOff => CurrentBackoffSeconds > 0;
    public int CurrentBackoffSeconds { get; private set; }

    /// <summary>Returns a frozen copy of a context's lock-free blacklist so the discovery service
    /// can read it while other threads mutate the live set (#316).</summary>
    private static HashSet<string> SnapshotBlacklistedUsers(DownloadContext ctx) =>
        ctx.SnapshotBlacklistedUsers();

    // Lets the circuit breaker's backoff wait wake up the instant Windows reports the network
    // came back (e.g. Wi-Fi reconnected), instead of always sitting out the current exponential
    // backoff step (up to 60s) even though the underlying cause already cleared. Released only on
    // IsAvailable transitions to true — see OnNetworkAvailabilityChanged.
    private readonly SemaphoreSlim _networkAvailabilitySignal = new(0);

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
        AutoSearchService autoSearchService,
        PathProviderService pathProvider,
        IFileWriteService fileWriteService,
        PrefetchVerifier prefetchVerifier,
        CrashRecoveryJournal crashJournal,
        PeerReliabilityService peerReliability,
        INetworkHealthService networkHealth,
        SLSKDONET.Services.Diagnostics.ITrackAuditLogger auditLogger, // Phase 1: Engine Overhaul
        SLSKDONET.Services.Library.AudioContentHasher? contentHasher = null,
        SLSKDONET.Services.Library.Files.QualityUpgradeService? qualityUpgradeService = null)

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
        _autoSearchService = autoSearchService;
        _pathProvider = pathProvider;
        _fileWriteService = fileWriteService;
        _crashJournal = crashJournal;
        _peerReliability = peerReliability;
        _prefetchVerifier = prefetchVerifier;
        _networkHealth = networkHealth;
        _auditLogger = auditLogger;
        _contentHasher = contentHasher;
        _qualityUpgradeService = qualityUpgradeService;

        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;

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

        // Phase 8: Automation Subscriptions — store IDisposables so they unsubscribe on Dispose()
        _eventBusSubs.Add(_eventBus.GetEvent<AutoDownloadTrackEvent>().Subscribe(OnAutoDownloadTrack));
        // #330: SoulseekConnected reads the adapter live, but nothing ever said it changed, so the Downloads header
        // kept showing whatever it read when the page was built ("Soulseek connected" while disconnected).
        _eventBusSubs.Add(_eventBus.GetEvent<SoulseekStateChangedEvent>().Subscribe(_ => OnPropertyChanged(nameof(SoulseekConnected))));
        _eventBusSubs.Add(_eventBus.GetEvent<ConnectionLifecycleStateChangedEvent>().Subscribe(_ => OnPropertyChanged(nameof(SoulseekConnected))));
        _eventBusSubs.Add(_eventBus.GetEvent<AutoDownloadUpgradeEvent>().Subscribe(OnAutoDownloadUpgrade));
        _eventBusSubs.Add(_eventBus.GetEvent<UpgradeAvailableEvent>().Subscribe(OnUpgradeAvailable));
        // Phase 6: Library Interactions
        _eventBusSubs.Add(_eventBus.GetEvent<ForceStartRequestEvent>().Subscribe(e => _ = ForceStartTrack(e.TrackGlobalId)));
        // Per-job priority state (_jobEffectivePriority/_jobBasePriority/_jobFocused/_jobManualSortOrder)
        // was never removed when a playlist/job was deleted — every job ever created kept 4
        // dictionary entries forever. Clean up on the existing project-deleted event.
        _eventBusSubs.Add(_eventBus.GetEvent<ProjectDeletedEvent>().Subscribe(e => RemoveJobPriorityState(e.ProjectId)));

        // Phase 12: Adapter Event Subscriptions
        _soulseek.DownloadProgressChanged += OnDownloadProgressChanged;
        _soulseek.DownloadCompleted += OnDownloadCompleted;
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

                    // Soulseek.NET fixes MaximumConcurrentDownloads for the lifetime of the client
                    // connection (SoulseekClientOptionsPatch, used for all other live reconfiguration,
                    // deliberately excludes it) — this app-level semaphore change is immediate, but the
                    // actual number of simultaneous protocol-level transfers won't reach the new ceiling
                    // until the next Soulseek reconnect.
                    if (_soulseek.IsConnected)
                    {
                        _logger.LogInformation(
                            "Note: the higher concurrent-download limit will only reach its full effect at the protocol level after the next Soulseek reconnect.");
                    }
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

    public int MaxQueueWaitTimeMinutes => _config.MaxQueueWaitTimeMinutes;

    public async Task InitAsync()

    {
        if (_isHydrated) return;
        try
        {
            await _databaseService.InitAsync();

            // Phase 3C.5: Lazy Hydration - Only load active/history and a buffer of pending tracks

            // Load job priority cache BEFORE hydration so JobPriority is correctly stamped on all tracks
            await LoadJobPriorityCacheAsync();

            // 1. Load ONLY actually active tracks (Downloading, Searching, Stalled)
            // This prevents the "Global Leak" where 10,000 history items are hydrated on boot
            var activeTracks = await _databaseService.GetActiveTracksAsync();
            HydrateAndAddEntities(activeTracks);

            _logger.LogInformation("Hydrated {Count} active tracks", activeTracks.Count);

            // Issue #46: hydrate last 50 completed/failed so the Downloads page is
            // not empty after a restart.
            var recentHistory = await _databaseService.GetRecentCompletedAndFailedTracksAsync(50);
            HydrateAndAddEntities(recentHistory);
            _logger.LogInformation("Hydrated {Count} recent completed/failed tracks", recentHistory.Count);

            // PERFORMANCE FIX: Defer queue refilling until after startup
            // The ProcessQueueLoop will call RefillQueueAsync when needed,
            // which is now project-aware to prevent mass-activation.
            // await RefillQueueAsync(); // Removed global refill on init

            // Epic 4.1: Restore persisted download queue so pending tracks survive restarts.
            // LoadPersistedDownloadQueueTracksAsync filters to Status=Missing and prunes
            // stale rows, so we only hydrate valid pending work items.
            try
            {
                var persistedPending = await _databaseService.LoadPersistedDownloadQueueTracksAsync();
                if (persistedPending.Count > 0)
                {
                    HydrateAndAddEntities(persistedPending);
                    _logger.LogInformation("[QueueRestore] Restored {Count} pending tracks from persisted download queue", persistedPending.Count);
                }
            }
            catch (Exception queueEx)
            {
                _logger.LogError(queueEx, "[QueueRestore] Failed to restore persisted queue on startup — queue will be empty until re-imported");
            }


            // Issue #48: reset zombie downloads that were Active (in-progress) when
            // the app last crashed.  Without this, the journal accumulates stale
            // Active entries and those tracks appear stuck in the download page.
            try
            {
                await _crashJournal.InitAsync();
                var zombies = await _crashJournal.GetPendingCheckpointsAsync();
                int resetCount = 0;
                foreach (var cp in zombies.Where(c => c.OperationType == OperationType.Download))
                {
                    try
                    {
                        // Remove the journal entry so it won't trigger again. It used to be marked
                        // dead-letter and was never removed, so every download in flight at close
                        // inflated the Dashboard's "Failed retries" forever; the track itself is
                        // reset below and carries the retry.
                        await _crashJournal.CompleteCheckpointAsync(cp.Id);
                        // Reset the corresponding PlaylistTrack to Pending so it can be retried.
                        if (!string.IsNullOrEmpty(cp.TargetPath))
                            await _databaseService.ResetTrackToMissingByPathAsync(cp.TargetPath);
                        resetCount++;
                    }
                    catch (Exception checkpointEx)
                    {
                        // One bad checkpoint must not block the rest of the journal from being processed.
                        _logger.LogError(checkpointEx, "[CrashRecovery] Failed to reset zombie checkpoint {CheckpointId} ({Path}) — skipping.", cp.Id, cp.TargetPath);
                    }
                }
                if (resetCount > 0)
                    _logger.LogWarning("[CrashRecovery] Reset {Count} zombie downloads from previous session", resetCount);
                // Clean up the dead letters earlier versions left behind for the same reason.
                await _crashJournal.PurgeDeadLettersAsync(OperationType.Download);
            }
            catch (Exception journalEx)
            {
                _logger.LogError(journalEx, "[CrashRecovery] Failed to process recovery journal on startup");
            }

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
    /// <param name="userInitiated">
    /// True for an import the user confirmed in the preview: new rows get priority 0, which also
    /// bypasses the EnableAutoAcquireOnImport gate. Background syncs pass false (priority 10).
    /// </param>
    public async Task<Services.Import.ProjectQueueResult> QueueProject(PlaylistJob job, bool userInitiated = true)
    {
        var result = Services.Import.ProjectQueueResult.None;

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
                var existingRows = new List<PlaylistTrack>();
                try
                {
                    var existingJob = await _libraryService.FindPlaylistJobAsync(job.Id);
                    if (existingJob != null)
                    {
                        existingRows = await _libraryService.LoadPlaylistTracksAsync(existingJob.Id);
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to load existing tracks for gap analysis, proceeding cautiously");
                }

                // Same identity rules the import preview used to label rows Owned / In playlist
                // (Services/Import/ImportMatching.cs), so the commit does what the preview said.
                // Membership is matched by hash, the resolved library file's hash, Spotify id and
                // a loose artist|title key. Rows linked from the library are stored under the
                // library file's hash, so the old hash-only lookup never recognised them and
                // every re-import of the same playlist appended those songs again.
                var library = new Services.Import.LibraryMatchIndex(await _libraryService.LoadAllLibraryEntriesAsync());
                var membership = new Services.Import.PlaylistMembershipIndex(existingRows);

                _logger.LogInformation("Converting {OriginalTrackCount} OriginalTracks to PlaylistTracks (Existing: {ExistingCount})",
                    job.OriginalTracks.Count, existingRows.Count);

                var playlistTracks = new List<PlaylistTrack>();
                var seenInBatch = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var touchedExisting = new HashSet<Guid>();
                int idx = existingRows.Count + 1;
                int retried = 0;
                int skipped = 0;
                int added = 0;
                int linkedFromLibrary = 0;
                int reconciledExisting = 0;
                int invalidOrRepeated = 0;

                foreach (var track in job.OriginalTracks)
                {
                    var hash = track.UniqueHash;
                    if (string.IsNullOrWhiteSpace(hash) || !seenInBatch.Add(hash))
                    {
                        _logger.LogDebug("Skipping track '{Title}' - invalid/duplicate hash in current batch", track.Title);
                        invalidOrRepeated++;
                        continue;
                    }

                    var existingLibraryEntry = library.Resolve(track.Artist, track.Title, track.SpotifyTrackId, hash);
                    var existingTrack = membership.Find(hash, existingLibraryEntry?.UniqueHash, track.SpotifyTrackId, track.Artist, track.Title);

                    if (existingTrack != null)
                    {
                        if (!touchedExisting.Add(existingTrack.Id))
                        {
                            // Two incoming rows resolved to the same existing playlist row.
                            invalidOrRepeated++;
                            continue;
                        }

                        if (existingTrack.Status != TrackStatus.Downloaded && existingLibraryEntry != null)
                        {
                            existingTrack.Status = TrackStatus.Downloaded;
                            existingTrack.ResolvedFilePath = string.IsNullOrWhiteSpace(existingTrack.ResolvedFilePath)
                                ? existingLibraryEntry.FilePath
                                : existingTrack.ResolvedFilePath;
                            existingTrack.TrackUniqueHash = existingLibraryEntry.UniqueHash;
                            existingTrack.CompletedAt ??= DateTime.UtcNow;
                            existingTrack.SourcePlaylistId = job.Id;
                            existingTrack.SourcePlaylistName = job.SourceTitle;

                            // Persist status reconciliation for this existing row.
                            playlistTracks.Add(existingTrack);
                            reconciledExisting++;
                            continue;
                        }

                        if (Services.Import.PlaylistMembershipIndex.NeedsRetry(existingTrack))
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
                            // Already in this playlist and healthy (downloaded or still in flight).
                            skipped++;
                        }

                        continue;
                    }

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
                        AvailabilityState = shouldMarkDownloaded ? (existingLibraryEntry?.AvailabilityState ?? TrackAvailabilityState.Ready) : TrackAvailabilityState.Ghost,
                        SpotifyPlaylistId = track.SpotifyPlaylistId,
                        SpotifyUri = track.SpotifyUri,
                        TrackNumber = idx++,
                        AddedAt = DateTime.UtcNow,
                        CompletedAt = shouldMarkDownloaded ? DateTime.UtcNow : null,
                        // Priority 0 = explicit user import — bypasses the EnableAutoAcquireOnImport gate
                        // and places the track in the VIP download lane.  Background syncs use 10.
                        Priority = shouldMarkDownloaded || !userInitiated ? 10 : 0,
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
                    "Merge result for job {JobId}: {Added} new ({LinkedFromLibrary} linked from library), {Retried} retried, {ReconciledExisting} reconciled existing to downloaded, {Skipped} unchanged existing, {Invalid} invalid/repeated",
                    job.Id,
                    added,
                    linkedFromLibrary,
                    retried,
                    reconciledExisting,
                    skipped,
                    invalidOrRepeated);

                job.PlaylistTracks = playlistTracks;
                job.TotalTracks = existingRows.Count + added;
                result = new Services.Import.ProjectQueueResult(added, linkedFromLibrary, retried, reconciledExisting, skipped, invalidOrRepeated);
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

        return result;
    }

    /// <summary>
    /// Internal method to queue a list of individual tracks for processing (e.g. from an existing project or ad-hoc).
    /// </summary>

    private async Task LogIngestionLifecycleAsync(Guid playlistId, string action, object details)
    {
        try
        {
            await _databaseService.LogActivityAsync(IngestionActivityLogFactory.Create(playlistId, action, details));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to log ingestion lifecycle action {Action}", action);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));


    public void Dispose()
    {
        foreach (var sub in _eventBusSubs) sub.Dispose();
        _eventBusSubs.Clear();
        _soulseek.DownloadProgressChanged -= OnDownloadProgressChanged;
        _soulseek.DownloadCompleted -= OnDownloadCompleted;
        System.Net.NetworkInformation.NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
        _globalCts.Cancel();
        _globalCts.Dispose();
        _processingTask?.Wait(1000);
        _downloadSemaphore.Dispose();
        _networkAvailabilitySignal.Dispose();
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


}
