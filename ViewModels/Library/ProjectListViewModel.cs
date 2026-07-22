using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Manages the list of projects/playlists in the library.
/// Handles project selection, creation, deletion, and refresh.
/// </summary>
public class ProjectListViewModel : INotifyPropertyChanged, IDisposable
{
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();
    private bool _isDisposed;
    private readonly EventHandler<bool> _authChangedHandler;

    private readonly ILogger<ProjectListViewModel> _logger;
    private readonly ILibraryService _libraryService;
    private readonly DownloadManager _downloadManager;
    private readonly INotificationService _notificationService;
    private readonly ArtworkCacheService? _artworkCacheService;
    private readonly PlaylistMosaicService? _mosaicService;
    private readonly IEventBus _eventBus;

    // Master List: All import jobs/projects
    private ObservableCollection<PlaylistJob> _allProjects = new();
    public ObservableCollection<PlaylistJob> AllProjects
    {
        get => _allProjects;
        set
        {
            _allProjects = value;
            OnPropertyChanged();
        }
    }

    private string _searchText = string.Empty;
    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText != value)
            {
                _searchText = value;
                OnPropertyChanged();
                RefreshFilteredProjects();
            }
        }
    }

    private ObservableCollection<PlaylistJob> _filteredProjects = new();
    public ObservableCollection<PlaylistJob> FilteredProjects
    {
        get => _filteredProjects;
        set
        {
            _filteredProjects = value;
            OnPropertyChanged();
        }
    }

    private ObservableCollection<LibraryPlaylistCardViewModel> _filteredProjectCards = new();
    public ObservableCollection<LibraryPlaylistCardViewModel> FilteredProjectCards
    {
        get => _filteredProjectCards;
        set
        {
            _filteredProjectCards = value;
            OnPropertyChanged();
        }
    }

    // Playlist Folders: nested tree used by the sidebar
    public ObservableCollection<PlaylistFolder> AllFolders { get; } = new();
    public ObservableCollection<PlaylistTreeNodeViewModel> RootTreeNodes { get; } = new();

    private PlaylistTreeNodeViewModel? _selectedTreeNode;
    public PlaylistTreeNodeViewModel? SelectedTreeNode
    {
        get => _selectedTreeNode;
        set
        {
            if (ReferenceEquals(_selectedTreeNode, value))
            {
                return;
            }

            _selectedTreeNode = value;
            OnPropertyChanged();

            if (value is PlaylistTreeCardNodeViewModel cardNode && !ReferenceEquals(SelectedProjectCard, cardNode.Card))
            {
                SelectedProjectCard = cardNode.Card;
            }
        }
    }

    private LibraryPlaylistCardViewModel? _selectedProjectCard;
    public LibraryPlaylistCardViewModel? SelectedProjectCard
    {
        get => _selectedProjectCard;
        set
        {
            if (_selectedProjectCard == value)
            {
                return;
            }

            _selectedProjectCard = value;
            OnPropertyChanged();
            SyncSelectedTreeNodeFromCard(value);

            var selectedFromCard = value?.Model;
            if (!ReferenceEquals(_selectedProject, selectedFromCard))
            {
                SelectedProject = selectedFromCard;
            }
        }
    }

    // Selected project
    private PlaylistJob? _selectedProject;
    public PlaylistJob? SelectedProject
    {
        get => _selectedProject;
        set
        {
            if (_selectedProject != value)
            {
                _logger.LogInformation("SelectedProject changing to {Id} - {Title}", value?.Id, value?.SourceTitle);
                _selectedProject = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(HasSelectedProject));
                OnPropertyChanged(nameof(CanDeleteProject));

                var cardForSelection = value is null
                    ? null
                    : FilteredProjectCards.FirstOrDefault(card => ReferenceEquals(card.Model, value));
                if (!ReferenceEquals(_selectedProjectCard, cardForSelection))
                {
                    _selectedProjectCard = cardForSelection;
                    OnPropertyChanged(nameof(SelectedProjectCard));
                }

                // Raise event for parent ViewModel to handle
                ProjectSelected?.Invoke(this, value);
            }
        }
    }

    public bool HasSelectedProject => SelectedProject != null;
    public bool CanDeleteProject => SelectedProject != null && !IsEditMode;

    private bool _isEditMode;
    public bool IsEditMode
    {
        get => _isEditMode;
        set
        {
            if (_isEditMode != value)
            {
                _isEditMode = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(CanDeleteProject));
            }
        }
    }

    // Special "All Tracks" pseudo-project
    private readonly PlaylistJob _allTracksJob = new()
    {
        Id = Guid.Empty,
        SourceTitle = "All Tracks",
        SourceType = "Global Library"
    };

    // Events
    public event EventHandler<PlaylistJob?>? ProjectSelected;
    public event PropertyChangedEventHandler? PropertyChanged;

    // Commands
    public System.Windows.Input.ICommand OpenProjectCommand { get; }
    public System.Windows.Input.ICommand DeleteProjectCommand { get; }
    public System.Windows.Input.ICommand AddPlaylistCommand { get; }
    public System.Windows.Input.ICommand RefreshLibraryCommand { get; }
    public System.Windows.Input.ICommand LoadAllTracksCommand { get; }
    public System.Windows.Input.ICommand ImportLikedSongsCommand { get; }
    public System.Windows.Input.ICommand SyncProjectCommand { get; }
    public System.Windows.Input.ICommand GenerateCuesCommand { get; }
    public System.Windows.Input.ICommand ExportProjectCommand { get; }

    // Playlist Folder commands
    public System.Windows.Input.ICommand CreateFolderCommand { get; }
    public System.Windows.Input.ICommand CreateSubfolderCommand { get; }
    public System.Windows.Input.ICommand RenameFolderCommand { get; }
    public System.Windows.Input.ICommand DeleteFolderCommand { get; }

    // Services
    private readonly ImportOrchestrator _importOrchestrator;
    private readonly IEnumerable<IImportProvider> _importProviders;
    private readonly Services.ImportProviders.SpotifyLikedSongsImportProvider _spotifyLikedSongsProvider;
    private readonly IDialogService _dialogService;
    private readonly SpotifyAuthService _spotifyAuthService;

    private bool _isSpotifyAuthenticated;
    public bool IsSpotifyAuthenticated
    {
        get => _isSpotifyAuthenticated;
        set
        {
            if (_isSpotifyAuthenticated != value)
            {
                _isSpotifyAuthenticated = value;
                OnPropertyChanged();
            }
        }
    }

    public ProjectListViewModel(
        ILogger<ProjectListViewModel> logger,
        ILibraryService libraryService,
        DownloadManager downloadManager,
        ImportOrchestrator importOrchestrator,
        IEnumerable<IImportProvider> importProviders,
        Services.ImportProviders.SpotifyLikedSongsImportProvider spotifyLikedSongsProvider,
        SpotifyAuthService spotifyAuthService,
        IDialogService dialogService,
        IEventBus eventBus,
        INotificationService notificationService,
        ArtworkCacheService? artworkCacheService = null,
        PlaylistMosaicService? mosaicService = null)
    {
        _logger = logger;
        _libraryService = libraryService;
        _downloadManager = downloadManager;
        _importOrchestrator = importOrchestrator;
        _importProviders = importProviders;
        _spotifyLikedSongsProvider = spotifyLikedSongsProvider;
        _spotifyAuthService = spotifyAuthService;
        _dialogService = dialogService;
        _notificationService = notificationService;
        _artworkCacheService = artworkCacheService;
        _mosaicService = mosaicService;
        _eventBus = eventBus;

        // Initialize commands
        OpenProjectCommand = new RelayCommand<PlaylistJob>(project => SelectedProject = project);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync);
        AddPlaylistCommand = new AsyncRelayCommand(ExecuteAddPlaylistAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadAllTracksCommand = new RelayCommand(() => SelectedProject = _allTracksJob);
        ImportLikedSongsCommand = new AsyncRelayCommand(ExecuteImportLikedSongsAsync, () => IsSpotifyAuthenticated);
        SyncProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteSyncProjectAsync);
        GenerateCuesCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteGenerateCuesAsync);
        ExportProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteExportProjectAsync);

        CreateFolderCommand = new AsyncRelayCommand(() => ExecuteCreateFolderAsync(null));
        CreateSubfolderCommand = new AsyncRelayCommand<PlaylistTreeFolderNodeViewModel>(node => ExecuteCreateFolderAsync(node?.Folder.Id));
        RenameFolderCommand = new AsyncRelayCommand<PlaylistTreeFolderNodeViewModel>(ExecuteRenameFolderAsync);
        DeleteFolderCommand = new AsyncRelayCommand<PlaylistTreeFolderNodeViewModel>(ExecuteDeleteFolderAsync);

        FilteredProjectCards.CollectionChanged += (_, _) => RebuildTree();
        AllFolders.CollectionChanged += (_, _) => RebuildTree();

        // Subscribe to auth changes
        _authChangedHandler = (s, authenticated) => 
        {
             IsSpotifyAuthenticated = authenticated;
             ((AsyncRelayCommand)ImportLikedSongsCommand).RaiseCanExecuteChanged();
        };
        _spotifyAuthService.AuthenticationChanged += _authChangedHandler;


        // Initial auth check
        _ = Task.Run(async () => 
        {
            IsSpotifyAuthenticated = await _spotifyAuthService.IsAuthenticatedAsync();
        });

        // Subscribe to events
        _disposables.Add(eventBus.GetEvent<ProjectAddedEvent>().Subscribe(async evt => 
        {
            var job = await _libraryService.FindPlaylistJobAsync(evt.ProjectId);
            if (job != null) OnPlaylistAdded(this, job);
        }));
        _disposables.Add(eventBus.GetEvent<ProjectUpdatedEvent>().Subscribe(evt => OnProjectUpdated(this, evt.ProjectId)));
        _disposables.Add(eventBus.GetEvent<ProjectDeletedEvent>().Subscribe(evt => OnProjectDeleted(this, evt.ProjectId)));
        
        // Subscribe to track state changes to update active download counts in real-time
        _disposables.Add(eventBus.GetEvent<TrackStateChangedEvent>().Subscribe(OnTrackStateChanged));
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
            _disposables.Dispose();
            if (_spotifyAuthService != null)
            {
                _spotifyAuthService.AuthenticationChanged -= _authChangedHandler;
            }
        }

        _isDisposed = true;
    }

    
    private void OnTrackStateChanged(TrackStateChangedEvent evt)
    {
        // PERFORMANCE FIX: Target specific project instead of looping through all
        Dispatcher.UIThread.InvokeAsync(() =>
        {
            try
            {
                // Fix 5: Threading Race Condition Safety
                // Ensure AllProjects isn't null or being modified by another thread (though UI thread marshaling helps)
                if (AllProjects == null) return;
                
                // Find the specific project that changed
                var project = AllProjects.FirstOrDefault(p => p.Id == evt.ProjectId);
                if (project != null)
                {
                    // Refresh ONLY the affected project's stats
                    project.RefreshStatusCounts();
                    
                    // Real tracking via DownloadManager
                    project.ActiveDownloadsCount = _downloadManager.GetActiveDownloadsCountForProject(project.Id);
                    project.CurrentDownloadingTrack = _downloadManager.GetCurrentlyDownloadingTrackName(project.Id);
                }
            }
            catch (Exception ex)
            {
                // Prevent crash if collection is modified during read or other race condition
                _logger.LogWarning(ex, "Race condition avoided in OnTrackStateChanged for project {Id}", evt.ProjectId);
            }
        });
    }

    private async Task ExecuteImportLikedSongsAsync()
    {
        _logger.LogInformation("Starting 'Liked Songs' import from Spotify...");
        
        // Use the unified orchestrator path. 
        // The orchestrator handles finding existing jobs and showing the preview.
        await _importOrchestrator.StartImportWithPreviewAsync(_spotifyLikedSongsProvider, "spotify:liked");
    }

    /// <summary>
    /// Loads all projects from the database.
    /// </summary>
    public async Task LoadFoldersAsync()
    {
        try
        {
            var folders = await _libraryService.LoadAllPlaylistFoldersAsync().ConfigureAwait(false);
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                AllFolders.Clear();
                foreach (var folder in folders)
                {
                    AllFolders.Add(folder);
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load playlist folders");
        }
    }

    public async Task LoadProjectsAsync()
    {
        try
        {
            _logger.LogInformation("Loading projects from database...");
            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();
            await LoadFoldersAsync();

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                // Performance fix: Batch add items instead of one-by-one to avoid repeated UI reflows
                // Clear collection
                if (AllProjects == null) AllProjects = new ObservableCollection<PlaylistJob>();
                AllProjects.Clear();
                
                // Add all items at once in sorted order
                var sortedJobs = jobs.OrderByDescending(j => j.CreatedAt).ToList();
                foreach (var job in sortedJobs)
                {
                    AllProjects.Add(job);
                }

                _logger.LogInformation("Loaded {Count} projects", AllProjects.Count);
                
                RefreshFilteredProjects();

                // Select first project if available
                if (FilteredProjects.Count > 0 && SelectedProject == null)
                {
                    SelectedProject = FilteredProjects[0];
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load projects");
        }
    }

    private void RefreshFilteredProjects()
    {
        // Safety: Ensure filtering runs on UI thread to update ObservableCollection
        Dispatcher.UIThread.Post(async () =>
        {
            var filtered = string.IsNullOrWhiteSpace(SearchText) 
                ? AllProjects.ToList()
                : AllProjects.Where(p => 
                    (p.SourceTitle?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false) ||
                    (p.SourceType?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false)).ToList();

            FilteredProjects.Clear();
            FilteredProjectCards.Clear();

            foreach (var p in filtered)
            {
                FilteredProjects.Add(p);
            }

            // Workstation 2026: populate card VM list in background-priority chunks
            // to keep the UI fluid with large libraries.
            const int initialChunk = 40;
            const int chunkSize = 30;

            foreach (var p in filtered.Take(initialChunk))
            {
                FilteredProjectCards.Add(new LibraryPlaylistCardViewModel(p, _artworkCacheService, _mosaicService));
            }
            SyncSelectedProjectCard();

            for (int i = initialChunk; i < filtered.Count; i += chunkSize)
            {
                var batch = filtered.Skip(i).Take(chunkSize).ToList();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var p in batch)
                    {
                        FilteredProjectCards.Add(new LibraryPlaylistCardViewModel(p, _artworkCacheService, _mosaicService));
                    }

                    SyncSelectedProjectCard();
                }, DispatcherPriority.Background);
            }
        });
    }

    private void SyncSelectedProjectCard()
    {
        var card = SelectedProject is null
            ? null
            : FilteredProjectCards.FirstOrDefault(candidate => ReferenceEquals(candidate.Model, SelectedProject));

        if (!ReferenceEquals(_selectedProjectCard, card))
        {
            _selectedProjectCard = card;
            OnPropertyChanged(nameof(SelectedProjectCard));
        }

        SyncSelectedTreeNodeFromCard(card);
    }

    private void SyncSelectedTreeNodeFromCard(LibraryPlaylistCardViewModel? card)
    {
        var node = card is null || RootTreeNodes is null ? null : FindCardNode(RootTreeNodes, card);
        if (!ReferenceEquals(_selectedTreeNode, node))
        {
            _selectedTreeNode = node;
            OnPropertyChanged(nameof(SelectedTreeNode));
        }
    }

    private static PlaylistTreeCardNodeViewModel? FindCardNode(IEnumerable<PlaylistTreeNodeViewModel> nodes, LibraryPlaylistCardViewModel card)
    {
        foreach (var node in nodes)
        {
            if (node is PlaylistTreeCardNodeViewModel cardNode && ReferenceEquals(cardNode.Card, card))
            {
                return cardNode;
            }

            if (node is PlaylistTreeFolderNodeViewModel folderNode)
            {
                var found = FindCardNode(folderNode.ChildNodes, card);
                if (found != null) return found;
            }
        }

        return null;
    }

    /// <summary>
    /// Rebuilds the nested folder/playlist tree from AllFolders + FilteredProjectCards.
    /// Called whenever either source collection changes.
    /// </summary>
    private void RebuildTree()
    {
        var folderNodes = AllFolders.ToDictionary(f => f.Id, f => new PlaylistTreeFolderNodeViewModel(f));

        foreach (var folder in AllFolders)
        {
            if (folder.ParentFolderId.HasValue && folderNodes.TryGetValue(folder.ParentFolderId.Value, out var parentNode))
            {
                parentNode.ChildNodes.Add(folderNodes[folder.Id]);
            }
        }

        foreach (var card in FilteredProjectCards)
        {
            var folderId = card.Model.FolderId;
            if (folderId.HasValue && folderNodes.TryGetValue(folderId.Value, out var folderNode))
            {
                folderNode.ChildNodes.Add(new PlaylistTreeCardNodeViewModel(card));
            }
        }

        var newRoots = new List<PlaylistTreeNodeViewModel>();
        newRoots.AddRange(AllFolders
            .Where(f => f.ParentFolderId == null)
            .OrderBy(f => f.SortOrder)
            .ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
            .Select(f => (PlaylistTreeNodeViewModel)folderNodes[f.Id]));
        newRoots.AddRange(FilteredProjectCards
            .Where(c => !c.Model.FolderId.HasValue || !folderNodes.ContainsKey(c.Model.FolderId.Value))
            .Select(c => (PlaylistTreeNodeViewModel)new PlaylistTreeCardNodeViewModel(c)));

        RootTreeNodes.Clear();
        foreach (var node in newRoots)
        {
            RootTreeNodes.Add(node);
        }

        SyncSelectedTreeNodeFromCard(SelectedProjectCard);
    }

    private async Task ExecuteCreateFolderAsync(Guid? parentFolderId)
    {
        var name = await _dialogService.ShowPromptAsync("New Folder", "Enter a name for the new folder:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _libraryService.CreatePlaylistFolderAsync(name.Trim(), parentFolderId);
            await LoadFoldersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create playlist folder");
            await _dialogService.ShowAlertAsync("Error", $"Could not create folder: {ex.Message}");
        }
    }

    private async Task ExecuteRenameFolderAsync(PlaylistTreeFolderNodeViewModel? node)
    {
        if (node == null) return;

        var name = await _dialogService.ShowPromptAsync("Rename Folder", "Enter a new name:", node.Folder.Name);
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            await _libraryService.RenamePlaylistFolderAsync(node.Folder.Id, name.Trim());
            await LoadFoldersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rename playlist folder {Id}", node.Folder.Id);
        }
    }

    private async Task ExecuteDeleteFolderAsync(PlaylistTreeFolderNodeViewModel? node)
    {
        if (node == null) return;

        var confirmed = await _dialogService.ConfirmAsync(
            "Delete Folder",
            $"Delete folder '{node.Folder.Name}'? Playlists and subfolders inside it will move up one level, not be deleted.");
        if (!confirmed) return;

        try
        {
            await _libraryService.DeletePlaylistFolderAsync(node.Folder.Id);
            await LoadFoldersAsync();
            await LoadProjectsAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete playlist folder {Id}", node.Folder.Id);
        }
    }

    /// <summary>
    /// Moves a playlist into a folder (or back to root if null). Called from the sidebar's
    /// drag-and-drop handler in code-behind.
    /// </summary>
    public async Task MovePlaylistToFolderAsync(Guid playlistId, Guid? folderId)
    {
        try
        {
            await _libraryService.MovePlaylistToFolderAsync(playlistId, folderId).ConfigureAwait(false);

            var job = AllProjects.FirstOrDefault(p => p.Id == playlistId);
            if (job != null)
            {
                job.FolderId = folderId;
            }

            await Dispatcher.UIThread.InvokeAsync(RebuildTree);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move playlist {Id} to folder {FolderId}", playlistId, folderId);
        }
    }

    /// <summary>
    /// Moves a folder under a new parent (or to root if null). Called from the sidebar's
    /// drag-and-drop handler in code-behind. Silently no-ops if the move would create a cycle.
    /// </summary>
    public async Task MoveFolderAsync(Guid folderId, Guid? newParentFolderId)
    {
        try
        {
            var moved = await _libraryService.MovePlaylistFolderAsync(folderId, newParentFolderId).ConfigureAwait(false);
            if (!moved)
            {
                _notificationService.Show("Move Folder", "Can't move a folder into itself or one of its own subfolders.", Views.NotificationType.Warning);
                return;
            }

            await LoadFoldersAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to move folder {Id} to parent {ParentId}", folderId, newParentFolderId);
        }
    }
    // ... existing methods ...

    // ... existing event handlers ...

    private async Task ExecuteRefreshAsync()
    {
        _logger.LogInformation("Manual refresh requested - reloading projects");
        var selectedProjectId = SelectedProject?.Id;

        await LoadProjectsAsync();

        // Restore selection
        if (selectedProjectId.HasValue)
        {
            if (selectedProjectId == Guid.Empty)
            {
                SelectedProject = _allTracksJob;
            }
            else
            {
                var project = AllProjects.FirstOrDefault(p => p.Id == selectedProjectId.Value);
                if (project != null)
                {
                    SelectedProject = project;
                }
            }
        }

        _logger.LogInformation("Manual refresh completed");
    }

    private async Task ExecuteAddPlaylistAsync()
    {
        var name = await _dialogService.ShowPromptAsync("New Playlist", "Enter a name for the new playlist:");
        if (string.IsNullOrWhiteSpace(name)) return;

        try
        {
            var job = await _libraryService.CreateEmptyPlaylistAsync(name.Trim());
            _logger.LogInformation("Created new playlist: {Title}", job.SourceTitle);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create playlist");
            await _dialogService.ShowAlertAsync("Error", $"Could not create playlist: {ex.Message}");
        }
    }

    private async Task ExecuteDeleteProjectAsync(PlaylistJob? job)
    {
        if (job == null) return;

        try
        {
            var confirmed = await _dialogService.ConfirmAsync(
                "Remove Playlist", 
                $"Remove '{job.SourceTitle}' from the playlist list? Tracks and downloaded files will be kept in the library.");
            
            if (!confirmed) return;

            _logger.LogInformation("Deleting project: {Title}", job.SourceTitle);
            await _libraryService.DeletePlaylistJobAsync(job.Id);
            _logger.LogInformation("Project deleted successfully");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to delete project");
        }
    }

    private async Task ExecuteSyncProjectAsync(PlaylistJob? job)
    {
        if (job == null || string.IsNullOrWhiteSpace(job.SourceUrl)) return;

        try
        {
            _logger.LogInformation("Syncing project: {Title} from {Url}", job.SourceTitle, job.SourceUrl);
            
            var provider = _importProviders.FirstOrDefault(p => p.CanHandle(job.SourceUrl));
            if (provider == null)
            {
                _notificationService.Show("Sync Error", "No suitable provider found for this project source.", Views.NotificationType.Error);
                return;
            }

            await _importOrchestrator.SilentImportAsync(provider, job.SourceUrl);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to sync project");
            _notificationService.Show("Sync Error", $"Failed to sync: {ex.Message}", Views.NotificationType.Error);
        }
    }

    private async Task ExecuteGenerateCuesAsync(PlaylistJob? job)
    {
        if (job == null) return;
        try
        {
            var tracks = await _libraryService.LoadPlaylistTracksAsync(job.Id);
            var eligible = tracks
                .Where(t => !string.IsNullOrEmpty(t.ResolvedFilePath) && System.IO.File.Exists(t.ResolvedFilePath))
                .ToList();

            if (eligible.Count == 0)
            {
                _notificationService.Show("Generate Cues",
                    "No downloaded tracks found in this playlist. Download tracks first.",
                    Views.NotificationType.Warning);
                return;
            }

            foreach (var track in eligible)
                _eventBus.Publish(new Models.TrackAnalysisRequestedEvent(track.TrackUniqueHash));

            _notificationService.Show("Generate Cues",
                $"Queued {eligible.Count} track(s) in '{job.SourceTitle}' for cue generation.",
                Views.NotificationType.Success);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "GenerateCues failed for playlist {Id}", job.Id);
            _notificationService.Show("Generate Cues Failed", ex.Message, Views.NotificationType.Error);
        }
    }

    private async Task ExecuteExportProjectAsync(PlaylistJob? job)
    {
        if (job == null) return;
        // Select the project so the main Export command in LibraryViewModel operates on it
        SelectedProject = job;
        await Task.CompletedTask;
        _notificationService.Show("Export",
            $"Select '{job.SourceTitle}' is now active — use Library → Export to Rekordbox.",
            Views.NotificationType.Information);
    }

    private async void OnPlaylistAdded(object? sender, PlaylistJob job)
    {
        _logger.LogInformation("[UI TRACE] OnPlaylistAdded event received for job {JobId}. Source: {SourceType}", job.Id, job.SourceType);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var existingProject = AllProjects.FirstOrDefault(j => j.Id == job.Id);
            if (existingProject != null)
            {
                _logger.LogInformation("[UI TRACE] Project {JobId} already exists in AllProjects, selecting existing one", job.Id);
                SelectedProject = existingProject;
                return;
            }

            // 1. Add to Master List (Insert at top for Newest First)
            _logger.LogInformation("[UI TRACE] Adding job {JobId} to AllProjects. Current Count: {Count}", job.Id, AllProjects.Count);
            AllProjects.Insert(0, job);
            
            // 2. Conditionally add to Filtered List
            // Only show if no filter active OR if it matches the current filter
            bool emptyFilter = string.IsNullOrWhiteSpace(SearchText);
            bool titleMatch = job.SourceTitle?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false;
            bool typeMatch = job.SourceType?.Contains(SearchText, StringComparison.OrdinalIgnoreCase) ?? false;
            
            bool matchesFilter = emptyFilter || titleMatch || typeMatch;

            _logger.LogInformation("[UI TRACE] Filter check for {JobId}: Empty={Empty}, TitleMatch={TitleMatch}, TypeMatch={TypeMatch} => Included={Result}. SourceTitle='{SourceTitle}'", 
                job.Id, emptyFilter, titleMatch, typeMatch, matchesFilter, job.SourceTitle);

            if (matchesFilter)
            {
                FilteredProjects.Insert(0, job);
                FilteredProjectCards.Insert(0, new LibraryPlaylistCardViewModel(job, _artworkCacheService, _mosaicService));
                _logger.LogInformation("[UI TRACE] Added job {JobId} to FilteredProjects", job.Id);
            }
            else 
            {
                 _logger.LogInformation("[UI TRACE] Job {JobId} excluded from FilteredProjects due to filter '{Filter}'", job.Id, SearchText);
            }

            SelectedProject = job; // Auto-select new project

            _logger.LogInformation("Project '{Title}' added to list", job.SourceTitle);
        });
    }

    private async void OnProjectUpdated(object? sender, Guid jobId)
    {
        var updatedJob = await _libraryService.FindPlaylistJobAsync(jobId);
        if (updatedJob == null) return;

        try 
        {
            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var existingJob = AllProjects.FirstOrDefault(j => j.Id == jobId);
                if (existingJob != null)
                {
                    existingJob.SourceTitle = updatedJob.SourceTitle;
                    existingJob.SourceType = updatedJob.SourceType;
                    existingJob.AlbumArtUrl = updatedJob.AlbumArtUrl;
                    existingJob.PlaylistTracks = updatedJob.PlaylistTracks;
                    existingJob.TotalTracks = updatedJob.TotalTracks;
                    existingJob.SuccessfulCount = updatedJob.SuccessfulCount;
                    existingJob.FailedCount = updatedJob.FailedCount;
                    existingJob.MissingCount = updatedJob.MissingCount;

                    _logger.LogDebug("Updated project {Title}: {Succ}/{Total}",
                        existingJob.SourceTitle, existingJob.SuccessfulCount, existingJob.TotalTracks);
                }
            });
        }
        catch (TaskCanceledException)
        {
            _logger.LogDebug("Project update UI task canceled for {JobId}", jobId);
        }
    }

    private async void OnProjectDeleted(object? sender, Guid projectId)
    {
        _logger.LogInformation("OnProjectDeleted event received for job {JobId}", projectId);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            var jobToRemove = AllProjects.FirstOrDefault(p => p.Id == projectId);
            if (jobToRemove != null)
            {
                // 1. Remove from Master List
                AllProjects.Remove(jobToRemove);

                // 2. Remove from Filtered List (if present)
                if (FilteredProjects.Contains(jobToRemove))
                {
                    FilteredProjects.Remove(jobToRemove);
                }

                var cardToRemove = FilteredProjectCards.FirstOrDefault(c => c.Model.Id == projectId);
                if (cardToRemove != null)
                {
                    FilteredProjectCards.Remove(cardToRemove);
                }

                // Auto-select next project if deleted one was selected
                if (SelectedProject == jobToRemove)
                {
                    SelectedProject = FilteredProjects.FirstOrDefault();
                }
            }
        });
    }


    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
