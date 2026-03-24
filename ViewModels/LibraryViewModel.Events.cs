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
        
        if (selectedTracks.Count == 1)
        {
            var single = selectedTracks.First();
            ReactiveUI.MessageBus.Current.SendMessage(new OpenInspectorEvent(single));
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
        if (project != null)
        {
            _logger.LogInformation("LibraryViewModel.OnProjectSelected: Switching to project {Title} (ID: {Id})", project.SourceTitle, project.Id);
            await Tracks.LoadProjectTracksAsync(project);
        }
    }

    /// <summary>
    /// Handles smart playlist selection event from SmartPlaylistViewModel.
    /// Coordinates updating track list.
    /// </summary>
    private async void OnSmartPlaylistSelected(object? sender, Library.SmartPlaylist? playlist)
    {
        if (playlist == null) return;

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
                
                _logger.LogInformation("Loaded Smart Crate '{Name}' with {Count} tracks", playlist.Name, ids.Count);
            }
            else
            {
                // Legacy: In-Memory Smart Playlists
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
