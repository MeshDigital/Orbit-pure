using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Events;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2.5 & 12.6: Unified Track ViewModel for Download Center and Lists.
/// Implements "Smart Component" architecture - self-managing state via EventBus.
/// </summary>
public class UnifiedTrackViewModel : ReactiveObject, IDisplayableTrack, IDisposable
{
    private readonly DownloadManager _downloadManager;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCache;
    private readonly ILibraryService _libraryService;
    private readonly CompositeDisposable _disposables = new();
    private string? _discoveryReasonOverride;

    private bool _hasPerformedHeadCheck5;
    private bool _hasPerformedHeadCheck15;

    // Core Data
    public PlaylistTrack Model { get; }
    
    // New: Raw Speed for Aggregation
    private long _downloadSpeed;
    public long DownloadSpeed 
    { 
        get => _downloadSpeed; 
        set => this.RaiseAndSetIfChanged(ref _downloadSpeed, value); 
    }

    private string? _peerName;
    public string? PeerName
    {
        get => _peerName;
        set => this.RaiseAndSetIfChanged(ref _peerName, value);
    }
    
    // Phase 12.3: Selection State
    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set => this.RaiseAndSetIfChanged(ref _isSelected, value);
    }
    
    
    private string? _crossProjectReference;
    private bool _synergyLoaded;  // Guard: ensures at most one DB lookup per ViewModel lifetime

    public string? CrossProjectReference
    {
        get => _crossProjectReference;
        set {
            this.RaiseAndSetIfChanged(ref _crossProjectReference, value);
            this.RaisePropertyChanged(nameof(HasCrossProjectReference));
        }
    }

    public bool HasCrossProjectReference => !string.IsNullOrEmpty(CrossProjectReference);

    // Cues Support
    private List<OrbitCue> _cues = new();
    public IEnumerable<OrbitCue> Cues => _cues;
    public IEnumerable<OrbitCue> OrbitCues => _cues;

    public UnifiedTrackViewModel(
        PlaylistTrack model, 
        DownloadManager downloadManager, 
        IEventBus eventBus,
        ArtworkCacheService artworkCache,
        ILibraryService libraryService)
    {
        Model = model ?? throw new ArgumentNullException(nameof(model));
        _downloadManager = downloadManager ?? throw new ArgumentNullException(nameof(downloadManager));
        _eventBus = eventBus ?? throw new ArgumentNullException(nameof(eventBus));
        _artworkCache = artworkCache ?? throw new ArgumentNullException(nameof(artworkCache));
        _libraryService = libraryService ?? throw new ArgumentNullException(nameof(libraryService));

        // Initialize State from Model
        _state = (PlaylistTrackState)model.Status; // Best effort mapping if simple cast works, otherwise logic needed
        // Fix: Model.Status is TrackStatus enum, State is PlaylistTrackState.
        // We'll trust the caller (DownloadCenter) to set initial state or wait for event.
        // But for display, we map roughly:
        if (model.Status == TrackStatus.Downloaded) _state = PlaylistTrackState.Completed;
        else if (model.Status == TrackStatus.Failed) _state = PlaylistTrackState.Failed;
        else _state = PlaylistTrackState.Pending;

        // Parse Cues
        if (!string.IsNullOrEmpty(Model.CuePointsJson))
        {
            try 
            {
                _cues = System.Text.Json.JsonSerializer.Deserialize<List<OrbitCue>>(Model.CuePointsJson) ?? new List<OrbitCue>();
            }
            catch { _cues = new List<OrbitCue>(); }
        }

        // Initialize Commands
        PlayCommand = ReactiveCommand.Create(PlayTrack, this.WhenAnyValue(x => x.IsCompleted));
        
        RevealFileCommand = ReactiveCommand.Create(() => 
        {
            if (!string.IsNullOrEmpty(Model.ResolvedFilePath))
            {
                 _eventBus.Publish(new RevealFileRequestEvent(Model.ResolvedFilePath));
            }
        }, this.WhenAnyValue(x => x.IsCompleted));

        AddToProjectCommand = ReactiveCommand.Create(() => 
        {
            // Optimistic UI: immediately collapse the synergy badge so the user sees instant
            // "success" feedback. The actual library addition happens via the event handler.
            CrossProjectReference = null;
            _eventBus.Publish(new AddToProjectRequestEvent(new[] { Model }));
        }, this.WhenAnyValue(x => x.IsCompleted, x => x.HasCrossProjectReference, (c, s) => c || s));

        PauseCommand = ReactiveCommand.CreateFromTask(async () => 
            await _downloadManager.PauseTrackAsync(GlobalId),
            this.WhenAnyValue(x => x.IsActive));

        ResumeCommand = ReactiveCommand.CreateFromTask(async () => 
            await _downloadManager.ResumeTrackAsync(GlobalId),
            this.WhenAnyValue(x => x.State, s => s == PlaylistTrackState.Paused));

        CancelCommand = ReactiveCommand.Create(() => 
            _downloadManager.CancelTrack(GlobalId),
            this.WhenAnyValue(x => x.IsActive));

        RetryCommand = ReactiveCommand.Create(() => 
            _downloadManager.HardRetryTrack(GlobalId),
            this.WhenAnyValue(x => x.IsFailed, x => x.IsStalled, (f, s) => f || s));

        ForceStartCommand = ReactiveCommand.CreateFromTask(async () => 
            await _downloadManager.ForceStartTrack(GlobalId),
            this.WhenAnyValue(x => x.State, s => s == PlaylistTrackState.Pending || s == PlaylistTrackState.Stalled || s == PlaylistTrackState.Paused));
            
        ForceDownloadIgnoreGuardsCommand = ReactiveCommand.CreateFromTask(async () => 
            await _downloadManager.ForceDownloadIgnoreGuardsAsync(GlobalId),
            this.WhenAnyValue(x => x.IsFailed));
            
        SearchAgainCommand = ReactiveCommand.Create(() => 
        {
            _eventBus.Publish(new ManualSearchRequestEvent(Model));
            _downloadManager.CancelTrack(GlobalId); // Cancel current if any and prepare for new search
        }, this.WhenAnyValue(x => x.IsFailed));
            
        CleanCommand = ReactiveCommand.CreateFromTask(async () =>
        {
             // Handled by parent collection usually, but could arguably be here if we had a Delete service method
             // For now, this command might just be a placeholder or call a service to remove self
             await _downloadManager.DeleteTrackFromDiskAndHistoryAsync(GlobalId);
        }, this.WhenAnyValue(x => x.IsCompleted, x => x.IsFailed, (c, f) => c || f));

        // Subscribe to Events with Rx Scheduler for Thread Safety
        _eventBus.GetEvent<TrackStateChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnStateChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackProgressChangedEvent>()
            .Where(e => e.TrackGlobalId == GlobalId)
            .Sample(TimeSpan.FromMilliseconds(250)) // Throttle: ~4 events/sec to prevent UI thread starvation
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnProgressChanged)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<TrackMetadataUpdatedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnMetadataUpdated)
            .DisposeWith(_disposables);

        // Fix: Subscribe to granular search status events for live console updates
        _eventBus.GetEvent<TrackDetailedStatusEvent>()
            .Where(e => e.TrackHash == GlobalId)
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnDetailedStatus)
            .DisposeWith(_disposables);
            
         // Initialize sliding window for speed
         _lastProgressTime = DateTime.MinValue;

         // Initialize Ghost Stall Timer (active UI polling for inactive downloads)
         // Checks every second if an active download has stalled
         _ghostStallTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, GhostStallCheck_Tick);
         _ghostStallTimer.Start();
         Disposable.Create(() => _ghostStallTimer.Stop()).DisposeWith(_disposables);

         // Phase 0: Load artwork via Proxy
         _artwork = new ArtworkProxy(_artworkCache, Model.AlbumArtUrl);
         
         FindSimilarCommand = ReactiveCommand.Create(FindSimilar);
         FindSimilarAiCommand = ReactiveCommand.Create(FindSimilarAi);
         FilterByVibeCommand = ReactiveCommand.Create(() => 
         {
             if (!string.IsNullOrEmpty(DetectedSubGenre))
             {
                 _eventBus.Publish(new SearchRequestedEvent(DetectedSubGenre));
             }
         });

         ViewAllSearchResultsCommand = ReactiveCommand.Create(() => 
         {
             _eventBus.Publish(new SearchRequestedEvent($"{Model.Artist} {Model.Title}"));
         });

         BumpToTopCommand = ReactiveCommand.Create(() => 
         {
             _downloadManager.BumpTrackToTop(GlobalId);
         }, this.WhenAnyValue(x => x.State, s => s == PlaylistTrackState.Pending || s == PlaylistTrackState.Paused || s == PlaylistTrackState.Stalled));

        ViewLogCommand = ReactiveCommand.Create(() => 
        {
            // Phase 0.8: View Log Logic
            var log = string.Join("\n", RejectionDetails?.Select(r => $"{r.Rank}. [{r.ShortReason}] {r.Filename} ({r.Bitrate}kbps) @{r.Username}") ?? Enumerable.Empty<string>());
            System.Diagnostics.Debug.WriteLine($"[Diagnostic Log] {log}");
            
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow?.Clipboard?.SetTextAsync(log);
            }
        }, this.WhenAnyValue(x => x.HasRejectionDetails));

        CopyLogCommand = ReactiveCommand.CreateFromTask(async () => 
        {
            if (Avalonia.Application.Current?.ApplicationLifetime is Avalonia.Controls.ApplicationLifetimes.IClassicDesktopStyleApplicationLifetime desktop)
            {
                var log = string.Join("\n", RejectionDetails?.Select(r => $"{r.Rank}. [{r.ShortReason}] {r.Filename} ({r.Bitrate}kbps) @{r.Username}") ?? Enumerable.Empty<string>());
                if (desktop.MainWindow?.Clipboard != null)
                {
                    await desktop.MainWindow.Clipboard.SetTextAsync(log);
                }
            }
        }, this.WhenAnyValue(x => x.HasRejectionDetails));

        // Only run synergy check immediately if this track is already in a terminal failed state.
        // For Pending/Downloading tracks the check fires lazily via the State setter when
        // they first transition to Failed — avoiding a DB flood during bulk list hydration.
        if (IsFailed) CheckSynergyAsync();
    }

    private async void CheckSynergyAsync()
    {
        // Guard: only one lookup per ViewModel lifetime, and only for non-completed tracks.
        if (_synergyLoaded || IsCompleted) return;
        if (string.IsNullOrEmpty(ArtistName) || string.IsNullOrEmpty(TrackTitle)) return;

        _synergyLoaded = true; // Set before await so concurrent state changes can't double-fire

        try
        {
            var currentProjId = Model?.PlaylistId ?? Guid.Empty;
            var matches = await _libraryService.FindTrackInOtherProjectsAsync(ArtistName, TrackTitle, currentProjId);
            if (matches != null && matches.Any())
            {
                var others = matches
                    .Select(m => m.SourcePlaylistName)
                    .Where(n => !string.IsNullOrEmpty(n))
                    .Distinct()
                    .ToList();

                if (others.Any())
                {
                    // Marshal to UI thread — CheckSynergyAsync runs on a thread-pool thread
                    await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CrossProjectReference = string.Join(", ", others);
                    });
                }
            }
        }
        catch { /* Synergy is non-critical; swallow errors silently */ }
    }
    
    private void FindSimilar()
    {
        if (Model == null) return;
        _eventBus.Publish(new FindSimilarRequestEvent(Model, useAi: false));
    }

    private void FindSimilarAi()
    {
        if (Model == null) return;
        _eventBus.Publish(new FindSimilarRequestEvent(Model, useAi: true));
    }



    // IDisplayableTrack Implementation
    public string GlobalId => Model.TrackUniqueHash;
    public string ArtistName => !string.IsNullOrWhiteSpace(Model.Artist) ? Model.Artist : "Unknown Artist";
    public string TrackTitle => !string.IsNullOrWhiteSpace(Model.Title) ? Model.Title : "Unknown Title";
    public string AlbumName => !string.IsNullOrWhiteSpace(Model.Album) ? Model.Album : "Unknown Album";
    public string? AlbumArtUrl => Model.AlbumArtUrl;

    private ArtworkProxy _artwork;
    public ArtworkProxy Artwork => _artwork;
    
    // Legacy support: redirects to Proxy.Image (which triggers load)
    public Avalonia.Media.Imaging.Bitmap? ArtworkBitmap => _artwork?.Image;

    private PlaylistTrackState _state;
    public PlaylistTrackState State
    {
        get => _state;
        set { 
            if (_state != value)
            {
                this.RaiseAndSetIfChanged(ref _state, value);
                
                // Core state flags
                this.RaisePropertyChanged(nameof(StatusText));
                this.RaisePropertyChanged(nameof(StatusColor));
                this.RaisePropertyChanged(nameof(DetailedStatusText));
                this.RaisePropertyChanged(nameof(IsIndeterminate));
                this.RaisePropertyChanged(nameof(IsFailed));
                this.RaisePropertyChanged(nameof(IsActive));
                this.RaisePropertyChanged(nameof(IsWaiting));
                this.RaisePropertyChanged(nameof(IsSearching));
                this.RaisePropertyChanged(nameof(IsDownloading));
                this.RaisePropertyChanged(nameof(IsPaused));
                this.RaisePropertyChanged(nameof(IsStalled));
                this.RaisePropertyChanged(nameof(IsCompleted));
                
                // Action enablement
                this.RaisePropertyChanged(nameof(CanForceStart));
                this.RaisePropertyChanged(nameof(CanRetry));
                this.RaisePropertyChanged(nameof(CanResume));
                this.RaisePropertyChanged(nameof(CanBumpToTop));
                
                // Display properties
                this.RaisePropertyChanged(nameof(TechnicalSummary));
                this.RaisePropertyChanged(nameof(DownloadDurationDisplay));
                this.RaisePropertyChanged(nameof(HasBpm));
                this.RaisePropertyChanged(nameof(HasKey));
                this.RaisePropertyChanged(nameof(HasGenre));
                this.RaisePropertyChanged(nameof(IsHighRisk));
                this.RaisePropertyChanged(nameof(MatchConfidence));
                
                // Clear detailed status when leaving search state
                if (value != PlaylistTrackState.Searching)
                    DetailedSearchStatus = null;

                // Lazy synergy check: fire once when track first reaches a failed/cancelled
                // terminal state. This avoids the constructor-time DB flood during bulk hydration.
                if (IsFailed && !_synergyLoaded) CheckSynergyAsync();
            }
        }
    }

    public string StatusText => State switch
    {
        PlaylistTrackState.Completed => "Ready",
        PlaylistTrackState.Downloading => $"{(int)(Progress)}%",
        PlaylistTrackState.Searching => !string.IsNullOrEmpty(DetailedSearchStatus) 
            ? DetailedSearchStatus 
            : (SearchAttemptCount > 1 ? $"Searching... ({SearchAttemptCount})" : "Searching..."),
        PlaylistTrackState.Queued => "Queued",
        PlaylistTrackState.Failed => !string.IsNullOrEmpty(FailureReason) ? FailureReason : 
                                     (FailureEnum != DownloadFailureReason.None ? FailureEnum.ToDisplayMessage() : "Failed"),
        PlaylistTrackState.Paused => "Paused",
        PlaylistTrackState.Stalled => !string.IsNullOrEmpty(StalledReason) ? $"Stalled: {StalledReason}" : "Stalled (Waiting for Peer)",
        PlaylistTrackState.WaitingForConnection => "Waiting for Connection...",

        _ => State.ToString()
    };
    
    // Fix: Added StatusColor property for UI binding
    public Avalonia.Media.IBrush StatusColor => State switch
    {
        PlaylistTrackState.Completed => Avalonia.Media.Brushes.LimeGreen,
        PlaylistTrackState.Failed => Avalonia.Media.Brushes.OrangeRed,
        PlaylistTrackState.Cancelled => Avalonia.Media.Brushes.Gray,
        PlaylistTrackState.Downloading => Avalonia.Media.Brushes.Cyan,
        PlaylistTrackState.Searching => Avalonia.Media.Brushes.Yellow,
        PlaylistTrackState.Stalled => Avalonia.Media.Brushes.Orange,
        PlaylistTrackState.WaitingForConnection => Avalonia.Media.Brushes.DarkGray,
        _ => Avalonia.Media.Brushes.LightGray
    };

    // Fix: Detailed tooltip text
    public string DetailedStatusText => IsFailed 
        ? $"Failed: {FailureReason ?? "Unknown Error"}\n(Click Retry to search for a new peer)" 
        : StatusText;

    private double _progress;
    public double Progress
    {
        get => _progress;
        set => this.RaiseAndSetIfChanged(ref _progress, value);
    }

    public bool IsIndeterminate => State == PlaylistTrackState.Searching || State == PlaylistTrackState.Queued || State == PlaylistTrackState.WaitingForConnection;
    public bool IsFailed => State == PlaylistTrackState.Failed || State == PlaylistTrackState.Cancelled;
    public bool IsPaused => State == PlaylistTrackState.Paused;
    
    // Phase 6 & 9: Refined IsActive for "Direct Active" swimlane (strictly downloading/searching)
    public bool IsActive => State == PlaylistTrackState.Downloading || State == PlaylistTrackState.Searching || State == PlaylistTrackState.WaitingForConnection;

    // Phase 11: Specific activity flags
    public bool IsSearching => State == PlaylistTrackState.Searching;
    public bool IsDownloading => State == PlaylistTrackState.Downloading;
    
    // Phase 11.1: UI Helper flags
    public bool CanRetry => IsFailed || State == PlaylistTrackState.Stalled;
    public bool CanResume => State == PlaylistTrackState.Paused;
    public bool CanForceStart => State == PlaylistTrackState.Pending || State == PlaylistTrackState.Stalled || State == PlaylistTrackState.Paused;

    // Phase 12: Priority Control
    public bool CanBumpToTop => (State == PlaylistTrackState.Pending || State == PlaylistTrackState.Paused || State == PlaylistTrackState.Stalled) && !IsCompleted;

    // Phase 9: Helper for "Waiting" swimlane (strictly queued/pending, NOT searching)
    public bool IsWaiting => State == PlaylistTrackState.Pending || State == PlaylistTrackState.Queued;

    public bool IsCompleted => State == PlaylistTrackState.Completed;

    // Phase 10: Spectral audit warning
    public bool IsTranscoded => Model.IsTranscoded;

    // Phase 11.1: Restored Missing Animation Flags
    private bool _isAnalyzing;
    public bool IsAnalyzing
    {
        get => _isAnalyzing;
        set => this.RaiseAndSetIfChanged(ref _isAnalyzing, value);
    }

    private bool _isEnriching;
    public bool IsEnriching
    {
        get => _isEnriching;
        set => this.RaiseAndSetIfChanged(ref _isEnriching, value);
    }

    public void PreservedDiagnostics(DownloadFailureReason reason, string? message, System.Collections.Generic.List<SearchAttemptLog>? attempts)
    {
        FailureEnum = reason;
        FailureReason = message;
        if (attempts != null)
        {
            RejectionDetails = new System.Collections.ObjectModel.ObservableCollection<RejectedResult>(
                attempts.SelectMany(a => a.Top3RejectedResults?.Select(r => new RejectedResult { Username = r.Username, RejectionReason = r.RejectionReason }) ?? Enumerable.Empty<RejectedResult>())
            );
            HasRejectionDetails = RejectionDetails.Any();
        }
        
        this.RaisePropertyChanged(nameof(StatusText));
        this.RaisePropertyChanged(nameof(DetailedStatusText));
        this.RaisePropertyChanged(nameof(SearchAttemptCount));
    }

    private string? _failureReason;
    public string? FailureReason
    {
        get => _failureReason;
        set 
        {
            this.RaiseAndSetIfChanged(ref _failureReason, value);
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(DetailedStatusText));
        }
    }

    private DownloadFailureReason _failureEnum;
    public DownloadFailureReason FailureEnum
    {
        get => _failureEnum;
        set
        {
            this.RaiseAndSetIfChanged(ref _failureEnum, value);
            this.RaisePropertyChanged(nameof(FailureDisplayMessage));
            this.RaisePropertyChanged(nameof(FailureActionSuggestion));
        }
    }

    public string FailureDisplayMessage 
    {
        get
        {
            // Fix: If we have rejection details but no specific FailureEnum, it means the search found things but rejected them all.
            if (_hasRejectionDetails && FailureEnum == DownloadFailureReason.None)
            {
                return "Search Rejected";
            }
            return FailureEnum.ToDisplayMessage();
        }
    }
    
    public string FailureActionSuggestion => FailureEnum.ToActionableSuggestion();

    // Phase 0.5: Search Diagnostics
    private System.Collections.ObjectModel.ObservableCollection<RejectedResult>? _rejectionDetails;
    public System.Collections.ObjectModel.ObservableCollection<RejectedResult>? RejectionDetails
    {
        get => _rejectionDetails;
        set => this.RaiseAndSetIfChanged(ref _rejectionDetails, value);
    }

    private bool _hasRejectionDetails;
    public bool HasRejectionDetails
    {
        get => _hasRejectionDetails;
        set => this.RaiseAndSetIfChanged(ref _hasRejectionDetails, value);
    }

    // Phase 9: Search Diagnostics
    public int SearchAttemptCount => RejectionDetails?.Count ?? 0;

    public string RejectionSummary
    {
        get
        {
            if (RejectionDetails == null || !RejectionDetails.Any()) return "No detailed forensic data captured for this failure.";
            
            var groups = RejectionDetails
                .Where(x => !string.IsNullOrEmpty(x.ShortReason))
                .GroupBy(x => x.ShortReason)
                .Select(g => $"{g.Count()} {g.Key}");
            
            return $"Rejections: {string.Join(", ", groups)}";
        }
    }

    // Phase 10: Performance Monitoring - Total Download Duration
    public string? DownloadDurationDisplay
    {
        get
        {
            if (Model.SearchStartedAt == null) return null;
            
            var end = Model.CompletedAt ?? (IsActive ? DateTime.UtcNow : (DateTime?)null);
            if (end == null) return null;
            
            var duration = end.Value - Model.SearchStartedAt.Value;
            if (duration < TimeSpan.Zero) return null;

            return duration.TotalMinutes >= 1 
                ? $"{(int)duration.TotalMinutes}m {duration.Seconds:D2}s"
                : $"{duration.Seconds}s";
        }
    }

    public string CompletedAtDisplay => Model.CompletedAt?.ToString("g") ?? Model.AddedAt.ToString("g");

    private bool _isClearedFromDownloadCenter;
    public bool IsClearedFromDownloadCenter
    {
        get => _isClearedFromDownloadCenter;
        set => this.RaiseAndSetIfChanged(ref _isClearedFromDownloadCenter, value);
    }

    public string TechnicalSummary
    {
        get
        {
            // "Soulseek • 320kbps • 12MB • [Time]"
            var parts = new System.Collections.Generic.List<string>();
            parts.Add("Soulseek"); // Source (Static for now)
            
            if (Model.Bitrate.HasValue) 
            {
                 // Phase 0.6: Truth in UI
                 string prefix = IsCompleted ? "" : "Est. ";
                 parts.Add($"{prefix}{Model.Bitrate}kbps");
            }
            if (!string.IsNullOrEmpty(Model.Format)) parts.Add(Model.Format.ToUpper());
            
            if (_totalBytes > 0) 
                parts.Add($"{_totalBytes / 1024.0 / 1024.0:F1} MB");
                
            if (IsCompleted || IsFailed)
                parts.Add(CompletedAtDisplay); 
            else if (IsActive)
                parts.Add(SpeedDisplay);

            return string.Join(" • ", parts);
        }
    }

    private int ParsedSampleRateHz
    {
        get
        {
            var details = Model.QualityDetails;
            if (string.IsNullOrWhiteSpace(details)) return 0;

            var part = details.Split('|').FirstOrDefault(p => p.EndsWith("Hz", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(part)) return 0;

            var digits = new string(part.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var value) ? value : 0;
        }
    }

    private int ParsedBitDepth
    {
        get
        {
            var details = Model.QualityDetails;
            if (string.IsNullOrWhiteSpace(details)) return 0;

            var part = details.Split('|').FirstOrDefault(p => p.EndsWith("bit", StringComparison.OrdinalIgnoreCase));
            if (string.IsNullOrWhiteSpace(part)) return 0;

            var digits = new string(part.Where(char.IsDigit).ToArray());
            return int.TryParse(digits, out var value) ? value : 0;
        }
    }

    public bool HasQualityPill => !string.IsNullOrWhiteSpace(QualityPillText);

    public string QualityPillText
    {
        get
        {
            var format = (Model.Format ?? string.Empty).ToUpperInvariant();
            var bitrate = Model.Bitrate ?? 0;
            var sampleRate = ParsedSampleRateHz;
            var bitDepth = ParsedBitDepth;

            if (string.Equals(format, "FLAC", StringComparison.OrdinalIgnoreCase))
            {
                if (bitDepth > 0 || sampleRate > 0)
                {
                    var sampleKhz = sampleRate > 0 ? $"{sampleRate / 1000.0:F1}k" : "?k";
                    var depth = bitDepth > 0 ? $"{bitDepth}b" : "?b";
                    return $"FLAC {depth}/{sampleKhz}";
                }

                return bitrate > 0 ? $"FLAC {bitrate}kbps" : "FLAC";
            }

            if (bitrate > 0)
            {
                return $"{bitrate}kbps";
            }

            return string.Empty;
        }
    }

    // ── Beta 2026: Forensic Quality Pill ─────────────────────────────────────
    // Shown on every active download row. Driven by format, bitrate, and live speed.

    /// <summary>Short badge label: "🧪 FLAC", "⚠️ FAKE", "⚡ FAST", "● MP3", etc.</summary>
    public string ForensicBadgeText
    {
        get
        {
            if (Model.IsTranscoded) return "⚠️ FAKE";
            var fmt = (Model.Format ?? string.Empty).ToUpperInvariant();
            var bitrate = Model.Bitrate ?? 0;
            if (fmt == "FLAC" && bitrate >= 400) return "🧪 FLAC";
            if (IsDownloading && CurrentSpeedBytes > 1_048_576) return "⚡ FAST";
            if (fmt is "MP3" or "AAC" or "OGG") return $"● {fmt}";
            if (bitrate > 0) return $"{bitrate}kbps";
            return string.Empty; // No data yet — badge hidden via IsDownloading guard
        }
    }

    public string ForensicBadgeBackground
    {
        get
        {
            if (Model.IsTranscoded) return "#3C1F1F";
            var fmt = (Model.Format ?? string.Empty).ToUpperInvariant();
            var bitrate = Model.Bitrate ?? 0;
            if (fmt == "FLAC" && bitrate >= 400) return "#1A3028";
            if (IsDownloading && CurrentSpeedBytes > 1_048_576) return "#0E1D33";
            return "#222222";
        }
    }

    public string ForensicBadgeForeground
    {
        get
        {
            if (Model.IsTranscoded) return "#FF5252";
            var fmt = (Model.Format ?? string.Empty).ToUpperInvariant();
            var bitrate = Model.Bitrate ?? 0;
            if (fmt == "FLAC" && bitrate >= 400) return "#1DB954";
            if (IsDownloading && CurrentSpeedBytes > 1_048_576) return "#00BFFF";
            return "#888888";
        }
    }

    public string ForensicBadgeBorderColor
    {
        get
        {
            if (Model.IsTranscoded) return "#FF5252";
            var fmt = (Model.Format ?? string.Empty).ToUpperInvariant();
            var bitrate = Model.Bitrate ?? 0;
            if (fmt == "FLAC" && bitrate >= 400) return "#1DB954";
            if (IsDownloading && CurrentSpeedBytes > 1_048_576) return "#00BFFF";
            return "#333333";
        }
    }

    /// <summary>Full hover HUD: bitrate · sample rate · bit depth · format · peer · transcode flag.</summary>
    public string ForensicBadgeHud
    {
        get
        {
            var parts = new System.Collections.Generic.List<string>();
            if ((Model.Bitrate ?? 0) > 0) parts.Add($"{Model.Bitrate}kbps");
            var sr = ParsedSampleRateHz;
            if (sr > 0) parts.Add($"{sr / 1000.0:F1}kHz");
            var bd = ParsedBitDepth;
            if (bd > 0) parts.Add($"{bd}-bit");
            if (!string.IsNullOrEmpty(Model.Format)) parts.Add(Model.Format.ToUpperInvariant());
            if (Model.IsTranscoded) parts.Add("⚠️ LIKELY TRANSCODE");
            if (!string.IsNullOrEmpty(PeerName)) parts.Add($"Peer: {PeerName}");
            return parts.Count > 0 ? string.Join(" • ", parts) : "Technical data pending";
        }
    }
    // ─────────────────────────────────────────────────────────────────────────

    public bool IsFakeFlacWarning
    {
        get
        {
            if (!string.Equals(Model.Format, "flac", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return (Model.Bitrate ?? 0) < 400;
        }
    }

    public bool HasShieldSanitized => string.Equals(Model.SourceProvenance, "ShieldSanitized", StringComparison.OrdinalIgnoreCase);
    public string ShieldTooltip => "Search query sanitized by ProtocolHardeningService";
    
    // Phase 0.6: Truth in UI - Tech Specs are Estimates until verified
    public string TechSpecPrefix => IsCompleted ? "" : "Est. ";

    // Curation Hub Properties
    public double IntegrityScore => Model.QualityConfidence ?? 0.0;
    // Phase 0.6: Truth in UI
    public bool IsSecure => IsCompleted && IntegrityScore > 0.9 && !string.IsNullOrEmpty(Model.ResolvedFilePath);

    // Phase 19: Search 2.0 Tiers for Library
    public SLSKDONET.Models.SearchTier Tier => SLSKDONET.Models.SearchTier.Gold;

    public string TierBadge => string.Empty;

    public Avalonia.Media.IBrush TierColor => Avalonia.Media.Brushes.Gray;
    
    // Legacy mapping for backward compatibility if needed, otherwise replaced by Tier
    public string QualityIcon => string.Empty;
    public Avalonia.Media.IBrush QualityColor => Avalonia.Media.Brushes.Gray;
    
    // Match & High Risk Logic
    public double MatchConfidence => (Model.QualityConfidence ?? 0) * 100;
    
    public string MatchConfidenceColor => MatchConfidence switch
    {
        >= 90 => "#1DB954", // Spotify Green
        >= 70 => "#FFD700", // Gold/Yellow
        _ => "#E91E63"      // Pink/Red
    };

    public bool IsHighRisk => Model.IsFlagged && State != PlaylistTrackState.Searching && State != PlaylistTrackState.Queued && State != PlaylistTrackState.Pending;
    public string? FlagReason => Model.FlagReason;

    public string CurationIcon => Model.CurationConfidence switch
    {
        SLSKDONET.Data.Entities.CurationConfidence.Manual => "🛡️",
        SLSKDONET.Data.Entities.CurationConfidence.High => "🏅",
        SLSKDONET.Data.Entities.CurationConfidence.Medium => "🥈",
        SLSKDONET.Data.Entities.CurationConfidence.Low => "📉",
        _ => string.Empty
    };
    
    public Avalonia.Media.IBrush CurationColor => Model.CurationConfidence switch
    {
        SLSKDONET.Data.Entities.CurationConfidence.Manual => Avalonia.Media.Brushes.LimeGreen,
        SLSKDONET.Data.Entities.CurationConfidence.High => Avalonia.Media.Brushes.Gold,
        SLSKDONET.Data.Entities.CurationConfidence.Medium => Avalonia.Media.Brushes.Silver,
        SLSKDONET.Data.Entities.CurationConfidence.Low => Avalonia.Media.Brushes.OrangeRed,
        _ => Avalonia.Media.Brushes.Transparent
    };
    
    public string BpmDisplay => Model.BPM.HasValue ? $"{Model.BPM:0}" : "—";
    public string KeyDisplay
    {
        get
        {
            if (string.IsNullOrEmpty(Model.MusicalKey)) return "—";
            
            var camelot = Utils.KeyConverter.ToCamelot(Model.MusicalKey);
            // Show both: "G minor (6A)" or just "6A" if already in Camelot format
            if (camelot == Model.MusicalKey)
                return camelot; // Already Camelot
            
            return $"{Model.MusicalKey} ({camelot})";
        }
    }

    public string CamelotDisplay => !string.IsNullOrEmpty(Model.MusicalKey) ? Utils.KeyConverter.ToCamelot(Model.MusicalKey) : "—";
    public string YearDisplay => Model.ReleaseDate.HasValue ? Model.ReleaseDate.Value.Year.ToString() : "";
    
    // Technical Audio Display
    public string LoudnessDisplay => Model.Loudness.HasValue ? $"{Model.Loudness:F1} LUFS" : "—";
    public string TruePeakDisplay => Model.TruePeak.HasValue ? $"{Model.TruePeak:F1} dBTP" : "—";
    public string DynamicRangeDisplay => Model.DynamicRange.HasValue ? $"{Model.DynamicRange:F1} LU" : "—";

    public bool IsEnriched => Model.IsEnriched;
    public bool IsPrepared => Model.IsPrepared;
    public string? PrimaryGenre => Model.PrimaryGenre;
    public string? DiscoveryReason => Model.DiscoveryReason ?? _discoveryReasonOverride;
    public string DiscoveryBadgeText => DiscoveryReason switch
    {
        var reason when !string.IsNullOrWhiteSpace(reason) && reason.Contains("Fast lane", StringComparison.OrdinalIgnoreCase) => "FAST",
        var reason when !string.IsNullOrWhiteSpace(reason) && reason.Contains("Curated", StringComparison.OrdinalIgnoreCase) => "CURATED",
        var reason when !string.IsNullOrWhiteSpace(reason) && reason.Contains("Golden", StringComparison.OrdinalIgnoreCase) => "GOLD",
        _ => "MATCH"
    };
    public bool IsStalled => State == PlaylistTrackState.Stalled;
    public string? StalledReason => Model.StalledReason;
    public string? DetectedSubGenre => Model.DetectedSubGenre;
    public float? SubGenreConfidence => Model.SubGenreConfidence;

    // UI Layout Bools (For clean XAML)
    public bool HasBpm => Model.BPM > 0;
    public bool HasKey => !string.IsNullOrEmpty(Model.MusicalKey) && Model.MusicalKey != "—";
    public bool HasGenre => !string.IsNullOrEmpty(DetectedSubGenre) || !string.IsNullOrEmpty(PrimaryGenre);

    // Phase 2: In-Flight Forensics
    private string _forensicVerdict = "Initializing Probe...";
    public string ForensicVerdict 
    { 
        get => _forensicVerdict; 
        private set => this.RaiseAndSetIfChanged(ref _forensicVerdict, value); 
    }

    private string _bitrateLed = "○"; // LED States: ○ (off), ● (active), ⚠️ (warning), ✅ (good)
    public string BitrateLed { get => _bitrateLed; private set => this.RaiseAndSetIfChanged(ref _bitrateLed, value); }
    
    private string _keyLed = "○";
    public string KeyLed { get => _keyLed; private set => this.RaiseAndSetIfChanged(ref _keyLed, value); }
    
    private string _peakLed = "○";
    public string PeakLed { get => _peakLed; private set => this.RaiseAndSetIfChanged(ref _peakLed, value); }

    public string ForensicDetails => $"Verdict: {ForensicVerdict}\nBIT: {BitrateLed}\nKEY: {KeyLed}\nPEAK: {PeakLed}";

    // Phase 12.7: Vibe Color Mapping
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, Avalonia.Media.IBrush> _vibeColorCache = new();
    public Avalonia.Media.IBrush VibeColor => GetVibeColor(DetectedSubGenre);

    private Avalonia.Media.IBrush GetVibeColor(string? genre)
    {
        if (string.IsNullOrEmpty(genre)) return Avalonia.Media.Brushes.Transparent;
        if (_vibeColorCache.TryGetValue(genre, out var brush)) return brush;

        // On-demand load from Style Lab (Phase 15 integration)
        Task.Run(async () => 
        {
            var styles = await _libraryService.GetStyleDefinitionsAsync();
            foreach (var style in styles)
            {
                if (Avalonia.Media.Color.TryParse(style.ColorHex, out var color))
                {
                    _vibeColorCache[style.Name] = new Avalonia.Media.SolidColorBrush(color);
                }
            }
            Avalonia.Threading.Dispatcher.UIThread.Post(() => this.RaisePropertyChanged(nameof(VibeColor)));
        });

        return Avalonia.Media.Brushes.Gray;
    }

    public string PreparationStatus => IsPrepared ? "Prepared" : "Raw";
    public Avalonia.Media.IBrush PreparationColor => IsPrepared ? Avalonia.Media.Brushes.DodgerBlue : Avalonia.Media.Brushes.Gray;

    // Phase 13C: Vibe Pills
    public record VibePill(string Icon, string Label, Avalonia.Media.IBrush Color, string Description);
    
    public System.Collections.Generic.IEnumerable<VibePill> VibePills
    {
        get
        {
            var pills = new System.Collections.Generic.List<VibePill>();
            
            // 💃 Dance Pill (High Danceability)
            if (Model.Danceability > 0.75)
            {
                pills.Add(new VibePill("💃", "Dance", Avalonia.Media.Brushes.DeepPink, "High Danceability detected by AI"));
            }
            
            // 🎻 Inst Pill (Instrumental)
            if (Model.QualityConfidence > 0.8) // High spectral quality often correlates with clean instrumentals/stable phase
            {
                 // Actually we'll use a specific threshold based on new fields if available
                 // For now, let's use the MoodTag if it matches
                 if (Model.MoodTag == "Relaxed")
                 {
                     pills.Add(new VibePill("🎻", "Inst", Avalonia.Media.Brushes.RoyalBlue, "Instrumental / Chill Vibe"));
                 }
            }

            // 🔥 Hard Pill (Aggressive/High Energy)
            if (Model.Energy > 0.8 || Model.MoodTag == "Aggressive")
            {
                pills.Add(new VibePill("🔥", "Hard", Avalonia.Media.Brushes.OrangeRed, "High Energy / Aggressive Vibe"));
            }

            // ✨ Vibe Pill (Primary Genre/Subgenre classification)
            if (!string.IsNullOrEmpty(DetectedSubGenre))
            {
                pills.Add(new VibePill("✨", DetectedSubGenre, VibeColor, $"Genre: {DetectedSubGenre} (Conf: {SubGenreConfidence:P0})"));
            }
            
            return pills;
        }
    }

    public WaveformAnalysisData WaveformData => new WaveformAnalysisData
    {
        PeakData = Model.WaveformData ?? Array.Empty<byte>(),
        RmsData = Model.RmsData ?? Array.Empty<byte>(),
        LowData = Model.LowData ?? Array.Empty<byte>(),
        MidData = Model.MidData ?? Array.Empty<byte>(),
        HighData = Model.HighData ?? Array.Empty<byte>(),
        DurationSeconds = (Model.CanonicalDuration ?? 0) / 1000.0
    };
    
    // Commands
    public ICommand PlayCommand { get; }
    public ICommand RevealFileCommand { get; }
    public ICommand AddToProjectCommand { get; }
    public ICommand PauseCommand { get; }
    public ICommand ResumeCommand { get; }
    public ICommand CancelCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand ForceStartCommand { get; }
    public ICommand ForceDownloadIgnoreGuardsCommand { get; }
    public ICommand SearchAgainCommand { get; }
    public ICommand CleanCommand { get; }
    public ICommand FilterByVibeCommand { get; }
    public ICommand FindSimilarCommand { get; }
    public ICommand FindSimilarAiCommand { get; }
    public ICommand ViewAllSearchResultsCommand { get; }
    public ICommand BumpToTopCommand { get; }
    public ICommand ViewLogCommand { get; }
    public ICommand CopyLogCommand { get; }

    // Internal State
    private long _totalBytes;
    private long _bytesReceived;
    private double _currentSpeed;
    private DateTime _lastProgressTime;
    public DateTime LastActivity { get; private set; } = DateTime.UtcNow;
    
    public double CurrentSpeedBytes => _currentSpeed;

    public string SpeedDisplay => _currentSpeed > 1024 * 1024 
        ? $"{_currentSpeed / 1024 / 1024:F1} MB/s" 
        : $"{_currentSpeed / 1024:F0} KB/s";


    // Phase 0.6: Truth in UI - Stems
    private bool? _hasStems;
    public bool HasStems
    {
        get
        {
            if (!IsCompleted) return false;
            
            if (!_hasStems.HasValue)
            {
                _hasStems = false;
                _ = CheckStemsAsync();
            }
            return _hasStems.Value;
        }
    }
    
    private readonly DispatcherTimer _ghostStallTimer;

    private void GhostStallCheck_Tick(object? sender, EventArgs e)
    {
        // Only care if we are supposedly downloading
        if (State != PlaylistTrackState.Downloading) return;

        // If explicitly set speed to 0 by logic
        if (DownloadSpeed == 0)
        {
             // Check how long since last activity
             var secondsSince = (DateTime.UtcNow - LastActivity).TotalSeconds;
             
             // If > 30s of silence, mark as visually stalled
             if (secondsSince > 30)
             {
                 State = PlaylistTrackState.Stalled;
                 // We set a custom StalledReason if none exists, to hint it's a timeout
                 Model.StalledReason = "Connection Timeout (Ghost)";
                 this.RaisePropertyChanged(nameof(StalledReason));
                 this.RaisePropertyChanged(nameof(StatusText)); // Refresh text
             }
        }
        else
        {
            // If speed > 0, we aren't stalled.
            // But if we haven't had progress in a while, decay speed to 0.
            var secondsSince = (DateTime.UtcNow - LastActivity).TotalSeconds;
            if (secondsSince > 5)
            {
                DownloadSpeed = 0; // Decay speed display
                // Next tick will catch the stall counter if it persists
            }
        }
    }
    
    private async Task CheckStemsAsync()
    {
         if (string.IsNullOrEmpty(Model.ResolvedFilePath)) return;
         try {
             await Task.Run(() => {
                 var dir = System.IO.Path.GetDirectoryName(Model.ResolvedFilePath);
                 var name = System.IO.Path.GetFileNameWithoutExtension(Model.ResolvedFilePath);
                 if (string.IsNullOrEmpty(dir)) return;
                 var path = System.IO.Path.Combine(dir, $"{name}_Stems");
                 var found = System.IO.Directory.Exists(path) && System.IO.Directory.GetFiles(path).Length > 0;
                 Avalonia.Threading.Dispatcher.UIThread.Post(() => {
                     _hasStems = found;
                     this.RaisePropertyChanged(nameof(HasStems));
                 });
             });
         } catch {}
    }

    // Event Handlers
    private void OnStateChanged(TrackStateChangedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        System.Diagnostics.Debug.WriteLine($"[UnifiedTrackVM] {GlobalId} State Changed: {e.State} (Error: {e.Error})");
        State = e.State;
        FailureReason = e.Error;
        FailureEnum = e.FailureReason;
        
        // Force-100%: Bypass throttle and ensure progress bar completes
        if (e.State == PlaylistTrackState.Completed)
        {
            Progress = 100;
            _currentSpeed = 0;
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(SpeedDisplay));
            this.RaisePropertyChanged(nameof(TechnicalSummary));
            this.RaisePropertyChanged(nameof(QualityPillText));
            this.RaisePropertyChanged(nameof(HasQualityPill));
            this.RaisePropertyChanged(nameof(IsFakeFlacWarning));
            this.RaisePropertyChanged(nameof(HasShieldSanitized));
        }
        
        // Capture Peer Name if provided in the event or available from manager
        if (e.State == PlaylistTrackState.Downloading)
        {
            PeerName = e.PeerName; // Assuming TrackStateChangedEvent now has PeerName
        }
        else
        {
            PeerName = null; // Clear peer name when not downloading
        }
        
        // Phase 0.5: Populate Search Diagnostics
        if (e.SearchLog != null && e.SearchLog.Top3RejectedResults.Any())
        {
             RejectionDetails = new System.Collections.ObjectModel.ObservableCollection<RejectedResult>(e.SearchLog.Top3RejectedResults);
             HasRejectionDetails = true;
             this.RaisePropertyChanged(nameof(SearchAttemptCount));
             this.RaisePropertyChanged(nameof(StatusText));
        }
        else if (State == PlaylistTrackState.Pending || State == PlaylistTrackState.Searching)
        {
             // Clear diagnostics on retry/restart
             RejectionDetails = null;
             HasRejectionDetails = false;
        }
    }

    private void OnDetailedStatus(TrackDetailedStatusEvent e)
    {
        // Already filtered by TrackHash in the Rx subscription (Where clause)
        if (e.Message.Contains("Fast lane", StringComparison.OrdinalIgnoreCase))
        {
            _discoveryReasonOverride = "⚡ Fast lane: idle peer match";
            this.RaisePropertyChanged(nameof(DiscoveryReason));
            this.RaisePropertyChanged(nameof(DiscoveryBadgeText));
        }
        else if (e.Message.Contains("Golden match", StringComparison.OrdinalIgnoreCase))
        {
            _discoveryReasonOverride = "🏁 Golden match";
            this.RaisePropertyChanged(nameof(DiscoveryReason));
            this.RaisePropertyChanged(nameof(DiscoveryBadgeText));
        }

        DetailedSearchStatus = e.Message;
    }

    private string? _detailedSearchStatus;
    /// <summary>
    /// Granular search progress message from the discovery service.
    /// Shows live updates like "🔎 Started Dirty search..." or "Rejected @user: Duration mismatch".
    /// Cleared automatically when the track leaves the Searching state.
    /// </summary>
    public string? DetailedSearchStatus
    {
        get => _detailedSearchStatus;
        set
        {
            this.RaiseAndSetIfChanged(ref _detailedSearchStatus, value);
            // Also refresh StatusText since it may incorporate this
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(DiscoveryReason));
            this.RaisePropertyChanged(nameof(DiscoveryBadgeText));
        }
    }

    private void OnProgressChanged(TrackProgressChangedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        // Auto-heal ghost stall if improved
        if (State == PlaylistTrackState.Stalled)
        {
            State = PlaylistTrackState.Downloading;
            Model.StalledReason = null; // Clear reason
            this.RaisePropertyChanged(nameof(StalledReason));
        }
        
         // Update all data + raise all properties (safe: only fires ~4x/sec via .Sample())
         Progress = e.Progress;
         _totalBytes = e.TotalBytes;
         
         // Speed Calc
         var now = DateTime.UtcNow;
         if (_lastProgressTime != DateTime.MinValue)
         {
             var seconds = (now - _lastProgressTime).TotalSeconds;
             if (seconds > 0)
             {
                 var bytesDiff = e.BytesReceived - _bytesReceived;
                 if (bytesDiff > 0)
                 {
                     var instantSpeed = bytesDiff / seconds;
                     _currentSpeed = (_currentSpeed * 0.7) + (instantSpeed * 0.3); 
                 }
             }
         }
         _bytesReceived = e.BytesReceived;
         _lastProgressTime = now;
         LastActivity = now;
         
         // Raise all display properties (safe at 4Hz via .Sample())
         this.RaisePropertyChanged(nameof(StatusText));
         this.RaisePropertyChanged(nameof(TechnicalSummary));
         this.RaisePropertyChanged(nameof(SpeedDisplay));
         this.RaisePropertyChanged(nameof(CurrentSpeedBytes));
    }

    private void OnMetadataUpdated(TrackMetadataUpdatedEvent e)
    {
        if (e.TrackGlobalId != GlobalId) return;
        
        // Reload from DB to ensure Model has new IDs (SpotifyAlbumId etc.)
        Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(async () =>
        {
            var updatedTrack = await _libraryService.GetPlaylistTrackByHashAsync(Model.PlaylistId, GlobalId);
            
            if (updatedTrack != null)
            {
                // Sync important fields back to Model instance
                Model.Artist = updatedTrack.Artist;
                Model.Title = updatedTrack.Title;
                Model.Album = updatedTrack.Album;
                Model.AlbumArtUrl = updatedTrack.AlbumArtUrl;
                Model.SpotifyAlbumId = updatedTrack.SpotifyAlbumId;
                Model.SpotifyTrackId = updatedTrack.SpotifyTrackId;
                Model.SpotifyArtistId = updatedTrack.SpotifyArtistId;
                Model.BPM = updatedTrack.BPM;
                Model.MusicalKey = updatedTrack.MusicalKey;
                Model.Bitrate = updatedTrack.Bitrate;
                Model.Format = updatedTrack.Format;
                Model.IsEnriched = updatedTrack.IsEnriched;
                Model.Energy = updatedTrack.Energy;
                Model.Danceability = updatedTrack.Danceability;
                Model.Valence = updatedTrack.Valence;
                Model.Genres = updatedTrack.Genres;
                Model.Popularity = updatedTrack.Popularity;
                Model.IsPrepared = updatedTrack.IsPrepared;
                Model.PrimaryGenre = updatedTrack.PrimaryGenre;
                Model.CuePointsJson = updatedTrack.CuePointsJson;
                Model.MoodTag = updatedTrack.MoodTag;
                Model.DetectedSubGenre = updatedTrack.DetectedSubGenre;
                Model.SubGenreConfidence = updatedTrack.SubGenreConfidence;
                
                // Sync Waveform bands
                Model.LowData = updatedTrack.LowData;
                Model.MidData = updatedTrack.MidData;
                Model.HighData = updatedTrack.HighData;
                Model.WaveformData = updatedTrack.WaveformData;
                Model.RmsData = updatedTrack.RmsData;
                Model.CanonicalDuration = updatedTrack.CanonicalDuration;
                
                // Technical Audio
                Model.Loudness = updatedTrack.Loudness;
                Model.TruePeak = updatedTrack.TruePeak;
                Model.DynamicRange = updatedTrack.DynamicRange;
                Model.FrequencyCutoff = updatedTrack.FrequencyCutoff;
                Model.QualityConfidence = updatedTrack.QualityConfidence;
                Model.SpectralHash = updatedTrack.SpectralHash;
                Model.IsTrustworthy = updatedTrack.IsTrustworthy;
                Model.Integrity = updatedTrack.Integrity;
                Model.QualityDetails = updatedTrack.QualityDetails;
                Model.SourceProvenance = updatedTrack.SourceProvenance;
                
                this.RaisePropertyChanged(nameof(ArtistName));
                this.RaisePropertyChanged(nameof(TrackTitle));
                this.RaisePropertyChanged(nameof(AlbumName));
                this.RaisePropertyChanged(nameof(AlbumArtUrl));
                this.RaisePropertyChanged(nameof(BpmDisplay));
                this.RaisePropertyChanged(nameof(KeyDisplay));
                this.RaisePropertyChanged(nameof(CamelotDisplay));
                this.RaisePropertyChanged(nameof(LoudnessDisplay));
                this.RaisePropertyChanged(nameof(TruePeakDisplay));
                this.RaisePropertyChanged(nameof(DynamicRangeDisplay));
                this.RaisePropertyChanged(nameof(IntegrityScore));
                this.RaisePropertyChanged(nameof(TechnicalSummary));
                this.RaisePropertyChanged(nameof(IsSecure));
                this.RaisePropertyChanged(nameof(QualityIcon));
                this.RaisePropertyChanged(nameof(QualityColor));
                this.RaisePropertyChanged(nameof(QualityPillText));
                this.RaisePropertyChanged(nameof(HasQualityPill));
                this.RaisePropertyChanged(nameof(IsFakeFlacWarning));
                this.RaisePropertyChanged(nameof(HasShieldSanitized));
                this.RaisePropertyChanged(nameof(IsPrepared));
                this.RaisePropertyChanged(nameof(PreparationStatus));
                this.RaisePropertyChanged(nameof(PreparationColor));
                this.RaisePropertyChanged(nameof(PrimaryGenre));
                this.RaisePropertyChanged(nameof(DetectedSubGenre));
                this.RaisePropertyChanged(nameof(VibeColor));
                this.RaisePropertyChanged(nameof(SubGenreConfidence));
                this.RaisePropertyChanged(nameof(VibePills));
                
                // Curation & Trust
                this.RaisePropertyChanged(nameof(CurationConfidence));
                this.RaisePropertyChanged(nameof(CurationIcon));
                this.RaisePropertyChanged(nameof(CurationColor));
                this.RaisePropertyChanged(nameof(ProvenanceTooltip));
                this.RaisePropertyChanged(nameof(DiscoveryReason));
                this.RaisePropertyChanged(nameof(DiscoveryBadgeText));

                // Audio features
                this.RaisePropertyChanged(nameof(IsEnriched));
                this.RaisePropertyChanged(nameof(WaveformData));
                
                // Update Artwork Proxy
                _artwork = new ArtworkProxy(_artworkCache, Model.AlbumArtUrl);
                this.RaisePropertyChanged(nameof(Artwork));
                this.RaisePropertyChanged(nameof(ArtworkBitmap));
            }
        });
    }

    private void PlayTrack()
    {
        // Construct a lightweight VM payload for the player
        // The Player expects a PlaylistTrackViewModel, ensuring it has the Model
        var payload = new PlaylistTrackViewModel(Model);
        
        // Publish event
        _eventBus.Publish(new PlayTrackRequestEvent(payload));
    }
    
    // Phase 11.5: Library Trust Badges
    public SLSKDONET.Data.Entities.CurationConfidence CurationConfidence => Model.CurationConfidence;
    public string ProvenanceTooltip => $"Confidence: {CurationConfidence}\nSource: {Model.Source}";

    public void Dispose()
    {
        _disposables.Dispose();
        // Artwork is a proxy, cache manages bitmap disposal
    }
}
