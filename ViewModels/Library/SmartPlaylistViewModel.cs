using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services;
using System.Text.Json;
using SLSKDONET.Views;
using Avalonia.Threading;
using System.Reactive;
using ReactiveUI;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// Manages smart playlists (Recently Added, Most Played, High Quality, Failed Downloads, Liked Tracks).
/// Handles dynamic filtering and playlist refresh logic.
/// </summary>
public class SmartPlaylistViewModel : INotifyPropertyChanged
{
    private readonly ILogger<SmartPlaylistViewModel> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly DatabaseService _db;
    private readonly ISmartPlaylistService _smartPlaylistService;
    private readonly SmartCrateService _smartCrateService;
    private readonly IDialogService _dialogService;
    private MainViewModel? _mainViewModel; // Injected post-construction

    public ObservableCollection<SmartPlaylist> SmartPlaylists { get; } = new();

    private SmartPlaylist? _selectedSmartPlaylist;
    public SmartPlaylist? SelectedSmartPlaylist
    {
        get => _selectedSmartPlaylist;
        set
        {
            if (_selectedSmartPlaylist != value)
            {
                _selectedSmartPlaylist = value;
                OnPropertyChanged();
                
                // Raise event for parent to handle
                SmartPlaylistSelected?.Invoke(this, value);
            }
        }
    }

    public event EventHandler<SmartPlaylist?>? SmartPlaylistSelected;
    public event PropertyChangedEventHandler? PropertyChanged;
    
    public ReactiveCommand<Unit, Unit> CreateCrateCommand { get; }

    public SmartPlaylistViewModel(
        ILogger<SmartPlaylistViewModel> logger,
        ILoggerFactory loggerFactory,
        DatabaseService db,
        ISmartPlaylistService smartPlaylistService,
        SmartCrateService smartCrateService,
        IDialogService dialogService)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _db = db;
        _smartPlaylistService = smartPlaylistService;
        _smartCrateService = smartCrateService;
        _dialogService = dialogService;

        CreateCrateCommand = ReactiveCommand.CreateFromTask(CreateCrateAsync);

        InitializeSmartPlaylists();
    }
    
    private async Task CreateCrateAsync()
    {
        try
        {
            var vm = new SmartCrateEditorViewModel(
                _smartCrateService, 
                _loggerFactory.CreateLogger<SmartCrateEditorViewModel>()
            );
            
            var newCrate = await _dialogService.ShowSmartCrateEditorAsync(vm);
            
            if (newCrate != null)
            {
                var playlist = new SmartPlaylist
                {
                    Id = newCrate.Id,
                    Name = newCrate.Name,
                    Icon = "🔮",
                    Definition = newCrate
                };
                
                SmartPlaylists.Add(playlist);
                SelectedSmartPlaylist = playlist; // Auto-select
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create smart crate");
        }
    }

    public void SetMainViewModel(MainViewModel mainViewModel)
    {
        _mainViewModel = mainViewModel;
    }

    /// <summary>
    /// Initializes the smart playlist definitions.
    /// </summary>
    public async void InitializeSmartPlaylists()
    {
        SmartPlaylists.Clear();

        // 1. Add System Smart Playlists (Hardcoded logic)
        SmartPlaylists.Add(new SmartPlaylist
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            Name = "Recently Added",
            Icon = "🕒",
            Filter = tracks => tracks.OrderByDescending(t => t.Model?.AddedAt).Take(50)
        });

        SmartPlaylists.Add(new SmartPlaylist
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Name = "Most Played",
            Icon = "🔥",
            Filter = tracks => tracks
                .Where(t => t.Model?.PlayCount > 0)
                .OrderByDescending(t => t.Model?.PlayCount)
                .Take(50)
        });

        SmartPlaylists.Add(new SmartPlaylist
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000003"),
            Name = "High Quality",
            Icon = "💎",
            Filter = tracks => tracks
                .Where(t => t.State == PlaylistTrackState.Completed)
                .OrderByDescending(t => t.Model?.AddedAt)
        });

        SmartPlaylists.Add(new SmartPlaylist
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000004"),
            Name = "Failed Downloads",
            Icon = "❌",
            Filter = tracks => tracks.Where(t => t.State == PlaylistTrackState.Failed)
        });

        SmartPlaylists.Add(new SmartPlaylist
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000005"),
            Name = "Liked Tracks",
            Icon = "❤️",
            Filter = tracks => tracks.Where(t => t.Model?.IsLiked == true)
        });

        SmartPlaylists.Add(new SmartPlaylist
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000006"),
            Name = "Curation Queue",
            Icon = "⚖️",
            Filter = tracks => tracks.Where(t => t.Model?.IsReviewNeeded == true || t.Model?.CurationConfidence == Data.Entities.CurationConfidence.Low)
        });

        // 2. Load User-Created Smart Playlists from DB (Phase 20)
        try
        {
            var jobs = await _db.LoadAllPlaylistJobsAsync();
            var smartJobs = jobs.Where(j => j.IsSmartPlaylist).ToList();

            foreach (var job in smartJobs)
            {
                if (!string.IsNullOrEmpty(job.SmartCriteriaJson))
                {
                    try
                    {
                        var criteria = JsonSerializer.Deserialize<SmartPlaylistCriteria>(job.SmartCriteriaJson);
                        if (criteria != null)
                        {
                            SmartPlaylists.Add(new SmartPlaylist
                            {
                                Id = job.Id,
                                Name = job.SourceTitle,
                                Icon = "🧠", // Brain icon for smart playlists
                                Criteria = criteria,
                                // Dynamic filter using the service
                                Filter = tracks => tracks.Where(t => 
                                    t.Model != null && _smartPlaylistService.EvaluateTrack(t.Model, criteria))
                            });
                        }
                    }
                    catch (Exception jsonEx)
                    {
                        _logger.LogWarning(jsonEx, "Failed to deserialize criteria for playlist {Id}", job.Id);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to load user smart playlists");
        }
        
        // 3. Phase 23: Load Smart Crates (DB-backed dynamic playlists)
        try
        {
            var crates = await _smartCrateService.GetAllCratesAsync();
            foreach (var crate in crates)
            {
                SmartPlaylists.Add(new SmartPlaylist
                {
                    Id = crate.Id,
                    Name = crate.Name,
                    Icon = "🔮", // Crystal Ball for Smart Crates
                    Definition = crate // Links to DB logic
                });
            }
        }
        catch (Exception ex)
        {
             _logger.LogError(ex, "Failed to load smart crates");
        }

        _logger.LogInformation("Initialized {Count} smart playlists", SmartPlaylists.Count);
    }

    /// <summary>
    /// Refreshes the selected smart playlist.
    /// </summary>
    public ObservableCollection<PlaylistTrackViewModel> RefreshSmartPlaylist(SmartPlaylist? playlist)
    {
        if (playlist == null)
            return new ObservableCollection<PlaylistTrackViewModel>();

        try
        {
            if (_mainViewModel == null) return new ObservableCollection<PlaylistTrackViewModel>();
            var allTracks = _mainViewModel.AllGlobalTracks;
            var filtered = playlist.Filter(allTracks).ToList();

            _logger.LogInformation("Smart playlist '{Name}' has {Count} tracks", 
                playlist.Name, filtered.Count);

            var result = new ObservableCollection<PlaylistTrackViewModel>();
            foreach (var track in filtered)
                result.Add(track);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh smart playlist: {Name}", playlist.Name);
            return new ObservableCollection<PlaylistTrackViewModel>();
        }
    }

    /// <summary>
    /// Refreshes the "Liked Tracks" smart playlist specifically.
    /// </summary>
    public ObservableCollection<PlaylistTrackViewModel> RefreshLikedTracks()
    {
        try
        {
            if (_mainViewModel == null) return new ObservableCollection<PlaylistTrackViewModel>();
            var likedTracks = _mainViewModel.AllGlobalTracks
                .Where(t => t.Model?.IsLiked == true)
                .OrderByDescending(t => t.Model?.AddedAt)
                .ToList();

            _logger.LogInformation("Found {Count} liked tracks", likedTracks.Count);

            var result = new ObservableCollection<PlaylistTrackViewModel>();
            foreach (var track in likedTracks)
                result.Add(track);

            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to refresh liked tracks");
            return new ObservableCollection<PlaylistTrackViewModel>();
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? propertyName = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}

/// <summary>
/// Represents a smart playlist definition.
/// </summary>
public class SmartPlaylist
{
    public Guid Id { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;
    public SmartPlaylistCriteria? Criteria { get; set; } // Null for system lists
    public Func<IEnumerable<PlaylistTrackViewModel>, IEnumerable<PlaylistTrackViewModel>> Filter { get; set; } = _ => Enumerable.Empty<PlaylistTrackViewModel>();
    
    // Phase 23: Smart Crate Link
    public Data.Entities.SmartCrateDefinitionEntity? Definition { get; set; }
}
