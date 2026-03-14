using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using Avalonia.VisualTree; // Added for FindAncestorOfType
using Microsoft.Extensions.DependencyInjection;
// using CommunityToolkit.Mvvm.Input; // Removed - using local AsyncRelayCommand

namespace SLSKDONET.Views.Avalonia;

public partial class PlayerControl : UserControl
{
    public PlayerControl()
    {
        InitializeComponent();
        
        // Enable drag-drop on the entire player control
        AddHandler(DragDrop.DragOverEvent, OnDragOver);
        AddHandler(DragDrop.DropEvent, OnDrop);
    }

    protected override void OnLoaded(RoutedEventArgs e)
    {
        base.OnLoaded(e);
        DragDrop.SetAllowDrop(this, true);
    }

    private void OnDragOver(object? sender, DragEventArgs e)
    {
        // Accept tracks or albums from library or queue
        if (e.Data.Contains(DragContext.LibraryTrackFormat) || 
            e.Data.Contains(DragContext.QueueTrackFormat) ||
            e.Data.Contains("ORBIT_LibraryAlbum"))
        {
            e.DragEffects = DragDropEffects.Copy;
            
            // Visual feedback - could add a highlight effect here
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private async void OnDrop(object? sender, DragEventArgs e)
    {
        // Identify the drop zone
        var dropTarget = e.Source as Control;
        
        bool isPlayNowZone = dropTarget != null && (dropTarget.Name == "PlayNowZone" || 
            dropTarget.GetVisualAncestors().OfType<Control>().Any(x => x.Name == "PlayNowZone"));
            
        bool isQueueZone = dropTarget != null && (dropTarget.Name == "QueueZone" || 
            dropTarget.GetVisualAncestors().OfType<Control>().Any(x => x.Name == "QueueZone"));

        // Get the dragged track or album ID
        string? trackGlobalId = null;
        string? albumIdStr = null;

        if (e.Data.Contains(DragContext.LibraryTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.LibraryTrackFormat) as string;
        }
        else if (e.Data.Contains(DragContext.QueueTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.QueueTrackFormat) as string;
        }
        else if (e.Data.Contains("ORBIT_LibraryAlbum"))
        {
            albumIdStr = e.Data.Get("ORBIT_LibraryAlbum") as string;
        }

        if (string.IsNullOrEmpty(trackGlobalId) && string.IsNullOrEmpty(albumIdStr))
            return;

        if (DataContext is not PlayerViewModel playerViewModel)
            return;

        var mainWindow = this.VisualRoot as MainWindow;
        var mainViewModel = mainWindow?.DataContext as MainViewModel;

        // HANDLE ALBUM DROP
        if (!string.IsNullOrEmpty(albumIdStr) && Guid.TryParse(albumIdStr, out var albumId))
        {
            // We need the tracks for this album.
            // Try to get them from the LibraryViewModel (SelectedProject or global)
            // or just trigger PlayAlbum/DownloadAlbum logic.
            if (mainViewModel?.LibraryViewModel != null)
            {
                // This is an in-memory collection, so we can access it synchronously
                // However, AllProjects is likely an ObservableCollection or similar
                // We should cast Guid? albumId to Guid before comparison if needed, or rely on == operator
                
                var project = mainViewModel.LibraryViewModel.Projects.AllProjects.FirstOrDefault(p => p.Id == albumId);
                
                if (project != null)
                {
                    if (isPlayNowZone)
                    {
                        // Fire and forget
                        mainViewModel.LibraryViewModel.PlayAlbumCommand.Execute(project);
                    }
                    else if (isQueueZone)
                    {
                         mainViewModel.LibraryViewModel.DownloadAlbumCommand.Execute(project);
                    }
                }
            }
            return;
        }

        // HANDLE TRACK DROP
        // Find the track - first check queue, then try to find in library via MainViewModel
        var track = playerViewModel.Queue.FirstOrDefault(t => t.GlobalId == trackGlobalId);
        
        if (track == null && mainViewModel != null)
        {
            // Try to find in download manager's global tracks
            track = mainViewModel.AllGlobalTracks
                .FirstOrDefault(t => t.GlobalId == trackGlobalId);
        }

        if (track != null)
        {
            if (isPlayNowZone)
            {
                if (!string.IsNullOrEmpty(track.Model?.ResolvedFilePath))
                {
                    playerViewModel.PlayTrack(
                        track.Model.ResolvedFilePath,
                        track.Title ?? "Unknown",
                        track.Artist ?? "Unknown Artist"
                    );
                }
            }
            else if (isQueueZone)
            {
                playerViewModel.AddToQueue(track);
            }
        }
    }
}
