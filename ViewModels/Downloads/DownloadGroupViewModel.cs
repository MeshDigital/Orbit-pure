using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive.Linq;
using DynamicData;
using DynamicData.Binding;
using ReactiveUI;
using SLSKDONET.Models;

namespace SLSKDONET.ViewModels.Downloads;

/// <summary>
/// Phase 2: Represents a grouped collection of downloads (Album or Project).
/// Aggregates progress, speed, and status from underlying tracks.
/// </summary>
public class DownloadGroupViewModel : ReactiveObject, IDisposable
{
    private readonly IDisposable _cleanUp;
    private bool _isExpanded = true;
    private double _totalProgress;
    private double _totalSpeed;
    private string _statusText = "Initializing";
    private bool _hasFailures;
    // private bool _isPaused; // Unused

    public Guid? GroupKey { get; } // AlbumId or ProjectId
    public string Title { get; }
    public string Subtitle { get; }
    public string? ArtworkUrl { get; }
    public ReadOnlyObservableCollection<UnifiedTrackViewModel> Tracks { get; }
    public DateTime LastActivity { get; private set; }
    
    // Aggregate Properties
    public double TotalProgress
    {
        get => _totalProgress;
        set => this.RaiseAndSetIfChanged(ref _totalProgress, value);
    }

    public double TotalSpeed
    {
        get => _totalSpeed;
        set => this.RaiseAndSetIfChanged(ref _totalSpeed, value);
    }
    
    public string StatusText
    {
        get => _statusText;
        set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }
    
    public bool HasFailures
    {
        get => _hasFailures;
        set => this.RaiseAndSetIfChanged(ref _hasFailures, value);
    }

    public bool IsExpanded
    {
        get => _isExpanded;
        set => this.RaiseAndSetIfChanged(ref _isExpanded, value);
    }
    
    // Commands
    public IReactiveCommand PauseCommand { get; }
    public IReactiveCommand ResumeCommand { get; }
    public IReactiveCommand CancelCommand { get; }
    public IReactiveCommand ToggleExpandedCommand { get; }

    public DownloadGroupViewModel(IGroup<UnifiedTrackViewModel, string, Guid> group)
    {
        GroupKey = group.Key;
        
        // Connect to the group cache
        var tracksLoader = group.Cache.Connect()
            .Bind(out var tracks)
            .Subscribe();

        Tracks = tracks;

        // Initialize Metadata from first track (assuming homogenous groups for now)
        var firstTrack = Tracks.FirstOrDefault()?.Model;
        
        if (!string.IsNullOrEmpty(firstTrack?.SourcePlaylistName))
        {
            Title = firstTrack.SourcePlaylistName;
            Subtitle = string.IsNullOrEmpty(firstTrack.Artist) ? "Imported Playlist" : $"By {firstTrack.Artist}";
            ArtworkUrl = firstTrack.AlbumArtUrl;
        }
        else if (GroupKey == null)
        {
            Title = "Singles & Ad-Hoc";
            Subtitle = "Individual Downloads";
            ArtworkUrl = null;
        }
        else
        {
            Title = firstTrack?.Album ?? "Unknown Album";
            Subtitle = firstTrack?.Artist ?? "Unknown Artist";
            ArtworkUrl = firstTrack?.AlbumArtUrl; // Or locally cached path
        }

        // Aggregate Progress & Speed
        // We observe property changes on all items in the collection
        var aggregates = group.Cache.Connect()
            .WhenAnyPropertyChanged(nameof(UnifiedTrackViewModel.Progress), nameof(UnifiedTrackViewModel.DownloadSpeed), nameof(UnifiedTrackViewModel.State))
            .Subscribe(_ => RecalculateAggregates());

        // Also recalculate when list changes (add/remove)
        var listChanges = group.Cache.Connect()
            .Subscribe(_ => RecalculateAggregates());

        RecalculateAggregates(); // Initial calc

        // Group Commands
        PauseCommand = ReactiveCommand.Create(() => 
        {
            var items = Tracks.ToList();
            foreach (var t in items) t.PauseCommand.Execute(null);
        });
        
        ResumeCommand = ReactiveCommand.Create(() => 
        {
            var items = Tracks.ToList();
            foreach (var t in items) t.ResumeCommand.Execute(null);
        });

        CancelCommand = ReactiveCommand.Create(() => 
        {
            var items = Tracks.ToList();
            foreach (var t in items) t.CancelCommand.Execute(null);
        });

        ToggleExpandedCommand = ReactiveCommand.Create(() => { IsExpanded = !IsExpanded; });

        _cleanUp = new System.Reactive.Disposables.CompositeDisposable(tracksLoader, aggregates, listChanges);
    }

    private void RecalculateAggregates()
    {
        if (Tracks.Count == 0)
        {
            TotalProgress = 0;
            TotalSpeed = 0;
            StatusText = "Empty";
            return;
        }

        // Simple Average Progress
        TotalProgress = Tracks.Average(t => t.Progress);
        
        // Sum Speed
        TotalSpeed = Tracks.Sum(t => t.DownloadSpeed);
        
        // Status Logic
        int completed = Tracks.Count(t => t.State == PlaylistTrackState.Completed);
        int failed = Tracks.Count(t => t.State == PlaylistTrackState.Failed);
        int downloading = Tracks.Count(t => t.State == PlaylistTrackState.Downloading);
        int queued = Tracks.Count(t => t.State == PlaylistTrackState.Pending);
        
        HasFailures = failed > 0;

        if (downloading > 0)
        {
            StatusText = queued > 0 
                ? $"{downloading} downloading, {queued} queued" 
                : $"{downloading} downloading";
        }
        else if (queued > 0)
        {
             StatusText = $"{queued} queued";
        }
        else if (HasFailures)
        {
            StatusText = $"{failed} failed";
        }
        else if (completed == Tracks.Count && Tracks.Count > 0)
        {
            StatusText = "Completed";
        }
        else
        {
            StatusText = $"{Tracks.Count} tracks";
        }

        LastActivity = Tracks.Any() ? Tracks.Max(t => t.Model.AddedAt) : DateTime.MinValue;
    }

    public void Dispose()
    {
        _cleanUp.Dispose();
    }
}
