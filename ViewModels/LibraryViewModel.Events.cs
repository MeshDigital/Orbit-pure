using System;
using System.Reactive;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using System.Linq;
using System.Threading.Tasks;
using System.Collections.Generic;
using SLSKDONET.Models;
using SLSKDONET.ViewModels.Library;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Views;
using SLSKDONET.Events;
using SLSKDONET.Models.Musical;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.ViewModels;

public partial class LibraryViewModel
{
    /// <summary>Last single-selected track while Mix mode is on — see OnTrackSelectionChanged's
    /// click-through pair-building. Not meaningful outside Mix mode.</summary>
    private PlaylistTrackViewModel? _mixModePendingOutgoingTrack;

    // _intelligenceContextRefreshRequests field + its debounced Rx subscription live in
    // LibraryViewModel.cs (not here) — ViewModelDisposalGuardTests scans each ViewModel source
    // file independently for IDisposable/Dispose(), and doesn't resolve subscriptions across a
    // partial class's other files, so putting that subscription's source text in this file would
    // false-flag as untracked even though it's genuinely disposed via LibraryViewModel.cs's own
    // _disposables.

    private void OnLibraryTrackRemoved(string globalId)
    {
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            var toRemove = Tracks.CurrentProjectTracks
                .Where(t => t.GlobalId == globalId)
                .ToList();
            foreach (var t in toRemove)
                Tracks.CurrentProjectTracks.Remove(t);

            _ = Intelligence.RefreshOverviewStatsAsync();
        });
    }

    private async void OnProjectAdded(ProjectAddedEvent evt)
    {
        try
        {
            _logger.LogInformation("[IMPORT TRACE] LibraryViewModel.OnProjectAdded: Received event for job {JobId}", evt.ProjectId);
            
            // Wait a moment for DB to settle
            await Task.Delay(500);
            
            await LoadProjectsAsync();
            _logger.LogInformation("[IMPORT TRACE] LoadProjectsAsync completed. AllProjects count: {Count}", Projects.AllProjects.Count);
            
            // Select the newly added project
            _logger.LogInformation("[IMPORT TRACE] Attempting to select project {JobId}", evt.ProjectId);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                var newProject = Projects.AllProjects.FirstOrDefault(p => p.Id == evt.ProjectId);
                if (newProject != null)
                {
                    Projects.SelectedProject = newProject;
                    _logger.LogInformation("[IMPORT TRACE] Successfully selected project {JobId}", evt.ProjectId);
                }
                else
                {
                    _logger.LogWarning("Could not find project {JobId} in AllProjects after import", evt.ProjectId);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to handle post-import navigation for project {JobId}", evt.ProjectId);
        }
    }

    private void OnTrackSelectionChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
    {
        var selectedTracks = Tracks.SelectedTracks.ToList();
        // DoubleInspector's pairwise DB/similarity lookup runs debounced (200ms), fired below —
        // see WireSelectionInspectorRefreshDebounce in LibraryViewModel.cs — so a shift-click range
        // or marquee drag spanning many rows doesn't fire one DB call per intermediate row.
        _selectionInspectorRefreshRequests.OnNext(Unit.Default);

        if (selectedTracks.Count == 2)
        {
            // Mix mode repurposes the existing "select exactly two tracks" gesture (normally
            // Double Inspector) to load a transition pair instead — outgoing/incoming order
            // taken from the two tracks' actual playlist position, not selection click order.
            if (Tracks.IsMixModeEnabled)
            {
                var ordered = Tracks.CurrentProjectTracks
                    .Where(t => selectedTracks.Contains(t))
                    .ToList();
                if (ordered.Count == 2)
                {
                    var outgoing = ordered[0];
                    var incoming = ordered[1];
                    var playlistId = outgoing.Model?.PlaylistId ?? Guid.Empty;
                    ReactiveUI.MessageBus.Current.SendMessage(
                        new OpenMixTransitionEvent(playlistId, outgoing.Id, incoming.Id));
                }
            }
            else
            {
                ReactiveUI.MessageBus.Current.SendMessage(
                    OpenInspectorEvent.Create(DoubleInspector, "Library.TrackSelection.Double"));
            }
        }
        
        if (selectedTracks.Count == 1)
        {
            var single = selectedTracks.First();

            // Mix mode: plain (non-ctrl/shift) clicks normally collapse a selection back down to
            // one track, which is exactly the "select a song, click another, get diverted away"
            // complaint — two ordinary single-clicks in a row never reach the Count==2 branch
            // above. Remember the previous single click and treat consecutive different picks as
            // a pair, so clicking through the playlist one track at a time still builds a
            // transition each time, matching how the badges work — no ctrl/shift required.
            if (Tracks.IsMixModeEnabled)
            {
                var previous = _mixModePendingOutgoingTrack;
                _mixModePendingOutgoingTrack = single;

                if (previous != null && previous != single)
                {
                    var indexA = Tracks.CurrentProjectTracks.IndexOf(previous);
                    var indexB = Tracks.CurrentProjectTracks.IndexOf(single);
                    var (outgoing, incoming) = indexA >= 0 && indexB >= 0 && indexA <= indexB
                        ? (previous, single)
                        : (single, previous);
                    var playlistId = outgoing.Model?.PlaylistId ?? Guid.Empty;
                    ReactiveUI.MessageBus.Current.SendMessage(
                        new OpenMixTransitionEvent(playlistId, outgoing.Id, incoming.Id));
                    return;
                }
            }
            else
            {
                _mixModePendingOutgoingTrack = null;
            }

            single.ClearInspectorA10PairwiseContext();
            ReactiveUI.MessageBus.Current.SendMessage(OpenInspectorEvent.Create(single, "Library.TrackSelection.Single"));
            RefreshSavedDoublesForLeadTrack(single);
            // TryAttachInspectorPairwiseContextAsync/TryAttachEnhancementsAsync run debounced —
            // see the _selectionInspectorRefreshRequests.OnNext(...) call above.
        }
        else
        {
            TrackInspector.ClearEnhancements();
            RefreshSavedDoublesForLeadTrack(null);

            if (selectedTracks.Count == 0 && IsLibraryIntelligencePanelVisible)
            {
                ReactiveUI.MessageBus.Current.SendMessage(
                    OpenInspectorEvent.Create(Intelligence, "Library.TrackSelection.EmptyIntelligence"));
            }
            else if (selectedTracks.Count == 0)
            {
                ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());
            }
        }

        // Debounced via _intelligenceContextRefreshRequests — see WireIntelligenceRefreshDebounce
        // in LibraryViewModel.cs, which eventually calls RefreshIntelligenceCandidatesNow below.
        // Note RefreshOverviewStatsAsync is deliberately NOT triggered here: it recomputes
        // whole-playlist stats (duration, BPM range, genre/key distribution) that don't depend on
        // which track is selected within that playlist, and calling it here meant every single
        // click did a full playlist reload from the DB (VirtualizedTrackCollection-backed projects
        // keep CurrentProjectTracks empty by design, so RefreshOverviewStatsAsync falls through to
        // LoadPlaylistTracksAsync — the whole playlist, not just the selected track). It's already
        // correctly triggered by project selection, TrackAdded/BatchTracksAdded for the open
        // playlist, and the currently-playing track changing.
        _intelligenceContextRefreshRequests.OnNext(Unit.Default);
    }

    /// <summary>
    /// The actual candidate-recompute work behind the debounced request above — pulled into its
    /// own method (rather than inlined into WireIntelligenceRefreshDebounce's subscription) so
    /// this file keeps calling these two methods directly, matching
    /// LibrarySidebarUnificationStartTests.LibraryEvents_SelectionFlow_RoutesThroughChildInspectorOwners'
    /// source-text assertion, while the actual Rx subscription (and its disposal tracking) lives
    /// in LibraryViewModel.cs — see WireIntelligenceRefreshDebounce for why that split exists.
    /// </summary>
    private void RefreshIntelligenceCandidatesNow()
    {
        _ = Intelligence.RefreshSuggestNextCandidatesAsync();
        _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();
    }

    private async Task TryAttachInspectorPairwiseContextAsync(PlaylistTrackViewModel selected)
    {
        try
        {
            var ordered = Tracks.FilteredTracks?.OfType<PlaylistTrackViewModel>().ToList()
                ?? new List<PlaylistTrackViewModel>();
            if (ordered.Count == 0)
                return;

            var selectedIndex = ordered.IndexOf(selected);
            if (selectedIndex < 0)
                return;

            PlaylistTrackViewModel? neighbor = null;
            string relationLabel = string.Empty;

            if (selectedIndex + 1 < ordered.Count)
            {
                neighbor = ordered[selectedIndex + 1];
                relationLabel = "Next";
            }
            else if (selectedIndex > 0)
            {
                neighbor = ordered[selectedIndex - 1];
                relationLabel = "Previous";
            }

            if (neighbor is null)
                return;

            if (string.IsNullOrWhiteSpace(selected.GlobalId) || string.IsNullOrWhiteSpace(neighbor.GlobalId))
                return;

            var similarity = TrackSimilarityService;
            if (similarity is null)
                return;

            var score = await similarity.ScoreAsync(
                selected.GlobalId,
                neighbor.GlobalId,
                TrackSimilarityProfile.BlendSafe).ConfigureAwait(false);

            if (score is null)
                return;

            // Skip stale writes when user has moved selection before async scoring completed.
            if (Tracks.SelectedTracks.Count != 1 || !ReferenceEquals(Tracks.SelectedTracks.FirstOrDefault(), selected))
                return;

            var contextLabel = $"{relationLabel}: {neighbor.ArtistName} - {neighbor.TrackTitle}";
            var reasonTags = string.Join(" • ", score.ReasonTags.Take(2));

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                selected.SetInspectorA10PairwiseContext(
                    contextLabel,
                    score.FinalSimilarity,
                    score.VectorScores.Harmonic,
                    score.VectorScores.Rhythm,
                    score.SegmentScores.Drop,
                    reasonTags));
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to compute inspector pairwise A10 context for {TrackHash}", selected.GlobalId);
        }
    }

    /// <summary>
    /// Loads all projects from the database.
    /// Delegates to ProjectListViewModel.
    /// </summary>
    public async Task LoadProjectsAsync()
    {
        await Projects.LoadProjectsAsync();
    }

    /// <summary>
    /// Handles project selection event from ProjectListViewModel.
    /// Coordinates loading tracks in TrackListViewModel.
    /// </summary>
    private async void OnProjectSelected(object? sender, PlaylistJob? project)
    {
        // LibraryViewModel.SelectedProject is a pass-through wrapper over Projects.SelectedProject
        // (kept for XAML binding convenience) that never raised its own PropertyChanged — anything
        // bound to {Binding SelectedProject} on this ViewModel (rather than Projects.SelectedProject
        // directly) saw only whatever value was current when the binding first attached and then
        // silently never updated again, since ProjectListViewModel's own notification obviously
        // can't know about this outer wrapper. Only surfaced once something (the playlist header)
        // actually needed live updates through the wrapper instead of the direct path.
        OnPropertyChanged(nameof(SelectedProject));

        SetSmartPlaylistContextMode(false);
        RaiseLibraryIntelligenceContextStateChanged();

        if (project == null)
        {
            Intelligence.ResetSmartInsertPairContext();
            _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();
            _ = Intelligence.RefreshOverviewStatsAsync();
            ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());
            return;
        }

        // Note: the "All Tracks" pseudo-project (ProjectListViewModel._allTracksJob) also uses
        // Guid.Empty as its Id, so it must NOT be treated as "nothing selected" here — it needs
        // the same load below as any real project. A prior pass added the null-check above and
        // mistakenly folded project.Id == Guid.Empty into it, which made LoadProjectTracksAsync
        // never run for "All Tracks": the grid stayed on whatever the previous project loaded
        // (or empty) until an unrelated event — typing in the search box — triggered the first
        // real load, which read as "search is broken and incomplete."
        _logger.LogInformation("LibraryViewModel.OnProjectSelected: Switching to project {Title} (ID: {Id})", project.SourceTitle, project.Id);
        await Tracks.LoadProjectTracksAsync(project);
        await RefreshSavedDoublesAsync();
        _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();
        _ = Intelligence.RefreshOverviewStatsAsync();

        if (Tracks.SelectedTracks.Count == 0)
        {
            // Land on the Overview tab (general playlist stats), not whichever tool tab (Smart
            // Insert etc.) happened to be selected last — the panel auto-opens here, and a tool
            // that needs an explicit source/target track pick isn't a useful first thing to show.
            Intelligence.FocusLibraryIntelligenceTab(PlaylistIntelligenceViewModel.IntelligenceTabOverview);
            ReactiveUI.MessageBus.Current.SendMessage(
                OpenInspectorEvent.Create(Intelligence, "Library.ProjectSelection.EmptyIntelligence"));
        }
    }

    /// <summary>
    /// Handles smart playlist selection event from SmartPlaylistViewModel.
    /// Coordinates updating track list.
    /// </summary>
    private async void OnSmartPlaylistSelected(object? sender, Library.SmartPlaylist? playlist)
    {
        if (playlist == null) return;

        SetSmartPlaylistContextMode(true);
        RaiseLibraryIntelligenceContextStateChanged();
        Intelligence.ResetSmartInsertPairContext();

        try
        {
            IsLoading = true;
            
            // Phase 23: Smart Crates (DB-backed)
            if (playlist.Definition != null)
            {
                _notificationService.Show("Smart Crate", $"Evaluating rules for '{playlist.Name}'...", NotificationType.Information);
                
                // 1. Evaluate rules against database (Global Index)
                var ids = await _smartCrateService.GetMatchingTrackIdsAsync(playlist.Definition);
                
                // 2. Load matching tracks via TrackListViewModel
                await Tracks.LoadSmartCrateAsync(ids);
                await RefreshSavedDoublesAsync();
                
                _logger.LogInformation("Loaded Smart Crate '{Name}' with {Count} tracks", playlist.Name, ids.Count);
            }
            else
            {
                // In-memory smart playlist fallback for playlists without DB-backed definitions.
                _notificationService.Show("Smart Playlist", $"Loading {playlist.Name}", NotificationType.Information);
                
                // Execute filter on loaded memory state
                var tracks = SmartPlaylists.RefreshSmartPlaylist(playlist);
                
                await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => 
                {
                    Tracks.CurrentProjectTracks = tracks;
                });
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load smart playlist {Name}", playlist.Name);
            _notificationService.Show("Error", "Failed to load crate.", NotificationType.Error);
        }
        finally
        {
            IsLoading = false;
        }
    }


}
