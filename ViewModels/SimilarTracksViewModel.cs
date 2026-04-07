using System;
using System.Collections.ObjectModel;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Services;
using SLSKDONET.Services.Similarity;

namespace SLSKDONET.ViewModels;

/// <summary>
/// One row in the Similar Tracks panel — a candidate track with its similarity score.
/// </summary>
public sealed class SimilarTrackRowViewModel : ReactiveObject
{
    public string TrackHash { get; }

    private string _title  = string.Empty;
    private string _artist = string.Empty;
    private string _key    = string.Empty;
    private float  _bpm;
    private double _score;

    public string Title  { get => _title;  private set => this.RaiseAndSetIfChanged(ref _title,  value); }
    public string Artist { get => _artist; private set => this.RaiseAndSetIfChanged(ref _artist, value); }
    public string Key    { get => _key;    private set => this.RaiseAndSetIfChanged(ref _key,    value); }
    public float  Bpm    { get => _bpm;    private set => this.RaiseAndSetIfChanged(ref _bpm,    value); }

    /// <summary>Cosine similarity to the seed track (0–1). Higher is more similar.</summary>
    public double Score
    {
        get => _score;
        set => this.RaiseAndSetIfChanged(ref _score, value);
    }

    public SimilarTrackRowViewModel(SimilarTrack result, DatabaseService db, ILogger logger)
    {
        TrackHash = result.TrackHash;
        _score    = result.Score;

        // Async metadata lookup — fire-and-forget; notifies UI when features arrive
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

    private readonly SimilarityIndex   _index;
    private readonly DatabaseService   _db;
    private readonly ILogger           _logger;
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
        ILogger<SimilarTracksViewModel> logger)
    {
        _index  = index;
        _db     = db;
        _logger = logger;

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

            await Avalonia.Threading.Dispatcher.UIThread.InvokeAsync(() =>
            {
                Results.Clear();
                foreach (var hit in hits)
                    Results.Add(new SimilarTrackRowViewModel(hit, _db, _logger));

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

    public void Dispose() => _disposables.Dispose();
}
