using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows.Input;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Models.Entertainment;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services;
using SLSKDONET.Services.Entertainment;
using SLSKDONET.Services.Similarity;
using SLSKDONET.Views;

// using DraggingService; // TODO: Fix drag-drop library reference

using System.Reactive; // Added for ReactiveCommand<Unit, Unit>
using System.Reactive.Linq;
using System.Reactive.Disposables;
using ReactiveUI;

namespace SLSKDONET.ViewModels
{
    using System.ComponentModel;
    using System.Runtime.CompilerServices;

    public partial class PlayerViewModel : INotifyPropertyChanged, IDisposable
    {

        private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
        private bool _isDisposed;

        public event PropertyChangedEventHandler? PropertyChanged;
        protected void OnPropertyChanged([CallerMemberName] string? name = null)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        protected bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return false;
            field = value;
            OnPropertyChanged(name);
            return true;
        }
        private readonly IAudioPlayerService _playerService;
        private readonly AppConfig? _config;
        private readonly ConfigManager? _configManager;
        private readonly DatabaseService _databaseService;
        private readonly ArtworkCacheService _artworkCacheService;
        private readonly IEventBus _eventBus;
        private readonly INavigationService _navigationService;
        private readonly IRightPanelService _rightPanelService;
        private readonly IAmbientModeService? _ambientModeService;
        private readonly IFlowModeService? _flowModeService;
        private readonly System.Threading.Timer _saveQueueTimer;
        private bool _suppressSave;
        private System.Threading.CancellationTokenSource? _errorDismissCts;
        private bool _playerNavigationInFlight;
        private DateTime _lastPlayerNavigationRequestUtc = DateTime.MinValue;

        private static readonly TimeSpan PlayerNavigationDebounceWindow = TimeSpan.FromMilliseconds(350);
        private static readonly TimeSpan PlayerNavigationSettleDelay = TimeSpan.FromMilliseconds(150);

        
        private string _trackTitle = "No Track Playing";
        public string TrackTitle
        {
            get => _trackTitle;
            set => SetProperty(ref _trackTitle, value);
        }

        private string _trackArtist = "";
        public string TrackArtist
        {
            get => _trackArtist;
            set => SetProperty(ref _trackArtist, value);
        }

        private bool _isPlaying;
        public bool IsPlaying
        {
            get => _isPlaying;
            set => SetProperty(ref _isPlaying, value);
        }

        private float _position; // 0.0 to 1.0
        public float Position
        {
            get => _position;
            set => SetProperty(ref _position, value);
        }

        private string _currentTimeStr = "0:00";
        public string CurrentTimeStr
        {
            get => _currentTimeStr;
            set => SetProperty(ref _currentTimeStr, value);
        }

        private string _totalTimeStr = "0:00";
        public string TotalTimeStr
        {
            get => _totalTimeStr;
            set => SetProperty(ref _totalTimeStr, value);
        }
        
        private int _volume = 100;
        public int Volume
        {
            get => _volume;
            set
            {
                if (SetProperty(ref _volume, value))
                {
                    OnVolumeChanged(value);
                    OnPropertyChanged(nameof(VolumeIcon));
                }
            }
        }

        private bool _isMuted;
        public bool IsMuted
        {
            get => _isMuted;
            set
            {
                if (SetProperty(ref _isMuted, value))
                {
                    OnPropertyChanged(nameof(VolumeIcon));
                }
            }
        }

        private int _preMuteVolume = 100;

        /// <summary>Returns the appropriate speaker icon based on mute state and volume level.</summary>
        public string VolumeIcon
        {
            get
            {
                if (_isMuted || _volume == 0) return "🔇";
                if (_volume < 33) return "🔈";
                if (_volume < 66) return "🔉";
                return "🔊";
            }
        }

        private long _lengthMs;
        public long LengthMs
        {
            get => _lengthMs;
            set => SetProperty(ref _lengthMs, value);
        }

        private bool _isPlayerInitialized;
        public bool IsPlayerInitialized
        {
            get => _isPlayerInitialized;
            set => SetProperty(ref _isPlayerInitialized, value);
        }
        
        // Queue Management
        public ObservableCollection<PlaylistTrackViewModel> Queue { get; } = new();

        /// <summary>Returns true when the queue has no tracks.</summary>
        public bool IsQueueEmpty => !Queue.Any();
        
        private int _currentQueueIndex = -1;
        public int CurrentQueueIndex
        {
            get => _currentQueueIndex;
            set => SetProperty(ref _currentQueueIndex, value);
        }
        
        private PlaylistTrackViewModel? _currentTrack;
        public PlaylistTrackViewModel? CurrentTrack
        {
            get => _currentTrack;
            set
            {
                var previousTrack = _currentTrack;
                if (SetProperty(ref _currentTrack, value))
                {
                    AttachCurrentTrackObservers(previousTrack, value);
                    RaiseCurrentTrackSummaryProperties();
                }
            }
        }

        /// <summary>
        /// Waveform data for the currently playing track, forwarded from <see cref="CurrentTrack"/>.
        /// Returns <see langword="null"/> when no track is loaded.
        /// </summary>
        public WaveformAnalysisData? WaveformData => _currentTrack?.WaveformData;
        public bool HasCurrentTrack => _currentTrack is not null;

        /// <summary>
        /// The file path actually loaded into the audio engine right now — ad-hoc PlayTrack()
        /// calls first, falling back to the queued CurrentTrack's resolved path. Lets other
        /// pages (e.g. Cue Forge) check whether they need to (re)load this track before trying
        /// to play/seek/audition it, without duplicating the resolution order used internally.
        /// </summary>
        public string? CurrentFilePath => _currentFilePath ?? CurrentTrack?.Model?.ResolvedFilePath;
        public string CurrentTrackContextSummary => BuildTrackContextSummary(_currentTrack);
        public string CurrentTrackWorkflowHint => BuildTrackWorkflowHint(_currentTrack);
        public string CurrentTrackWorkstationPrepSummary => BuildWorkstationPrepSummary(_currentTrack);
        public string CurrentTrackRoutingSummary => BuildRoutingSummary(_currentTrack);
        public string CurrentTrackTransitionPlanSummary => BuildTransitionPlanSummary(_currentTrack);
        private string _analysisLaneSummary = "Analysis lane idle • queue prep jobs for cues, timing, and stems";
        public string AnalysisLaneSummary
        {
            get => _analysisLaneSummary;
            private set => SetProperty(ref _analysisLaneSummary, value);
        }
        public string CurrentTrackStatusBadge => BuildStatusBadge(_currentTrack);
        public string CurrentTrackTempoBadge => _currentTrack is null || string.IsNullOrWhiteSpace(_currentTrack.BpmDisplay) || _currentTrack.BpmDisplay == "—"
            ? "TEMPO —"
            : $"TEMPO {_currentTrack.BpmDisplay}";
        public string CurrentTrackKeyBadge => _currentTrack is null || string.IsNullOrWhiteSpace(_currentTrack.CamelotDisplay) || _currentTrack.CamelotDisplay == "—"
            ? "KEY —"
            : $"KEY {_currentTrack.CamelotDisplay}";
        public string CurrentTrackEnergyBadge => _currentTrack is null || string.IsNullOrWhiteSpace(_currentTrack.EnergyRating) || _currentTrack.EnergyRating == "—"
            ? "ENERGY —"
            : $"ENERGY {_currentTrack.EnergyRating}/10";
        public string CurrentTrackCueBadge => _currentTrack is null
            ? "CUES —"
            : _currentTrack.HasCues
                ? $"CUES {_currentTrack.Cues.Count()}"
                : "CUES AUTO";
        public string CurrentTrackPhraseJumpSummary => BuildPhraseJumpSummary(_currentTrack?.Cues);
        
        // Shuffle & Repeat
        private bool _isShuffling;
        public bool IsShuffling
        {
            get => _isShuffling;
            set { if (SetProperty(ref _isShuffling, value)) SchedulePreloadNext(); }
        }

        private RepeatMode _repeatMode = RepeatMode.Off;
        public RepeatMode RepeatMode
        {
            get => _repeatMode;
            set { if (SetProperty(ref _repeatMode, value)) SchedulePreloadNext(); }
        }

        /// <summary>Gapless/crossfade: when enabled, the preloaded next track overlaps and
        /// fades in while the current one fades out, instead of a hard cut at track boundaries.</summary>
        public bool IsCrossfadeEnabled
        {
            get => _playerService.CrossfadeEnabled;
            set
            {
                if (_playerService.CrossfadeEnabled == value) return;
                _playerService.CrossfadeEnabled = value;
                OnPropertyChanged();
                PersistPlaybackSettings(c => c.PlaybackCrossfadeEnabled = value);
            }
        }

        /// <summary>Length of the crossfade overlap, in seconds. Only used when
        /// <see cref="IsCrossfadeEnabled"/> is true.</summary>
        public double CrossfadeSeconds
        {
            get => _playerService.CrossfadeSeconds;
            set
            {
                if (Math.Abs(_playerService.CrossfadeSeconds - value) < 0.01) return;
                _playerService.CrossfadeSeconds = value;
                OnPropertyChanged();
                PersistPlaybackSettings(c => c.PlaybackCrossfadeSeconds = value);
            }
        }
        
        // Player Dock Location
        private PlayerDockLocation _currentDockLocation = PlayerDockLocation.RightSidebar;
        public PlayerDockLocation CurrentDockLocation
        {
            get => _currentDockLocation;
            set => SetProperty(ref _currentDockLocation, value);
        }
        
        private bool _isPlayerVisible = true;
        public bool IsPlayerVisible
        {
            get => _isPlayerVisible;
            set => SetProperty(ref _isPlayerVisible, value);
        }
        
        // Queue Visibility
        private bool _isQueueOpen;
        public bool IsQueueOpen
        {
            get => _isQueueOpen;
            set => SetProperty(ref _isQueueOpen, value);
        }

        private bool _isTheaterMode;
        public bool IsTheaterMode
        {
            get => _isTheaterMode;
            set => SetProperty(ref _isTheaterMode, value);
        }

        private VisualizerStyle _currentVisualStyle = VisualizerStyle.Glow;
        public VisualizerStyle CurrentVisualStyle
        {
            get => _currentVisualStyle;
            set => SetProperty(ref _currentVisualStyle, value);
        }

        // ── Entertainment Engine Properties ─────────────────────────────────

        private VisualizerPreset _currentVisualizerPreset = VisualizerPreset.SpectrumBars;
        /// <summary>Active SkiaSharp visualizer preset for the expanded player.</summary>
        public VisualizerPreset CurrentVisualizerPreset
        {
            get => _currentVisualizerPreset;
            set => SetProperty(ref _currentVisualizerPreset, value);
        }

        private VisualizerEngineMode _visualizerEngineMode = VisualizerEngineMode.Standard;
        /// <summary>Whether the visualizer adapts to metadata or is in ambient mode.</summary>
        public VisualizerEngineMode VisualizerEngineMode
        {
            get => _visualizerEngineMode;
            set => SetProperty(ref _visualizerEngineMode, value);
        }

        private bool _isAmbientMode;
        /// <summary>True when Ambient Mode is active (slow, meditative visuals).</summary>
        public bool IsAmbientMode
        {
            get => _isAmbientMode;
            set
            {
                if (SetProperty(ref _isAmbientMode, value))
                {
                    VisualizerEngineMode = value
                        ? VisualizerEngineMode.Ambient
                        : VisualizerEngineMode.Standard;
                }
            }
        }

        private bool _isFlowMode;
        /// <summary>True when Flow Mode (smart auto-mixing) is active.</summary>
        public bool IsFlowMode
        {
            get => _isFlowMode;
            set => SetProperty(ref _isFlowMode, value);
        }

        private bool _isMetadataDrivenVisuals;
        /// <summary>True when the visualizer adapts dynamically based on track metadata.</summary>
        public bool IsMetadataDrivenVisuals
        {
            get => _isMetadataDrivenVisuals;
            set
            {
                if (SetProperty(ref _isMetadataDrivenVisuals, value) && !_isAmbientMode)
                {
                    VisualizerEngineMode = value
                        ? VisualizerEngineMode.MetadataDriven
                        : VisualizerEngineMode.Standard;
                }
            }
        }

        private bool _isExpandedPlayerOpen;
        /// <summary>True when the full visualizer-first expanded player is visible.</summary>
        public bool IsExpandedPlayerOpen
        {
            get => _isExpandedPlayerOpen;
            set => SetProperty(ref _isExpandedPlayerOpen, value);
        }

        private FlowModeState _flowModeState = new();
        /// <summary>Live state of the Flow Mode engine.</summary>
        public FlowModeState FlowModeState
        {
            get => _flowModeState;
            set => SetProperty(ref _flowModeState, value);
        }

        /// <summary>Album-art-derived hue (0–360), or -1 for default energy-based color.</summary>
        private float _albumArtHue = -1f;
        public float AlbumArtHue
        {
            get => _albumArtHue;
            set => SetProperty(ref _albumArtHue, value);
        }
        
        // Phase 9.2: Loading & Error States
        private bool _isLoading;
        public bool IsLoading
        {
            get => _isLoading;
            set => SetProperty(ref _isLoading, value);
        }

        private bool _hasPlaybackError;
        public bool HasPlaybackError
        {
            get => _hasPlaybackError;
            set => SetProperty(ref _hasPlaybackError, value);
        }

        private string _playbackError = string.Empty;
        public string PlaybackError
        {
            get => _playbackError;
            set => SetProperty(ref _playbackError, value);
        }

        // Phase 9.2: Album Artwork
        private string? _albumArtUrl;
        public string? AlbumArtUrl
        {
            get => _albumArtUrl;
            set => SetProperty(ref _albumArtUrl, value);
        }

        // Phase 9.3: Like Feature
        private bool _isCurrentTrackLiked;
        public bool IsCurrentTrackLiked
        {
            get => _isCurrentTrackLiked;
            set => SetProperty(ref _isCurrentTrackLiked, value);
        }
        
        // Sprint B: High-Fidelity Features
        private float _vuLeft;
        public float VuLeft
        {
            get => _vuLeft;
            set => SetProperty(ref _vuLeft, value);
        }

        private float[] _spectrumData = Array.Empty<float>();
        public float[] SpectrumData
        {
            get => _spectrumData;
            set => SetProperty(ref _spectrumData, value);
        }

        private float _vuRight;
        public float VuRight
        {
            get => _vuRight;
            set => SetProperty(ref _vuRight, value);
        }



        private double _pitch = 1.0;
        public double Pitch
        {
            get => _pitch;
            set
            {
                if (SetProperty(ref _pitch, value))
                {
                    _playerService.Pitch = value;
                    PersistPlaybackSettings(c => c.PlaybackPitch = value);
                }
            }
        }

        /// <summary>Writes a playback setting change to disk. Fire-and-forget, same pattern as
        /// other ViewModels' incidental settings saves (e.g. DownloadManager, SearchViewModel).</summary>
        private void PersistPlaybackSettings(Action<AppConfig> apply)
        {
            if (_config == null || _configManager == null) return;
            apply(_config);
            _ = _configManager.SaveAsync(_config);
        }
        
        // Shuffle history to prevent immediate repeats
        private readonly List<int> _shuffleHistory = new();

        public ICommand TogglePlayPauseCommand { get; }
        public ICommand StopCommand { get; }
        public ICommand NextTrackCommand { get; }
        public ICommand PreviousTrackCommand { get; }
        public ICommand AddToQueueCommand { get; }
        public ICommand RemoveFromQueueCommand { get; }

        // Visual Style Commands
        public ReactiveCommand<Unit, Unit> CycleVisualStyleCommand { get; }
        public ICommand ClearQueueCommand { get; }
        public ICommand ToggleShuffleCommand { get; }
        public ICommand ToggleRepeatCommand { get; }
        public ICommand ToggleCrossfadeCommand { get; }
        public ICommand TogglePlayerDockCommand { get; }
        public ICommand ToggleQueueCommand { get; }
        public ICommand ToggleLikeCommand { get; } // Phase 9.3
        public ICommand ToggleMuteCommand { get; }
        public ICommand ResetPitchCommand { get; }
        public ICommand SeekCommand { get; } // Phase 12.6: Waveform Seeking
        public ICommand SeekForwardCommand { get; }
        public ICommand SeekBackwardCommand { get; }
        public ICommand JumpToIntroCommand { get; }
        public ICommand JumpToBuildCommand { get; }
        public ICommand JumpToDropCommand { get; }
        public ICommand JumpToOutroCommand { get; }
        public ICommand ToggleTheaterModeCommand { get; }
        public ICommand GoBackCommand { get; } // NowPlayingPage back navigation
        public ICommand OpenPlayerViewCommand { get; }
        public ICommand OpenCurrentTrackInspectorCommand { get; }
        public ICommand OpenCurrentTrackWorkstationCommand { get; }
        public ICommand OpenCurrentTrackFlowCommand { get; }
        public ICommand OpenCurrentTrackCueForgeCommand { get; }
        public ICommand LoadCurrentTrackToDeckACommand { get; }
        public ICommand LoadCurrentTrackToDeckBCommand { get; }
        public ICommand AnalyzeCurrentTrackCommand { get; }
        public ICommand SeparateCurrentTrackStemsCommand { get; }
        public ICommand RevealCurrentTrackCommand { get; }
        public ICommand AddCurrentTrackToProjectCommand { get; }
        public ICommand PlayQueueItemCommand { get; }

        // Entertainment Engine Commands
        public ICommand ToggleAmbientModeCommand { get; }
        public ICommand ToggleFlowModeCommand { get; }
        public ICommand ToggleMetadataDrivenVisualsCommand { get; }
        public ICommand ToggleExpandedPlayerCommand { get; }
        public ReactiveCommand<Unit, Unit> CycleVisualizerPresetCommand { get; }

        // Phase 5C: UI Throttling
        private DateTime _lastTimeUpdate = DateTime.MinValue;

        public PlayerViewModel(IAudioPlayerService playerService, DatabaseService databaseService, IEventBus eventBus, ArtworkCacheService artworkCacheService, INavigationService navigationService, IRightPanelService rightPanelService, IAmbientModeService? ambientModeService = null, IFlowModeService? flowModeService = null, AppConfig? config = null, ConfigManager? configManager = null)
        {
            _playerService = playerService;
            _databaseService = databaseService;
            _artworkCacheService = artworkCacheService;
            _eventBus = eventBus;
            _navigationService = navigationService;
            _rightPanelService = rightPanelService;
            _ambientModeService = ambientModeService;
            _flowModeService = flowModeService;
            _config = config;
            _configManager = configManager;

            // Restore persisted playback settings (crossfade/pitch used to reset to defaults every restart)
            if (_config != null)
            {
                _playerService.CrossfadeEnabled = _config.PlaybackCrossfadeEnabled;
                _playerService.CrossfadeSeconds = _config.PlaybackCrossfadeSeconds;
                _pitch = _config.PlaybackPitch;
                _playerService.Pitch = _config.PlaybackPitch;
            }

            // Wire Ambient Mode service events
            if (_ambientModeService is not null)
            {
                _ambientModeService.ActiveChanged += (_, active) =>
                {
                    Dispatcher.UIThread.Post(() => IsAmbientMode = active);
                };
            }

            // Wire Flow Mode service events
            if (_flowModeService is not null)
            {
                _flowModeService.StateChanged += (_, state) =>
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        FlowModeState = state;
                        IsFlowMode = state.IsActive;
                    });
                };
            }
            
            _saveQueueTimer = new System.Threading.Timer(_ => 
            {
                _ = SaveQueueAsync();
            }, null, System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            
            // Phase 6B: Subscribe to playback requests
            eventBus.GetEvent<PlayTrackRequestEvent>().Subscribe(evt => 
            {
                if (evt.Track != null && !string.IsNullOrEmpty(evt.Track.Model.ResolvedFilePath))
                {
                    Dispatcher.UIThread.Post(() =>
                    {
                        // 1. Check if track is already in queue
                        var existing = Queue.FirstOrDefault(t => t.Model.TrackUniqueHash == evt.Track.Model.TrackUniqueHash);
                        if (existing != null)
                        {
                            var index = Queue.IndexOf(existing);
                            CurrentQueueIndex = index;
                            PlayTrackAtIndex(index);
                        }
                        else
                        {
                            // 2. Add to queue and play
                            Queue.Add(evt.Track);
                            var index = Queue.Count - 1;
                            CurrentQueueIndex = index;
                            PlayTrackAtIndex(index);
                        }
                    });
                }
            }).DisposeWith(_disposables);

            eventBus.GetEvent<AddToQueueRequestEvent>().Subscribe(evt => 
            {
                if (evt.Track != null)
                {
                    AddToQueue(evt.Track);
                }
            }).DisposeWith(_disposables);

            eventBus.GetEvent<AnalysisQueueStatusChangedEvent>().Subscribe(evt =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    AnalysisLaneSummary = BuildAnalysisLaneSummary(
                        evt.QueuedCount,
                        evt.ProcessedCount,
                        evt.CurrentTrackHash,
                        evt.IsPaused,
                        evt.PerformanceMode,
                        evt.MaxConcurrency);
                });
            }).DisposeWith(_disposables);

            // Phase 6B: Play Album Request (Queue Management)
            eventBus.GetEvent<PlayAlbumRequestEvent>().Subscribe(evt =>
            {
                if (evt.Tracks == null || !evt.Tracks.Any()) return;
                
                Dispatcher.UIThread.Post(() =>
                {
                    _suppressSave = true;
                    try
                    {
                        // 1. Clear existing queue
                        ClearQueue();
                        
                        // 2. Add all tracks to queue
                        foreach (var track in evt.Tracks)
                        {
                            // Only add tracks with valid file paths
                            if (!string.IsNullOrEmpty(track.ResolvedFilePath))
                            {
                                var vm = new PlaylistTrackViewModel(track, eventBus, null, _artworkCacheService);
                                Queue.Add(vm);
                            }
                        }
                    }
                    finally
                    {
                        _suppressSave = false;
                        DebounceSaveQueue();
                    }
                    
                    // 3. Play first track if any were added
                    if (Queue.Any())
                    {
                        CurrentQueueIndex = 0;
                        PlayTrackAtIndex(0);
                    }
                });
            }).DisposeWith(_disposables);
            
            // Ensure IsPlaying is synced
            IsPlaying = _playerService.IsPlaying;
            
            // Phase 9.6: Removed premature check for IsPlayerInitialized. 
            // AudioPlayerService initializes lazily, so this check was always failing on startup.
            // We now rely on Play() to trigger init and handle errors there.
            
            // Player Service Events via Reactive patterns to ensure cleanup
            Observable.FromEventPattern(h => _playerService.PausableChanged += h, h => _playerService.PausableChanged -= h)
                .Subscribe(_ => Dispatcher.UIThread.Post(() =>
                {
                    IsPlaying = _playerService.IsPlaying;
                    _ambientModeService?.NotifyPlaybackState(_playerService.IsPlaying);
                }))
                .DisposeWith(_disposables);

            Observable.FromEventPattern(h => _playerService.EndReached += h, h => _playerService.EndReached -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => OnEndReached(e.Sender, EventArgs.Empty)))
                .DisposeWith(_disposables);

            // Gapless/crossfade: the engine advanced to a preloaded track on its own (either a
            // hard gapless swap or a completed crossfade) — sync our "now playing" state to
            // match without calling Play() again, since the audio is already running.
            Observable.FromEventPattern(h => _playerService.TrackAdvanced += h, h => _playerService.TrackAdvanced -= h)
                .Subscribe(_ => Dispatcher.UIThread.Post(OnTrackAdvanced))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float>(h => _playerService.PositionChanged += h, h => _playerService.PositionChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(50)) // 20fps for progress markers
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => Position = e.EventArgs)
                .DisposeWith(_disposables);

            Observable.FromEventPattern<long>(h => _playerService.TimeChanged += h, h => _playerService.TimeChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(250)) // 4fps for time text is plenty
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => CurrentTimeStr = TimeSpan.FromMilliseconds(e.EventArgs).ToString(@"m\:ss"))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<long>(h => _playerService.LengthChanged += h, h => _playerService.LengthChanged -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() => 
                {
                    LengthMs = e.EventArgs;
                    TotalTimeStr = TimeSpan.FromMilliseconds(e.EventArgs).ToString(@"m\:ss");
                }))
                .DisposeWith(_disposables);



            Observable.FromEventPattern<AudioLevelsEventArgs>(h => _playerService.AudioLevelsChanged += h, h => _playerService.AudioLevelsChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(40)) // 25fps for VU meters - visually smooth but efficient
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => 
                {
                    VuLeft = e.EventArgs.Left;
                    VuRight = e.EventArgs.Right;
                })
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float[]>(h => _playerService.SpectrumChanged += h, h => _playerService.SpectrumChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(40)) // 25fps for Spectrum
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => SpectrumData = e.EventArgs)
                .DisposeWith(_disposables);

            TogglePlayPauseCommand = new RelayCommand(TogglePlayPause);
            StopCommand = new RelayCommand(Stop);
            NextTrackCommand = new RelayCommand(PlayNextTrack, () => HasNextTrack());
            PreviousTrackCommand = new RelayCommand(PlayPreviousTrack, () => HasPreviousTrack());
            AddToQueueCommand = new RelayCommand<PlaylistTrackViewModel>(AddToQueue);
            RemoveFromQueueCommand = new RelayCommand<PlaylistTrackViewModel>(RemoveFromQueue);

            CycleVisualStyleCommand = ReactiveCommand.Create(() => 
            {
                var values = Enum.GetValues<VisualizerStyle>();
                int next = ((int)CurrentVisualStyle + 1) % values.Length;
                CurrentVisualStyle = (VisualizerStyle)next;
            });
            ClearQueueCommand = new RelayCommand(ClearQueue, () => Queue.Any());
            ToggleShuffleCommand = new RelayCommand(ToggleShuffle);
            ToggleRepeatCommand = new RelayCommand(ToggleRepeat);
            ToggleCrossfadeCommand = new RelayCommand(() => IsCrossfadeEnabled = !IsCrossfadeEnabled);
            TogglePlayerDockCommand = new RelayCommand(TogglePlayerDock);
            ToggleQueueCommand = new RelayCommand(ToggleQueue);
            ToggleLikeCommand = new AsyncRelayCommand(ToggleLikeAsync); // Phase 9.3
            ToggleMuteCommand = new RelayCommand(ToggleMute);
            ResetPitchCommand = new RelayCommand(() => Pitch = 1.0);
            SeekCommand = new RelayCommand<float>(Seek);
            SeekForwardCommand = new RelayCommand(() => SeekRelative(10)); // Seek forward 10 seconds
            SeekBackwardCommand = new RelayCommand(() => SeekRelative(-10)); // Seek backward 10 seconds
            JumpToIntroCommand = new RelayCommand(() => JumpToPhrase(CueRole.Intro, 0.08d));
            JumpToBuildCommand = new RelayCommand(() => JumpToPhrase(CueRole.Build, 0.35d));
            JumpToDropCommand = new RelayCommand(() => JumpToPhrase(CueRole.Drop, 0.55d));
            JumpToOutroCommand = new RelayCommand(() => JumpToPhrase(CueRole.Outro, 0.82d));
            ToggleTheaterModeCommand = new RelayCommand(() => _eventBus.Publish(new RequestTheaterModeEvent()));
            GoBackCommand = new RelayCommand(() => _eventBus.Publish(new NavigateToPageEvent("Library")));
            OpenPlayerViewCommand = new RelayCommand(() =>
            {
                _ = OpenPlayerViewAsync();
            });
            OpenCurrentTrackInspectorCommand = new RelayCommand(OpenCurrentTrackInspector);
            OpenCurrentTrackWorkstationCommand = new RelayCommand(OpenCurrentTrackWorkstation);
            OpenCurrentTrackFlowCommand = new RelayCommand(OpenCurrentTrackFlow);
            OpenCurrentTrackCueForgeCommand = new RelayCommand(OpenCurrentTrackCueForge);
            LoadCurrentTrackToDeckACommand = new RelayCommand(() => RouteCurrentTrackToWorkstation("A"));
            LoadCurrentTrackToDeckBCommand = new RelayCommand(() => RouteCurrentTrackToWorkstation("B"));
            AnalyzeCurrentTrackCommand = new RelayCommand(AnalyzeCurrentTrack);
            SeparateCurrentTrackStemsCommand = new RelayCommand(SeparateCurrentTrackStems);
            RevealCurrentTrackCommand = new RelayCommand(RevealCurrentTrack);
            AddCurrentTrackToProjectCommand = new RelayCommand(AddCurrentTrackToProject);
            PlayQueueItemCommand = new RelayCommand<PlaylistTrackViewModel>(PlayQueueItem);

            // Entertainment Engine Commands
            ToggleAmbientModeCommand = new RelayCommand(() =>
            {
                if (_ambientModeService is not null)
                    _ambientModeService.Toggle();
                else
                    IsAmbientMode = !IsAmbientMode;
            });
            ToggleFlowModeCommand = new RelayCommand(() =>
            {
                if (_flowModeService is not null)
                    _flowModeService.Toggle();
                else
                    IsFlowMode = !IsFlowMode;
            });
            ToggleMetadataDrivenVisualsCommand = new RelayCommand(() =>
            {
                IsMetadataDrivenVisuals = !IsMetadataDrivenVisuals;
            });
            ToggleExpandedPlayerCommand = new RelayCommand(() =>
            {
                try
                {
                    if (CurrentTrack == null)
                    {
                        PlaybackError = "Load a track before opening the expanded player.";
                        HasPlaybackError = true;
                        IsExpandedPlayerOpen = false;
                        return;
                    }

                    var shouldOpen = !IsExpandedPlayerOpen;
                    IsExpandedPlayerOpen = shouldOpen;

                    if (shouldOpen)
                    {
                        IsQueueOpen = false;
                        _rightPanelService.IsPanelOpen = false;
                    }
                }
                catch (Exception)
                {
                    PlaybackError = "Could not open the expanded player.";
                    HasPlaybackError = true;
                    IsExpandedPlayerOpen = false;
                }
            });
            CycleVisualizerPresetCommand = ReactiveCommand.Create(() =>
            {
                var values = Enum.GetValues<VisualizerPreset>();
                int next = ((int)CurrentVisualizerPreset + 1) % values.Length;
                CurrentVisualizerPreset = (VisualizerPreset)next;
            });

            
            // Phase 0: Queue persistence - auto-save on changes
            Queue.CollectionChanged += OnQueueCollectionChanged;
            
            // Phase 12.6: Waveform Seeking
            eventBus.GetEvent<SeekRequestEvent>().Subscribe(evt => 
            {
                Seek((float)evt.PositionPercent);
            }).DisposeWith(_disposables);

            eventBus.GetEvent<SeekToSecondsRequestEvent>().Subscribe(evt => 
            {
                if (_playerService.Length > 0)
                {
                    Seek((float)(evt.Seconds * 1000.0 / _playerService.Length));
                }
            }).DisposeWith(_disposables);
            // Load saved queue on startup
            _ = LoadQueueAsync();
        }



        private void OnQueueCollectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        {
             if (!_suppressSave)
             {
                 DebounceSaveQueue();
             }
             OnPropertyChanged(nameof(IsQueueEmpty));
        }

        private void DebounceSaveQueue()
        {
            _saveQueueTimer.Change(500, System.Threading.Timeout.Infinite);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (_isDisposed) return;
            if (disposing)
            {
                _errorDismissCts?.Cancel();
                _errorDismissCts?.Dispose();
                _disposables.Dispose();
                Queue.CollectionChanged -= OnQueueCollectionChanged;
                
                // Dispose items in queue if they are IDisposable
                foreach (var item in Queue)
                {
                    item.Dispose();
                }
            }
            _isDisposed = true;
        }

        
        private void ToggleQueue()
        {
            IsQueueOpen = !IsQueueOpen;

            if (_navigationService.CurrentPage?.GetType().Name == "NowPlayingPage")
            {
                return;
            }

            if (IsQueueOpen)
            {
                _rightPanelService.OpenPanel(this, "QUEUE", "📋");
            }
            else
            {
                _rightPanelService.OpenPanel(this, "NOW PLAYING", "🎵");
            }
        }

        private void ToggleMute()
        {
            if (IsMuted)
            {
                IsMuted = false;
                Volume = _preMuteVolume > 0 ? _preMuteVolume : 100;
            }
            else
            {
                _preMuteVolume = _volume;
                IsMuted = true;
                _playerService.Volume = 0;
            }
        }

        private void OpenCurrentTrackInspector()
        {
            if (CurrentTrack == null)
            {
                PlaybackError = "Load a track before opening the inspector.";
                HasPlaybackError = true;
                return;
            }

            var selected = CurrentTrack;
            var selectedQueueIndex = CurrentQueueIndex;
            selected.ClearInspectorA10PairwiseContext();

            IsExpandedPlayerOpen = false;
            IsQueueOpen = false;
            _rightPanelService.OpenPanel(selected, "TRACK INSPECTOR", "🔬");
            _ = TryAttachInspectorPairwiseContextFromQueueAsync(selected, selectedQueueIndex);
        }

        private async Task OpenPlayerViewAsync()
        {
            if (_playerNavigationInFlight)
                return;

            var now = DateTime.UtcNow;
            if (now - _lastPlayerNavigationRequestUtc < PlayerNavigationDebounceWindow)
                return;

            _playerNavigationInFlight = true;
            _lastPlayerNavigationRequestUtc = now;

            try
            {
                IsExpandedPlayerOpen = false;
                IsQueueOpen = false;
                _rightPanelService.IsPanelOpen = false;
                _navigationService.NavigateTo("Player");

                await Task.Delay(PlayerNavigationSettleDelay).ConfigureAwait(false);

                var currentPageName = _navigationService.CurrentPage?.GetType().Name;
                if (!string.Equals(currentPageName, "NowPlayingPage", StringComparison.Ordinal))
                {
                    _navigationService.NavigateTo("Home");
                }
            }
            finally
            {
                _playerNavigationInFlight = false;
            }
        }

        private async System.Threading.Tasks.Task TryAttachInspectorPairwiseContextFromQueueAsync(PlaylistTrackViewModel selected, int selectedQueueIndex)
        {
            try
            {
                var ordered = Queue.ToList();
                if (ordered.Count == 0)
                    return;

                var selectedIndex = selectedQueueIndex;
                if (selectedIndex < 0 || selectedIndex >= ordered.Count || !ReferenceEquals(ordered[selectedIndex], selected))
                    selectedIndex = ordered.IndexOf(selected);

                if (selectedIndex < 0)
                    return;

                PlaylistTrackViewModel? neighbor = null;
                string relationLabel = string.Empty;

                if (selectedIndex + 1 < ordered.Count)
                {
                    neighbor = ordered[selectedIndex + 1];
                    relationLabel = "Next in queue";
                }
                else if (selectedIndex > 0)
                {
                    neighbor = ordered[selectedIndex - 1];
                    relationLabel = "Previous in queue";
                }

                if (neighbor is null)
                    return;

                if (string.IsNullOrWhiteSpace(selected.GlobalId) || string.IsNullOrWhiteSpace(neighbor.GlobalId))
                    return;

                if (global::Avalonia.Application.Current is not SLSKDONET.App app || app.Services is null)
                    return;

                var similarity = app.Services.GetService(typeof(TrackSimilarityService)) as TrackSimilarityService;
                if (similarity is null)
                    return;

                var score = await similarity.ScoreAsync(
                    selected.GlobalId,
                    neighbor.GlobalId,
                    TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);

                if (score is null)
                    return;

                var queueStillMatches = selectedIndex >= 0
                    && selectedIndex < Queue.Count
                    && ReferenceEquals(Queue[selectedIndex], selected)
                    && ReferenceEquals(CurrentTrack, selected)
                    && CurrentQueueIndex == selectedIndex;
                if (!queueStillMatches)
                    return;

                var contextLabel = $"{relationLabel}: {neighbor.ArtistName} - {neighbor.TrackTitle}";
                var reasonTags = string.Join(" • ", score.ReasonTags.Take(2));

                await Dispatcher.UIThread.InvokeAsync(() =>
                    selected.SetInspectorA10PairwiseContext(
                        contextLabel,
                        score.FinalSimilarity,
                        score.VectorScores.Harmonic,
                        score.VectorScores.Rhythm,
                        score.SegmentScores.Drop,
                        reasonTags));
            }
            catch
            {
                // Fail quietly to keep inspector opening resilient.
            }
        }

        private void OpenCurrentTrackWorkstation()
        {
            RouteCurrentTrackToWorkstation();
        }

        private void OpenCurrentTrackCueForge()
        {
            IsExpandedPlayerOpen = false;
            IsQueueOpen = false;
            _navigationService.NavigateTo("CueForge");
        }

        private void OpenCurrentTrackFlow()
        {
            if (CurrentTrack == null)
            {
                PlaybackError = "Load a track before opening the flow workspace.";
                HasPlaybackError = true;
                return;
            }

            if (!CurrentTrack.IsCompleted || string.IsNullOrWhiteSpace(CurrentTrack.Model.ResolvedFilePath))
            {
                PlaybackError = "Only completed local tracks can be sent to the flow workspace.";
                HasPlaybackError = true;
                return;
            }

            HasPlaybackError = false;
            PlaybackError = string.Empty;
            IsExpandedPlayerOpen = false;
            IsQueueOpen = false;
            _rightPanelService.IsPanelOpen = false;
            _eventBus.Publish(new AddToTimelineRequestEvent(new[] { CurrentTrack.Model }));
        }

        private void RouteCurrentTrackToWorkstation(string? preferredDeck = null, bool openStemRack = false)
        {
            if (CurrentTrack == null)
            {
                PlaybackError = "Load a track before opening the workstation.";
                HasPlaybackError = true;
                return;
            }

            if (!CurrentTrack.IsCompleted || string.IsNullOrWhiteSpace(CurrentTrack.Model.ResolvedFilePath))
            {
                PlaybackError = "Only completed local tracks can be sent to the workstation.";
                HasPlaybackError = true;
                return;
            }

            HasPlaybackError = false;
            PlaybackError = string.Empty;
            IsExpandedPlayerOpen = false;
            IsQueueOpen = false;
            _rightPanelService.IsPanelOpen = false;
            _eventBus.Publish(new OpenStemWorkspaceRequestEvent(CurrentTrack.Model, preferredDeck, openStemRack));
        }

        private void AnalyzeCurrentTrack()
        {
            if (CurrentTrack?.AnalyzeTrackCommand?.CanExecute(null) == true)
            {
                CurrentTrack.AnalyzeTrackCommand.Execute(null);
                HasPlaybackError = false;
                PlaybackError = string.Empty;
                return;
            }

            PlaybackError = "Only completed local tracks can be analyzed.";
            HasPlaybackError = true;
        }

        private void SeparateCurrentTrackStems()
        {
            if (CurrentTrack == null)
            {
                PlaybackError = "Load a track before opening the stem rack.";
                HasPlaybackError = true;
                return;
            }

            if (HasPersistedStems(CurrentTrack))
            {
                RouteCurrentTrackToWorkstation(openStemRack: true);
                return;
            }

            if (CurrentTrack.SeparateStemsCommand?.CanExecute(null) == true)
            {
                CurrentTrack.SeparateStemsCommand.Execute(null);
                HasPlaybackError = false;
                PlaybackError = string.Empty;
                return;
            }

            PlaybackError = "Stem prep is only available for completed local tracks.";
            HasPlaybackError = true;
        }

        private void RevealCurrentTrack()
        {
            if (CurrentTrack?.RevealFileCommand?.CanExecute(null) == true)
            {
                CurrentTrack.RevealFileCommand.Execute(null);
                return;
            }

            PlaybackError = "No local file is available to reveal.";
            HasPlaybackError = true;
        }

        private void AddCurrentTrackToProject()
        {
            if (CurrentTrack?.AddToProjectCommand?.CanExecute(null) == true)
            {
                CurrentTrack.AddToProjectCommand.Execute(null);
                return;
            }

            PlaybackError = "Load a completed track before adding it to the mix.";
            HasPlaybackError = true;
        }

        private void PlayQueueItem(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return;

            var index = Queue.IndexOf(track);
            if (index >= 0)
                PlayTrackAtIndex(index);
        }

        // Phase 9.3: Like Feature Implementation
        private async System.Threading.Tasks.Task ToggleLikeAsync()
        {
            if (CurrentTrack == null) return;

            // Toggle local state
            bool newLikedStatus = !IsCurrentTrackLiked;
            IsCurrentTrackLiked = newLikedStatus;

            try
            {
                // Global persistence (Library + all Project instances)
                if (global::Avalonia.Application.Current is SLSKDONET.App app && app.Services != null)
                {
                    var libraryService = app.Services.GetService(typeof(ILibraryService)) as ILibraryService;
                    if (libraryService != null)
                    {
                        await libraryService.UpdateLikeStatusAsync(CurrentTrack.Model.TrackUniqueHash, newLikedStatus);
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to save global like status: {ex.Message}");
                // Revert on failure
                IsCurrentTrackLiked = !newLikedStatus;
            }
        }        
        // Queue Management Methods
        private void OnEndReached(object? sender, EventArgs e)
        {
            Dispatcher.UIThread.Post(() =>
            {
                IsPlaying = false;
                
                // Auto-play next track if available
                if (HasNextTrack())
                {
                    PlayNextTrack();
                }
                else if (RepeatMode == RepeatMode.All && Queue.Any())
                {
                    // Restart queue from beginning
                    CurrentQueueIndex = 0;
                    PlayTrackAtIndex(0);
                }
            });
        }
        
        public void AddToQueue(PlaylistTrackViewModel? track)
        {
            if (track == null) return;
            
            Dispatcher.UIThread.Post(() =>
            {
                Queue.Add(track);
                
                // If nothing playing, start immediately
                if (!IsPlaying && Queue.Count == 1)
                {
                    CurrentQueueIndex = 0;
                    PlayTrackAtIndex(0);
                }
            });
        }
        
        public void RemoveFromQueue(PlaylistTrackViewModel? track)
        {
            if (track == null) return;
            
            Dispatcher.UIThread.Post(() =>
            {
                var index = Queue.IndexOf(track);
                if (index >= 0)
                {
                    Queue.RemoveAt(index);
                    
                    // Adjust current index if needed
                    if (index < CurrentQueueIndex)
                    {
                        CurrentQueueIndex--;
                    }
                    else if (index == CurrentQueueIndex)
                    {
                        // Removed currently playing track
                        if (Queue.Any())
                        {
                            PlayTrackAtIndex(Math.Min(CurrentQueueIndex, Queue.Count - 1));
                        }
                        else
                        {
                            Stop();
                        }
                    }
                }
            });
        }
        
        public void ClearQueue()
        {
            Dispatcher.UIThread.Post(() =>
            {
                Queue.Clear();
                CurrentQueueIndex = -1;
                CurrentTrack = null;
                _shuffleHistory.Clear();
                Stop();
            });
        }
        
        /// <summary>
        /// Moves a track in the queue from one position to another.
        /// Used for drag-and-drop reordering.
        /// </summary>
        public void MoveTrack(string globalId, int targetIndex)
        {
            if (string.IsNullOrEmpty(globalId) || targetIndex < 0)
                return;
                
            Dispatcher.UIThread.Post(() =>
            {
                var track = Queue.FirstOrDefault(t => t.GlobalId == globalId);
                if (track == null) return;
                    
                var oldIndex = Queue.IndexOf(track);
                if (oldIndex < 0 || oldIndex == targetIndex) return;
                    
                targetIndex = Math.Clamp(targetIndex, 0, Queue.Count - 1);
                Queue.Move(oldIndex, targetIndex);
                
                if (oldIndex == CurrentQueueIndex)
                    CurrentQueueIndex = targetIndex;
                else if (oldIndex < CurrentQueueIndex && targetIndex >= CurrentQueueIndex)
                    CurrentQueueIndex--;
                else if (oldIndex > CurrentQueueIndex && targetIndex <= CurrentQueueIndex)
                    CurrentQueueIndex++;
            });
        }
        
        private void PlayNextTrack()
        {
            if (!Queue.Any()) return;
            
            int nextIndex;
            
            if (RepeatMode == RepeatMode.One)
            {
                // Repeat current track
                nextIndex = CurrentQueueIndex;
            }
            else if (IsShuffling)
            {
                nextIndex = GetRandomTrackIndex();
            }
            else
            {
                nextIndex = CurrentQueueIndex + 1;
                if (nextIndex >= Queue.Count)
                {
                    if (RepeatMode == RepeatMode.All)
                    {
                        nextIndex = 0;
                    }
                    else
                    {
                        return; // End of queue
                    }
                }
            }
            
            PlayTrackAtIndex(nextIndex);
        }
        
        private void PlayPreviousTrack()
        {
            if (!Queue.Any()) return;
            
            // If more than 3 seconds into track, restart current track
            if (Position > 0.05f)
            {
                Seek(0);
                return;
            }
            
            int prevIndex = CurrentQueueIndex - 1;
            if (prevIndex < 0)
            {
                if (RepeatMode == RepeatMode.All)
                {
                    prevIndex = Queue.Count - 1;
                }
                else
                {
                    return; // Start of queue
                }
            }
            
            PlayTrackAtIndex(prevIndex);
        }
        
        private void PlayTrackAtIndex(int index)
        {
            if (index < 0 || index >= Queue.Count) return;

            var track = Queue[index];
            SetNowPlayingState(index, track);

            var filePath = track.Model?.ResolvedFilePath;
            if (!string.IsNullOrEmpty(filePath))
            {
                PlayTrack(filePath, track.Title ?? "Unknown", track.Artist ?? "Unknown");
            }

            SchedulePreloadNext();
        }

        /// <summary>
        /// Updates queue position, current-track pointer, artwork, and like status. Shared by
        /// PlayTrackAtIndex (starting a track ourselves) and OnTrackAdvanced (the engine already
        /// advanced audio to a preloaded track and we're just syncing UI state to match).
        /// </summary>
        private void SetNowPlayingState(int index, PlaylistTrackViewModel track)
        {
            CurrentQueueIndex = index;
            CurrentTrack = track;

            // Phase 9.2 & 9.3: Set album artwork and like status
            Dispatcher.UIThread.Post(async () =>
            {
                AlbumArtUrl = track.Model?.AlbumArtUrl;
                IsCurrentTrackLiked = track.Model?.IsLiked ?? false;

                // Ensure bitmap is loaded for UI
                // Phase 0: Artwork loaded via Proxy
                // if (track.ArtworkBitmap == null) await track.LoadAlbumArtworkAsync();
            });
        }

        /// <summary>Queue index the engine currently has preloaded, or null if nothing is preloaded.</summary>
        private int? _preloadedQueueIndex;

        /// <summary>
        /// Works out what track will play after the current one (without side effects) and
        /// asks the engine to have it hot and ready, so the transition can be gapless or
        /// crossfaded instead of a cold file-open. Call whenever the current track changes or
        /// anything that affects "what's next" changes (repeat mode, shuffle toggle).
        /// </summary>
        private void SchedulePreloadNext()
        {
            var nextIndex = PeekNextIndex();
            if (nextIndex is int idx && idx >= 0 && idx < Queue.Count)
            {
                var path = Queue[idx].Model?.ResolvedFilePath;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    _playerService.PreloadNext(path);
                    _preloadedQueueIndex = idx;
                    return;
                }
            }

            _playerService.CancelPreload();
            _preloadedQueueIndex = null;
        }

        /// <summary>
        /// Determines the next queue index without mutating any state (in particular, without
        /// touching shuffle history) so it's safe to call speculatively for preloading. Shuffle
        /// mode intentionally returns null — the actual pick has side effects (recorded into
        /// shuffle history) that must only happen once, at the moment playback truly advances.
        /// </summary>
        private int? PeekNextIndex()
        {
            if (!Queue.Any()) return null;
            if (IsShuffling) return null;
            if (RepeatMode == RepeatMode.One) return CurrentQueueIndex;

            var nextIndex = CurrentQueueIndex + 1;
            if (nextIndex >= Queue.Count)
            {
                return RepeatMode == RepeatMode.All ? 0 : null;
            }

            return nextIndex;
        }

        /// <summary>
        /// The engine autonomously advanced to the preloaded track (gapless swap or crossfade
        /// completion). Sync our "now playing" state to match — the audio is already playing,
        /// so this must NOT call PlayTrack()/Play() again.
        /// </summary>
        private void OnTrackAdvanced()
        {
            if (_preloadedQueueIndex is not int index || index < 0 || index >= Queue.Count)
            {
                return;
            }

            var track = Queue[index];
            _preloadedQueueIndex = null;
            SetNowPlayingState(index, track);
            SchedulePreloadNext();
        }

        private bool HasNextTrack()
        {
            if (!Queue.Any()) return false;
            if (RepeatMode != RepeatMode.Off) return true;
            return CurrentQueueIndex < Queue.Count - 1;
        }
        
        private bool HasPreviousTrack()
        {
            if (!Queue.Any()) return false;
            if (RepeatMode == RepeatMode.All) return true;
            return CurrentQueueIndex > 0;
        }
        
        private int GetRandomTrackIndex()
        {
            if (Queue.Count <= 1) return 0;
            
            var random = new Random();
            int nextIndex;
            int attempts = 0;
            
            do
            {
                nextIndex = random.Next(Queue.Count);
                attempts++;
            }
            while (_shuffleHistory.Contains(nextIndex) && attempts < 10);
            
            // Track shuffle history (last 10 tracks)
            _shuffleHistory.Add(nextIndex);
            if (_shuffleHistory.Count > 10)
            {
                _shuffleHistory.RemoveAt(0);
            }
            
            return nextIndex;
        }
        
        private void ToggleShuffle()
        {
            IsShuffling = !IsShuffling;
            if (!IsShuffling)
            {
                _shuffleHistory.Clear();
            }
        }
        
        private void ToggleRepeat()
        {
            RepeatMode = RepeatMode switch
            {
                RepeatMode.Off => RepeatMode.All,
                RepeatMode.All => RepeatMode.One,
                RepeatMode.One => RepeatMode.Off,
                _ => RepeatMode.Off
            };
        }

        public static string BuildTrackContextSummary(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return "Load a track to inspect analysis and send it to the workstation";

            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(track.BpmDisplay) && track.BpmDisplay != "—")
                parts.Add($"{track.BpmDisplay} BPM");

            if (!string.IsNullOrWhiteSpace(track.CamelotDisplay) && track.CamelotDisplay != "—")
                parts.Add(track.CamelotDisplay);

            var genre = !string.IsNullOrWhiteSpace(track.DetectedSubGenre)
                ? track.DetectedSubGenre
                : !string.IsNullOrWhiteSpace(track.Model.PrimaryGenre)
                    ? track.Model.PrimaryGenre
                    : track.Model.Genres?
                        .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                        .FirstOrDefault();

            if (!string.IsNullOrWhiteSpace(genre))
                parts.Add(genre);

            return parts.Count == 0
                ? "Ready for playback • open the Inspector for deeper track analysis"
                : string.Join(" • ", parts.Take(3));
        }

        public static string BuildTrackWorkflowHint(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return "Queue a track to prep cues, inspect analysis, and route it into the workstation";

            var parts = new List<string>();
            parts.Add(track.IsCompleted
                ? (track.HasAnalysisData ? "Analysis ready" : "Playback ready")
                : track.StatusText);

            if (!string.IsNullOrWhiteSpace(track.EnergyRating) && track.EnergyRating != "—")
                parts.Add($"Energy {track.EnergyRating}/10");

            parts.Add(track.HasCues
                ? $"{track.Cues.Count()} cues loaded"
                : "cue prep next");

            return string.Join(" • ", parts);
        }

        public static string BuildWorkstationPrepSummary(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return "Prep a track to route it into the workstation";

            if (!track.IsCompleted)
                return "Finish the download before workstation prep";

            bool hasPersistedCues = track.HasCues || !string.IsNullOrWhiteSpace(track.Model?.CuePointsJson);
            bool hasPersistedStems = HasPersistedStems(track);

            var parts = new List<string>
            {
                track.HasAnalysisData && hasPersistedCues ? "Workstation ready" : track.HasAnalysisData ? "Analysis loaded" : "Analyze for waveform and timing",
                hasPersistedCues ? "cue jumps ready" : "cue prep recommended",
                hasPersistedStems ? "stem rack ready" : "stems on demand"
            };

            return string.Join(" • ", parts);
        }

        public static string BuildRoutingSummary(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return "Route a track into Flow, Deck A/B, or the mix project from here";

            if (!track.IsCompleted)
                return "Complete the download to unlock deck routing and flow handoff";

            var handoff = track.HasAnalysisData ? "deck handoff primed" : "analysis sharpens the handoff";
            var project = "mix project armed";
            var stems = HasPersistedStems(track) ? "stem rack on standby" : "stems available on demand";
            return $"Flow launch ready • {project} • {handoff} • {stems}";
        }

        public static string BuildAnalysisLaneSummary(int queuedCount, int processedCount, string? currentTrackHash, bool isPaused, string? performanceMode, int maxConcurrency)
        {
            var mode = string.IsNullOrWhiteSpace(performanceMode) ? "Standard" : performanceMode;
            var concurrency = maxConcurrency > 0 ? $"{maxConcurrency} lane{(maxConcurrency == 1 ? string.Empty : "s")}" : "auto lanes";

            if (queuedCount <= 0 && string.IsNullOrWhiteSpace(currentTrackHash))
            {
                return $"Analysis lane idle • {processedCount} prepped • {mode} • {concurrency}";
            }

            if (isPaused)
            {
                return $"Analysis paused • {queuedCount} queued • {processedCount} prepped • {mode}";
            }

            return $"Analysis rolling • {queuedCount} queued • {processedCount} prepped • {mode} • {concurrency}";
        }

        public static string BuildTransitionPlanSummary(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return "Transition plan: load a track to map intro, drop, and exit anchors";

            if (!track.IsCompleted)
                return "Transition plan: finish the download before setting your entry and exit strategy";

            bool hasPersistedCues = track.HasCues || !string.IsNullOrWhiteSpace(track.Model?.CuePointsJson);
            var energy = !string.IsNullOrWhiteSpace(track.EnergyRating) && track.EnergyRating != "—"
                ? $"energy {track.EnergyRating}/10"
                : "energy pending";

            if (!hasPersistedCues)
                return $"Transition plan: analyze this track for intro/drop/outro anchors • {energy}";

            return $"Transition plan: intro in • drop handoff ready • outro exit mapped • {energy}";
        }

        private static bool HasPersistedStems(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return false;

            if (track.HasStems)
                return true;

            var resolvedFilePath = track.Model?.ResolvedFilePath;
            if (string.IsNullOrWhiteSpace(resolvedFilePath))
                return false;

            try
            {
                var trackDir = System.IO.Path.GetDirectoryName(resolvedFilePath);
                var trackName = System.IO.Path.GetFileNameWithoutExtension(resolvedFilePath);

                if (string.IsNullOrWhiteSpace(trackDir) || string.IsNullOrWhiteSpace(trackName))
                    return false;

                var stemPathA = System.IO.Path.Combine(trackDir, "Stems", trackName);
                var stemPathB = System.IO.Path.Combine(trackDir, $"{trackName}_Stems");
                var stemPathC = System.IO.Path.Combine(trackDir, "_stems");

                return (System.IO.Directory.Exists(stemPathA) && System.IO.Directory.GetFiles(stemPathA).Length > 0)
                    || (System.IO.Directory.Exists(stemPathB) && System.IO.Directory.GetFiles(stemPathB).Length > 0)
                    || (System.IO.Directory.Exists(stemPathC) && System.IO.Directory.GetFiles(stemPathC).Length > 0);
            }
            catch
            {
                return false;
            }
        }

        public static string BuildPhraseJumpSummary(IEnumerable<OrbitCue>? cues)
        {
            if (cues == null)
                return "Quick jump: guided Intro / Build / Drop / Outro";

            var ordered = cues
                .Where(c => c is not null)
                .OrderBy(c => c.Timestamp)
                .ToList();

            if (ordered.Count == 0)
                return "Quick jump: guided Intro / Build / Drop / Outro";

            var labels = new List<string>();

            if (FindFirstCueByRoles(ordered, CueRole.Intro, CueRole.PhraseStart) is not null)
                labels.Add("Intro");
            if (FindFirstCueByRoles(ordered, CueRole.Build, CueRole.Bridge) is not null)
                labels.Add("Build");
            if (FindFirstCueByRoles(ordered, CueRole.Drop, CueRole.Climax, CueRole.KickIn) is not null)
                labels.Add("Drop");
            if (FindLatestCueByRoles(ordered, CueRole.Outro, CueRole.Breakdown2, CueRole.Breakdown) is not null)
                labels.Add("Outro");

            return labels.Count == 0
                ? "Quick jump: guided Intro / Build / Drop / Outro"
                : $"Quick jump: {string.Join(" · ", labels)}";
        }

        private static string BuildStatusBadge(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return "STATUS IDLE";

            if (!track.IsCompleted)
                return $"STATUS {track.StatusText.ToUpperInvariant()}";

            if (track.HasAnalysisData && track.HasCues)
                return "STATUS MIX-READY";

            if (track.HasAnalysisData)
                return "STATUS ANALYZED";

            return "STATUS READY";
        }
        
        private void TogglePlayerDock()
        {
            // 3-state cycle for music panel button:
            // State 1: Visible + Bottom (sidepanel can be open) → State 2: Hidden
            // State 2: Hidden → State 3: Visible + RightSidebar  
            // State 3: Visible + RightSidebar → State 1: Visible + Bottom
            
            if (IsPlayerVisible && CurrentDockLocation == PlayerDockLocation.BottomBar)
            {
                // State 1 → State 2: Hide everything  
                IsPlayerVisible = false;
            }
            else if (!IsPlayerVisible)
            {
                // State 2 → State 3: Show player on right only
                IsPlayerVisible = true;
                CurrentDockLocation = PlayerDockLocation.RightSidebar;
            }
            else // IsPlayerVisible && CurrentDockLocation == RightSidebar
            {
                // State 3 → State 1: Move to bottom (allows sidepanel)
                CurrentDockLocation = PlayerDockLocation.BottomBar;
            }
        }

        private string? _currentFilePath;

        private void TogglePlayPause()
        {
            if (IsPlaying)
            {
                _playerService.Pause();
                IsPlaying = false; // Update immediate state
            }
            else
            {
                // Case 1: Track is loaded but paused/stopped
                // Check _currentFilePath (Ad-hoc play) OR CurrentTrack (Queue play)
                string? path = _currentFilePath ?? CurrentTrack?.Model?.ResolvedFilePath;
                string title = TrackTitle;
                string artist = TrackArtist;

                if (!string.IsNullOrEmpty(path))
                {
                    // Case: Track is loaded. Check if we can resume.
                    // We check if we are significantly into the track and not at the end.
                    bool canResume = _playerService.Length > 0 && _playerService.Time < _playerService.Length - 100; // 100ms buffer
                    
                    if (canResume)
                    {
                        _playerService.Pause(); // Resume
                        IsPlaying = true; // Assume success
                    }
                    else
                    {
                       // Track finished or not loaded. Restart.
                       PlayTrack(path!, CurrentTrack?.Title ?? "Unknown", CurrentTrack?.Artist ?? "Unknown");
                    }
                }
                // Case 2: No track loaded, but Queue has items
                else if (Queue.Any())
                {
                    // Start from beginning or current index
                    if (CurrentQueueIndex < 0) CurrentQueueIndex = 0;
                    PlayTrackAtIndex(CurrentQueueIndex);
                }
                
                IsPlaying = _playerService.IsPlaying;
            }
        }

        private void Stop()
        {
            _playerService.Stop();
            IsPlaying = false;
            Position = 0;
            CurrentTimeStr = "0:00";
        }
        
        // Volume Change
        private void OnVolumeChanged(int value)
        {
            if (IsMuted && value > 0)
            {
                IsMuted = false;
            }
            _playerService.Volume = IsMuted ? 0 : value;
        }

        // Seek (User Drag)
        public void Seek(float position)
        {
            _playerService.Position = position;
        }

        private void JumpToPhrase(CueRole role, double fallbackRatio)
        {
            if (_playerService.Length <= 0)
                return;

            double durationSeconds = _playerService.Length / 1000.0;
            double targetSeconds = ResolvePhraseJumpTarget(_currentTrack?.Cues, role, durationSeconds, fallbackRatio);
            float targetPosition = (float)Math.Clamp(targetSeconds * 1000.0 / _playerService.Length, 0.0, 1.0);
            Seek(targetPosition);
        }

        private static OrbitCue? FindFirstCueByRoles(IEnumerable<OrbitCue>? cues, params CueRole[] roles)
        {
            if (cues == null)
                return null;

            var roleSet = roles.Length == 0 ? null : new HashSet<CueRole>(roles);
            return cues
                .Where(c => c is not null && (roleSet == null || roleSet.Contains(c.Role)))
                .OrderBy(c => c.Timestamp)
                .FirstOrDefault();
        }

        private static OrbitCue? FindLatestCueByRoles(IEnumerable<OrbitCue>? cues, params CueRole[] roles)
        {
            if (cues == null)
                return null;

            var roleSet = roles.Length == 0 ? null : new HashSet<CueRole>(roles);
            return cues
                .Where(c => c is not null && (roleSet == null || roleSet.Contains(c.Role)))
                .OrderByDescending(c => c.Timestamp)
                .FirstOrDefault();
        }

        public static double ResolvePhraseJumpTarget(IEnumerable<OrbitCue>? cues, CueRole role, double durationSeconds, double fallbackRatio)
        {
            OrbitCue? cue = role switch
            {
                CueRole.Intro => FindFirstCueByRoles(cues, CueRole.Intro, CueRole.PhraseStart),
                CueRole.Build => FindFirstCueByRoles(cues, CueRole.Build, CueRole.Bridge),
                CueRole.Drop => FindFirstCueByRoles(cues, CueRole.Drop, CueRole.Climax, CueRole.KickIn),
                CueRole.Outro => FindLatestCueByRoles(cues, CueRole.Outro, CueRole.Breakdown2, CueRole.Breakdown),
                _ => FindFirstCueByRoles(cues, role)
            };

            if (cue is not null)
                return Math.Max(0d, cue.Timestamp);

            return Math.Max(0d, durationSeconds * Math.Clamp(fallbackRatio, 0d, 1d));
        }

        private void AttachCurrentTrackObservers(PlaylistTrackViewModel? previousTrack, PlaylistTrackViewModel? nextTrack)
        {
            if (previousTrack is not null)
                previousTrack.PropertyChanged -= OnCurrentTrackPropertyChanged;

            if (nextTrack is not null)
                nextTrack.PropertyChanged += OnCurrentTrackPropertyChanged;
        }

        private void OnCurrentTrackPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            RaiseCurrentTrackSummaryProperties();
        }

        private void RaiseCurrentTrackSummaryProperties()
        {
            OnPropertyChanged(nameof(WaveformData));
            OnPropertyChanged(nameof(HasCurrentTrack));
            OnPropertyChanged(nameof(CurrentTrackContextSummary));
            OnPropertyChanged(nameof(CurrentTrackWorkflowHint));
            OnPropertyChanged(nameof(CurrentTrackWorkstationPrepSummary));
            OnPropertyChanged(nameof(CurrentTrackRoutingSummary));
            OnPropertyChanged(nameof(CurrentTrackTransitionPlanSummary));
            OnPropertyChanged(nameof(CurrentTrackStatusBadge));
            OnPropertyChanged(nameof(CurrentTrackTempoBadge));
            OnPropertyChanged(nameof(CurrentTrackKeyBadge));
            OnPropertyChanged(nameof(CurrentTrackEnergyBadge));
            OnPropertyChanged(nameof(CurrentTrackCueBadge));
            OnPropertyChanged(nameof(CurrentTrackPhraseJumpSummary));
        }

        // Seek relative by seconds
        private void SeekRelative(double seconds)
        {
            if (_playerService.Length > 0)
            {
                double currentSeconds = _playerService.Position * _playerService.Length / 1000.0;
                double newSeconds = Math.Max(0, Math.Min(_playerService.Length / 1000.0, currentSeconds + seconds));
                double newPosition = newSeconds * 1000.0 / _playerService.Length;
                _playerService.Position = (float)newPosition;
            }
        }
        
        // Helper to load track
        public void PlayTrack(string filePath, string title, string artist)
            => LoadTrackCore(filePath, title, artist, autoPlay: true);

        /// <summary>
        /// Loads a track "hot and ready" without starting playback — e.g. Cue Forge loading
        /// whatever track it's editing so Play/Seek/Audition have something to act on, without
        /// audibly blipping the track that's about to be silently loaded. A PlayTrack()-then-
        /// immediately-Pause() sequence still lets a moment of real audio through the WASAPI
        /// buffer before the pause takes effect.
        /// </summary>
        public void LoadTrackPaused(string filePath, string title, string artist)
            => LoadTrackCore(filePath, title, artist, autoPlay: false);

        private void LoadTrackCore(string filePath, string title, string artist, bool autoPlay)
        {
            Console.WriteLine($"[PlayerViewModel] LoadTrackCore called with: {filePath} (autoPlay={autoPlay})");

            // Phase 9.2: Show loading state
            Dispatcher.UIThread.Post(() =>
            {
                IsLoading = true;
                HasPlaybackError = false;
                PlaybackError = string.Empty;
            });

            try
            {
                _currentFilePath = filePath;
                TrackTitle = title;
                TrackArtist = artist;

                if (autoPlay) _playerService.Play(filePath);
                else _playerService.LoadWithoutPlaying(filePath);
                IsPlaying = autoPlay;

                // Hide loading state
                Dispatcher.UIThread.Post(() => IsLoading = false);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Playback error: {ex.Message}");

                // Phase 9.2: Show error state with thread-safe updates
                Dispatcher.UIThread.Post(() =>
                {
                    IsLoading = false;
                    HasPlaybackError = true;
                    PlaybackError = $"Playback failed: {ex.Message}";

                    // Cancel any previous auto-dismiss, then schedule a new one
                    _errorDismissCts?.Cancel();
                    _errorDismissCts?.Dispose();
                    var cts = new System.Threading.CancellationTokenSource();
                    _errorDismissCts = cts;
                    _ = DismissErrorAfterDelayAsync(cts.Token);
                });

                IsPlaying = false;
            }
        }

        // Phase 0: Queue Persistence Methods

        /// <summary>
        /// Auto-dismisses the playback error after 7 seconds unless cancelled.
        /// </summary>
        private async System.Threading.Tasks.Task DismissErrorAfterDelayAsync(System.Threading.CancellationToken cancellationToken)
        {
            try
            {
                await System.Threading.Tasks.Task.Delay(7000, cancellationToken).ConfigureAwait(false);
                Dispatcher.UIThread.Post(() =>
                {
                    HasPlaybackError = false;
                    PlaybackError = string.Empty;
                });
            }
            catch (OperationCanceledException)
            {
                // Dismissed early or a new error occurred — no action needed.
            }
        }

        /// <summary>
        /// Saves the current queue to the database.
        /// </summary>
        private async System.Threading.Tasks.Task SaveQueueAsync()
        {
            try
            {
                var queueItems = Queue.Select((track, index) => (
                    trackId: track.Id,
                    position: index,
                    isCurrent: index == CurrentQueueIndex
                )).ToList();

                await _databaseService.SaveQueueAsync(queueItems);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to save queue: {ex.Message}");
            }
        }

        /// <summary>
        /// Loads the saved queue from the database on startup.
        /// </summary>
        private async System.Threading.Tasks.Task LoadQueueAsync()
        {
            try
            {
                var savedQueue = await _databaseService.LoadQueueAsync();
                
                if (!savedQueue.Any())
                    return;

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    Queue.Clear();
                    
                    int currentIndex = -1;
                    for (int i = 0; i < savedQueue.Count; i++)
                    {
                        var (track, isCurrent) = savedQueue[i];
                        var vm = new PlaylistTrackViewModel(track);
                        Queue.Add(vm);
                        
                        if (isCurrent)
                            currentIndex = i;
                    }
                    
                    // Restore current track position
                    if (currentIndex >= 0 && currentIndex < Queue.Count)
                    {
                        CurrentQueueIndex = currentIndex;
                        CurrentTrack = Queue[currentIndex];
                    }
                    
                    Console.WriteLine($"[PlayerViewModel] Loaded {savedQueue.Count} tracks from saved queue");
                });
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[PlayerViewModel] Failed to load queue: {ex.Message}");
            }
        }

        // Drag & Drop
        // TODO: Fix drag-drop library reference
        /*
        public DraggingServiceDropEvent OnDropQueue => (DraggingServiceDropEventsArgs args) => {
            var droppedTracks = DragContext.Current as List<PlaylistTrackViewModel>;
            if (droppedTracks != null && droppedTracks.Any())
            {
                Dispatcher.UIThread.Post(() => {
                    foreach (var track in droppedTracks)
                    {
                        AddToQueue(track);
                    }
                });
            }
        };
        */
    }
}
