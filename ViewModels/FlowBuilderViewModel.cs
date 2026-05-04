using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Threading.Tasks;
using Avalonia.Threading;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Playlist;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Powers the Flow Builder mode — lets users assemble a DJ set as an ordered sequence
/// of tracks with AI-computed transition scores between adjacent pairs.
///
/// Workflow:
///   1. User selects a playlist from <see cref="Playlists"/>.
///   2. <see cref="LoadSelectedPlaylistCommand"/> loads tracks into <see cref="Tracks"/>.
///   3. <see cref="SuggestNextCommand"/> appends the best next track via
///      <see cref="PlaylistOptimizer"/> greedy nearest-neighbour.
///   4. User reorders cards with MoveLeft/MoveRight or removes cards with Remove.
///   5. Transition bridges are recalculated after every structural change.
/// </summary>
public sealed class FlowBuilderViewModel : ReactiveObject, IDisposable
{
    private readonly CompositeDisposable _disposables = new();
    private readonly ILibraryService     _library;
    private readonly PlaylistOptimizer   _optimizer;
    private readonly SLSKDONET.Services.Similarity.SectionVectorService? _sectionVectors;

    // ── Playlist selector ─────────────────────────────────────────────────────

    public ObservableCollection<PlaylistJob> Playlists { get; } = new();

    private PlaylistJob? _selectedPlaylist;
    public PlaylistJob? SelectedPlaylist
    {
        get => _selectedPlaylist;
        set => this.RaiseAndSetIfChanged(ref _selectedPlaylist, value);
    }

    // ── Set timeline ──────────────────────────────────────────────────────────

    public ObservableCollection<FlowTrackCardViewModel> Tracks { get; } = new();

    // ── UI state ──────────────────────────────────────────────────────────────

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            this.RaiseAndSetIfChanged(ref _isLoading, value);
            this.RaisePropertyChanged(nameof(IsNotLoading));
        }
    }

    public bool IsNotLoading => !_isLoading;

    public bool HasTracks    => Tracks.Count > 0;
    public bool HasNoTracks  => Tracks.Count == 0;

    private string _statusText = "Select a playlist and click Load to begin.";
    public string StatusText
    {
        get => _statusText;
        private set => this.RaiseAndSetIfChanged(ref _statusText, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────────

    public ReactiveCommand<Unit, Unit> LoadSelectedPlaylistCommand { get; }
    public ReactiveCommand<Unit, Unit> SuggestNextCommand          { get; }
    public ReactiveCommand<Unit, Unit> LoadPlaylistsCommand        { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand                { get; }

    // ── Constructor ───────────────────────────────────────────────────────────

    public FlowBuilderViewModel(
        ILibraryService library,
        PlaylistOptimizer optimizer,
        SLSKDONET.Services.Similarity.SectionVectorService? sectionVectors = null)
    {
        _library   = library;
        _optimizer = optimizer;
        _sectionVectors = sectionVectors;

        LoadPlaylistsCommand = ReactiveCommand.CreateFromTask(LoadPlaylistsAsync);
        LoadSelectedPlaylistCommand = ReactiveCommand.CreateFromTask(
            LoadSelectedPlaylistAsync,
            this.WhenAnyValue(x => x.SelectedPlaylist, x => x.IsLoading,
                (pl, loading) => pl != null && !loading));

        SuggestNextCommand = ReactiveCommand.CreateFromTask(
            SuggestNextAsync,
            this.WhenAnyValue(x => x.IsLoading, loading => !loading));

        ClearCommand = ReactiveCommand.Create(
            () =>
            {
                Tracks.Clear();
                RaiseTrackCollectionChanged();
            },
            this.WhenAnyValue(x => x.HasTracks));

        // Notify HasTracks/HasNoTracks when the collection changes
        Tracks.CollectionChanged += (_, _) => RaiseTrackCollectionChanged();

        _ = LoadPlaylistsAsync();
    }

    // ── Command implementations ───────────────────────────────────────────────

    private async Task LoadPlaylistsAsync()
    {
        var jobs = await _library.LoadAllPlaylistJobsAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Playlists.Clear();
            foreach (var j in jobs) Playlists.Add(j);
            if (Playlists.Count > 0 && SelectedPlaylist == null)
                SelectedPlaylist = Playlists[0];
        });
    }

    private async Task LoadSelectedPlaylistAsync()
    {
        if (SelectedPlaylist == null) return;

        IsLoading  = true;
        StatusText = $"Loading \"{SelectedPlaylist.SourceTitle}\"\u2026";
        try
        {
            var tracks = await _library.GetPagedPlaylistTracksAsync(
                SelectedPlaylist.Id, skip: 0, take: 500);

            // Optimise the order: AI-powered greedy sort by Camelot + BPM + energy
            var hashes = tracks.Select(t => t.TrackUniqueHash ?? "").Where(h => h.Length > 0).ToList();
            PlaylistOptimizationResult? result = null;
            try
            {
                result = await _optimizer.OptimizeAsync(hashes);
            }
            catch
            {
                // Fall back to original order if optimizer fails (e.g. no audio features yet)
            }

            // Re-order tracks to match optimized hash order (unanalysed tracks appended at end)
            var trackByHash = tracks.ToDictionary(t => t.TrackUniqueHash ?? "", t => t);
            var orderedTracks = result != null
                ? result.OrderedHashes
                    .Where(h => trackByHash.ContainsKey(h))
                    .Select(h => trackByHash[h])
                    .Concat(tracks.Where(t => !result.OrderedHashes.Contains(t.TrackUniqueHash ?? "")))
                    .ToList()
                : tracks;

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Clear();
                foreach (var t in orderedTracks)
                    Tracks.Add(BuildCard(t));
                StatusText = result?.UnanalyzedTrackCount > 0
                    ? $"Loaded {Tracks.Count} tracks ({result.UnanalyzedTrackCount} unanalysed, appended at end)"
                    : $"Loaded {Tracks.Count} tracks — transitions optimised";
            });

            await RefreshBridgesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    private async Task SuggestNextAsync()
    {
        if (SelectedPlaylist == null) return;

        IsLoading  = true;
        StatusText = "Finding the next best track…";
        try
        {
            // Load all available tracks in the playlist
            var all = await _library.GetPagedPlaylistTracksAsync(
                SelectedPlaylist.Id, skip: 0, take: 1000);

            // Exclude already-queued hashes
            var queued = Tracks.Select(t => t.TrackHash).ToHashSet(StringComparer.Ordinal);
            var candidates = all.Where(t => !queued.Contains(t.TrackUniqueHash ?? "")).ToList();

            if (candidates.Count == 0)
            {
                StatusText = "No more tracks to suggest from this playlist.";
                return;
            }

            // Use optimizer to pick the single best next track relative to current set tail
            string? startHash = Tracks.LastOrDefault()?.TrackHash;
            var opts = new PlaylistOptimizerOptions { StartTrackHash = startHash };
            var result = await _optimizer.OptimizeAsync(
                candidates.Select(c => c.TrackUniqueHash ?? ""), opts);

            string? nextHash = result.OrderedHashes.FirstOrDefault();
            var nextTrack = nextHash != null
                ? candidates.FirstOrDefault(t => t.TrackUniqueHash == nextHash)
                : candidates.First();

            if (nextTrack == null)
            {
                StatusText = "Suggestion unavailable.";
                return;
            }

            await Dispatcher.UIThread.InvokeAsync(() =>
            {
                Tracks.Add(BuildCard(nextTrack));
                StatusText = $"Added: {nextTrack.Artist} — {nextTrack.Title}";
            });

            await RefreshBridgesAsync();
        }
        catch (Exception ex)
        {
            StatusText = $"Error: {ex.Message}";
        }
        finally
        {
            IsLoading = false;
        }
    }

    // ── Card factory ──────────────────────────────────────────────────────────

    private FlowTrackCardViewModel BuildCard(PlaylistTrack track)
    {
        var card = new FlowTrackCardViewModel(
            track,
            onMoveLeft:  () => MoveCard(track, -1),
            onMoveRight: () => MoveCard(track, +1),
            onRemove:    () => RemoveCard(track));
        return card;
    }

    private void MoveCard(PlaylistTrack track, int delta)
    {
        var card = Tracks.FirstOrDefault(c => c.TrackHash == (track.TrackUniqueHash ?? ""));
        if (card == null) return;

        int idx     = Tracks.IndexOf(card);
        int newIdx  = Math.Clamp(idx + delta, 0, Tracks.Count - 1);
        if (newIdx == idx) return;

        Tracks.RemoveAt(idx);
        Tracks.Insert(newIdx, card);
        _ = RefreshBridgesAsync();
    }

    private void RemoveCard(PlaylistTrack track)
    {
        var card = Tracks.FirstOrDefault(c => c.TrackHash == (track.TrackUniqueHash ?? ""));
        if (card == null) return;
        Tracks.Remove(card);
        _ = RefreshBridgesAsync();
    }

    // ── Bridge computation ────────────────────────────────────────────────────

    private async Task RefreshBridgesAsync()
    {
        if (_sectionVectors != null)
        {
            var hashes = Tracks.Select(t => t.TrackHash).Where(h => !string.IsNullOrWhiteSpace(h)).ToList();
            await _sectionVectors.PreloadAsync(hashes);
        }

        for (int i = 0; i < Tracks.Count; i++)
        {
            var next = i < Tracks.Count - 1 ? Tracks[i + 1] : null;
            if (next == null)
            {
                Tracks[i].SetBridgeTo(null);
                continue;
            }

            double? sectionBlend = _sectionVectors != null
                ? _sectionVectors.TransitionScoreCached(Tracks[i].TrackHash, next.TrackHash)
                : null;
            double? doubleDropBlend = _sectionVectors != null
                ? _sectionVectors.DropSimilarityCached(Tracks[i].TrackHash, next.TrackHash)
                : null;

            Tracks[i].SetBridgeTo(next, sectionBlend, doubleDropBlend);
        }
    }

    private void RaiseTrackCollectionChanged()
    {
        this.RaisePropertyChanged(nameof(HasTracks));
        this.RaisePropertyChanged(nameof(HasNoTracks));
    }

    public void Dispose() => _disposables.Dispose();
}
