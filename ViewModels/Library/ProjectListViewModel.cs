using System;
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

        // Initialize commands
        OpenProjectCommand = new RelayCommand<PlaylistJob>(project => SelectedProject = project);
        DeleteProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteDeleteProjectAsync);
        AddPlaylistCommand = new AsyncRelayCommand(ExecuteAddPlaylistAsync);
        RefreshLibraryCommand = new AsyncRelayCommand(ExecuteRefreshAsync);
        LoadAllTracksCommand = new RelayCommand(() => SelectedProject = _allTracksJob);
        ImportLikedSongsCommand = new AsyncRelayCommand(ExecuteImportLikedSongsAsync, () => IsSpotifyAuthenticated);
        SyncProjectCommand = new AsyncRelayCommand<PlaylistJob>(ExecuteSyncProjectAsync);

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
    public async Task LoadProjectsAsync()
    {
        try
        {
            _logger.LogInformation("Loading projects from database...");
            var jobs = await _libraryService.LoadAllPlaylistJobsAsync();

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

            for (int i = initialChunk; i < filtered.Count; i += chunkSize)
            {
                var batch = filtered.Skip(i).Take(chunkSize).ToList();
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    foreach (var p in batch)
                    {
                        FilteredProjectCards.Add(new LibraryPlaylistCardViewModel(p, _artworkCacheService, _mosaicService));
                    }
                }, DispatcherPriority.Background);
            }
        });
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
        // TODO: Implement add playlist dialog
        _logger.LogInformation("Add playlist command executed");
        await Task.CompletedTask;
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
