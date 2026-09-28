using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Reactive;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Discovery;
using SLSKDONET.Utils;
using SLSKDONET.Views;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Discover sidepanel tab: tracks worth adding to the selected playlist, from Beatport and
/// Deezer, ranked by fit. Queue adds a suggestion to the playlist as a missing track and hands it
/// to the Soulseek download pipeline; Search opens it on the Search page; Open shows the store page.
/// </summary>
public sealed class PlaylistDiscoveryViewModel : ReactiveObject, IDisposable
{
    private readonly PlaylistDiscoveryService _discovery;
    private readonly ILibraryService _library;
    private readonly DownloadManager _downloads;
    private readonly ILibraryPreviewPlayer _preview;
    private readonly ConfigManager _config;
    private readonly IEventBus _eventBus;
    private readonly INavigationService _navigation;
    private readonly IServiceProvider _services;
    private readonly ILogger<PlaylistDiscoveryViewModel> _logger;
    private readonly ArtworkCacheService _artworkCache;
    private readonly IDisposable _contextSubscription;
    private static readonly HttpClient PreviewHttp = new() { Timeout = TimeSpan.FromSeconds(20) };

    // Results per playlist: in memory for this session, on disk across restarts (DiscoveryCache).
    private readonly DiscoveryCache _diskCache;
    private readonly Dictionary<Guid, DiscoveryCacheEntry> _cache = new();
    private readonly Dictionary<Guid, DateTime> _lastDriftCheckUtc = new();
    private static readonly TimeSpan DriftCheckInterval = TimeSpan.FromMinutes(2);
    private CancellationTokenSource? _loadCts;
    private int _refreshSeed;
    private string? _previewTempFile;

    public PlaylistDiscoveryViewModel(
        PlaylistDiscoveryService discovery,
        ILibraryService library,
        DownloadManager downloads,
        ILibraryPreviewPlayer preview,
        ConfigManager config,
        IEventBus eventBus,
        INavigationService navigation,
        IServiceProvider services,
        ArtworkCacheService artworkCache,
        DiscoveryCache diskCache,
        ILogger<PlaylistDiscoveryViewModel> logger)
    {
        _diskCache = diskCache;
        _artworkCache = artworkCache;
        _discovery = discovery;
        _library = library;
        _downloads = downloads;
        _preview = preview;
        _config = config;
        _eventBus = eventBus;
        _navigation = navigation;
        _services = services;
        _logger = logger;

        var cfg = config.GetCurrent();
        _useBeatport = cfg.DiscoverUseBeatport;
        _useDeezer = cfg.DiscoverUseDeezer;

        RefreshCommand = ReactiveCommand.CreateFromTask(() => LoadAsync(forceRefresh: true));
        QueueCommand = ReactiveCommand.CreateFromTask<DiscoverySuggestionViewModel>(QueueAsync);
        QueueAllCommand = ReactiveCommand.CreateFromTask(QueueTopAsync);
        SearchCommand = ReactiveCommand.CreateFromTask<DiscoverySuggestionViewModel>(SearchAsync);
        OpenLinkCommand = ReactiveCommand.Create<DiscoverySuggestionViewModel>(OpenLink);
        PreviewCommand = ReactiveCommand.CreateFromTask<DiscoverySuggestionViewModel>(TogglePreviewAsync);
        DismissCommand = ReactiveCommand.Create<DiscoverySuggestionViewModel>(Dismiss);

        _preview.PreviewStopped += OnPreviewStopped;

        // Follow the Library's selected playlist; only fetch while this tab is showing.
        _contextSubscription = MessageBus.Current.Listen<PlaylistContextChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => SetPlaylist(e.PlaylistId, e.Title, load: IsActive));
    }

    public ObservableCollection<DiscoverySuggestionViewModel> Suggestions { get; } = new();

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<DiscoverySuggestionViewModel, Unit> QueueCommand { get; }
    public ReactiveCommand<Unit, Unit> QueueAllCommand { get; }
    public ReactiveCommand<DiscoverySuggestionViewModel, Unit> SearchCommand { get; }
    public ReactiveCommand<DiscoverySuggestionViewModel, Unit> OpenLinkCommand { get; }
    public ReactiveCommand<DiscoverySuggestionViewModel, Unit> PreviewCommand { get; }
    public ReactiveCommand<DiscoverySuggestionViewModel, Unit> DismissCommand { get; }

    private Guid? _playlistId;
    public Guid? PlaylistId { get => _playlistId; private set => this.RaiseAndSetIfChanged(ref _playlistId, value); }

    private string _playlistTitle = string.Empty;
    public string PlaylistTitle { get => _playlistTitle; private set => this.RaiseAndSetIfChanged(ref _playlistTitle, value); }

    public bool HasPlaylist => PlaylistId is { } id && id != Guid.Empty;

    private bool _isLoading;
    public bool IsLoading { get => _isLoading; private set => this.RaiseAndSetIfChanged(ref _isLoading, value); }

    private string _status = "Select a playlist in the Library to get suggestions.";
    public string Status { get => _status; private set => this.RaiseAndSetIfChanged(ref _status, value); }

    private string _summary = string.Empty;
    public string Summary { get => _summary; private set => this.RaiseAndSetIfChanged(ref _summary, value); }

    public bool HasSuggestions => Suggestions.Count > 0;

    /// <summary>Set by the sidebar while the Discover tab is the one showing.</summary>
    public bool IsActive { get; set; }

    private bool _useBeatport;
    public bool UseBeatport
    {
        get => _useBeatport;
        set { if (_useBeatport == value) return; this.RaiseAndSetIfChanged(ref _useBeatport, value); SaveSources(); }
    }

    private bool _useDeezer;
    public bool UseDeezer
    {
        get => _useDeezer;
        set { if (_useDeezer == value) return; this.RaiseAndSetIfChanged(ref _useDeezer, value); SaveSources(); }
    }

    /// <summary>Points the panel at a playlist; loads (from cache when possible) if <paramref name="load"/>.</summary>
    public void SetPlaylist(Guid? playlistId, string? title, bool load)
    {
        var changed = PlaylistId != playlistId;
        PlaylistId = playlistId;
        PlaylistTitle = title ?? string.Empty;
        this.RaisePropertyChanged(nameof(HasPlaylist));

        if (!HasPlaylist)
        {
            _loadCts?.Cancel();
            ShowEntry(null);
            Status = "Select a playlist in the Library to get suggestions.";
            return;
        }
        if (changed) ShowEntry(_cache.TryGetValue(playlistId!.Value, out var cached) ? cached : null);
        if (load) _ = LoadAsync(forceRefresh: false);
    }

    /// <summary>Called when the tab becomes visible.</summary>
    public void OnActivated()
    {
        IsActive = true;
        if (!HasPlaylist || IsLoading) return;
        var id = PlaylistId!.Value;
        // Re-check the playlist for drift on activation, but not on every tab flick.
        if (_cache.ContainsKey(id) && _lastDriftCheckUtc.TryGetValue(id, out var last) && DateTime.UtcNow - last < DriftCheckInterval)
            return;
        _ = LoadAsync(forceRefresh: false);
    }

    /// <summary>
    /// Saved results first (memory, then disk) — shown instantly, minus anything downloaded or
    /// queued since — then re-evaluated in the background only when the playlist changed enough
    /// or the results got old (DiscoveryCache.Evaluate). No saved results: a normal fetch.
    /// </summary>
    private async Task LoadAsync(bool forceRefresh)
    {
        if (!HasPlaylist) return;
        var playlistId = PlaylistId!.Value;

        if (!forceRefresh)
        {
            var entry = _cache.GetValueOrDefault(playlistId) ?? await Task.Run(() => _diskCache.Load(playlistId));
            if (PlaylistId != playlistId) return;
            if (entry != null)
            {
                _cache[playlistId] = entry;
                ShowEntry(entry);
                try
                {
                    var (fingerprint, stillNew) = await Task.Run(() => _discovery.CheckCachedAsync(playlistId, entry.Suggestions));
                    if (PlaylistId != playlistId) return;
                    _lastDriftCheckUtc[playlistId] = DateTime.UtcNow;
                    if (stillNew.Count != entry.Suggestions.Count)
                    {
                        entry.Suggestions = stillNew;
                        SaveEntry(entry);
                        ShowEntry(entry);
                    }

                    var drift = DiscoveryCache.Evaluate(entry.Fingerprint, fingerprint, entry.CreatedUtc, DateTime.UtcNow);
                    if (!drift.Refresh) return;
                    _logger.LogInformation("[Discover] Re-evaluating {Id}: {Reason}", playlistId, drift.Reason);
                    await FetchAsync(playlistId, keepVisible: true, reason: drift.Reason, newSeed: false);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[Discover] Checking saved suggestions failed for {Id}", playlistId);
                }
                return;
            }
        }

        await FetchAsync(playlistId, keepVisible: false, reason: null, newSeed: forceRefresh);
    }

    private async Task FetchAsync(Guid playlistId, bool keepVisible, string? reason, bool newSeed)
    {
        if (!UseBeatport && !UseDeezer)
        {
            if (!keepVisible) ShowEntry(null);
            Status = "Turn on Beatport or Deezer to get suggestions.";
            return;
        }

        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        IsLoading = true;
        if (!keepVisible)
        {
            Suggestions.Clear();
            this.RaisePropertyChanged(nameof(HasSuggestions));
        }
        string prefix = reason != null ? $"Updating — {reason}: " : string.Empty;
        var progress = new Progress<string>(msg => { if (!cts.IsCancellationRequested) Status = prefix + msg; });

        try
        {
            if (newSeed) _refreshSeed++;
            var options = new PlaylistDiscoveryOptions(UseBeatport, UseDeezer, _refreshSeed == 0 ? 0 : playlistId.GetHashCode() ^ _refreshSeed);
            var result = await Task.Run(() => _discovery.SuggestAsync(playlistId, options, progress, cts.Token), cts.Token);
            if (cts.IsCancellationRequested || PlaylistId != playlistId) return;

            var entry = new DiscoveryCacheEntry
            {
                PlaylistId = playlistId,
                CreatedUtc = DateTime.UtcNow,
                Fingerprint = result.Fingerprint.ToList(),
                Summary = result.Summary,
                Suggestions = result.Suggestions.ToList(),
                // Hidden suggestions stay hidden across refreshes.
                Dismissed = _cache.TryGetValue(playlistId, out var previous) ? previous.Dismissed : new List<string>(),
            };
            _cache[playlistId] = entry;
            _lastDriftCheckUtc[playlistId] = DateTime.UtcNow;
            SaveEntry(entry);
            ShowEntry(entry);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Discover] Loading suggestions failed for {Id}", playlistId);
            Status = "Couldn't load suggestions: " + ex.Message;
        }
        finally
        {
            if (ReferenceEquals(_loadCts, cts)) IsLoading = false;
        }
    }

    private void SaveEntry(DiscoveryCacheEntry entry) => _ = Task.Run(() => _diskCache.Save(entry));

    private void ShowEntry(DiscoveryCacheEntry? entry)
    {
        Suggestions.Clear();
        var dismissed = entry == null ? null : new HashSet<string>(entry.Dismissed, StringComparer.Ordinal);
        var shown = entry?.Suggestions.Where(c => !dismissed!.Contains(DiscoveryCache.SuggestionKey(c))).ToList()
                    ?? new List<DiscoveryCandidate>();
        foreach (var c in shown) Suggestions.Add(new DiscoverySuggestionViewModel(c, _artworkCache));
        Summary = entry?.Summary ?? string.Empty;

        if (entry == null)
            Status = HasPlaylist ? "Press ↻ to look for suggestions." : Status;
        else if (shown.Count == 0)
            Status = "No new tracks right now — press ↻ to look again.";
        else
        {
            int agreed = shown.Count(c => c.IsCrossSource);
            Status = $"{shown.Count} suggestions" + (agreed > 0 ? $" · {agreed} both sources agree on" : "") + $" · {Age(entry.CreatedUtc)}";
        }
        this.RaisePropertyChanged(nameof(HasSuggestions));
    }

    private static string Age(DateTime createdUtc)
    {
        var age = DateTime.UtcNow - createdUtc;
        return age.TotalMinutes < 2 ? "just now"
             : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min ago"
             : age.TotalDays < 1 ? $"{(int)age.TotalHours} h ago"
             : $"{(int)age.TotalDays} d ago";
    }

    private void Dismiss(DiscoverySuggestionViewModel? s)
    {
        if (s == null) return;
        Suggestions.Remove(s);
        this.RaisePropertyChanged(nameof(HasSuggestions));
        if (PlaylistId is { } id && _cache.TryGetValue(id, out var entry))
        {
            var key = DiscoveryCache.SuggestionKey(s.Candidate);
            if (!entry.Dismissed.Contains(key)) entry.Dismissed.Add(key);
            SaveEntry(entry);
        }
    }

    private void SaveSources()
    {
        var cfg = _config.GetCurrent();
        cfg.DiscoverUseBeatport = UseBeatport;
        cfg.DiscoverUseDeezer = UseDeezer;
        _config.Save(cfg);
    }

    // ── Actions ─────────────────────────────────────────────────────────────

    private async Task QueueAsync(DiscoverySuggestionViewModel? s)
    {
        if (s == null || !HasPlaylist || s.IsQueued) return;
        var ok = await QueueCoreAsync(new[] { s });
        if (ok > 0)
            _eventBus.Publish(new NotificationEvent("Queued for download",
                $"{s.Artist} – {s.Title} was added to \"{PlaylistTitle}\" and is being searched on Soulseek.", NotificationType.Success));
    }

    /// <summary>Queues the top 10 suggestions not yet queued.</summary>
    private async Task QueueTopAsync()
    {
        var batch = Suggestions.Where(s => !s.IsQueued).Take(10).ToList();
        if (batch.Count == 0 || !HasPlaylist) return;
        var ok = await QueueCoreAsync(batch);
        _eventBus.Publish(new NotificationEvent("Queued for download",
            $"{ok} suggestion(s) added to \"{PlaylistTitle}\" and queued for Soulseek search.", NotificationType.Success));
    }

    private async Task<int> QueueCoreAsync(IReadOnlyList<DiscoverySuggestionViewModel> items)
    {
        var playlistId = PlaylistId!.Value;
        var title = PlaylistTitle;
        try
        {
            var tracks = items.Select(s => BuildTrack(s.Candidate, playlistId, title)).ToList();
            await _library.AddTracksToProjectAsync(tracks, playlistId);

            // AddTracksToProjectAsync saves copies with new ids; queue the saved rows.
            var hashes = tracks.Select(t => t.TrackUniqueHash).ToHashSet(StringComparer.Ordinal);
            var saved = (await _library.LoadPlaylistTracksAsync(playlistId))
                .Where(t => hashes.Contains(t.TrackUniqueHash) && t.Status != TrackStatus.Downloaded)
                .ToList();
            foreach (var t in saved)
            {
                t.Priority = 0; // explicit user action
                t.SourcePlaylistId = playlistId;
                t.SourcePlaylistName = title;
            }
            if (saved.Count > 0) _downloads.QueueTracks(saved);

            foreach (var s in items) s.IsQueued = true;
            return items.Count;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[Discover] Queueing suggestions failed");
            _eventBus.Publish(new NotificationEvent("Couldn't queue", ex.Message, NotificationType.Error));
            return 0;
        }
    }

    /// <summary>The playlist row a suggestion becomes: a missing track with the catalog's metadata. Public for tests.</summary>
    public static PlaylistTrack BuildTrack(DiscoveryCandidate c, Guid playlistId, string playlistTitle) => new()
    {
        Id = Guid.NewGuid(),
        PlaylistId = playlistId,
        Artist = c.Artist,
        Title = c.SearchTitle,
        Album = c.Album ?? string.Empty,
        TrackUniqueHash = TrackHashUtil.Compute(c.Artist, c.SearchTitle),
        Status = TrackStatus.Missing,
        AlbumArtUrl = c.ImageUrl,
        BPM = c.Bpm,
        MusicalKey = c.CamelotKey,
        Label = c.Label,
        Genres = c.Genre,
        ReleaseDate = c.ReleaseDate,
        // Used by the search matcher to pick the right version (extended vs radio edit).
        CanonicalDuration = c.DurationSeconds is { } d ? (int)Math.Round(d * 1000) : null,
        SourcePlaylistId = playlistId,
        SourcePlaylistName = playlistTitle,
        Priority = 0,
        AddedAt = DateTime.UtcNow,
    };

    private async Task SearchAsync(DiscoverySuggestionViewModel? s)
    {
        if (s == null) return;
        if (_services.GetService(typeof(SearchViewModel)) is not SearchViewModel search) return;
        search.SearchQuery = $"{s.Candidate.Artist} {s.Candidate.SearchTitle}".Trim();
        _navigation.NavigateTo("Search");
        await Task.Delay(50); // let the page attach
        if (search.UnifiedSearchCommand.CanExecute(null))
            search.UnifiedSearchCommand.Execute(null);
    }

    private void OpenLink(DiscoverySuggestionViewModel? s)
    {
        var url = s?.Candidate.BeatportUrl ?? s?.Candidate.DeezerUrl;
        if (string.IsNullOrEmpty(url)) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Discover] Could not open {Url}", url);
        }
    }

    // ── Preview (30 s / 2 min store clips) ──────────────────────────────────

    private DiscoverySuggestionViewModel? _previewing;

    private async Task TogglePreviewAsync(DiscoverySuggestionViewModel? s)
    {
        if (s?.Candidate.PreviewUrl is not { } url) return;
        if (_previewing == s && _preview.IsPreviewPlaying)
        {
            _preview.StopPreview();
            return;
        }

        if (_previewing != null) _previewing.IsPreviewing = false;
        _previewing = s;
        s.IsPreviewLoading = true;
        try
        {
            // The preview player reads files, so the clip is cached to a temp file first.
            var bytes = await PreviewHttp.GetByteArrayAsync(url);
            if (_previewing != s) return;
            var path = Path.Combine(Path.GetTempPath(), $"orbit-discover-{Guid.NewGuid():N}.mp3");
            await File.WriteAllBytesAsync(path, bytes);
            DeletePreviewTemp();
            _previewTempFile = path;
            _preview.RequestPreview(path, s.Candidate.Bpm, startSeconds: 0.01); // >0 skips the hover debounce
            s.IsPreviewing = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Discover] Preview failed for {Url}", url);
            _eventBus.Publish(new NotificationEvent("Preview unavailable", $"{s.Artist} – {s.Title}", NotificationType.Warning));
        }
        finally
        {
            s.IsPreviewLoading = false;
        }
    }

    private void OnPreviewStopped(object? sender, EventArgs e)
    {
        // Switching clips raises this for the old clip too, so check shortly after whether our
        // clip is still the one playing.
        Observable.Timer(TimeSpan.FromMilliseconds(400)).ObserveOn(RxApp.MainThreadScheduler).Subscribe(_ =>
        {
            var ours = _preview.IsPreviewPlaying
                       && string.Equals(_preview.CurrentPreviewPath, _previewTempFile, StringComparison.OrdinalIgnoreCase);
            if (_previewing != null && !ours && !_previewing.IsPreviewLoading)
                _previewing.IsPreviewing = false;
        });
    }

    private void DeletePreviewTemp()
    {
        if (_previewTempFile == null) return;
        try { File.Delete(_previewTempFile); } catch { /* still open by the player; temp dir cleans up */ }
        _previewTempFile = null;
    }

    public void Dispose()
    {
        _contextSubscription.Dispose();
        _preview.PreviewStopped -= OnPreviewStopped;
        _loadCts?.Cancel();
        DeletePreviewTemp();
    }
}

/// <summary>One row in the Discover list.</summary>
public sealed class DiscoverySuggestionViewModel : ReactiveObject
{
    public DiscoverySuggestionViewModel(DiscoveryCandidate candidate, ArtworkCacheService? artworkCache = null)
    {
        Candidate = candidate;
        Artwork = artworkCache != null && !string.IsNullOrEmpty(candidate.ImageUrl) ? new ArtworkProxy(artworkCache, candidate.ImageUrl) : null;
    }

    public ArtworkProxy? Artwork { get; }

    public DiscoveryCandidate Candidate { get; }
    public string Artist => Candidate.Artist;
    public string Title => Candidate.SearchTitle;
    public string? ImageUrl => Candidate.ImageUrl;
    public string ReasonText => string.Join(" · ", Candidate.Reasons);
    public string? Label => Candidate.Label;
    public string SourceText =>
        string.Join(" + ", new[]
        {
            Candidate.Sources.HasFlag(DiscoverySource.BeatportArtist) || Candidate.Sources.HasFlag(DiscoverySource.BeatportChart) ? "Beatport" : null,
            Candidate.Sources.HasFlag(DiscoverySource.DeezerArtist) || Candidate.Sources.HasFlag(DiscoverySource.DeezerRelated) ? "Deezer" : null,
        }.Where(x => x != null));
    public string? Price => Candidate.Price;
    public bool HasPreview => !string.IsNullOrEmpty(Candidate.PreviewUrl);
    public bool HasLink => !string.IsNullOrEmpty(Candidate.BeatportUrl ?? Candidate.DeezerUrl);
    public string LinkTip => Candidate.BeatportUrl != null ? "Open on Beatport (buy)" : "Open on Deezer";
    public int FitPercent => (int)Math.Round(Math.Min(1.0, Candidate.Score) * 100);
    public bool IsCrossSource => Candidate.IsCrossSource;
    public string MetaText => string.Join(" · ", new[] { Candidate.Label, Candidate.Price }.Where(x => !string.IsNullOrEmpty(x)));

    private bool _isQueued;
    public bool IsQueued { get => _isQueued; set => this.RaiseAndSetIfChanged(ref _isQueued, value); }

    private bool _isPreviewing;
    public bool IsPreviewing { get => _isPreviewing; set { this.RaiseAndSetIfChanged(ref _isPreviewing, value); this.RaisePropertyChanged(nameof(PreviewGlyph)); } }

    private bool _isPreviewLoading;
    public bool IsPreviewLoading { get => _isPreviewLoading; set { this.RaiseAndSetIfChanged(ref _isPreviewLoading, value); this.RaisePropertyChanged(nameof(PreviewGlyph)); } }

    public string PreviewGlyph => IsPreviewLoading ? "…" : IsPreviewing ? "■" : "▶";
}
