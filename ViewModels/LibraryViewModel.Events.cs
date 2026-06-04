using System;
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
        _ = DoubleInspector.HandleSelectionChangedAsync(selectedTracks);

        if (selectedTracks.Count == 2)
        {
            ReactiveUI.MessageBus.Current.SendMessage(
                OpenInspectorEvent.Create(DoubleInspector, "Library.TrackSelection.Double"));
        }
        
        if (selectedTracks.Count == 1)
        {
            var single = selectedTracks.First();
            single.ClearInspectorA10PairwiseContext();
            ReactiveUI.MessageBus.Current.SendMessage(OpenInspectorEvent.Create(single, "Library.TrackSelection.Single"));
            RefreshSavedDoublesForLeadTrack(single);
            _ = TryAttachInspectorPairwiseContextAsync(single);
            _ = TrackInspector.TryAttachEnhancementsAsync(single);
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
        SetSmartPlaylistContextMode(false);
        RaiseLibraryIntelligenceContextStateChanged();

        if (project == null || project.Id == Guid.Empty)
        {
            Intelligence.ResetSmartInsertPairContext();
            _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();
            ReactiveUI.MessageBus.Current.SendMessage(new CloseInspectorEvent());
            return;
        }

        if (project != null)
        {
            _logger.LogInformation("LibraryViewModel.OnProjectSelected: Switching to project {Title} (ID: {Id})", project.SourceTitle, project.Id);
            await Tracks.LoadProjectTracksAsync(project);
            await RefreshSavedDoublesAsync();
            _ = Intelligence.RefreshPlaylistUpgradeCandidatesAsync();

            if (Tracks.SelectedTracks.Count == 0)
            {
                ReactiveUI.MessageBus.Current.SendMessage(
                    OpenInspectorEvent.Create(Intelligence, "Library.ProjectSelection.EmptyIntelligence"));
            }
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
