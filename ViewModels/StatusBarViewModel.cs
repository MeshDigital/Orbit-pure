using System;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Avalonia.Threading;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Share health tier, used by the Status Bar LED indicator.
/// Green  = sharing and healthy (≥1 folder configured, connected)
/// Yellow = sharing configured but possibly empty / low file count
/// Red    = not sharing (share folder unset or Soulseek disconnected)
/// </summary>
public enum ShareHealthTier
{
    Unknown,
    Good,
    Warn,
    Bad,
}

public class StatusBarViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    
    private int _queuedCount;
    private int _processedCount;
    private string? _currentTrack;
    private bool _isPaused;
    private bool _isHealthy = true; // Phase 10.5
    private readonly NativeDependencyHealthService _dependencyHealthService; // Phase 10.5

    // ───────────────────────────────────────────────────
    // Phase 6: Share Health Indicator
    // ───────────────────────────────────────────────────
    private ShareHealthTier _shareHealthTier = ShareHealthTier.Unknown;
    private int _sharedFolderCount;
    private int _sharedFileCount;
    private bool _isSharing;

    public ShareHealthTier ShareHealthTier
    {
        get => _shareHealthTier;
        set
        {
            this.RaiseAndSetIfChanged(ref _shareHealthTier, value);
            this.RaisePropertyChanged(nameof(ShareHealthColor));
            this.RaisePropertyChanged(nameof(ShareHealthTooltip));
            this.RaisePropertyChanged(nameof(IsShareWarning));
        }
    }

    public int SharedFolderCount
    {
        get => _sharedFolderCount;
        set => this.RaiseAndSetIfChanged(ref _sharedFolderCount, value);
    }

    public int SharedFileCount
    {
        get => _sharedFileCount;
        set => this.RaiseAndSetIfChanged(ref _sharedFileCount, value);
    }

    public bool IsSharing
    {
        get => _isSharing;
        set => this.RaiseAndSetIfChanged(ref _isSharing, value);
    }

    /// <summary>Hex colour for the share-health LED.</summary>
    public string ShareHealthColor => ShareHealthTier switch
    {
        ShareHealthTier.Good    => "#1DB954", // green
        ShareHealthTier.Warn    => "#FFA500", // amber
        ShareHealthTier.Bad     => "#F44336", // red
        _                       => "#666666", // grey (unknown/startup)
    };

    /// <summary>Tooltip text surfaced when the user hovers the share LED.</summary>
    public string ShareHealthTooltip => ShareHealthTier switch
    {
        ShareHealthTier.Good => $"Sharing ✓  —  {SharedFolderCount} folder(s), {SharedFileCount} file(s) shared",
        ShareHealthTier.Warn => $"Sharing may be degraded  —  {SharedFolderCount} folder(s) configured, {SharedFileCount} file(s) visible",
        ShareHealthTier.Bad  => "Not sharing  —  Configure a shared folder in Settings to contribute to the network",
        _                    => "Share status unknown",
    };

    /// <summary>True when the share health is not Good; drives a gentle pulse animation in XAML.</summary>
    public bool IsShareWarning => ShareHealthTier is ShareHealthTier.Warn or ShareHealthTier.Bad;

    private void OnShareHealthUpdated(ShareHealthUpdatedEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            IsSharing         = e.IsSharing;
            SharedFolderCount = e.SharedFolderCount;
            SharedFileCount   = e.SharedFileCount;

            ShareHealthTier = (e.IsSharing, e.SharedFolderCount, e.SharedFileCount) switch
            {
                (true,  >= 1, > 0) => ShareHealthTier.Good,
                (true,  >= 1, 0)   => ShareHealthTier.Warn,   // folder configured but 0 files visible yet
                _                  => ShareHealthTier.Bad,
            };
        });
    }

    // ───────────────────────────────────────────────────
    // Existing queue / health properties (unchanged)
    // ───────────────────────────────────────────────────

    public int QueuedCount
    {
        get => _queuedCount;
        set
        {
            this.RaiseAndSetIfChanged(ref _queuedCount, value);
            this.RaisePropertyChanged(nameof(StatusText));
            this.RaisePropertyChanged(nameof(IsProcessing));
        }
    }
    
    public int ProcessedCount
    {
        get => _processedCount;
        set => this.RaiseAndSetIfChanged(ref _processedCount, value);
    }
    
    public string? CurrentTrack
    {
        get => _currentTrack;
        set
        {
            this.RaiseAndSetIfChanged(ref _currentTrack, value);
            this.RaisePropertyChanged(nameof(IsProcessing));
        }
    }
    
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            this.RaiseAndSetIfChanged(ref _isPaused, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public bool IsHealthy
    {
        get => _isHealthy;
        set
        {
            this.RaiseAndSetIfChanged(ref _isHealthy, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }
    
    // ... (keep existing properties) ...
    private bool _isBulkOperationRunning;
    private string _bulkOperationTitle = string.Empty;
    private int _bulkOperationProgress;

    public bool IsBulkOperationRunning
    {
        get => _isBulkOperationRunning;
        set
        {
            this.RaiseAndSetIfChanged(ref _isBulkOperationRunning, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public string BulkOperationTitle
    {
        get => _bulkOperationTitle;
        set
        {
            this.RaiseAndSetIfChanged(ref _bulkOperationTitle, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    public int BulkOperationProgress
    {
        get => _bulkOperationProgress;
        set
        {
            this.RaiseAndSetIfChanged(ref _bulkOperationProgress, value);
            this.RaisePropertyChanged(nameof(StatusText));
        }
    }

    // Computed properties
    public string StatusText
    {
        get
        {
            if (IsBulkOperationRunning) return $"⚙️ {BulkOperationTitle} ({BulkOperationProgress}%)";
            if (!IsHealthy) return "⚠️ Repair Required: Missing Analysis Tools";
            if (IsPaused) return "⏸️ Analysis Paused";
            if (QueuedCount > 0) return $"🔬 Analyzing... {QueuedCount} pending";
            if (ProcessedCount > 0) return $"✓ All tracks analyzed ({ProcessedCount} total)";
            return "✓ Ready";
        }
    }
    
    public bool IsProcessing => QueuedCount > 0 || !string.IsNullOrEmpty(CurrentTrack) || IsBulkOperationRunning;
    
    public StatusBarViewModel(
        IEventBus eventBus,
        NativeDependencyHealthService dependencyHealthService)
    {
        _dependencyHealthService = dependencyHealthService;

        // Phase 10.5: Subscribe to Health Events
        _dependencyHealthService.HealthChanged += (s, healthy) =>
        {
             Dispatcher.UIThread.Post(() => IsHealthy = healthy);
        };

        // Subscribe to queue status changes
        eventBus.GetEvent<AnalysisQueueStatusChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                QueuedCount = e.QueuedCount;
                ProcessedCount = e.ProcessedCount;
                CurrentTrack = e.CurrentTrackHash;
                IsPaused = e.IsPaused;
            })
            .DisposeWith(_disposables);

        // Phase 6: Subscribe to share health updates
        eventBus.GetEvent<ShareHealthUpdatedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnShareHealthUpdated)
            .DisposeWith(_disposables);

        // Phase 10.5: Bulk Operation Subscriptions
        eventBus.GetEvent<SLSKDONET.Services.BulkOperationStartedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                BulkOperationTitle = e.Title;
                IsBulkOperationRunning = true;
                BulkOperationProgress = 0;
            })
            .DisposeWith(_disposables);

        eventBus.GetEvent<SLSKDONET.Services.BulkOperationProgressEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                BulkOperationProgress = e.Percentage;
            })
            .DisposeWith(_disposables);

        eventBus.GetEvent<SLSKDONET.Services.BulkOperationCompletedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                IsBulkOperationRunning = false;
            })
            .DisposeWith(_disposables);
            
        // Initial Check
        IsHealthy = _dependencyHealthService.IsHealthy;
    }
    
    public void Dispose()
    {
        _disposables?.Dispose();
    }
}

