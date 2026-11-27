using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Services.Library;
using SLSKDONET.ViewModels;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// A single row in the Library left-sidebar GENRES tree (Epic #561 / #564 GV3).
/// Represents a club bucket (level 1), subgenre / Other tag (level 2),
/// synthetic "N more…" overflow expander under Other, or Unclassified (last).
/// </summary>
public sealed class GenreNodeViewModel : INotifyPropertyChanged
{
    public GenreKey Key { get; }
    public string DisplayName { get; }
    public int TrackCount { get; }
    public string TrackCountText => IsOverflowRow ? string.Empty : TrackCount.ToString("N0");

    public bool IsBucket { get; }
    public bool IsUnclassified { get; }
    public bool IsOverflowRow { get; }
    public bool HasChildren => Children.Count > 0;
    public bool ShowContextMenu => !IsOverflowRow;

    public bool CanPlay { get; }
    public bool CanShuffle { get; }
    public bool CanSaveAsPlaylist { get; }
    public bool CanSaveAsSmartCrate { get; }
    public bool CanAnalyzeUnclassified => IsUnclassified && AnalyzeUnclassifiedCommand != null;

    public ObservableCollection<GenreNodeViewModel> Children { get; } = new();

    private bool _isExpanded;
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value) return;
            _isExpanded = value;
            OnPropertyChanged();
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            OnPropertyChanged();
        }
    }

    public ICommand ActivateCommand { get; }
    public ICommand ToggleExpandCommand { get; }
    public ICommand? PlayCommand { get; }
    public ICommand? ShuffleCommand { get; }
    public ICommand? SaveAsPlaylistCommand { get; }
    public ICommand? SaveAsSmartCrateCommand { get; }
    public ICommand? AnalyzeUnclassifiedCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public GenreNodeViewModel(
        GenreKey key,
        string displayName,
        int trackCount,
        bool isBucket,
        bool isOverflowRow,
        Action activate,
        Action<GenreKey>? onPlay = null,
        Action<GenreKey>? onShuffle = null,
        Action<GenreKey>? onSaveAsPlaylist = null,
        Action<GenreKey>? onSaveAsSmartCrate = null,
        Action? onAnalyzeUnclassified = null)
    {
        Key = key;
        DisplayName = displayName;
        TrackCount = trackCount;
        IsBucket = isBucket;
        IsUnclassified = !isOverflowRow && key.IsUnclassified;
        IsOverflowRow = isOverflowRow;

        ActivateCommand = new RelayCommand(activate);
        ToggleExpandCommand = new RelayCommand(() => IsExpanded = !IsExpanded);

        if (!isOverflowRow)
        {
            if (onPlay != null)
            {
                PlayCommand = new RelayCommand(() => onPlay(Key));
                CanPlay = true;
            }
            if (onShuffle != null)
            {
                ShuffleCommand = new RelayCommand(() => onShuffle(Key));
                CanShuffle = true;
            }
            if (onSaveAsPlaylist != null)
            {
                SaveAsPlaylistCommand = new RelayCommand(() => onSaveAsPlaylist(Key));
                CanSaveAsPlaylist = true;
            }
            if (onSaveAsSmartCrate != null)
            {
                SaveAsSmartCrateCommand = new RelayCommand(() => onSaveAsSmartCrate(Key));
                CanSaveAsSmartCrate = true;
            }
            if (IsUnclassified && onAnalyzeUnclassified != null)
            {
                AnalyzeUnclassifiedCommand = new RelayCommand(onAnalyzeUnclassified);
            }
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

/// <summary>
/// ViewModel for the collapsible GENRES section in the Library left sidebar (Epic #561 / #564 GV3).
/// Reads directly from <see cref="LibraryGenreIndex"/> and stays in two-way sync with
/// <see cref="TrackListViewModel.GenreFilter"/>.
/// </summary>
public sealed class LibraryGenresSectionViewModel : INotifyPropertyChanged, IDisposable
{
    public const int OtherTopLimit = 10;

    private readonly LibraryGenreIndex? _genreIndex;
    private readonly TrackListViewModel? _tracks;
    private readonly Action<GenreKey> _onGenreActivated;
    private readonly Action<GenreKey>? _onPlayGenre;
    private readonly Action<GenreKey>? _onShuffleGenre;
    private readonly Action<GenreKey>? _onSaveAsPlaylist;
    private readonly Action<GenreKey>? _onSaveAsSmartCrate;
    private readonly Action? _onAnalyzeUnclassified;
    private readonly Action? _onOpenAnalysis;
    private readonly Action<bool>? _onCollapsedChanged;

    private bool _otherOverflowExpanded;
    private bool _isDisposed;

    public ObservableCollection<GenreNodeViewModel> Nodes { get; } = new();

    private bool _isCollapsed;
    public bool IsCollapsed
    {
        get => _isCollapsed;
        set
        {
            if (_isCollapsed == value) return;
            _isCollapsed = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsExpanded));
            _onCollapsedChanged?.Invoke(value);
        }
    }

    public bool IsExpanded => !IsCollapsed;

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (_isLoading == value) return;
            _isLoading = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNodes));
        }
    }

    private string? _lastError;
    public string? LastError
    {
        get => _lastError;
        private set
        {
            if (_lastError == value) return;
            _lastError = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasError));
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNodes));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(LastError);
    public bool IsEmpty => !IsLoading && !HasError && Nodes.Count == 0;
    public bool HasNodes => !IsLoading && !HasError && Nodes.Count > 0;

    private int _totalTrackCount;
    public int TotalTrackCount
    {
        get => _totalTrackCount;
        private set
        {
            if (_totalTrackCount == value) return;
            _totalTrackCount = value;
            OnPropertyChanged();
        }
    }

    public ICommand ToggleCollapsedCommand { get; }
    public ICommand RetryCommand { get; }
    public ICommand OpenAnalysisCommand { get; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public LibraryGenresSectionViewModel(
        LibraryGenreIndex? genreIndex,
        TrackListViewModel? tracks,
        Action<GenreKey> onGenreActivated,
        Action<GenreKey>? onPlayGenre = null,
        Action<GenreKey>? onShuffleGenre = null,
        Action<GenreKey>? onSaveAsPlaylist = null,
        Action<GenreKey>? onSaveAsSmartCrate = null,
        Action? onAnalyzeUnclassified = null,
        Action? onOpenAnalysis = null,
        bool initialCollapsed = false,
        Action<bool>? onCollapsedChanged = null)
    {
        _genreIndex = genreIndex;
        _tracks = tracks;
        _onGenreActivated = onGenreActivated;
        _onPlayGenre = onPlayGenre;
        _onShuffleGenre = onShuffleGenre;
        _onSaveAsPlaylist = onSaveAsPlaylist;
        _onSaveAsSmartCrate = onSaveAsSmartCrate;
        _onAnalyzeUnclassified = onAnalyzeUnclassified;
        _onOpenAnalysis = onOpenAnalysis;
        _isCollapsed = initialCollapsed;
        _onCollapsedChanged = onCollapsedChanged;

        ToggleCollapsedCommand = new RelayCommand(() => IsCollapsed = !IsCollapsed);
        RetryCommand = new AsyncRelayCommand(RetryBuildAsync);
        OpenAnalysisCommand = new RelayCommand(() => _onOpenAnalysis?.Invoke());

        if (_tracks != null)
        {
            _tracks.PropertyChanged += OnTracksPropertyChanged;
        }
        if (_genreIndex != null)
        {
            _genreIndex.Changed += OnGenreIndexChanged;
            RefreshFromIndex();
            if (!_genreIndex.IsReady && !_genreIndex.IsBuilding)
            {
                _ = EnsureBuiltAsync();
            }
        }
    }

    private async Task EnsureBuiltAsync()
    {
        if (_genreIndex == null || _isDisposed) return;
        await _genreIndex.BuildAsync();
        PostToUi(RefreshFromIndex);
    }

    private async Task RetryBuildAsync()
    {
        if (_genreIndex == null || _isDisposed) return;
        IsLoading = true;
        LastError = null;
        await _genreIndex.BuildAsync();
        PostToUi(RefreshFromIndex);
    }

    private void OnGenreIndexChanged(object? sender, EventArgs e)
    {
        if (_isDisposed) return;
        PostToUi(RefreshFromIndex);
    }

    private void OnTracksPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isDisposed || _tracks == null) return;
        if (e.PropertyName == nameof(TrackListViewModel.GenreFilter))
        {
            SyncSelection(_tracks.GenreFilter);
        }
    }

    public void RefreshFromIndex()
    {
        if (_genreIndex == null)
        {
            IsLoading = false;
            LastError = null;
            Nodes.Clear();
            TotalTrackCount = 0;
            OnPropertyChanged(nameof(IsEmpty));
            OnPropertyChanged(nameof(HasNodes));
            return;
        }

        LastError = _genreIndex.LastError;
        IsLoading = !_genreIndex.IsReady && string.IsNullOrEmpty(LastError);
        if (IsLoading || HasError)
        {
            return;
        }

        // Preserve which buckets were expanded across rebuilds
        var expandedBuckets = new HashSet<GenreKey>(
            Nodes.Where(n => n.IsExpanded).Select(n => n.Key));

        var tree = _genreIndex.GetTree();
        Nodes.Clear();

        int total = 0;
        foreach (var bucket in tree)
        {
            total += bucket.TrackCount;
            var bucketNode = new GenreNodeViewModel(
                bucket.Key,
                bucket.DisplayName,
                bucket.TrackCount,
                isBucket: true,
                isOverflowRow: false,
                activate: () => _onGenreActivated(bucket.Key),
                onPlay: _onPlayGenre,
                onShuffle: _onShuffleGenre,
                onSaveAsPlaylist: _onSaveAsPlaylist,
                onSaveAsSmartCrate: _onSaveAsSmartCrate,
                onAnalyzeUnclassified: bucket.Key.IsUnclassified ? _onAnalyzeUnclassified : null)
            {
                IsExpanded = expandedBuckets.Contains(bucket.Key)
            };

            var children = bucket.Children;
            var isOtherWithOverflow = !bucket.Key.IsUnclassified
                && bucket.Key.Bucket == GenreBucket.Other
                && !_otherOverflowExpanded
                && children.Count > OtherTopLimit;

            var visibleChildren = isOtherWithOverflow
                ? children.Take(OtherTopLimit)
                : children;

            foreach (var child in visibleChildren)
            {
                bucketNode.Children.Add(new GenreNodeViewModel(
                    child.Key,
                    child.DisplayName,
                    child.TrackCount,
                    isBucket: false,
                    isOverflowRow: false,
                    activate: () => _onGenreActivated(child.Key),
                    onPlay: _onPlayGenre,
                    onShuffle: _onShuffleGenre,
                    onSaveAsPlaylist: _onSaveAsPlaylist,
                    onSaveAsSmartCrate: _onSaveAsSmartCrate));
            }

            if (isOtherWithOverflow)
            {
                var overflowCount = children.Count - OtherTopLimit;
                bucketNode.Children.Add(new GenreNodeViewModel(
                    new GenreKey(GenreBucket.Other, "__overflow__"),
                    $"{overflowCount} more…",
                    overflowCount,
                    isBucket: false,
                    isOverflowRow: true,
                    activate: ExpandOtherOverflow));
            }

            Nodes.Add(bucketNode);
        }

        TotalTrackCount = total;
        SyncSelection(_tracks?.GenreFilter);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasNodes));
    }

    public void ExpandOtherOverflow()
    {
        if (_otherOverflowExpanded) return;
        _otherOverflowExpanded = true;
        RefreshFromIndex();
        var otherNode = Nodes.FirstOrDefault(n => !n.IsUnclassified && n.Key.Bucket == GenreBucket.Other);
        if (otherNode != null)
            otherNode.IsExpanded = true;
    }

    public void SyncSelection(GenreKey? activeFilter)
    {
        foreach (var bucket in Nodes)
        {
            var bucketSelected = activeFilter.HasValue && bucket.Key == activeFilter.Value;
            bucket.IsSelected = bucketSelected;

            var anyChildSelected = false;
            foreach (var child in bucket.Children)
            {
                var childSelected = !child.IsOverflowRow && activeFilter.HasValue && child.Key == activeFilter.Value;
                child.IsSelected = childSelected;
                if (childSelected) anyChildSelected = true;
            }

            if (anyChildSelected)
            {
                bucket.IsExpanded = true;
            }
        }
    }

    private static void PostToUi(Action action)
    {
        try
        {
            if (Dispatcher.UIThread.CheckAccess())
                action();
            else
                Dispatcher.UIThread.Post(action);
        }
        catch
        {
            // Headless unit test without Avalonia dispatcher loop: run inline.
            action();
        }
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;
        if (_tracks != null)
        {
            _tracks.PropertyChanged -= OnTracksPropertyChanged;
        }
        if (_genreIndex != null)
        {
            _genreIndex.Changed -= OnGenreIndexChanged;
        }
    }
}
