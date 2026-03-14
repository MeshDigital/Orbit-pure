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

namespace SLSKDONET.ViewModels.Library;

/// <summary>
/// A collection that virtualizes data by loading tracks in pages from the database.
/// Optimized for large libraries (50k+ tracks).
/// </summary>
public class VirtualizedTrackCollection : IList<PlaylistTrackViewModel>, IList, INotifyCollectionChanged, INotifyPropertyChanged, IDisposable
{
    private readonly ILogger _logger;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ArtworkCacheService _artworkCache;
    private readonly Guid _playlistId;
    private readonly string? _filter;
    private readonly bool? _downloadedOnly;
    private readonly int _pageSize;
    
    private int _count = -1;
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
        int pageSize = 100)
    {
        _logger = logger;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _artworkCache = artworkCache;
        _playlistId = playlistId;
        _filter = filter;
        _downloadedOnly = downloadedOnly;
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
            var count = await _libraryService.GetTrackCountAsync(_playlistId, _filter, _downloadedOnly);
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
            if (index < 0 || index >= Count) return GetPlaceholder(); // Return placeholder instead of throwing

            int pageIndex = index / _pageSize;
            int itemIndex = index % _pageSize;

            if (_pages.TryGetValue(pageIndex, out var page))
            {
                if (itemIndex < page.Items.Count)
                    return page.Items[itemIndex];
            }

            // Trigger async load if not already pending
            if (!_pendingPages.Contains(pageIndex))
            {
                 // Trace access to see if virtualization is working
                 _logger.LogDebug("[VirtualizedTrackCollection] Accessing unloaded index {Index} (Page {Page})", index, pageIndex);
                 _ = LoadPageAsync(pageIndex);
            }

            return GetPlaceholder();
        }
        set => throw new NotSupportedException();
    }

    private PlaylistTrackViewModel? _placeholder;
    private PlaylistTrackViewModel GetPlaceholder()
    {
        if (_placeholder == null)
        {
            _placeholder = new PlaylistTrackViewModel(new PlaylistTrack 
            { 
                 Id = Guid.Empty, 
                 Title = "Loading...", 
                 Artist = "...",
                 PlaylistId = _playlistId
            }, _eventBus, _libraryService, _artworkCache);
        }
        return _placeholder;
    }

    private CancellationTokenSource? _notifyThrottleCts;
    private bool _hasPendingNotify;

    private async Task LoadPageAsync(int pageIndex)
    {
        if (!_pendingPages.Add(pageIndex)) return;

        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            _logger.LogInformation("[VirtualizedTrackCollection] Loading page {PageIndex}...", pageIndex);
            
            int skip = pageIndex * _pageSize;
            var tracks = await _libraryService.GetPagedPlaylistTracksAsync(_playlistId, skip, _pageSize, _filter, _downloadedOnly);
            sw.Stop();
            _logger.LogInformation("[VirtualizedTrackCollection] Page {Page} query took {Ms}ms ({Count} tracks)", pageIndex, sw.ElapsedMilliseconds, tracks.Count);
            
            var viewModels = tracks.Select(t => new PlaylistTrackViewModel(t, _eventBus, _libraryService, _artworkCache)).ToList();
            
            _pages[pageIndex] = new PageInfo { Items = viewModels, LastAccess = DateTime.UtcNow };

            // Update ViewModel cache for quick event dispatch
            foreach (var vm in viewModels)
            {
                if (!string.IsNullOrEmpty(vm.GlobalId))
                {
                    _viewModelCache[vm.GlobalId] = vm;
                }
            }

            // Cache management
            if (_pages.Count > 20) 
            {
                var oldest = _pages.OrderBy(p => p.Value.LastAccess).First();
                
                // Remove from VM cache before discarding page
                foreach (var vm in oldest.Value.Items)
                {
                    if (!string.IsNullOrEmpty(vm.GlobalId))
                    {
                        _viewModelCache.Remove(vm.GlobalId);
                    }
                    vm.Dispose();
                }

                _pages.Remove(oldest.Key);
            }

            // PERFORMANCE FIX: Throttle UI notifications to prevent cascading Reset events
            // Only notify UI after 200ms of no new page loads to batch updates
            ScheduleThrottledNotify();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[VirtualizedTrackCollection] LoadPageAsync Failed");
        }
        finally
        {
            _pendingPages.Remove(pageIndex);
        }
    }

    private void ScheduleThrottledNotify()
    {
        _hasPendingNotify = true;
        _notifyThrottleCts?.Cancel();
        _notifyThrottleCts = new CancellationTokenSource();
        var token = _notifyThrottleCts.Token;

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(200, token); // Wait 200ms for more page loads to batch
                if (!token.IsCancellationRequested && _hasPendingNotify)
                {
                    _hasPendingNotify = false;
                    await Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
                    });
                }
            }
            catch (OperationCanceledException) { /* Expected when new pages load */ }
        });
    }

    public int Count => _count == -1 ? 0 : _count;
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
    public bool Contains(PlaylistTrackViewModel item) => _pages.Values.Any(p => p.Items.Contains(item));
    public void CopyTo(PlaylistTrackViewModel[] array, int arrayIndex) 
    { 
        for (int i = 0; i < Count && arrayIndex + i < array.Length; i++)
        {
             array[arrayIndex + i] = this[i];
        }
    }
    public IEnumerator<PlaylistTrackViewModel> GetEnumerator()
    {
        for (int i = 0; i < Count; i++) yield return this[i];
    }
    public int IndexOf(PlaylistTrackViewModel item) => -1;
    public void Insert(int index, PlaylistTrackViewModel item) => throw new NotSupportedException();
    public bool Remove(PlaylistTrackViewModel item) => throw new NotSupportedException();
    public void RemoveAt(int index) => throw new NotSupportedException();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    public void Dispose()
    {
        _disposables.Dispose();
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
