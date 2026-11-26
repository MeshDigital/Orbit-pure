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
        private readonly SLSKDONET.Services.Repositories.ITransitionRepository? _transitionRepository;
        private readonly SLSKDONET.Services.Repositories.ITrackRepository? _trackRepository;
        private readonly ICuePointService? _cuePointService;
        private readonly IDialogService? _dialogService;
        private readonly SLSKDONET.Services.AnalysisQueueService? _analysisQueueService;
        // Singletons — stopped whenever real queue/playlist playback starts (see LoadTrackCore),
        // so a Mix Editor waveform click-preview or a Library row hover-preview never keeps
        // playing underneath the track the user actually pressed play on. Both are self-contained
        // WASAPI outputs (never hijack this main player), so nothing crashes if they overlap —
        // they'd just audibly mix together, which is the actual problem this prevents.
        private readonly SLSKDONET.Services.Audio.ILibraryPreviewPlayer? _libraryPreviewPlayer;
        private readonly SLSKDONET.Services.Audio.ITransitionPreviewPlayer? _transitionPreviewPlayer;
        private static readonly SLSKDONET.Engine.Transitions.TransitionEngine _pointSuggestionEngine = new();

        /// <summary>The same singleton instance the CONTEXT sidepanel's "Mix" tab uses (see
        /// SidebarViewModel) — shared so loading a pair here and loading one via a badge click
        /// elsewhere always agree on state, matching the existing OpenMixTransitionCommand's
        /// choice to route through the same shared editor rather than a separate copy.</summary>
        public MixTransitionViewModel? MixTransitionVm { get; }

        // Waveform appearance pass-through — set once from AppConfig in the constructor.
        public bool WaveformUseNeonPalette { get; }
        public double WaveformGain { get; }
        public bool WaveformShowEnergyCurve { get; }
        public bool WaveformShowVocalGhost { get; }
        public bool WaveformShowPhraseSections { get; }
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
            set
            {
                if (SetProperty(ref _isPlaying, value))
                {
                    // AnalysisQueueService.StealthMode (throttled dispatch, halved worker count)
                    // already existed fully built but was never actually turned on by anything —
                    // background track analysis ran at full ProcessorCount/2 parallelism
                    // regardless of whether audio was playing, competing with the real-time audio
                    // thread for CPU and causing exactly the "performance drops while playing and
                    // analysing" symptom. Now follows playback state directly.
                    _analysisQueueService?.SetStealthMode(value);
                }
            }
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

        /// <summary>The next couple of tracks after the one currently playing — a glanceable
        /// "up next" strip that doesn't require opening the full queue panel.</summary>
        public List<PlaylistTrackViewModel> UpNextPreview =>
            Queue.Skip(CurrentQueueIndex + 1).Take(2).ToList();

        public bool HasUpNext => CurrentQueueIndex + 1 < Queue.Count;

        /// <summary>Everything after the playing track, in play order (fullscreen player's Up Next).</summary>
        public List<PlaylistTrackViewModel> UpNextQueue =>
            Queue.Skip(Math.Max(0, CurrentQueueIndex + 1)).ToList();

        /// <summary>"12 up next · 58 min" — or empty when nothing is queued after the current track.</summary>
        public string UpNextSummary
        {
            get
            {
                var upcoming = Queue.Skip(Math.Max(0, CurrentQueueIndex + 1)).ToList();
                if (upcoming.Count == 0) return string.Empty;
                var ms = upcoming.Sum(t => (long)(t.Model?.CanonicalDuration ?? 0));
                var minutes = (int)Math.Round(ms / 60000.0);
                return minutes > 0 ? $"{upcoming.Count} up next · {minutes} min" : $"{upcoming.Count} up next";
            }
        }

        /// <summary>"Track 3 of 14" while playing from the queue.</summary>
        public string QueuePositionText =>
            CurrentQueueIndex >= 0 && CurrentQueueIndex < Queue.Count ? $"Track {CurrentQueueIndex + 1} of {Queue.Count}" : string.Empty;

        private bool _isExpandedQueueVisible = true;
        /// <summary>Up Next column in the fullscreen player; shown by default whenever there is a queue.</summary>
        public bool IsExpandedQueueVisible
        {
            get => _isExpandedQueueVisible;
            set
            {
                if (SetProperty(ref _isExpandedQueueVisible, value))
                    OnPropertyChanged(nameof(ShowExpandedQueue));
            }
        }

        public bool ShowExpandedQueue => IsExpandedQueueVisible && Queue.Count > 0;

        private bool _queueStatesScheduled;

        /// <summary>Coalesces queue/index changes into one pass over the rows (bulk loads add one row at a time).</summary>
        private void ScheduleUpdateQueueStates()
        {
            if (_queueStatesScheduled) return;
            _queueStatesScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _queueStatesScheduled = false;
                UpdateQueueStates();
            }, DispatcherPriority.Background);
        }

        /// <summary>Marks each queue row as played / playing / upcoming and numbers it.</summary>
        internal void UpdateQueueStates()
        {
            var index = CurrentQueueIndex;
            for (int i = 0; i < Queue.Count; i++)
            {
                var row = Queue[i];
                row.QueueNumber = i + 1;
                row.IsQueueCurrent = i == index;
                row.IsQueuePlayed = index >= 0 && i < index;
            }
            OnPropertyChanged(nameof(UpNextQueue));
            OnPropertyChanged(nameof(UpNextSummary));
            OnPropertyChanged(nameof(QueuePositionText));
        }

        // ── Mix transition visibility ────────────────────────────────────────────────────────
        // Previously AudioPlayerService computed all of this (whether a crossfade was active, how
        // far through it was, which preset) with zero external visibility — no UI could show
        // "mixing now" during real playback. Wired to AudioPlayerService.CrossfadeStarted/
        // CrossfadeProgressChanged/CrossfadeEnded in the constructor below.

        private bool _isCrossfading;
        /// <summary>True while the engine is actively crossfading into the next track.</summary>
        public bool IsCrossfading
        {
            get => _isCrossfading;
            set => SetProperty(ref _isCrossfading, value);
        }

        private double _crossfadeProgressPercent;
        /// <summary>0-100 progress through the active crossfade.</summary>
        public double CrossfadeProgressPercent
        {
            get => _crossfadeProgressPercent;
            set => SetProperty(ref _crossfadeProgressPercent, value);
        }

        private string? _activeCrossfadePresetName;
        /// <summary>Preset name driving the crossfade currently in progress, or null for the
        /// legacy fixed crossfade (no saved Mix transition for this pair).</summary>
        public string? ActiveCrossfadePresetName
        {
            get => _activeCrossfadePresetName;
            set => SetProperty(ref _activeCrossfadePresetName, value);
        }

        private string? _upcomingTransitionPresetName;
        /// <summary>Preset name that will drive the crossfade into <see cref="UpNextPreview"/>'s
        /// first track, resolved as soon as a saved Mix transition is found for (current, next) —
        /// visible ahead of time, not just once the crossfade actually starts. Null when no saved
        /// transition exists for this pair (nothing Mix-specific will happen, though the legacy
        /// fixed crossfade may still apply if enabled).</summary>
        public string? UpcomingTransitionPresetName
        {
            get => _upcomingTransitionPresetName;
            set => SetProperty(ref _upcomingTransitionPresetName, value);
        }

        private bool _isMixPanelExpanded;
        /// <summary>Whether the inline Mix editor (dual waveform, preset picker, trigger
        /// controls — the same content the CONTEXT sidepanel's "Mix" tab hosts) is expanded
        /// within the Now Playing screen itself. See <see cref="ToggleMixPanelCommand"/>.</summary>
        public bool IsMixPanelExpanded
        {
            get => _isMixPanelExpanded;
            set => SetProperty(ref _isMixPanelExpanded, value);
        }

        private int _currentQueueIndex = -1;
        public int CurrentQueueIndex
        {
            get => _currentQueueIndex;
            set
            {
                if (SetProperty(ref _currentQueueIndex, value))
                {
                    OnPropertyChanged(nameof(UpNextPreview));
                    OnPropertyChanged(nameof(HasUpNext));
                    ScheduleUpdateQueueStates();
                }
            }
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
                    ResetAndLoadBeatGrid(value);
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
        /// <summary>The file the audio engine really has open; null right after startup, when the queue track is only restored.</summary>
        public string? LoadedFilePath => _playerService.LoadedFilePath;
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
            set
            {
                if (SetProperty(ref _currentVisualizerPreset, value))
                    OnPropertyChanged(nameof(CurrentVisualizerPresetName));
            }
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

        // ── Flow Mode controls ───────────────────────────────────────────────────────────────
        // The Now Playing card exposes what Flow is optimising for (preset), which direction the set
        // should move (energy nudge), and the plan itself. Without these the toggle was the only
        // Flow control in the app and the engine's weights were unreachable.

        /// <summary>The four Flow weightings offered in the Now Playing card.</summary>
        public IReadOnlyList<FlowPresetOption> FlowPresets => FlowPresetOption.All;

        private FlowPresetOption? _selectedFlowPreset;
        /// <summary>The weighting Flow currently scores with; changing it re-plans immediately.</summary>
        public FlowPresetOption SelectedFlowPreset
        {
            get => _selectedFlowPreset ??= FlowPresetOption.For(FlowModeState.ActivePreset);
            set
            {
                if (value is null || ReferenceEquals(value, _selectedFlowPreset)) return;
                if (!SetProperty(ref _selectedFlowPreset, value)) return;

                _flowModeService?.SetPreset(value.Preset);
                OnPropertyChanged(nameof(SelectedFlowPresetDescription));
                PlanFlowPath();
            }
        }

        /// <summary>One-line explanation of the selected preset, shown under the picker.</summary>
        public string SelectedFlowPresetDescription => SelectedFlowPreset.Description;

        /// <summary>
        /// Energy nudge as a −100…+100 slider (the service takes −1…+1). Moving it re-plans the path
        /// so the arc bends toward the new target instead of only affecting the next rebuild.
        /// </summary>
        public double FlowEnergyNudgePercent
        {
            get => Math.Round(FlowModeState.EnergyNudge * 100.0);
            set
            {
                var nudge = Math.Clamp(value / 100.0, -1.0, 1.0);
                if (Math.Abs(nudge - FlowModeState.EnergyNudge) < 0.004) return;

                _flowModeService?.SetEnergyNudge(nudge);
                OnPropertyChanged();
                PlanFlowPath();
            }
        }

        /// <summary>Queue hashes played recently (newest first), so Flow doesn't loop straight back.</summary>
        private readonly LinkedList<string> _flowRecentlyPlayed = new();
        private const int FlowHistoryLength = 8;

        /// <summary>Re-plans Flow from the current track and every other playable queued track.</summary>
        internal void PlanFlowPath()
        {
            if (_flowModeService is null || !IsFlowMode) return;

            var current = CurrentTrack is null ? null : ToFlowCandidate(CurrentTrack);

            var candidates = new List<FlowCandidate>(Queue.Count);
            for (int i = 0; i < Queue.Count; i++)
            {
                // The playing row is excluded by hash inside the planner too; skipping it here keeps
                // a duplicate hash elsewhere in the queue from being treated as "not the current".
                if (i == CurrentQueueIndex) continue;

                // Deliberately a path check, not File.Exists: planning runs on every track change and
                // stat-ing a 500-row queue on the UI thread would stall the handover. The pick is
                // validated for real in PeekFlowNextIndex before anything is preloaded.
                if (string.IsNullOrEmpty(Queue[i].Model?.ResolvedFilePath)) continue;

                candidates.Add(ToFlowCandidate(Queue[i]));
            }

            _flowModeService.RebuildFlowPath(current, candidates, _flowRecentlyPlayed.ToList());

            // Mirror the fresh snapshot immediately instead of waiting for the service's event: the
            // very next thing that happens is SchedulePreloadNext reading FlowModeState.NextStep,
            // and it must never read a stale plan.
            SyncFlowState(_flowModeService.State);
        }

        /// <summary>Coalesces burst queue mutations (bulk loads add row by row) into one re-plan.</summary>
        private bool _flowPlanScheduled;
        private void ScheduleFlowPlan()
        {
            if (_flowPlanScheduled || !IsFlowMode) return;
            _flowPlanScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _flowPlanScheduled = false;
                PlanFlowPath();
            }, DispatcherPriority.Background);
        }

        private void RememberFlowHistory(PlaylistTrackViewModel? track)
        {
            var hash = track?.GlobalId;
            if (string.IsNullOrWhiteSpace(hash)) return;

            _flowRecentlyPlayed.Remove(hash);
            _flowRecentlyPlayed.AddFirst(hash);
            while (_flowRecentlyPlayed.Count > FlowHistoryLength)
                _flowRecentlyPlayed.RemoveLast();
        }

        /// <summary>Flattens a queued row into the feature set Flow scores on.</summary>
        private static FlowCandidate ToFlowCandidate(PlaylistTrackViewModel track)
        {
            var camelot = track.CamelotDisplay;

            return new FlowCandidate
            {
                Hash = track.GlobalId ?? string.Empty,
                Artist = track.Artist ?? string.Empty,
                Title = track.Title ?? string.Empty,
                Bpm = track.BPM,
                CamelotKey = camelot == "—" ? string.Empty : camelot,
                // PlaylistTrackViewModel.Energy collapses "no analysis" to 0; Flow needs to tell the
                // two apart, so 0 becomes the "unknown" sentinel here.
                Energy = track.Energy > 0 ? track.Energy : -1.0,
                Mood = track.HasMood ? track.MoodTag ?? string.Empty : string.Empty,
                Genre = track.PrimaryGenre ?? string.Empty,
            };
        }

        /// <summary>Mirrors a Flow state snapshot into the bindable properties.</summary>
        private void SyncFlowState(FlowModeState state)
        {
            FlowModeState = state;
            IsFlowMode = state.IsActive;

            _selectedFlowPreset = FlowPresetOption.For(state.ActivePreset);
            OnPropertyChanged(nameof(SelectedFlowPreset));
            OnPropertyChanged(nameof(SelectedFlowPresetDescription));
            OnPropertyChanged(nameof(FlowEnergyNudgePercent));
            (NextTrackCommand as RelayCommand)?.RaiseCanExecuteChanged();
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

        private float[] _waveformSamples = Array.Empty<float>();
        /// <summary>Latest mono time-domain block (oscilloscope/phase visuals).</summary>
        public float[] WaveformSamples
        {
            get => _waveformSamples;
            set => SetProperty(ref _waveformSamples, value);
        }

        private float _vuRight;
        public float VuRight
        {
            get => _vuRight;
            set => SetProperty(ref _vuRight, value);
        }

        // Real beat-synced visualization pulse (0 → 1, peaking exactly on each beat, decaying
        // fast between beats). Backed by the track's own analyzed beat grid when available;
        // falls back to a VU-threshold approximation for unanalyzed tracks so the visualizer
        // still reacts to something rather than sitting flat. See UpdateBeatPulseFromGrid /
        // the AudioLevelsChanged subscription in the constructor for the two update paths.
        private double _beatPulse;
        public double BeatPulse
        {
            get => _beatPulse;
            private set => SetProperty(ref _beatPulse, value);
        }

        private double[]? _beatGridSeconds;
        private string? _beatGridLoadedForHash;
        private double _elapsedSecondsSinceStart;
        private double _lastVuBeatTimeSec = double.NegativeInfinity;
        private const double BeatPulseDecayPerSecond = 9.0; // ~250ms to fall under ~10% — punchy, not flickery



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

        /// <summary>Badge-click: opens the "Mix" tab in the CONTEXT sidepanel for this queue
        /// row's transition into the next track — the same event the Library track list's own
        /// Mix badges publish, giving a live-session route into adjusting a transition without
        /// navigating back to the Library page and re-finding the pair.</summary>
        public ICommand OpenMixTransitionCommand { get; }

        /// <summary>Expands/collapses the inline Mix editor in the Now Playing screen itself
        /// (see <see cref="IsMixPanelExpanded"/>) instead of requiring a trip to the separate
        /// CONTEXT-sidepanel "Mix" tab. Loads the current→next pair into the shared
        /// <see cref="MixTransitionVm"/> on expand.</summary>
        public ICommand ToggleMixPanelCommand { get; }

        // Entertainment Engine Commands
        public ICommand ToggleAmbientModeCommand { get; }
        public ICommand ToggleFlowModeCommand { get; }
        public ICommand ToggleMetadataDrivenVisualsCommand { get; }
        public ICommand ToggleExpandedPlayerCommand { get; }
        public ReactiveCommand<Unit, Unit> CycleVisualizerPresetCommand { get; }
        public ReactiveCommand<Unit, Unit> PreviousVisualizerPresetCommand { get; private set; } = null!;
        public ICommand ToggleExpandedQueueCommand { get; private set; } = null!;

        /// <summary>"Spectrum Bars", "Circular Wave"… for the fullscreen player's preset label.</summary>
        public string CurrentVisualizerPresetName =>
            System.Text.RegularExpressions.Regex.Replace(CurrentVisualizerPreset.ToString(), "(?<=[a-z])(?=[A-Z])", " ");

        // Phase 5C: UI Throttling
        private DateTime _lastTimeUpdate = DateTime.MinValue;

        public PlayerViewModel(IAudioPlayerService playerService, DatabaseService databaseService, IEventBus eventBus, ArtworkCacheService artworkCacheService, INavigationService navigationService, IRightPanelService rightPanelService, IAmbientModeService? ambientModeService = null, IFlowModeService? flowModeService = null, AppConfig? config = null, ConfigManager? configManager = null, SLSKDONET.Services.Repositories.ITransitionRepository? transitionRepository = null, MixTransitionViewModel? mixTransitionViewModel = null, SLSKDONET.Services.Repositories.ITrackRepository? trackRepository = null, ICuePointService? cuePointService = null, IDialogService? dialogService = null, SLSKDONET.Services.AnalysisQueueService? analysisQueueService = null, SLSKDONET.Services.Audio.ILibraryPreviewPlayer? libraryPreviewPlayer = null, SLSKDONET.Services.Audio.ITransitionPreviewPlayer? transitionPreviewPlayer = null, SLSKDONET.Services.Transitions.TransitionPlanService? transitionPlanService = null, SLSKDONET.Services.Transitions.ITransitionResolver? transitionResolver = null, SLSKDONET.Services.Transitions.ITransitionPlayLogService? transitionPlayLog = null)
        {
            _transitionPlayLog = transitionPlayLog;
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
            _transitionRepository = transitionRepository;
            MixTransitionVm = mixTransitionViewModel;
            _trackRepository = trackRepository;
            _cuePointService = cuePointService;
            _transitionPlanService = transitionPlanService;
            _transitionResolver = transitionResolver;
            _dialogService = dialogService;
            _analysisQueueService = analysisQueueService;
            _libraryPreviewPlayer = libraryPreviewPlayer;
            _transitionPreviewPlayer = transitionPreviewPlayer;

            // Restore persisted playback settings (crossfade/pitch used to reset to defaults every restart)
            if (_config != null)
            {
                _playerService.CrossfadeEnabled = _config.PlaybackCrossfadeEnabled;
                _playerService.CrossfadeSeconds = _config.PlaybackCrossfadeSeconds;
                _pitch = _config.PlaybackPitch;
                _playerService.Pitch = _config.PlaybackPitch;
            }

            // Waveform appearance — read-only pass-through to AppConfig, same convention as every
            // other settings-backed value in this app (applies on next view load, no live cross-VM
            // push notification, matching how other settings toggles already behave here).
            WaveformUseNeonPalette = !string.Equals(_config?.WaveformPalette, "ClassicRgb", StringComparison.OrdinalIgnoreCase);
            WaveformGain = _config?.WaveformGain ?? 1.0f;
            WaveformShowEnergyCurve = _config?.WaveformShowEnergyCurve ?? true;
            WaveformShowVocalGhost = _config?.WaveformShowVocalGhost ?? true;
            WaveformShowPhraseSections = _config?.WaveformShowPhraseSections ?? true;

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
                    if (Dispatcher.UIThread.CheckAccess()) SyncFlowState(state);
                    else Dispatcher.UIThread.Post(() => SyncFlowState(state));
                };

                // A queue that grows or shrinks changes what Flow may pick; coalesced because a
                // playlist load adds rows one at a time.
                Queue.CollectionChanged += (_, _) => ScheduleFlowPlan();
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

            // Loudness backfill / inspector "Measure": queued rows can be separate view-model
            // instances from the library list, so copy the new value onto them here. Normalised
            // playback reads Model.Loudness when a track starts, so the next play uses it.
            eventBus.GetEvent<TrackLoudnessMeasuredEvent>().Where(e => e.Success).Subscribe(evt =>
            {
                Dispatcher.UIThread.Post(() =>
                {
                    foreach (var queued in Queue)
                        if (queued.Model is { } m && m.TrackUniqueHash == evt.TrackGlobalId)
                            m.Loudness = evt.Lufs;
                });
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
                    // Starting a whole playlist's worth of playback is exactly a "Mix session" —
                    // default to the bottom playbar (Spotify-style) instead of leaving whatever
                    // dock the player happened to be in, so the Up Next/Mix-badge queue strip is
                    // immediately visible without the user having to switch layouts themselves.
                    CurrentDockLocation = PlayerDockLocation.BottomBar;

                    _suppressSave = true;
                    try
                    {
                        // 1. Clear existing queue — inlined rather than calling the public
                        // ClearQueue() helper, which itself does another Dispatcher.UIThread.Post.
                        // Posting from inside a callback that's already running via Post doesn't
                        // run synchronously — it queues a SEPARATE callback to run only after this
                        // entire block (including the "add all tracks" loop and PlayTrackAtIndex
                        // below) has already finished. That meant the "clear" actually ran AFTER
                        // the fresh queue was populated and playback had already started, silently
                        // wiping the queue and stopping playback moments later (observed as a
                        // "Saved queue with 36 items" DB write immediately followed by a "Saved
                        // queue with 0 items" one a second or two after). We're already on the UI
                        // thread here (inside the outer Post), so just mutate directly.
                        Queue.Clear();
                        CurrentQueueIndex = -1;
                        CurrentTrack = null;
                        _shuffleHistory.Clear();
                        Stop();

                        // 2. Add all tracks to queue
                        foreach (var track in evt.Tracks)
                        {
                            // A non-empty ResolvedFilePath alone isn't enough — it can go stale
                            // (file moved/deleted after the path was resolved) without the row's
                            // other fields ever being corrected. Confirmed live: a track with a
                            // stale path made it into the queue at index 0, LoadTrackCore threw
                            // "Could not find file", and playback silently stopped right there —
                            // "Playing Album" had already fired, so the only visible symptom was a
                            // notification with no audio. SchedulePreloadNext already guards the
                            // same way for the same reason; this brings the initial queue build up
                            // to that same standard.
                            if (!string.IsNullOrEmpty(track.ResolvedFilePath) && System.IO.File.Exists(track.ResolvedFilePath))
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
                    
                    // 3. Play the requested start track (or the first) if any were added
                    if (Queue.Any())
                    {
                        int startIndex = 0;
                        if (evt.StartTrackId is Guid startId)
                        {
                            var found = Queue.ToList().FindIndex(t => t.Model?.Id == startId);
                            if (found >= 0) startIndex = found;
                        }
                        CurrentQueueIndex = startIndex;
                        PlayTrackAtIndex(startIndex);

                        // Mix was enabled on the playlist when Play was pressed — surface the
                        // transition settings for the first hop immediately instead of leaving the
                        // user to discover the MIX module (and that it does anything) on their own.
                        if (evt.MixModeEnabled)
                        {
                            ShowMixPanelForCurrentPair();
                        }
                    }
                    else
                    {
                        // Every candidate track's ResolvedFilePath turned out stale (file moved or
                        // deleted since the path was resolved) — nothing playable made it into the
                        // queue. "Playing Album" already fired before this event was processed, so
                        // without this the user sees that toast and then silence with zero clue why.
                        Serilog.Log.Warning("[PlayerViewModel] PlayAlbumRequestEvent had {Count} track(s) but none had a file that still exists on disk", evt.Tracks.Count());
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

            // Mix transition visibility during real playback — see the IsCrossfading/
            // CrossfadeProgressPercent/ActiveCrossfadePresetName properties above.
            Observable.FromEventPattern<CrossfadeStartedEventArgs>(h => _playerService.CrossfadeStarted += h, h => _playerService.CrossfadeStarted -= h)
                .Subscribe(e => Dispatcher.UIThread.Post(() =>
                {
                    IsCrossfading = true;
                    CrossfadeProgressPercent = 0;
                    ActiveCrossfadePresetName = e.EventArgs.PresetName;
                    BeginTrackingActiveMixPlay(e.EventArgs.PresetName);
                }))
                .DisposeWith(_disposables);

            Observable.FromEventPattern<double>(h => _playerService.CrossfadeProgressChanged += h, h => _playerService.CrossfadeProgressChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(50))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => CrossfadeProgressPercent = e.EventArgs * 100.0)
                .DisposeWith(_disposables);

            Observable.FromEventPattern(h => _playerService.CrossfadeEnded += h, h => _playerService.CrossfadeEnded -= h)
                .Subscribe(evt => Dispatcher.UIThread.Post(() =>
                {
                    IsCrossfading = false;
                    CrossfadeProgressPercent = 0;
                    ActiveCrossfadePresetName = null;
                    _ = RecordCompletedMixPlayAsync();
                }))
                .DisposeWith(_disposables);

            // Queue-wide Mix badges (ShowMixTransitionBadge/TransitionPresetLabel/
            // TransitionBadgeColor on each PlaylistTrackViewModel) — recomputed whenever the
            // queue's contents change, e.g. a whole playlist loading in one track-at-a-time burst.
            Queue.CollectionChanged += (_, __) => ScheduleUpdateQueueTransitionBadges();

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

            // Separate, faster-sampled subscription to the same event, purely for beat-pulse
            // timing — the visualizer needs ~25fps granularity, not the 4fps the time label needs.
            Observable.FromEventPattern<long>(h => _playerService.TimeChanged += h, h => _playerService.TimeChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(40))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e =>
                {
                    _elapsedSecondsSinceStart = e.EventArgs / 1000.0;
                    if (_beatGridSeconds is { Length: > 0 } beats)
                        UpdateBeatPulseFromGrid(beats, _elapsedSecondsSinceStart);
                })
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

                    // Fallback beat approximation for tracks with no analyzed beat grid yet —
                    // same decay envelope as the real grid-driven path, just triggered by a VU
                    // threshold crossing instead of an actual beat timestamp.
                    if (_beatGridSeconds == null)
                    {
                        float vu = (VuLeft + VuRight) / 2f;
                        if (vu > 0.4f && _elapsedSecondsSinceStart - _lastVuBeatTimeSec > 0.3)
                            _lastVuBeatTimeSec = _elapsedSecondsSinceStart;

                        double dt = Math.Max(0, _elapsedSecondsSinceStart - _lastVuBeatTimeSec);
                        BeatPulse = Math.Exp(-dt * BeatPulseDecayPerSecond);
                    }
                })
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float[]>(h => _playerService.SpectrumChanged += h, h => _playerService.SpectrumChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(25)) // FFT blocks arrive ~43/s (50% overlap); the visualizer smooths at display rate
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => SpectrumData = e.EventArgs)
                .DisposeWith(_disposables);

            Observable.FromEventPattern<float[]>(h => _playerService.WaveformChanged += h, h => _playerService.WaveformChanged -= h)
                .Sample(TimeSpan.FromMilliseconds(33))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(e => WaveformSamples = e.EventArgs)
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
            ClearQueueCommand = new AsyncRelayCommand(ClearQueueWithConfirmationAsync, () => Queue.Any());
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
            OpenMixTransitionCommand = new RelayCommand<PlaylistTrackViewModel>(OpenMixTransition);
            ToggleMixPanelCommand = new RelayCommand(ToggleMixPanel);

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
                {
                    _flowModeService.Toggle();

                    // The service raises StateChanged (queued onto the dispatcher); mirror it here
                    // first so PlanFlowPath below sees the new state instead of planning on stale
                    // IsFlowMode.
                    SyncFlowState(_flowModeService.State);

                    if (_flowModeService.State.IsActive)
                    {
                        // Flow owns the order while it runs — leaving shuffle on would silently
                        // override every pick, which is exactly the "it does nothing" impression
                        // this mode had.
                        if (IsShuffling) IsShuffling = false;
                        PlanFlowPath();
                    }

                    return;
                }

                IsFlowMode = !IsFlowMode;
                if (IsFlowMode) PlanFlowPath();
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
                    // Closing while in Theater Mode (borderless fullscreen) must leave that too,
                    // or the window stays chrome-less with nothing on screen to get out of it.
                    if (!shouldOpen && IsTheaterMode)
                    {
                        _eventBus.Publish(new RequestTheaterModeEvent());
                        return;
                    }
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
            PreviousVisualizerPresetCommand = ReactiveCommand.Create(() =>
            {
                var values = Enum.GetValues<VisualizerPreset>();
                int prev = ((int)CurrentVisualizerPreset - 1 + values.Length) % values.Length;
                CurrentVisualizerPreset = (VisualizerPreset)prev;
            });
            ToggleExpandedQueueCommand = new RelayCommand(() => IsExpandedQueueVisible = !IsExpandedQueueVisible);

            
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
             OnPropertyChanged(nameof(ShowExpandedQueue));
             OnPropertyChanged(nameof(UpNextPreview));
             OnPropertyChanged(nameof(HasUpNext));
             ScheduleUpdateQueueStates();
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

    }
}
