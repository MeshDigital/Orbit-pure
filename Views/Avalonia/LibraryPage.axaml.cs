using System;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives; // Added for TreeDataGridRow
using Avalonia.Controls.Selection; // Added for ITreeDataGridRowSelectionModel
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.VisualTree;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.ViewModels;
using SLSKDONET.ViewModels.Library;
using Microsoft.Extensions.Logging;

namespace SLSKDONET.Views.Avalonia;

public partial class LibraryPage : UserControl
{
    private readonly ILogger<LibraryPage>? _logger;

    public LibraryPage()
    {
        InitializeComponent();
    }

    public LibraryPage(LibraryViewModel viewModel, ILogger<LibraryPage>? logger = null)
    {
        _logger = logger;
        DataContext = viewModel; // CRITICAL: Set DataContext from DI
        InitializeComponent();
        
        // Enable drag-drop on playlist ListBox
        AddHandler(DragDrop.DragOverEvent, OnPlaylistDragOver);
        AddHandler(DragDrop.DropEvent, OnPlaylistDrop);

        // DataGrid Professionalization
        var dataGrid = this.FindControl<DataGrid>("ProDataGrid");
        if (dataGrid != null)
        {
            dataGrid.ColumnReordered += OnDataGridColumnReordered;
            // dataGrid.ColumnResized += OnDataGridColumnResized;
            dataGrid.SelectionChanged += OnDataGridSelectionChanged;
            
            // Context menu for headers
            SetupColumnContextMenu(dataGrid);
        }

        // Sidebar Navigation Drag-Drop
        var navListBox = this.FindControl<ListBox>("SidebarNavListBox");
        if (navListBox != null)
        {
            DragDrop.SetAllowDrop(navListBox, true);
            navListBox.AddHandler(DragDrop.DragOverEvent, OnSidebarNavDragOver);
            navListBox.AddHandler(DragDrop.DropEvent, OnSidebarNavDrop);
        }
    }

    private void OnDataGridColumnReordered(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && sender is DataGrid dg)
        {
            // Update DisplayOrder in AvailableColumns
            foreach (var col in dg.Columns)
            {
                var def = vm.AvailableColumns.FirstOrDefault(c => c.Header?.ToString() == col.Header?.ToString());
                if (def != null)
                {
                    def.DisplayOrder = col.DisplayIndex;
                }
            }
            vm.OnColumnLayoutChanged();
        }
    }

    private void OnDataGridColumnResized(object? sender, DataGridColumnEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            var def = vm.AvailableColumns.FirstOrDefault(c => c.Header?.ToString() == e.Column.Header?.ToString());
            if (def != null)
            {
                def.Width = (int)e.Column.ActualWidth;
                vm.OnColumnLayoutChanged();
            }
        }
    }

    private void OnDataGridSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm && sender is DataGrid dg && dg.IsVisible)
        {
            // Sync DataGrid selection to Tracks.SelectedTracks
            // FilteredTracks are PlaylistTrackViewModels
            var selected = dg.SelectedItems.Cast<PlaylistTrackViewModel>().ToList();
            
            // Update VM selection logic (calling internal method if possible or using Commands)
            // For now, we assume simple sync is needed
             vm.Tracks.UpdateSelection(selected);
        }
    }

    private void SetupColumnContextMenu(DataGrid dg)
    {
        // Headers are tricky to catch in Avalonia DataGrid without styles, 
        // but we can add a context menu to the whole grid and filter for header area or just have it everywhere.
        // Professional approach: Context menu on the grid itself that lists columns.
        
        var menu = new ContextMenu();
        
        if (DataContext is LibraryViewModel vm)
        {
            foreach (var colDef in vm.AvailableColumns)
            {
                var item = new MenuItem 
                { 
                    Header = colDef.Header, 
                    Icon = colDef.IsVisible ? "✓" : "",
                    Command = vm.ToggleColumnCommand,
                    CommandParameter = colDef
                };
                
                // Add binding for Icon would be better but let's keep it simple for now
                menu.Items.Add(item);
            }
            
            menu.Items.Add(new Separator());
            menu.Items.Add(new MenuItem 
            { 
                Header = "Reset to Studio Default", 
                Command = vm.ResetViewCommand 
            });
        }
        
        dg.ContextMenu = menu;
    }

    private void CloseRemovalHistory_Click(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            vm.IsRemovalHistoryVisible = false;
        }
    }

    protected override async void OnLoaded(RoutedEventArgs e)
    {
        var totalSw = System.Diagnostics.Stopwatch.StartNew();
        var stepSw = System.Diagnostics.Stopwatch.StartNew();
        base.OnLoaded(e);
        _logger?.LogInformation("[PERF] LibraryPage.OnLoaded: base.OnLoaded took {Ms}ms", stepSw.ElapsedMilliseconds);
        
        // BUGFIX: Ensure projects are loaded when user navigates to Library page
        // Previously only loaded during startup or after imports, not on manual navigation
        if (DataContext is LibraryViewModel vm)
        {
            try
            {
                stepSw.Restart();
                // FIX: Check if projects are already loaded to prevent aggressive reloading on tab switch
                if (!vm.Projects.AllProjects.Any())
                {
                    _logger?.LogInformation("[PERF] LibraryPage.OnLoaded: Starting LoadProjectsAsync");
                    await vm.LoadProjectsAsync();
                    _logger?.LogInformation("[PERF] LoadProjectsAsync took {Ms}ms", stepSw.ElapsedMilliseconds);
                }
                else
                {
                    _logger?.LogInformation("[PERF] Projects already loaded ({Count} items), check took {Ms}ms", vm.Projects.AllProjects.Count, stepSw.ElapsedMilliseconds);
                    
                    // FIX: Eagerly select first project if none selected to avoid 3s UI binding delay
                    if (vm.Projects.SelectedProject == null && vm.Projects.FilteredProjects.Count > 0)
                    {
                        stepSw.Restart();
                        _logger?.LogInformation("[PERF] Selecting first project...");
                        vm.Projects.SelectedProject = vm.Projects.FilteredProjects[0];
                        _logger?.LogInformation("[PERF] Project selection took {Ms}ms", stepSw.ElapsedMilliseconds);
                    }
                }
                
                stepSw.Restart();
                _logger?.LogInformation("[PERF] LoadProjectsAsync completed. AllProjects count: {Count}", vm.Projects.AllProjects.Count);
                _logger?.LogInformation("[PERF] First project: {Title}", vm.Projects.AllProjects.FirstOrDefault()?.SourceTitle ?? "none");
                _logger?.LogInformation("[PERF] Logging took {Ms}ms", stepSw.ElapsedMilliseconds);
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "[PERF] EXCEPTION in LibraryPage.OnLoaded");
            }
        }
        else
        {
            _logger?.LogWarning("[PERF] LibraryPage.OnLoaded: DataContext is NOT LibraryViewModel!");
        }
        
        stepSw.Restart();
        // Find the playlist ListBox and enable drop
        var playlistListBox = this.FindControl<ListBox>("PlaylistListBox");
        if (playlistListBox != null)
        {
            DragDrop.SetAllowDrop(playlistListBox, true);
        }
        _logger?.LogInformation("[PERF] FindControl/DragDrop took {Ms}ms", stepSw.ElapsedMilliseconds);
        
        totalSw.Stop();
        _logger?.LogInformation("[PERF] TOTAL LibraryPage.OnLoaded took {Ms}ms", totalSw.ElapsedMilliseconds);
        
        // TODO: Restore Drag and Drop for the new Track ListBox
    }

    private void OnPlaylistDragOver(object? sender, DragEventArgs e)
    {
        // Accept tracks from library or queue
        if (e.Data.Contains(DragContext.LibraryTrackFormat) || e.Data.Contains(DragContext.QueueTrackFormat))
        {
            e.DragEffects = DragDropEffects.Copy;
        }
        else
        {
            e.DragEffects = DragDropEffects.None;
        }
    }

    private void OnPlaylistDrop(object? sender, DragEventArgs e)
    {
        // Get the target playlist
        var listBoxItem = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>();
        if (listBoxItem?.DataContext is not PlaylistJob targetPlaylist)
            return;

        // Get the dragged track GlobalId
        string? trackGlobalId = null;
        if (e.Data.Contains(DragContext.LibraryTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.LibraryTrackFormat) as string;
        }
        else if (e.Data.Contains(DragContext.QueueTrackFormat))
        {
            trackGlobalId = e.Data.Get(DragContext.QueueTrackFormat) as string;
        }

        if (string.IsNullOrEmpty(trackGlobalId))
            return;

        // Find the track in the library
        if (DataContext is not LibraryViewModel libraryViewModel)
            return;

        var sourceTrack = libraryViewModel.CurrentProjectTracks
            .FirstOrDefault(t => t.GlobalId == trackGlobalId);

        if (sourceTrack == null)
        {
            // Try to find in player queue
            var playerViewModel = libraryViewModel.PlayerViewModel;
            
            sourceTrack = playerViewModel?.Queue
                .FirstOrDefault(t => t.GlobalId == trackGlobalId);
        }

        if (sourceTrack != null && targetPlaylist != null)
        {
            // Use existing AddToPlaylist method (includes deduplication)
            libraryViewModel.AddToPlaylist(targetPlaylist, sourceTrack);
        }
    }

    private void OnSidebarNavDragOver(object? sender, DragEventArgs e)
    {
        if (e.Data.Contains(DragContext.LibraryTrackFormat) || e.Data.Contains(DragContext.QueueTrackFormat))
        {
            var listBoxItem = (e.Source as Control)?.FindAncestorOfType<ListBoxItem>();
            if (listBoxItem != null)
            {
                e.DragEffects = DragDropEffects.Link;
            }
            else
            {
                e.DragEffects = DragDropEffects.None;
            }
        }
    }

    private void OnSidebarNavDrop(object? sender, DragEventArgs e)
    {
        // Dropping to sidebar navigation is disabled
    }
    
    private void ToggleHelpPanel(object? sender, RoutedEventArgs e)
    {
        if (DataContext is LibraryViewModel vm)
        {
            vm.IsHelpPanelOpen = !vm.IsHelpPanelOpen;
        }
    }
}
