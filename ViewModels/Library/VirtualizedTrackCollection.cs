using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;
using SLSKDONET.Services;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using System.Reactive.Disposables;
using SLSKDONET.Helpers;

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// A collection that virtualizes data by loading tracks in pages from the database.
/// Optimized for large libraries (50k+ tracks).
/// </summary>
public class VirtualizedTrackCollection : IList<PlaylistTrackViewModel>, IList, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable, ISupportIncrementalLoading
{
    private readonly ILogger _logger;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCache;
    private readonly Guid _playlistId;
    private readonly string? _filter;
    private readonly bool? _downloadedOnly;
    private readonly int _pageSize;
    private readonly IEnumerable<string>? _hashFilter;
    
    private int _count = -1;
    private readonly List<PlaylistTrackViewModel> _loadedItems = new();
    private readonly Dictionary<int, PageInfo> _pages = new();
    private readonly Dictionary<string, PlaylistTrackViewModel> _viewModelCache = new();
    private readonly HashSet<int> _pendingPages = new();
    private readonly CompositeDisposable _disposables = new();
    
    public event NotifyCollectionChangedEventHandler? CollectionChanged;
    public event PropertyChangedEventHandler? PropertyChanged;

    public VirtualizedTrackCollection(
        ILogger logger,
        ILibraryService libraryService,
        IEventBus eventBus,
        ArtworkCacheService artworkCache,
        Guid playlistId,
        string? filter = null,
        bool? downloadedOnly = null,
        IEnumerable<string>? hashFilter = null,
        int pageSize = 100)
    {
        _logger = logger;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _playlistId = playlistId;
        _filter = filter;
        _downloadedOnly = downloadedOnly;
        _hashFilter = hashFilter;
        _pageSize = pageSize;
        
        // Centralized event dispatch
        SubscribeToEvents();

        // Initial count load
        _ = LoadCountAsync();
    }

    private void SubscribeToEvents()
    {
        _disposables.Add(_eventBus.GetEvent<TrackStateChangedEvent>().Subscribe(evt => DispatchToViewModel(evt.TrackGlobalId, vm => vm.OnStateChanged(evt))));
        _disposables.Add(_eventBus.GetEvent<TrackProgressChangedEvent>().Subscribe(evt => DispatchToViewModel(evt.TrackGlobalId, vm => vm.OnProgressChanged(evt))));
        _disposables.Add(_eventBus.GetEvent<Models.TrackMetadataUpdatedEvent>().Subscribe(evt => DispatchToViewModel(evt.TrackGlobalId, vm => vm.OnMetadataUpdated(evt))));
        _disposables.Add(_eventBus.GetEvent<Models.TrackAnalysisStartedEvent>().Subscribe(evt => DispatchToViewModel(evt.TrackGlobalId, vm => vm.OnAnalysisStarted(evt))));
        _disposables.Add(_eventBus.GetEvent<Models.TrackAnalysisFailedEvent>().Subscribe(evt => DispatchToViewModel(evt.TrackGlobalId, vm => vm.OnAnalysisFailed(evt))));
        _disposables.Add(_eventBus.GetEvent<Events.TrackDetailedStatusEvent>().Subscribe(evt => DispatchToViewModel(evt.TrackHash, vm => vm.OnDetailedStatus(evt))));
    }

    private void DispatchToViewModel(string globalId, Action<PlaylistTrackViewModel> action)
    {
        if (string.IsNullOrEmpty(globalId)) return;
        if (_viewModelCache.TryGetValue(globalId, out var vm))
        {
            action(vm);
        }
    }

    private async Task LoadCountAsync()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try 
        {
            _logger.LogInformation("[VirtualizedTrackCollection] Starting count query...");
            var count = await _libraryService.GetTrackCountAsync(_playlistId, _filter, _downloadedOnly, _hashFilter);
            sw.Stop();
            _logger.LogInformation("[VirtualizedTrackCollection] Count query took {Ms}ms, returned {Count}", sw.ElapsedMilliseconds, count);
            _count = count;
            
            // PERFORMANCE FIX: Only notify Count change, NO Reset event here.
            // The Reset causes all 3 views (List/Cards/Pro) to recalculate, triggering massive page loads.
            // The UI will naturally update as items are accessed through virtualization.
            Dispatcher.UIThread.Post(() =>
            {
                OnPropertyChanged(nameof(Count));
                _logger.LogInformation("[VirtualizedTrackCollection] Count updated, no Reset fired");
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VirtualizedTrackCollection] LoadCountAsync Failed");
        }
    }

    public PlaylistTrackViewModel this[int index]
    {
        get
        {
            if (index < 0 || index >= _loadedItems.Count) throw new ArgumentOutOfRangeException(nameof(index));
            return _loadedItems[index];
        }
        set
        {
            if (index < 0 || index >= _loadedItems.Count) throw new ArgumentOutOfRangeException(nameof(index));
            _loadedItems[index] = value;
        }
    }



    public int Count => _loadedItems.Count;
    public bool IsReadOnly => true;

    public IEnumerable<PlaylistTrackViewModel> GetSubset(int count)
    {
        for (int i = 0; i < Math.Min(count, Count); i++)
        {
            yield return this[i];
        }
    }

    // IList (non-generic) Implementation
    object? IList.this[int index] 
    { 
        get => this[index]; 
        set => throw new NotSupportedException(); 
    }
    bool IList.IsFixedSize => true;
    bool IList.IsReadOnly => true;
    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    bool IList.Contains(object? value) => value is PlaylistTrackViewModel vm && Contains(vm);
    int IList.IndexOf(object? value) => (value is PlaylistTrackViewModel vm) ? IndexOf(vm) : -1;
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
    
    // ICollection (non-generic) Implementation
    void ICollection.CopyTo(Array array, int index)
    {
        for (int i = 0; i < Count; i++)
        {
            array.SetValue(this[i], index + i);
        }
    }
    bool ICollection.IsSynchronized => false;
    object ICollection.SyncRoot => this;

    // Generic IList/ICollection/IEnumerable
    public void Add(PlaylistTrackViewModel item) => throw new NotSupportedException();
    public void Clear() => throw new NotSupportedException();
    public bool Contains(PlaylistTrackViewModel item) => _loadedItems.Contains(item);
    public void CopyTo(PlaylistTrackViewModel[] array, int arrayIndex) 
    { 
        _loadedItems.CopyTo(array, arrayIndex);
    }
    public IEnumerator<PlaylistTrackViewModel> GetEnumerator()
    {
        return _loadedItems.GetEnumerator();
    }
    public int IndexOf(PlaylistTrackViewModel item) => _loadedItems.IndexOf(item);
    public void Insert(int index, PlaylistTrackViewModel item) => throw new NotSupportedException();
    public bool Remove(PlaylistTrackViewModel item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public bool HasMoreItems => _count == -1 || _loadedItems.Count < _count;

    public async Task<int> LoadMoreItemsAsync(uint count)
    {
        var itemsToLoad = (int)Math.Min(count, _pageSize);
        var startIndex = _loadedItems.Count;
        var pageIndex = startIndex / _pageSize;

        if (_pendingPages.Contains(pageIndex)) return 0;

        _pendingPages.Add(pageIndex);

        try
        {
            var tracks = await _libraryService.GetPagedPlaylistTracksAsync(_playlistId, startIndex, itemsToLoad, _filter, _downloadedOnly, _hashFilter);
            var viewModels = tracks.Select(t => new PlaylistTrackViewModel(t, _eventBus, _libraryService, _artworkCache)).ToList();

            _loadedItems.AddRange(viewModels);

            // Store in pages for cache management
            var pageInfo = new PageInfo { Items = viewModels, LastAccess = DateTime.Now };
            _pages[pageIndex] = pageInfo;

            // Notify collection changed
            CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Add, viewModels, startIndex));

            return viewModels.Count;
        }
        finally
        {
            _pendingPages.Remove(pageIndex);
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();
        foreach (var item in _loadedItems) item.Dispose();
        _loadedItems.Clear();
        foreach (var page in _pages.Values)
        {
            foreach (var vm in page.Items) vm.Dispose();
        }
        _pages.Clear();
        _viewModelCache.Clear();
    }

    protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private class PageInfo
    {
        public List<PlaylistTrackViewModel> Items { get; set; } = new();
        public DateTime LastAccess { get; set; }
    }
}
