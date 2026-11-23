using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reactive.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Engine.Analysis;
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Models;
using SLSKDONET.Services.Enrichment;
using SLSKDONET.Services.Library.Maintenance;

namespace SLSKDONET.Services.Genre;

/// <summary>Raised when a library genre changed (batch edit, tag fix, Use as genre), so the personal head can retrain.</summary>
public sealed record GenreLabelsChangedEvent;

/// <summary>
/// Owns the personal genre head (GA11 #256): finds the labelled tracks (your genres, clean file tags, trusted
/// providers), retrains in the background when they change, gates it on cross-validation, and persists it to
/// <c>%AppData%/ORBIT/models/personal-genre-head.json</c>. Analysis and the genre refresh read
/// <see cref="ActiveModel"/>. Never throws from its constructor (DI singleton): a missing or unreadable model file
/// just means "no head".
/// </summary>
public sealed class PersonalGenreHeadService : IDisposable
{
    /// <summary>Quiet time after the last genre change before retraining.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(30);
    /// <summary>Delay before the startup check, so it never competes with app start.</summary>
    public static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(60);

    private const double TrustedProviderMinConfidence = 0.85;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AppConfig _config;
    private readonly IEventBus _eventBus;
    private readonly ILogger<PersonalGenreHeadService> _logger;
    private readonly Func<string?> _currentEmbeddingTag;
    private readonly string _modelPath;
    private readonly SemaphoreSlim _trainGate = new(1, 1);
    private readonly CancellationTokenSource _cts = new();
    private IDisposable? _subscription;
    private int _started;
    private PersonalGenreModel? _model;

    public PersonalGenreHeadService(
        IDbContextFactory<AppDbContext> dbFactory,
        AppConfig config,
        IEventBus eventBus,
        ILogger<PersonalGenreHeadService> logger,
        StyleBackfillService? styleBackfill = null,
        string? modelPath = null,
        Func<string?>? currentEmbeddingTag = null)
    {
        _dbFactory = dbFactory;
        _config = config;
        _eventBus = eventBus;
        _logger = logger;
        _currentEmbeddingTag = currentEmbeddingTag ?? (() => StyleBackfillService.EmbeddingTagOf(styleBackfill?.CurrentStyleTag));
        _modelPath = modelPath ?? Path.Combine(OrbitPaths.AppDataDirectory, "models", "personal-genre-head.json");
        _model = LoadModel();
    }

    /// <summary>The last trained model (enabled or not), for the Settings readout.</summary>
    public PersonalGenreModel? Model => _model;

    /// <summary>The model fusion should use: setting on, cross-validation passed. Null otherwise.</summary>
    public PersonalGenreModel? ActiveModel => _config.LearnFromGenreCorrections && _model is { Enabled: true } m ? m : null;

    public bool IsTraining { get; private set; }

    /// <summary>Raised (on a background thread) after every training run or when training starts.</summary>
    public event EventHandler? StateChanged;

    /// <summary>Subscribes to genre changes and schedules the startup check. Idempotent.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) == 1) return;
        _subscription = _eventBus.GetEvent<GenreLabelsChangedEvent>().Select(_ => true)
            .Merge(_eventBus.GetEvent<TrackMetadataUpdatedEvent>().Select(_ => true))
            .Throttle(Debounce)
            .Subscribe(changed => _ = RunInBackgroundAsync(force: false));
        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(StartupDelay, _cts.Token).ConfigureAwait(false);
                await RunInBackgroundAsync(force: false).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (_cts.IsCancellationRequested)
            {
                // intentional: shutdown before the startup check ran.
            }
        });
    }

    private async Task RunInBackgroundAsync(bool force)
    {
        try
        {
            await TrainIfChangedAsync(force, _cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_cts.IsCancellationRequested)
        {
            // intentional: app shutdown cancels training.
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[PersonalGenre] Training failed; the previous model (if any) stays in use");
        }
    }

    /// <summary>
    /// Retrains when the labels (or the embedding model) changed since the stored model, or always with
    /// <paramref name="force"/>. Returns the model now in effect, or null when the feature is off or untrainable.
    /// </summary>
    public async Task<PersonalGenreModel?> TrainIfChangedAsync(bool force = false, CancellationToken ct = default)
    {
        if (!_config.LearnFromGenreCorrections) return null;
        var tag = _currentEmbeddingTag();
        if (tag is null)
        {
            _logger.LogInformation("[PersonalGenre] No EffNet model available; not training");
            return _model;
        }

        await _trainGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var labels = await LoadLabelsAsync(tag, ct).ConfigureAwait(false);
            var fingerprint = PersonalGenreTrainer.Fingerprint(labels.Select(l => (l.Hash, l.Label, l.Weight)), tag);
            if (!force && _model is { } current && current.LabelFingerprint == fingerprint && current.EmbeddingModelTag == tag)
                return _model;

            IsTraining = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var samples = await LoadSamplesAsync(labels, ct).ConfigureAwait(false);
            var model = await Task.Run(() => PersonalGenreTrainer.Train(samples, tag, fingerprint, ct: ct), ct).ConfigureAwait(false);
            Save(model);
            _model = model;
            _logger.LogInformation("[PersonalGenre] Trained on {Count} labels in {Ms} ms: {Enabled} — {Reason}",
                samples.Count, sw.ElapsedMilliseconds, model.Enabled ? "enabled" : "disabled", model.Reason);
            return model;
        }
        finally
        {
            IsTraining = false;
            _trainGate.Release();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>One label per track: hash, bucket, trust weight, artist. No embeddings yet (cheap fingerprint check).</summary>
    internal sealed record GenreLabel(string Hash, GenreBucket Label, float Weight, string? Artist);

    /// <summary>The label rule (as #249): user 1.0, clean file tags 0.7, Beatport/Discogs at ≥ 0.85 and unknown origin 0.5;
    /// never Library Maintenance genres (model-derived, circular) or artist/album-level providers.</summary>
    public static float? LabelWeight(string? source, double confidence) => source?.Trim().ToLowerInvariant() switch
    {
        "user" => 1.0f,
        "tags" => 0.7f,
        "beatport" or "discogs" => confidence >= TrustedProviderMinConfidence ? 0.5f : null,
        null or "" => 0.5f,
        _ => null, // maintenance, spotify, deezer, musicbrainz, …
    };

    internal async Task<List<GenreLabel>> LoadLabelsAsync(string embeddingTag, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var withEmbedding = await db.AudioFeatures.AsNoTracking()
            .Where(f => f.EmbeddingBlob != null && f.EmbeddingModelTag == embeddingTag)
            .Select(f => f.TrackUniqueHash)
            .ToListAsync(ct).ConfigureAwait(false);
        var embedded = withEmbedding.ToHashSet(StringComparer.Ordinal);

        var library = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.Genres != null && e.Genres != "")
            .Select(e => new { e.UniqueHash, e.Genres, e.Artist })
            .ToListAsync(ct).ConfigureAwait(false);
        const string genreField = MetadataMerger.Fields.Genre;
        var provenance = (await db.MetadataProvenance.AsNoTracking()
                .Where(p => p.Field == genreField)
                .Select(p => new { p.TrackUniqueHash, p.Source, p.Confidence })
                .ToListAsync(ct).ConfigureAwait(false))
            .GroupBy(p => p.TrackUniqueHash).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);

        var labels = new List<GenreLabel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in library)
        {
            if (!embedded.Contains(entry.UniqueHash) || !seen.Add(entry.UniqueHash)) continue;
            provenance.TryGetValue(entry.UniqueHash, out var prov);
            if (LabelWeight(prov?.Source, prov?.Confidence ?? 1) is not { } weight) continue;
            var values = GenreTaxonomy.MapFreeTextAll(entry.Genres).Where(v => !v.IsJunk && v.Weight > 0).ToList();
            if (values.Count == 0) continue;
            // "Techno, House": the tag can't settle it, so it can't teach it either.
            if (values.Where(v => v.Bucket != GenreBucket.Other).Select(v => v.Bucket).Distinct().Count() > 1) continue;
            labels.Add(new GenreLabel(entry.UniqueHash, values[0].Bucket, weight, entry.Artist));
        }
        return labels;
    }

    private async Task<List<PersonalGenreSample>> LoadSamplesAsync(List<GenreLabel> labels, CancellationToken ct)
    {
        var samples = new List<PersonalGenreSample>(labels.Count);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        foreach (var chunk in labels.Chunk(500))
        {
            ct.ThrowIfCancellationRequested();
            var hashes = chunk.Select(l => l.Hash).ToList();
            var rows = await db.AudioFeatures.AsNoTracking()
                .Where(f => hashes.Contains(f.TrackUniqueHash))
                .Select(f => new { f.TrackUniqueHash, f.EmbeddingBlob, f.GenreDistributionJson })
                .ToListAsync(ct).ConfigureAwait(false);
            var byHash = rows.GroupBy(r => r.TrackUniqueHash).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
            foreach (var label in chunk)
            {
                if (!byHash.TryGetValue(label.Hash, out var row) || row.EmbeddingBlob is not { Length: >= 4 } blob) continue;
                var embedding = MemoryMarshal.Cast<byte, float>(blob.AsSpan()).ToArray();
                samples.Add(new PersonalGenreSample(label.Hash, label.Artist, embedding, label.Label, label.Weight,
                    BaseScoresFrom(row.GenreDistributionJson)));
            }
        }
        return samples;
    }

    /// <summary>The stored fusion distribution (display label → score) folded back into buckets.</summary>
    public static IReadOnlyDictionary<GenreBucket, double>? BaseScoresFrom(string? distributionJson)
    {
        var distribution = AudioGenreOpinion.TryDeserialize<Dictionary<string, double>>(distributionJson);
        if (distribution is null || distribution.Count == 0) return null;
        var result = new Dictionary<GenreBucket, double>();
        foreach (var (label, score) in distribution)
        {
            if (score <= 0) continue;
            var bucket = string.Equals(label, "Other", StringComparison.OrdinalIgnoreCase)
                ? GenreBucket.Other
                : GenreTaxonomy.MapFreeText(label) is { IsJunk: false } m ? m.Bucket : GenreBucket.Other;
            result[bucket] = result.GetValueOrDefault(bucket) + score;
        }
        return result;
    }

    private PersonalGenreModel? LoadModel()
    {
        try
        {
            if (!File.Exists(_modelPath)) return null;
            var model = PersonalGenreModel.TryParse(File.ReadAllText(_modelPath));
            if (model is null) _logger.LogWarning("[PersonalGenre] Ignoring unreadable model file {Path}; it will be retrained", _modelPath);
            return model;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "[PersonalGenre] Could not read {Path}; it will be retrained", _modelPath);
            return null;
        }
    }

    private void Save(PersonalGenreModel model)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_modelPath)!);
            var tmp = _modelPath + ".tmp";
            File.WriteAllText(tmp, model.ToJson());
            File.Move(tmp, _modelPath, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The model still applies for this session; it just retrains next launch.
            _logger.LogWarning(ex, "[PersonalGenre] Could not save {Path}; using the model for this session only", _modelPath);
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _subscription?.Dispose();
        _cts.Dispose();
    }
}
