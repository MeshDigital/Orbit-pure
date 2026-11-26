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
    /// Queue management, next/previous/shuffle/repeat navigation, mix transitions, and preloading (#307).
    /// </summary>
    public partial class PlayerViewModel
    {
        private void PlayQueueItem(PlaylistTrackViewModel? track)
        {
            if (track == null)
                return;

            var index = Queue.IndexOf(track);
            if (index >= 0)
                PlayTrackAtIndex(index);
        }

        private void OpenMixTransition(PlaylistTrackViewModel? outgoing)
        {
            if (outgoing?.NextPlaylistTrackId is not Guid incomingId) return;

            var playlistId = outgoing.Model?.PlaylistId ?? Guid.Empty;
            ReactiveUI.MessageBus.Current.SendMessage(
                new SLSKDONET.Events.OpenMixTransitionEvent(playlistId, outgoing.Id, incomingId));
        }

        /// <summary>
        /// Expands/collapses the inline Mix editor in the Now Playing screen. Loads the actual
        /// current→next pair into the shared MixTransitionVm on every expand (not just the first
        /// time) so re-opening after the track has advanced always reflects the real upcoming
        /// hop rather than whatever pair happened to be loaded last.
        /// </summary>
        private void ToggleMixPanel()
        {
            IsMixPanelExpanded = !IsMixPanelExpanded;
            if (IsMixPanelExpanded) LoadCurrentPairIntoMixPanel();
        }

        /// <summary>
        /// Surfaces the transition settings for the current→next pair immediately after starting
        /// playback of a Mix-mode playlist (see the PlayAlbumRequestEvent handler below), instead
        /// of leaving the user to discover the MIX module — and that it does anything — on their
        /// own. Does both of this app's two separate "show the Mix editor" routes: expands the
        /// inline module for whenever the player is docked in the vertical sidebar, AND opens the
        /// CONTEXT sidepanel's own "Mix" tab (the same route a badge click uses) since starting
        /// playback also switches the player to the bottom bar by default — the inline module
        /// lives inside the vertical PlayerControl view, which isn't the visible surface in that
        /// dock mode, so relying on it alone would silently show nothing.
        ///
        /// Public so LibraryViewModel can also call it when the "+ Mix" toggle is switched on
        /// mid-playback (not just at the moment Play is first pressed) — turning Mix on should
        /// surface the current pair's settings right away, not only the next time Play happens to
        /// be pressed.
        /// </summary>
        public void ShowMixPanelForCurrentPair()
        {
            IsMixPanelExpanded = true;
            LoadCurrentPairIntoMixPanel();

            var nextIndex = PeekNextIndex();
            if (CurrentTrack == null || nextIndex is not int idx || idx < 0 || idx >= Queue.Count) return;
            var next = Queue[idx];
            var playlistId = CurrentTrack.Model?.PlaylistId ?? next.Model?.PlaylistId ?? Guid.Empty;
            if (playlistId == Guid.Empty) return;

            ReactiveUI.MessageBus.Current.SendMessage(
                new SLSKDONET.Events.OpenMixTransitionEvent(playlistId, CurrentTrack.Id, next.Id));
        }

        private void LoadCurrentPairIntoMixPanel()
        {
            if (MixTransitionVm == null) return;

            var nextIndex = PeekNextIndex();
            if (CurrentTrack == null || nextIndex is not int idx || idx < 0 || idx >= Queue.Count) return;

            var next = Queue[idx];
            var playlistId = CurrentTrack.Model?.PlaylistId ?? next.Model?.PlaylistId ?? Guid.Empty;
            if (playlistId == Guid.Empty) return;

            _ = MixTransitionVm.LoadPairAsync(playlistId, CurrentTrack.Id, next.Id);
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
                Serilog.Log.Warning(ex, "[PlayerViewModel] Failed to save global like status");
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
        
        /// <summary>
        /// Stops playback synchronously (not posted to the dispatcher) if <paramref name="globalId"/>
        /// is the track currently open — playing or paused. A caller about to permanently delete
        /// that track's file must call this and let it return BEFORE deleting: NAudio's
        /// AudioFileReader holds the file open for as long as this deck is alive, so File.Delete
        /// on a still-playing track fails outright (silently, from DownloadManager's perspective)
        /// until the deck is disposed and releases the handle. Also strips any other occurrences
        /// of the track from the queue so a deleted file can't be played again later in the session.
        /// </summary>
        public void StopIfCurrentTrack(string globalId)
        {
            if (string.IsNullOrEmpty(globalId)) return;

            if (string.Equals(CurrentTrack?.GlobalId, globalId, StringComparison.OrdinalIgnoreCase))
            {
                Stop();
                CurrentTrack = null;
            }

            foreach (var stale in Queue.Where(t => string.Equals(t.GlobalId, globalId, StringComparison.OrdinalIgnoreCase)).ToList())
            {
                RemoveFromQueue(stale);
            }
        }

        /// <summary>
        /// User-facing "Clear all" button handler — confirms first, since ClearQueue() also stops
        /// playback and there was previously no way to back out of a misclick. Matches the
        /// existing confirm-before-destructive-action pattern elsewhere in the app (e.g.
        /// UserProfileViewModel.ClearConversationCommand). ClearQueue() itself stays
        /// confirmation-free for the one other, programmatic caller (TrackOperationsViewModel).
        /// </summary>
        private async Task ClearQueueWithConfirmationAsync()
        {
            if (_dialogService != null)
            {
                var confirmed = await _dialogService.ConfirmAsync(
                    "Clear Queue",
                    "This stops playback and removes every track from the queue. Continue?");
                if (!confirmed) return;
            }

            ClearQueue();
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
            else if (PeekFlowNextIndex() is int flowNext)
            {
                // Flow is driving: "next" is the track it planned, which may be anywhere in the
                // queue (flowing back to an earlier track is legal), not just the following row.
                nextIndex = flowNext;
            }
            else if (IsShuffling)
            {
                nextIndex = GetRandomTrackIndex();
                for (int attempt = 0; attempt < 10 && !IsPlayableQueueItem(nextIndex); attempt++)
                    nextIndex = GetRandomTrackIndex();
            }
            else
            {
                // Skip tracks whose file is gone — one missing file used to end playback there.
                if (NextPlayableIndex(CurrentQueueIndex + 1, wrap: RepeatMode == RepeatMode.All) is not int found)
                    return; // End of queue
                nextIndex = found;
            }

            PlayTrackAtIndex(nextIndex);
        }

        /// <summary>The queue row has a file on disk that can be played.</summary>
        private bool IsPlayableQueueItem(int index) =>
            index >= 0 && index < Queue.Count
            && Queue[index].Model?.ResolvedFilePath is { Length: > 0 } path
            && System.IO.File.Exists(path);

        /// <summary>
        /// First playable queue index at or after <paramref name="from"/> (wrapping around once when
        /// <paramref name="wrap"/>), or null. Queues are built from every track with a recorded
        /// path, and files do go missing — skipping them keeps a playlist (and its mixes) going.
        /// </summary>
        private int? NextPlayableIndex(int from, bool wrap)
        {
            int count = Queue.Count;
            for (int step = 0; step < count; step++)
            {
                int i = from + step;
                if (i >= count)
                {
                    if (!wrap) return null;
                    i -= count;
                }
                if (IsPlayableQueueItem(i))
                {
                    if (step > 0)
                        Serilog.Log.Information("[Player] Skipping {Count} queued track(s) with no playable file", step);
                    return i;
                }
            }
            return null;
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
            if (!_resuming) ClearResumePoint(); // the user chose other playback: the old return point is stale
            // A queued track that is missing or fails to open is skipped rather than ending
            // playback there. Bounded to one pass over the queue so a queue of nothing but broken
            // files can't loop forever.
            for (int attempts = 0; attempts < Math.Max(1, Queue.Count); attempts++)
            {
                if (index < 0 || index >= Queue.Count) return;

                var track = Queue[index];
                SetNowPlayingState(index, track);

                var filePath = track.Model?.ResolvedFilePath;
                bool loaded = false;
                if (!string.IsNullOrEmpty(filePath) && System.IO.File.Exists(filePath))
                {
                    loaded = LoadTrackCore(filePath, track.Title ?? "Unknown", track.Artist ?? "Unknown", autoPlay: true, track.Model?.Loudness);
                }
                else
                {
                    Serilog.Log.Warning("[Player] Queued track has no file on disk, skipping: {Artist} - {Title} ({Path})", track.Artist, track.Title, filePath);
                }

                if (loaded)
                {
                    SchedulePreloadNext();
                    return;
                }

                if (IsShuffling || RepeatMode == RepeatMode.One ||
                    NextPlayableIndex(index + 1, wrap: RepeatMode == RepeatMode.All) is not int next || next == index)
                {
                    return;
                }
                index = next;
            }
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

            // CurrentFilePath prefers this cached path over CurrentTrack's, and it used to be set
            // only by LoadTrackCore. OnTrackAdvanced (the engine promoting its preloaded deck on a
            // gapless/crossfade hand-over) never calls LoadTrackCore — so after every automatic
            // advance this still reported the PREVIOUS track's file, and anything asking "is the
            // player on this track?" (Cue Forge's attach check, reveal file, …) got a stale answer
            // until the next manually started track.
            if (track.Model?.ResolvedFilePath is { Length: > 0 } advancedPath)
                _currentFilePath = advancedPath;

            // TrackTitle/TrackArtist (what the bottom player bar actually displays) are otherwise
            // only set inside LoadTrackCore — fine for PlayTrackAtIndex, which calls PlayTrack()
            // right after this, but OnTrackAdvanced (the engine autonomously promoting a deck on
            // crossfade completion) deliberately never calls PlayTrack()/LoadTrackCore — the audio
            // is already playing — so without this, the bar kept showing whatever track was
            // playing before the LAST manually-initiated play, unchanged across every automatic
            // crossfade advance for the rest of the session.
            TrackTitle = track.Title ?? "Unknown";
            TrackArtist = track.Artist ?? "Unknown";

            // Flow plans from the track that just started, BEFORE PlayTrackAtIndex asks for the next
            // preload — a plan that lands after SchedulePreloadNext would pick the wrong deck.
            RememberFlowHistory(track);
            PlanFlowPath();

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
            // Cleared up front so stale info from whatever pair was previously "up next" doesn't
            // linger on screen while the new pair's saved-transition lookup is still in flight.
            UpcomingTransitionPresetName = null;

            var nextIndex = PeekNextIndex();
            if (nextIndex is int idx && idx >= 0 && idx < Queue.Count)
            {
                var path = Queue[idx].Model?.ResolvedFilePath;
                if (!string.IsNullOrEmpty(path) && System.IO.File.Exists(path))
                {
                    _playerService.PreloadNext(path, Queue[idx].Model?.Loudness);
                    _preloadedQueueIndex = idx;

                    // Resolve any saved Mix transition for (current, next) so the crossfade the
                    // engine performs when it reaches this pair reflects what was chosen in the
                    // Mix editor, not the app-wide default. Attached once resolved rather than
                    // blocking the (already-issued) file preload above on a DB round-trip.
                    _ = AttachSavedTransitionAsync(path, CurrentTrack, Queue[idx]);
                    return;
                }
            }

            _playerService.CancelPreload();
            _preloadedQueueIndex = null;
        }

        private readonly SLSKDONET.Services.Transitions.TransitionPlanService? _transitionPlanService;
        private readonly SLSKDONET.Services.Transitions.ITransitionResolver? _transitionResolver;

        private async Task AttachSavedTransitionAsync(string preloadedPath, PlaylistTrackViewModel? outgoing, PlaylistTrackViewModel incoming)
        {
            if ((_transitionResolver == null && _transitionRepository == null) || outgoing == null)
            {
                return;
            }

            var bpm = incoming.Model?.BPM is > 0 ? incoming.Model.BPM!.Value : 128.0;
            double? outgoingBpm = outgoing.Model?.BPM is > 0 ? outgoing.Model.BPM : null;

            // Structure-based plan (sections, phrases, DnB transition types, vocal check) — also the
            // source of exact analysed BPMs, which the engine uses to tempo-match the incoming deck.
            var score = Services.Playlist.TrackPairCompatibilityScorer.Score(
                outgoing.CamelotDisplay, incoming.CamelotDisplay, outgoing.Energy, incoming.Energy);

            SLSKDONET.Models.Timeline.PlaylistTrackTransition? saved = null;
            (SLSKDONET.Engine.Transitions.TransitionPlan Plan, SLSKDONET.Engine.Transitions.TrackStructure Outgoing, SLSKDONET.Engine.Transitions.TrackStructure Incoming)? planned = null;

            if (_transitionResolver != null)
            {
                var resolved = await _transitionResolver.ResolveAsync(
                    SLSKDONET.Data.Entities.TransitionScopeKind.Playlist,
                    outgoing.Model?.PlaylistId ?? Guid.Empty,
                    outgoing.Id,
                    incoming.Id,
                    outgoing.Model?.TrackUniqueHash,
                    incoming.Model?.TrackUniqueHash,
                    score.CombinedScore);

                saved = resolved.SavedTransition;
                if (resolved.AutoPlan != null && resolved.OutgoingStructure != null && resolved.IncomingStructure != null)
                {
                    planned = (resolved.AutoPlan, resolved.OutgoingStructure, resolved.IncomingStructure);
                }
            }
            else
            {
                saved = await _transitionRepository!.GetTransitionAsync(outgoing.Id, incoming.Id);
                if (_transitionPlanService != null
                    && outgoing.Model?.TrackUniqueHash is { Length: > 0 } outHash
                    && incoming.Model?.TrackUniqueHash is { Length: > 0 } inHash)
                {
                    try
                    {
                        planned = await _transitionPlanService.PlanAsync(outHash, inHash, score.CombinedScore);
                    }
                    catch (Exception ex)
                    {
                        Serilog.Log.Warning(ex, "[Mix] Transition planning failed for {Out} -> {In}", outHash, inHash);
                    }
                }
            }

            if (planned is { } p)
            {
                bpm = p.Incoming.Bpm;
                outgoingBpm = p.Outgoing.Bpm;
            }

            SLSKDONET.Models.Timeline.TransitionModel model;
            string presetName;
            double? sourceTrigger;
            double? targetTrigger;

            if (saved != null)
            {
                model = saved.ToTransitionModel();
                presetName = saved.PresetName;
                sourceTrigger = saved.SourceTriggerSeconds;
                targetTrigger = saved.TargetTriggerSeconds;
            }
            else
            {
                // No explicit save for this pair — every pair's badge already defaults its label
                // to "Auto" (see UpdateQueueTransitionBadgesAsync/TrackListViewModel's equivalent),
                // but nothing ever actually built and attached that Auto transition to real
                // playback: this method used to just return here, so an unsaved pair silently
                // played a hard cut/gapless swap with no mix effect at all — the "built but never
                // wired" gap behind "can't get songs to play with effect". Score the pair the same
                // way the badge does and materialize the same Auto transition TransitionPresetLibrary
                // already knows how to build (duration/type chosen from harmonic+energy fit) so
                // every hop in a playlist actually mixes by default, not just ones saved by hand.
                model = Services.Timeline.TransitionPresetLibrary.Build("Auto", score);
                presetName = "Auto";

                // Without an explicit trigger point, AudioPlayerService falls back to "start the
                // crossfade N seconds before the literal end of the file" (N derived from the
                // preset's bar count) — reasonable, but structure-blind: it has no idea where the
                // track's actual outro/2nd-drop/tail is, so a track with a long instrumental outro
                // and one that cuts hard right after the last chorus get treated identically. Reuse
                // the same cue-point/phrase-aware suggestion (TransitionEngine.OptimizeTransition)
                // the Mix editor already computes for a manually-picked pair, so an Auto-scored pair
                // during real playback gets the same structure-aware mix-out/mix-in points instead
                // of a generic duration-based guess.
                sourceTrigger = null;
                targetTrigger = null;
                if (planned is { } plan)
                {
                    model = Services.Timeline.TransitionPresetLibrary.Build(plan.Plan.PresetName, score, plan.Plan.DurationBars);
                    presetName = plan.Plan.PresetName;
                    sourceTrigger = plan.Plan.SourceTriggerSeconds;
                    targetTrigger = plan.Plan.TargetTriggerSeconds;
                    Serilog.Log.Information("[Mix] {Out} -> {In}: {Reason} (out {Src:0.0}s, in {Tgt:0.0}s)",
                        outgoing.Title, incoming.Title, plan.Plan.Reason, sourceTrigger, targetTrigger);
                }
                else if (_trackRepository != null && _cuePointService != null)
                {
                    var outgoingHash = outgoing.Model?.TrackUniqueHash;
                    var incomingHash = incoming.Model?.TrackUniqueHash;
                    if (!string.IsNullOrWhiteSpace(outgoingHash) && !string.IsNullOrWhiteSpace(incomingHash))
                    {
                        try
                        {
                            var sourceEntity = await _trackRepository.FindTrackAsync(outgoingHash);
                            var targetEntity = await _trackRepository.FindTrackAsync(incomingHash);
                            if (sourceEntity != null && targetEntity != null)
                            {
                                var sourceCues = await _cuePointService.GetByTrackIdAsync(outgoingHash);
                                var targetCues = await _cuePointService.GetByTrackIdAsync(incomingHash);
                                var suggestion = _pointSuggestionEngine.OptimizeTransition(sourceEntity, targetEntity, sourceCues, targetCues);
                                sourceTrigger = Math.Max(0, suggestion.SourceTriggerTime);
                                targetTrigger = Math.Max(0, suggestion.TargetTriggerTime);
                                // Old-style cues (e.g. a "Mix-Out Warning" at 0:00 on a badly analysed
                                // track) must not cut a track short: before half way, mix at the end instead.
                                double realLength = _playerService.Duration;
                                if (realLength > 0 && sourceTrigger < Engine.Transitions.TransitionPlanner.MinPlayFraction * realLength)
                                {
                                    sourceTrigger = null;
                                    targetTrigger = null;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Serilog.Log.Warning(ex, "[PlayerViewModel] Auto transition point suggestion failed for {Out}->{In}", outgoingHash, incomingHash);
                        }
                    }
                }
            }

            // Guard against a race: by the time this DB round-trip resolves, playback may already
            // have moved past this pair (e.g. the user skipped ahead) — don't paint stale
            // "up next via <preset>" visibility for a pair that's no longer relevant. Set directly
            // (not inside the Dispatcher.Post below) so this class' own reflection-based unit
            // tests can await it deterministically, matching UpdateQueueTransitionBadgesAsync's
            // equivalent choice just above.
            if (_preloadedQueueIndex is int stillPreloadedIndex && Queue.ElementAtOrDefault(stillPreloadedIndex)?.Model?.ResolvedFilePath == preloadedPath)
            {
                UpcomingTransitionPresetName = presetName;
            }

            Dispatcher.UIThread.Post(() => _playerService.SetPendingTransitionForNext(
                preloadedPath, model, bpm, sourceTrigger, targetTrigger, presetName, outgoingBpm));
        }

        private bool _queueTransitionBadgesScheduled;

        /// <summary>
        /// Debounces bursts of Queue mutations (bulk-loading a playlist adds one track at a time)
        /// into a single badge recompute per burst, mirroring TrackListViewModel's
        /// ScheduleUpdateMixTransitionBadges for the Library track list's own badges.
        /// </summary>
        private void ScheduleUpdateQueueTransitionBadges()
        {
            if (_queueTransitionBadgesScheduled) return;
            _queueTransitionBadgesScheduled = true;
            Dispatcher.UIThread.Post(() =>
            {
                _queueTransitionBadgesScheduled = false;
                _ = UpdateQueueTransitionBadgesAsync();
            }, DispatcherPriority.Background);
        }

        /// <summary>
        /// Resolves and sets ShowMixTransitionBadge/TransitionPresetLabel/TransitionBadgeColor on
        /// every consecutive pair in the live playback Queue — the same properties
        /// TrackListViewModel.UpdateMixTransitionBadgesAsync computes for the Library track list,
        /// but nothing populated them for the player's own Queue instances (a separate set of
        /// PlaylistTrackViewModel objects), so the Up Next strip and queue panel never showed
        /// which preset would actually drive each upcoming hop. Always shown here (not gated by
        /// a "Mix mode" toggle — the Library page's toggle is a track-list display preference,
        /// not a gate on whether saved transitions apply during real playback).
        /// </summary>
        private async Task UpdateQueueTransitionBadgesAsync()
        {
            if (_transitionRepository == null || Queue.Count == 0) return;

            // No ConfigureAwait(false)/Dispatcher.Post marshaling — matching
            // TrackListViewModel.UpdateMixTransitionBadgesAsync's own established pattern for the
            // exact same kind of "await a DB lookup, then set view-model properties" method: the
            // continuation resumes via the ambient SynchronizationContext (the UI thread, since
            // this is always invoked from a UI-thread-originated call), so no manual marshaling
            // is needed — and unlike Dispatcher.UIThread.Post, that continuation is exactly what
            // this class' own reflection-based unit tests can already await deterministically.
            var playlistId = Queue[0].Model?.PlaylistId ?? Guid.Empty;
            var saved = playlistId != Guid.Empty
                ? (await _transitionRepository.GetTransitionsForPlaylistAsync(playlistId))
                    .ToDictionary(t => (t.OutgoingPlaylistTrackId, t.IncomingPlaylistTrackId))
                : new Dictionary<(Guid, Guid), Models.Timeline.PlaylistTrackTransition>();

            for (int i = 0; i < Queue.Count; i++)
            {
                var current = Queue[i];
                if (i == Queue.Count - 1)
                {
                    current.ShowMixTransitionBadge = false;
                    current.NextPlaylistTrackId = null;
                    continue;
                }

                var next = Queue[i + 1];
                current.ShowMixTransitionBadge = true;
                current.NextPlaylistTrackId = next.Id;

                var score = Services.Playlist.TrackPairCompatibilityScorer.Score(
                    current.CamelotDisplay, next.CamelotDisplay, current.Energy, next.Energy);
                current.TransitionBadgeColor = Services.Playlist.TrackPairCompatibilityScorer.CompatibilityColor(score.CombinedScore);
                current.TransitionPresetLabel = saved.TryGetValue((current.Id, next.Id), out var savedTransition)
                    ? savedTransition.PresetName
                    : "Auto";
            }
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

            // Flow plans the order while it is active, so the deck preloaded for the crossfade is the
            // track Flow actually picked — not merely "whatever came next in the queue".
            if (PeekFlowNextIndex() is int flowIndex) return flowIndex;

            return NextPlayableIndex(CurrentQueueIndex + 1, wrap: RepeatMode == RepeatMode.All);
        }

        /// <summary>
        /// The queue row Flow wants to play next, or null when Flow is off/has no plan/the planned
        /// track is gone from the queue. The hash (not the index) is the contract: the queue can be
        /// edited between planning and playback.
        /// </summary>
        private int? PeekFlowNextIndex()
        {
            if (!IsFlowMode) return null;

            var next = FlowModeState.NextStep;
            if (next is null || string.IsNullOrWhiteSpace(next.Hash)) return null;

            for (int i = 0; i < Queue.Count; i++)
            {
                if (string.Equals(Queue[i].GlobalId, next.Hash, StringComparison.OrdinalIgnoreCase) &&
                    IsPlayableQueueItem(i))
                {
                    return i;
                }
            }

            return null;
        }

        /// <summary>
        /// The engine autonomously advanced to the preloaded track (gapless swap or crossfade
        /// completion). Sync our "now playing" state to match — the audio is already playing,
        /// so this must NOT call PlayTrack()/Play() again.
        /// </summary>
        private void OnTrackAdvanced()
        {
            // The engine has already moved to its preloaded deck. If the preloaded index got lost
            // this used to return without updating anything: audio played the next track while
            // the UI stayed on the old one and nothing more was preloaded — so at that track's end
            // "next" was computed from the stale position and the mix chain broke.
            int? resolved = _preloadedQueueIndex is int preloaded && preloaded >= 0 && preloaded < Queue.Count
                ? preloaded
                : PeekNextIndex();
            if (resolved is not int index)
            {
                Serilog.Log.Warning("[Player] Engine advanced but the next queue track is unknown (current index {Index})", CurrentQueueIndex);
                return;
            }
            if (_preloadedQueueIndex == null)
                Serilog.Log.Warning("[Player] Engine advanced without a recorded preload — assuming queue index {Index}", index);

            var track = Queue[index];
            _preloadedQueueIndex = null;
            SetNowPlayingState(index, track);
            SchedulePreloadNext();
        }

        private bool HasNextTrack()
        {
            if (!Queue.Any()) return false;
            // Flow can pick a track that sits before the current one in the queue, so the plain
            // "is there a row after this one" test does not describe it.
            if (IsFlowMode && PeekFlowNextIndex() is not null) return true;
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
                return;
            }

            // Shuffle and Flow both decide the play order; the one the user just switched on wins.
            // Leaving both on would make Flow look broken (its picks silently ignored) — so turning
            // shuffle on stands Flow down and the Flow chip visibly goes off.
            if (IsFlowMode)
            {
                if (_flowModeService is not null)
                {
                    _flowModeService.Deactivate();
                    SyncFlowState(_flowModeService.State);
                }
                else
                {
                    IsFlowMode = false;
                }
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

    }
}
