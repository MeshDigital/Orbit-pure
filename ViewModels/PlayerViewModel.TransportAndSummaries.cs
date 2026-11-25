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


    /// <summary>
    /// Transport controls, phrase jumps, beatgrid pulse tracking, track loading, and UI summaries (#307).
    /// </summary>
    public partial class PlayerViewModel
    {
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
                        _libraryPreviewPlayer?.StopPreview();
                        _transitionPreviewPlayer?.StopPreview();
                        _playerService.Pause(); // Resume
                        IsPlaying = true; // Assume success
                    }
                    else
                    {
                       // Track finished or not loaded. Restart. Thread the loudness gain through
                       // like PlayTrackAtIndex does — omitting it here meant restarting a track
                       // that had already played to the end (via Play, not Next) silently dropped
                       // back to unnormalized volume for the replay.
                       PlayTrack(path!, CurrentTrack?.Title ?? "Unknown", CurrentTrack?.Artist ?? "Unknown", CurrentTrack?.Model?.Loudness);
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

        /// <summary>Enables/disables the Cue Forge metronome click on the shared player (issue #222).</summary>
        public void SetMetronome(IReadOnlyList<double>? beatTimes, double downbeatSeconds, bool enabled) =>
            _playerService.SetMetronome(beatTimes, downbeatSeconds, enabled);

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

        /// <summary>Clears any previous track's beat grid immediately (so a stale grid never
        /// drives the new track's visualizer) and kicks off an async load of the new one.</summary>
        private void ResetAndLoadBeatGrid(PlaylistTrackViewModel? track)
        {
            _beatGridSeconds = null;
            _lastVuBeatTimeSec = double.NegativeInfinity;
            BeatPulse = 0;

            var hash = track?.Model?.TrackUniqueHash;
            _beatGridLoadedForHash = hash;
            if (string.IsNullOrWhiteSpace(hash))
                return;

            _ = LoadBeatGridAsync(hash);
        }

        private async System.Threading.Tasks.Task LoadBeatGridAsync(string hash)
        {
            try
            {
                var features = await _databaseService.GetAudioFeaturesByHashAsync(hash).ConfigureAwait(true);

                // The current track changed again while this was in flight — discard.
                if (!string.Equals(_beatGridLoadedForHash, hash, StringComparison.Ordinal))
                    return;

                if (features is null || string.IsNullOrWhiteSpace(features.BeatGridJson) || features.BeatGridJson == "[]")
                    return;

                var beats = System.Text.Json.JsonSerializer.Deserialize<double[]>(features.BeatGridJson);
                if (beats is { Length: > 0 })
                    _beatGridSeconds = beats;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "PlayerViewModel: failed to load beat grid for {Hash} — visualizer will fall back to VU-based pulsing", hash);
            }
        }

        /// <summary>Real beat-synced pulse: 1.0 exactly at the nearest past beat, decaying
        /// smoothly afterward. Binary search (not an incrementally-advanced index) so seeking
        /// backward/forward is handled correctly with no special-casing.</summary>
        private void UpdateBeatPulseFromGrid(double[] beats, double elapsedSeconds)
        {
            int lo = 0, hi = beats.Length - 1;
            while (lo < hi)
            {
                int mid = (lo + hi + 1) / 2;
                if (beats[mid] <= elapsedSeconds) lo = mid; else hi = mid - 1;
            }

            double dt = Math.Max(0, elapsedSeconds - beats[lo]);
            BeatPulse = Math.Exp(-dt * BeatPulseDecayPerSecond);
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
        public void PlayTrack(string filePath, string title, string artist, double? loudnessLufs = null)
        {
            if (!_resuming) ClearResumePoint();
            _ = LoadTrackCore(filePath, title, artist, autoPlay: true, loudnessLufs);
        }

        /// <summary>
        /// Loads a track "hot and ready" without starting playback — e.g. Cue Forge loading
        /// whatever track it's editing so Play/Seek/Audition have something to act on, without
        /// audibly blipping the track that's about to be silently loaded. A PlayTrack()-then-
        /// immediately-Pause() sequence still lets a moment of real audio through the WASAPI
        /// buffer before the pause takes effect.
        /// </summary>
        public void LoadTrackPaused(string filePath, string title, string artist, double? loudnessLufs = null)
            => _ = LoadTrackCore(filePath, title, artist, autoPlay: false, loudnessLufs);

        private bool LoadTrackCore(string filePath, string title, string artist, bool autoPlay, double? loudnessLufs = null)
        {
            Serilog.Log.Debug("[PlayerViewModel] LoadTrackCore called with: {Path} (autoPlay={AutoPlay})", filePath, autoPlay);

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

                if (autoPlay)
                {
                    // Real playback starting — cannot have several sources racing for the audio
                    // output, so any waveform-click or hover preview stops first.
                    _libraryPreviewPlayer?.StopPreview();
                    _transitionPreviewPlayer?.StopPreview();
                    _playerService.Play(filePath, loudnessLufs);
                }
                else _playerService.LoadWithoutPlaying(filePath, loudnessLufs);
                IsPlaying = autoPlay;

                // Hide loading state
                Dispatcher.UIThread.Post(() => IsLoading = false);
                return true;
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[Player] Could not play {File}", filePath);

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
                return false;
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
                await System.Threading.Tasks.Task.Delay(7000, cancellationToken);
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
                Serilog.Log.Warning(ex, "[PlayerViewModel] Failed to save queue");
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
                    // This DB read can take long enough (racing against heavy startup work like
                    // the similarity index's HNSW graph build) that the user may already have
                    // started playing something themselves — e.g. via the playlist header's Play
                    // button — before this resolves. Restoring the OLD saved queue at that point
                    // would silently wipe out the track they just started playing. Only restore
                    // into a genuinely still-empty queue.
                    if (Queue.Count > 0)
                    {
                        Serilog.Log.Information("[PlayerViewModel] Skipped restoring saved queue — a queue is already active");
                        return;
                    }

                    Queue.Clear();

                    // Same bug class as the PlayAlbumRequestEvent stale-path fix: a track's file
                    // can be moved/deleted between sessions without its saved ResolvedFilePath
                    // ever being corrected. Restoring it anyway meant pressing Play on a dead
                    // "now playing" entry with no error until the user actually tried — skip it
                    // instead, same as the album-play queue-build path does.
                    int currentIndex = -1;
                    int skipped = 0;
                    foreach (var (track, isCurrent) in savedQueue)
                    {
                        if (string.IsNullOrEmpty(track.ResolvedFilePath) || !System.IO.File.Exists(track.ResolvedFilePath))
                        {
                            skipped++;
                            continue;
                        }

                        var vm = new PlaylistTrackViewModel(track);
                        Queue.Add(vm);

                        if (isCurrent)
                            currentIndex = Queue.Count - 1;
                    }

                    // Restore current track position
                    if (currentIndex >= 0 && currentIndex < Queue.Count)
                    {
                        CurrentQueueIndex = currentIndex;
                        CurrentTrack = Queue[currentIndex];
                    }

                    Serilog.Log.Information("[PlayerViewModel] Loaded {Count} tracks from saved queue{Skipped}", Queue.Count, skipped > 0 ? $" ({skipped} skipped — file no longer exists)" : "");
                });
            }
            catch (Exception ex)
            {
                Serilog.Log.Warning(ex, "[PlayerViewModel] Failed to load queue");
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
