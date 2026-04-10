using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One row in the Similar Tracks panel — a candidate track with its similarity score.
/// </summary>
public sealed class SimilarTrackRowViewModel : ReactiveObject
{
    public string TrackHash { get; }

    private string _title          = string.Empty;
    private string _artist         = string.Empty;
    private string _key            = string.Empty;
    private float  _bpm;
    private double _score;
    private float  _overallScore;
    private float  _harmonyScore;
    private float  _beatScore;
    private float  _dropSonicScore;
    private float  _doubleDropScore;
    private string _harmonyLabel   = string.Empty;
    private string _beatLabel      = string.Empty;
    private string _dropLabel      = string.Empty;
    private bool   _isPotentialDoubleDrop;

    public string Title  { get => _title;  private set => this.RaiseAndSetIfChanged(ref _title,  value); }
    public string Artist { get => _artist; private set => this.RaiseAndSetIfChanged(ref _artist, value); }
    public string Key    { get => _key;    private set => this.RaiseAndSetIfChanged(ref _key,    value); }
    public float  Bpm    { get => _bpm;    private set => this.RaiseAndSetIfChanged(ref _bpm,    value); }

    /// <summary>Raw embedding cosine similarity to the seed track (0–1).</summary>
    public double Score { get => _score; set => this.RaiseAndSetIfChanged(ref _score, value); }

    /// <summary>Weighted overall compatibility (0–1) from <see cref="TrackMatchScore"/>.</summary>
    public float OverallScore    { get => _overallScore;    set => this.RaiseAndSetIfChanged(ref _overallScore,    value); }
    public float HarmonyScore    { get => _harmonyScore;    set => this.RaiseAndSetIfChanged(ref _harmonyScore,    value); }
    public float BeatScore       { get => _beatScore;       set => this.RaiseAndSetIfChanged(ref _beatScore,       value); }
    public float DropSonicScore  { get => _dropSonicScore;  set => this.RaiseAndSetIfChanged(ref _dropSonicScore,  value); }
    public float DoubleDropScore { get => _doubleDropScore; set => this.RaiseAndSetIfChanged(ref _doubleDropScore, value); }
    public string HarmonyLabel   { get => _harmonyLabel;    set => this.RaiseAndSetIfChanged(ref _harmonyLabel,    value); }
    public string BeatLabel      { get => _beatLabel;       set => this.RaiseAndSetIfChanged(ref _beatLabel,       value); }
    public string DropLabel      { get => _dropLabel;       set => this.RaiseAndSetIfChanged(ref _dropLabel,       value); }

    /// <summary>True when <see cref="DoubleDropScore"/> ≥ 0.75.</summary>
    public bool IsPotentialDoubleDrop
    {
        get => _isPotentialDoubleDrop;
        set => this.RaiseAndSetIfChanged(ref _isPotentialDoubleDrop, value);
    }

    public SimilarTrackRowViewModel(
        SimilarTrack result,
        DatabaseService db,
        ILogger logger,
        TrackMatchScore? matchScore = null)
    {
        TrackHash = result.TrackHash;
        _score    = result.Score;

        // Apply pre-computed match scores immediately if supplied.
        if (matchScore is not null) ApplyMatchScore(matchScore);

        // Async metadata lookup — fire-and-forget; notifies UI when features arrive.
        Task.Run(async () =>
        {
            try
            {
                var features = await db.GetAudioFeaturesByHashAsync(TrackHash);
                if (features is null) return;

                Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                {
                    Key = features.CamelotKey ?? features.Key ?? string.Empty;
                    Bpm = features.Bpm;
                });
            }
            catch (Exception ex)
            {
                logger.LogDebug(ex, "[SimilarTracksVM] Metadata fetch failed for {Hash}", TrackHash);
            }
        });
    }

    internal void ApplyMatchScore(TrackMatchScore s)
    {
        OverallScore          = s.OverallScore;
        HarmonyScore          = s.HarmonyScore;
        BeatScore             = s.BeatScore;
        DropSonicScore        = s.DropSonicScore;
        DoubleDropScore       = s.DoubleDropScore;
        HarmonyLabel          = s.HarmonyLabel;
        BeatLabel             = s.BeatLabel;
        DropLabel             = s.DropLabel;
        IsPotentialDoubleDrop = s.IsPotentialDoubleDrop;
    }
}

/// <summary>
/// Task 2.4 — ViewModel for the "Similar Tracks" inspector panel.
///
/// Usage:
///   1. Set <see cref="SeedTrackHash"/> to trigger an automatic search.
///   2. Bind <see cref="Results"/> to a ListView/ItemsControl.
///   3. <see cref="IsBusy"/> drives a loading spinner.
///
/// Debouncing: when <see cref="SeedTrackHash"/> changes rapidly (e.g. arrow-key
/// navigation), the actual query waits for <see cref="DebounceMs"/> of quiet time.
/// </summary>
public sealed class SimilarTracksViewModel : ReactiveObject, IDisposable
{
    public const int DefaultTopN    = 12;
    public const int DebounceMs     = 300;

    private readonly SimilarityIndex     _index;
    private readonly DatabaseService     _db;
    private readonly SectionVectorService? _sectionVectors;
    private readonly ILogger             _logger;
    private readonly CompositeDisposable _disposables = new();

    // ── Seed ──────────────────────────────────────────────────────────────

    private string? _seedTrackHash;

    /// <summary>
    /// Hash of the currently selected / inspected track.
    /// Setting this property triggers a debounced similarity query.
    /// </summary>
    public string? SeedTrackHash
    {
        get => _seedTrackHash;
        set => this.RaiseAndSetIfChanged(ref _seedTrackHash, value);
    }

    // ── Results ───────────────────────────────────────────────────────────

    public ObservableCollection<SimilarTrackRowViewModel> Results { get; } = new();

    // ── State ─────────────────────────────────────────────────────────────

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        private set => this.RaiseAndSetIfChanged(ref _isBusy, value);
    }

    private string? _statusMessage;
    public string? StatusMessage
    {
        get => _statusMessage;
        private set => this.RaiseAndSetIfChanged(ref _statusMessage, value);
    }

    // ── Count ─────────────────────────────────────────────────────────────

    private int _topN = DefaultTopN;

    /// <summary>Maximum number of results to display.</summary>
    public int TopN
    {
        get => _topN;
        set => this.RaiseAndSetIfChanged(ref _topN, value);
    }

    // ── Commands ──────────────────────────────────────────────────────────

    /// <summary>Forces a refresh with the current seed hash, bypassing the debounce timer.</summary>
    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }

    // ── Constructor ───────────────────────────────────────────────────────

    public SimilarTracksViewModel(
        SimilarityIndex index,
        DatabaseService db,
        ILogger<SimilarTracksViewModel> logger,
        SectionVectorService? sectionVectors = null)
    {
        _index          = index;
        _db             = db;
        _logger         = logger;
        _sectionVectors = sectionVectors;

        RefreshCommand = ReactiveCommand.CreateFromTask(
            () => QueryAsync(_seedTrackHash, CancellationToken.None));

        // Debounced subscription on SeedTrackHash changes
        this.WhenAnyValue(x => x.SeedTrackHash)
            .Throttle(TimeSpan.FromMilliseconds(DebounceMs),
                      RxApp.TaskpoolScheduler)
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .SelectMany(h => Observable.FromAsync(ct => QueryAsync(h, ct)))
            .Subscribe()
            .DisposeWith(_disposables);
    }

    // ── Query ─────────────────────────────────────────────────────────────

    private async Task QueryAsync(string? hash, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(hash)) return;

        await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
        {
            IsBusy = true;
            StatusMessage = null;
            Results.Clear();
        });

        try
        {
            var hits = await _index.GetSimilarTracksAsync(hash, TopN, ct);

            // Compute multi-dimensional match scores for every result.
            var matchScores = await ComputeMatchScoresAsync(hash, hits, ct);

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Results.Clear();
                foreach (var hit in hits)
                {
                    matchScores.TryGetValue(hit.TrackHash, out var ms);
                    Results.Add(new SimilarTrackRowViewModel(hit, _db, _logger, ms));
                }

                StatusMessage = hits.Count == 0
                    ? "No similar tracks found — analysis embeddings may not be available."
                    : null;
            });
        }
        catch (OperationCanceledException)
        {
            // Debounce superseded; ignore
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[SimilarTracksVM] Query failed for seed {Hash}", hash);
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
                StatusMessage = "Error querying similar tracks.");
        }
        finally
        {
            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() => IsBusy = false);
        }
    }

    // ── Match score computation ────────────────────────────────────────────

    private async Task<Dictionary<string, TrackMatchScore>> ComputeMatchScoresAsync(
        string seedHash,
        IReadOnlyList<SimilarTrack> hits,
        CancellationToken ct)
    {
        var result = new Dictionary<string, TrackMatchScore>(hits.Count);

        try
        {
            // Batch-load audio features for seed + all candidates in one query.
            var allHashes = new List<string>(hits.Count + 1) { seedHash };
            allHashes.AddRange(hits.Select(h => h.TrackHash));

            Dictionary<string, Data.Entities.AudioFeaturesEntity> featureMap;
            using (var db = new AppDbContext())
            {
                var rows = await db.AudioFeatures
                    .Where(f => allHashes.Contains(f.TrackUniqueHash))
                    .ToListAsync(ct);
                featureMap = rows.ToDictionary(r => r.TrackUniqueHash);
            }

            featureMap.TryGetValue(seedHash, out var seedFeatures);

            // Pre-warm section vector cache for all involved tracks.
            if (_sectionVectors != null)
                await _sectionVectors.PreloadAsync(allHashes, ct);

            Models.SectionFeatureVector? seedDrop = null;
            if (_sectionVectors != null && seedFeatures != null)
                seedDrop = await _sectionVectors.GetSectionAsync(
                    seedHash, Data.Entities.PhraseType.Drop, ct);

            foreach (var hit in hits)
            {
                if (ct.IsCancellationRequested) break;

                featureMap.TryGetValue(hit.TrackHash, out var hitFeatures);

                Models.SectionFeatureVector? hitDrop = null;
                if (_sectionVectors != null && hitFeatures != null)
                    hitDrop = await _sectionVectors.GetSectionAsync(
                        hit.TrackHash, Data.Entities.PhraseType.Drop, ct);

                var score = TrackMatchScorer.Compute(
                    seedFeatures,
                    hitFeatures,
                    hit.Score,   // already cosine similarity (0-1) from SimilarityIndex
                    seedDrop,
                    hitDrop);

                result[hit.TrackHash] = score;
            }
        }
        catch (OperationCanceledException) { /* debounce cancelled */ }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[SimilarTracksVM] Match score computation failed");
        }

        return result;
    }

    public void Dispose() => _disposables.Dispose();
}
