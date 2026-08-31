using System;
using System.Collections.Generic;
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
using SLSKDONET.Utils;

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
    private ObservableCollection<SelectableTrack> _importedTracks;
    private ObservableCollection<AlbumGroupViewModel> _albumGroups = new();
    private bool _isLoading;
    private string _statusMessage = "Ready to import";
    private int _selectedCount;

    // Cached for this session so fuzzy dedup doesn't reload the full library per track.
    private List<LibraryEntry>? _cachedLibraryEntries;

    // Normalized "artist title" key computed once per library entry, not once per (imported track x entry) pair.
    private List<(LibraryEntry Entry, string NormalizedKey)>? _normalizedLibraryEntries;

    // Background Spotify enrichment — cancelled when a new preview loads.
    private CancellationTokenSource? _enrichmentCts;

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
        set
        {
            if (_importedTracks != null)
                _importedTracks.CollectionChanged -= OnImportedTracksChanged;
            _importedTracks = value;
            if (_importedTracks != null)
                _importedTracks.CollectionChanged += OnImportedTracksChanged;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TrackCount));
        }
    }

    private void OnImportedTracksChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        OnPropertyChanged(nameof(TrackCount));
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
    public bool CanAddToLibrary => !IsLoading && SelectedCount > 0 && (!IsMergeMode || SelectedMergeTarget != null);

    public ICommand AddToLibraryCommand { get; }
    public ICommand SelectAllCommand { get; }
    public ICommand DeselectAllCommand { get; }
    public ICommand SelectMissingCommand { get; }
    public ICommand SelectMergeCandidatesCommand { get; }
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
        _importedTracks = new ObservableCollection<SelectableTrack>();
        _importedTracks.CollectionChanged += OnImportedTracksChanged;

        AddToLibraryCommand = new AsyncRelayCommand(AddToLibraryAsync, () => CanAddToLibrary);
        SelectAllCommand = new RelayCommand(SelectAll);
        DeselectAllCommand = new RelayCommand(DeselectAll);
        SelectMissingCommand = new RelayCommand(SelectMissing);
        SelectMergeCandidatesCommand = new AsyncRelayCommand(SelectMergeCandidatesAsync);
        CancelCommand = new RelayCommand(Cancel);
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
                    // Preserve raw strings for "⚠️ Cleaned" badge in preview UI
                    OriginalArtist = query.OriginalArtist,
                    OriginalTitle = query.OriginalTitle,
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

            // Check for duplicates asynchronously (exact + fuzzy)
            if (_libraryService != null)
            {
                try
                {
                    _cachedLibraryEntries ??= await _libraryService.LoadAllLibraryEntriesAsync();
                    var normalizedEntries = await GetNormalizedLibraryEntriesAsync();
                    foreach (var track in tempTracks)
                        ApplyLibraryDuplicateCheck(track, _cachedLibraryEntries, normalizedEntries);
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
            // Enrich tracks in background: fills in CanonicalDuration, cover art, BPM, key, etc.
            // Runs as a background fire-and-forget so it doesn't block the preview UI.
            // For paste imports this is the only chance to get Spotify metadata before download.
            if (_metadataService != null)
            {
                _enrichmentCts?.Cancel();
                _enrichmentCts = new CancellationTokenSource();
                var token = _enrichmentCts.Token;
                _ = Task.Run(() => EnrichTracksInBackgroundAsync(ImportedTracks.Select(st => st.Model).ToList(), token), token);
            }
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
    private bool _isMergeTargetLoading;

    /// <summary>
    /// URL identifying the original source, persisted onto the created <see cref="PlaylistJob"/>.
    /// Initially set from the raw import input; providers that detect a more specific URL inside
    /// their content (e.g. a 1001Tracklists backlink found inside a pasted tracklist) can override
    /// it post-hoc via <see cref="UpdateSourceUrl"/> once parsing completes.
    /// </summary>
    public string? SourceUrl
    {
        get => _sourceUrl;
        private set { _sourceUrl = value; OnPropertyChanged(); }
    }

    /// <summary>Overrides the source URL with a more specific one detected during streaming.</summary>
    public void UpdateSourceUrl(string sourceUrl)
    {
        if (!string.IsNullOrWhiteSpace(sourceUrl))
            SourceUrl = sourceUrl;
    }

    public ObservableCollection<MergeTargetOptionViewModel> MergeTargets { get; } = new();

    private MergeTargetOptionViewModel? _selectedMergeTarget;

    /// <summary>
    /// The playlist currently picked in the merge-target dropdown. Changing this while
    /// <see cref="IsMergeMode"/> is on immediately re-applies row selection against the new
    /// target (equivalent to the old "Use As Merge Target" button click) — a passive choice
    /// doesn't need a separate confirm step.
    /// </summary>
    public MergeTargetOptionViewModel? SelectedMergeTarget
    {
        get => _selectedMergeTarget;
        set
        {
            if (_selectedMergeTarget == value) return;
            _selectedMergeTarget = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CommitButtonLabel));
            OnPropertyChanged(nameof(CanAddToLibrary));

            if (_isMergeMode && value != null)
            {
                _ = ApplyMergeTargetAsync(value);
            }
        }
    }

    public bool HasMergeTargets => MergeTargets.Count > 0;

    public bool IsMergeTargetLoading
    {
        get => _isMergeTargetLoading;
        set { _isMergeTargetLoading = value; OnPropertyChanged(); }
    }

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

    private bool _isMergeMode;

    /// <summary>
    /// The single "where do these tracks go?" choice: merge into <see cref="SelectedMergeTarget"/>,
    /// or create a brand-new playlist. Replaces the old three-separate-commit-button design
    /// (Create New Clone / Merge to Existing / Add to Library) — this only changes which target
    /// <see cref="AddToLibraryAsync"/> will commit to; it never commits by itself.
    /// </summary>
    public bool IsMergeMode
    {
        get => _isMergeMode;
        set
        {
            if (_isMergeMode == value) return;
            _isMergeMode = value;
            OnPropertyChanged();

            if (_isMergeMode)
            {
                if (SelectedMergeTarget != null)
                {
                    _ = ApplyMergeTargetAsync(SelectedMergeTarget);
                }
            }
            else
            {
                // Same reset "Create New Clone" used to do, minus the auto-commit.
                _targetJobId = Guid.NewGuid();
                _existingJob = null;
                IsDuplicate = false;
                ExistingTrackCount = 0;
                SelectMissing();
            }

            OnPropertyChanged(nameof(CommitButtonLabel));
            OnPropertyChanged(nameof(CanAddToLibrary));
        }
    }

    /// <summary>Drives the footer commit button's text so it always says what it's about to do.</summary>
    public string CommitButtonLabel =>
        !IsMergeMode
            ? "Add to Library"
            : SelectedMergeTarget != null
                ? $"Merge into '{SelectedMergeTarget.Title}'"
                : "Select a playlist to merge into";

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
        MergeTargets.Clear();
        SelectedMergeTarget = null;
        OnPropertyChanged(nameof(HasMergeTargets));

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

        // Seed the destination toggle to match — bypasses IsMergeMode's setter side effects
        // (which would reset _targetJobId/_existingJob) since the fields above are already
        // correctly set up for the initial state. SelectedMergeTarget isn't loaded yet
        // (LoadMergeTargetsAsync below fills it in), so there's nothing to react to yet either.
        _isMergeMode = _existingJob != null;
        OnPropertyChanged(nameof(IsMergeMode));
        OnPropertyChanged(nameof(CommitButtonLabel));

        _ = LoadMergeTargetsAsync(sourceType);
    }

    /// <summary>
    /// Switches the merge destination to <paramref name="target"/> and re-applies row selection
    /// against it — used both when the user picks a different playlist from the dropdown and
    /// when they flip the destination toggle into merge mode. Never commits anything itself.
    /// </summary>
    private async Task ApplyMergeTargetAsync(MergeTargetOptionViewModel target)
    {
        _existingJob = target.Job;
        _targetJobId = _existingJob.Id;
        IsDuplicate = true;
        ExistingTrackCount = _existingJob.TotalTracks;

        await SelectMergeCandidatesAsync();
        OnPropertyChanged(nameof(CommitButtonLabel));
        OnPropertyChanged(nameof(CanAddToLibrary));
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
                // Preserve raw strings for "⚠️ Cleaned" badge in preview UI
                OriginalArtist = query.OriginalArtist,
                OriginalTitle = query.OriginalTitle,
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
                     _cachedLibraryEntries ??= await _libraryService.LoadAllLibraryEntriesAsync();
                     var normalizedEntries = await GetNormalizedLibraryEntriesAsync();
                     ApplyLibraryDuplicateCheck(track, _cachedLibraryEntries, normalizedEntries);
                 }
                 catch (Exception ex)
                 {
                     _logger.LogWarning(ex, "Library duplicate-check failed for streamed track '{Title}' — skipping dedup for this track", track.Title);
                 }
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

             // Auto-select strategy:
             // - Fresh imports: select tracks not already in library.
             // - Duplicate imports: defer to playlist-aware merge selection so larger set merges stay accurate.
             if (!IsDuplicate)
             {
                 foreach (var selectable in enrichedTracks)
                 {
                     if (!selectable.IsInLibrary)
                         selectable.IsSelected = true;
                 }
             }

             // IsLoading is NOT cleared here — this runs once per streamed batch (Spotify
             // playlists page in, e.g., chunks of ~100), not once per import. Confirmed live:
             // clearing it here hid the loading spinner and re-enabled "Add to Library"/"Merge"
             // after just the first batch, while a large playlist (~1800 tracks) was still
             // streaming in the background — nothing stopped the user from committing an
             // incomplete import. Only ImportOrchestrator.StreamPreviewAsync's own finally block,
             // which runs once the whole await-foreach loop is exhausted, clears it now.
             StatusMessage = $"Loading tracks… ({ImportedTracks.Count} so far)";
             UpdateSelectedCount();
         });

         if (IsDuplicate)
         {
             await SelectMergeCandidatesAsync();
         }

         // Kick off background enrichment for any tracks that don't yet have Spotify metadata
         // (always the case for paste imports; Spotify imports are already enriched via API).
         if (_metadataService != null)
         {
             var needsEnrichment = enrichedTracks
                 .Select(s => s.Model)
                 .Where(t => string.IsNullOrEmpty(t.SpotifyTrackId))
                 .ToList();

             if (needsEnrichment.Count > 0)
             {
                 _enrichmentCts?.Cancel();
                 _enrichmentCts = new CancellationTokenSource();
                 var token = _enrichmentCts.Token;
                 _ = Task.Run(() => EnrichTracksInBackgroundAsync(needsEnrichment, token), token);
             }
         }
    }

    private async Task SelectMergeCandidatesAsync()
    {
        if (!IsDuplicate || _existingJob == null || _libraryService == null)
        {
            SelectMissing();
            return;
        }

        try
        {
            var existingTracks = await _libraryService.LoadPlaylistTracksAsync(_existingJob.Id);
            var existingByHash = existingTracks
                .Where(t => !string.IsNullOrWhiteSpace(t.TrackUniqueHash))
                .GroupBy(t => t.TrackUniqueHash, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

            // Off-thread when called from the background Spotify streaming path
            // (AddTracksToPreviewAsync) — the DB load above is fine off the UI thread, but the
            // IsSelected/StatusMessage mutations below are bound to the UI and threw "Call from
            // invalid thread" without this.
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                int added = 0;
                int retried = 0;
                int skipped = 0;

                foreach (var selectable in ImportedTracks)
                {
                    var hash = selectable.Model.UniqueHash;

                    if (string.IsNullOrWhiteSpace(hash))
                    {
                        selectable.IsSelected = true;
                        added++;
                        continue;
                    }

                    if (!existingByHash.TryGetValue(hash, out var existing))
                    {
                        selectable.IsSelected = true;
                        added++;
                        continue;
                    }

                    if (existing.Status == TrackStatus.Failed || existing.Status == TrackStatus.OnHold)
                    {
                        selectable.IsSelected = true;
                        retried++;
                        continue;
                    }

                    selectable.IsSelected = false;
                    skipped++;
                }

                UpdateSelectedCount();
                StatusMessage = $"Merge ready: {added} new, {retried} retry, {skipped} already healthy.";
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to compute merge candidates; falling back to Select Missing");
            SelectMissing();
        }
    }

    private async Task LoadMergeTargetsAsync(string sourceType)
    {
        if (_libraryService == null)
        {
            return;
        }

        try
        {
            IsMergeTargetLoading = true;

            var allJobs = await _libraryService.LoadAllPlaylistJobsAsync();
            var candidates = allJobs
                .Where(j => !j.IsDeleted)
                .Where(j => string.IsNullOrWhiteSpace(sourceType) || j.SourceType.Equals(sourceType, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(j => j.CreatedAt)
                .Take(50)
                .ToList();

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                MergeTargets.Clear();
                foreach (var job in candidates)
                {
                    MergeTargets.Add(new MergeTargetOptionViewModel(job));
                }

                OnPropertyChanged(nameof(HasMergeTargets));

                if (_existingJob != null)
                {
                    SelectedMergeTarget = MergeTargets.FirstOrDefault(m => m.Job.Id == _existingJob.Id);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load merge target candidates");
        }
        finally
        {
            IsMergeTargetLoading = false;
        }
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

    /// Enriches PlaylistTrack records already persisted in the DB (for paste imports queued
    /// before enrichment could finish in the preview). Runs in background after QueueProject.
    private async Task EnrichPersistedTracksAsync(Guid jobId)
    {
        try
        {
            await Task.Delay(800); // Let QueueProject DB writes settle
            var tracks = await _libraryService!.LoadPlaylistTracksAsync(jobId);
            var needsEnrichment = tracks.Where(t => string.IsNullOrEmpty(t.SpotifyTrackId)).ToList();
            if (needsEnrichment.Count == 0) return;

            _logger.LogInformation("Post-queue enrichment: {Count} tracks need Spotify metadata", needsEnrichment.Count);
            int enriched = 0;
            foreach (var pt in needsEnrichment)
            {
                try
                {
                    if (await _metadataService!.EnrichTrackAsync(pt))
                    {
                        await _libraryService.UpdatePlaylistTrackAsync(pt);
                        enriched++;
                    }
                    await Task.Delay(300); // Respect Spotify rate limit
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Post-queue enrichment failed for {Artist} - {Title}", pt.Artist, pt.Title);
                }
            }
            _logger.LogInformation("Post-queue enrichment complete: {Enriched}/{Total} tracks enriched for job {JobId}", enriched, needsEnrichment.Count, jobId);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Post-queue enrichment task failed for job {JobId}", jobId);
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
            var queued = await HandlePlaylistJobAddedAsync(job);
            if (!queued)
            {
                // HandlePlaylistJobAddedAsync already set StatusMessage/logged the specific
                // failure — don't fire AddedToLibrary or navigate away as if this succeeded,
                // which previously left the user looking at an empty Library while the actual
                // error sat unseen on the screen they'd just been navigated off of.
                _logger.LogWarning("[IMPORT TRACE] QueueProject failed for job {JobId} — not firing AddedToLibrary", job.Id);
                return;
            }

            _logger.LogInformation("[IMPORT TRACE] Step 2: HandlePlaylistJobAddedAsync completed, firing AddedToLibrary event");

            // Post-queue enrichment: for paste imports without Spotify data, enrich PlaylistTracks
            // in DB now that they're persisted. Runs in background so it doesn't block navigation.
            if (_metadataService != null && _libraryService != null)
            {
                var jobIdToEnrich = job.Id;
                _ = Task.Run(() => EnrichPersistedTracksAsync(jobIdToEnrich));
            }

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
        // Called both from a UI-thread command (SelectMergeCandidatesCommand) and from the
        // background Spotify streaming path (AddTracksToPreviewAsync, off the UI thread once
        // resumed past an await with no SynchronizationContext) — mutating IsSelected directly
        // from that second caller threw "Call from invalid thread" against a bound checkbox.
        // Dispatcher.UIThread.Post is safe to call from either thread.
        Avalonia.Threading.Dispatcher.UIThread.Post(() =>
        {
            foreach (var track in ImportedTracks)
            {
                track.IsSelected = !track.IsInLibrary;
            }
            UpdateSelectedCount();
        });
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
    /// <returns>False if queueing failed — callers must not treat this as a successful import (no <c>AddedToLibrary</c> event, no navigation) when it returns false.</returns>
    public async Task<bool> HandlePlaylistJobAddedAsync(PlaylistJob job)
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
            return true;
        }
        catch (Exception ex)
        {
            StatusMessage = $"Error adding to library: {ex.Message}";
            _logger.LogError(ex, "Failed to handle PlaylistJob addition");
            return false;
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

    // Thresholds for library duplicate detection:
    // ≥88% → auto-mark as already in library (skip download)
    // 75–88% → flag as "possible duplicate" for user review
    private const double AutoDedupThreshold = 0.88;
    private const double WarnDuplicateThreshold = 0.75;

    /// <summary>
    /// Lazily builds (and caches) the normalized "artist title" key for every library entry once
    /// per import session, instead of re-normalizing every entry for every imported track.
    /// </summary>
    private async Task<List<(LibraryEntry Entry, string NormalizedKey)>> GetNormalizedLibraryEntriesAsync()
    {
        if (_normalizedLibraryEntries != null)
            return _normalizedLibraryEntries;

        _cachedLibraryEntries ??= await _libraryService!.LoadAllLibraryEntriesAsync();
        _normalizedLibraryEntries = _cachedLibraryEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.Artist) && !string.IsNullOrWhiteSpace(e.Title))
            .Select(e => (e, StringDistanceUtils.Normalize($"{e.Artist} {e.Title}")))
            .ToList();
        return _normalizedLibraryEntries;
    }

    /// <summary>
    /// Checks a track against the cached library entries using exact hash first, then fuzzy
    /// matching as a fallback.  Sets <see cref="Track.IsInLibrary"/> and/or
    /// <see cref="Track.IsPossibleDuplicate"/> accordingly.
    /// </summary>
    private static void ApplyLibraryDuplicateCheck(Track track, List<LibraryEntry> allEntries, List<(LibraryEntry Entry, string NormalizedKey)> normalizedEntries)
    {
        // Fast path: exact hash
        var exactEntry = allEntries.FirstOrDefault(e =>
            string.Equals(e.UniqueHash, track.UniqueHash, StringComparison.OrdinalIgnoreCase));
        if (exactEntry != null)
        {
            track.IsInLibrary = true;
            return;
        }

        // No artist/title → can't do fuzzy
        if (string.IsNullOrWhiteSpace(track.Artist) || string.IsNullOrWhiteSpace(track.Title))
            return;

        var inputKey = StringDistanceUtils.Normalize($"{track.Artist} {track.Title}");
        LibraryEntry? bestEntry = null;
        double bestScore = 0;

        foreach (var (entry, candidateKey) in normalizedEntries)
        {
            var score = StringDistanceUtils.GetNormalizedMatchScore(inputKey, candidateKey);
            if (score > bestScore)
            {
                bestScore = score;
                bestEntry = entry;
            }
        }

        if (bestEntry == null) return;

        if (bestScore >= AutoDedupThreshold)
        {
            track.IsInLibrary = true;
        }
        else if (bestScore >= WarnDuplicateThreshold)
        {
            track.IsPossibleDuplicate = true;
            track.FuzzyMatchScore = bestScore;
            track.FuzzyMatchArtist = bestEntry.Artist;
            track.FuzzyMatchTitle = bestEntry.Title;
        }
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

public class MergeTargetOptionViewModel
{
    public MergeTargetOptionViewModel(PlaylistJob job)
    {
        Job = job;
    }

    public PlaylistJob Job { get; }
    public string Title => Job.SourceTitle;
    public string Subtitle => $"{Job.SourceType} • {Job.TotalTracks} tracks";
    public string Display => $"{Job.SourceTitle} ({Job.TotalTracks} tracks)";
}
