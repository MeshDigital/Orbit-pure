using System;
using System.Linq;
using System.Reactive;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.KnowledgeLifecycle;
using SLSKDONET.Services.Timeline;

namespace SLSKDONET.ViewModels.TransitionLab;

/// <summary>
/// Slice K6: the Transition Lab surface — a template editor, a score/rating panel, and the
/// outgoing/incoming track pair. Commands audition the overlap window, save a reusable template, and
/// upsert the knowledge edge (5-tuple UNIQUE) with the DJ's manual rating.
/// </summary>
public sealed class TransitionLabViewModel : ReactiveObject
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITransitionPreviewPlayer _previewPlayer;
    private readonly ILogger<TransitionLabViewModel>? _logger;

    public TransitionTemplateViewModel Template { get; } = new();
    public TransitionScoreViewModel Score { get; } = new();

    private string _sourceTrackHash = string.Empty;
    public string SourceTrackHash { get => _sourceTrackHash; set => this.RaiseAndSetIfChanged(ref _sourceTrackHash, value); }

    private string _targetTrackHash = string.Empty;
    public string TargetTrackHash { get => _targetTrackHash; set => this.RaiseAndSetIfChanged(ref _targetTrackHash, value); }

    private string _sourceFilePath = string.Empty;
    private string _targetFilePath = string.Empty;
    private string _sourceTitle = string.Empty;
    private string _targetTitle = string.Empty;
    private double _projectBpm = 120.0;
    private double _sourceTrigger;
    private double _targetTrigger;

    private TransitionWaveformData? _waveformData;
    public TransitionWaveformData? WaveformData
    {
        get => _waveformData;
        set => this.RaiseAndSetIfChanged(ref _waveformData, value);
    }

    public ReactiveCommand<Unit, Unit> AuditionCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveTemplateCommand { get; }
    public ReactiveCommand<Unit, Unit> SaveKnowledgeCommand { get; }

    public TransitionLabViewModel(
        IDbContextFactory<AppDbContext> dbFactory,
        ITransitionPreviewPlayer previewPlayer,
        ILogger<TransitionLabViewModel>? logger = null)
    {
        _dbFactory = dbFactory;
        _previewPlayer = previewPlayer;
        _logger = logger;

        AuditionCommand = ReactiveCommand.CreateFromTask(AuditionAsync);
        SaveTemplateCommand = ReactiveCommand.CreateFromTask(SaveTemplateAsync);
        SaveKnowledgeCommand = ReactiveCommand.CreateFromTask(SaveKnowledgeAsync);
    }

    public void SetTrackPair(string sourceTrackHash, string targetTrackHash)
    {
        SourceTrackHash = sourceTrackHash;
        TargetTrackHash = targetTrackHash;
    }

    /// <summary>Resolves file paths/titles/BPM/RMS/beat-grid/cues so the canvas and audition have real data.</summary>
    public async Task LoadAsync(string sourceTrackHash, string targetTrackHash)
    {
        SetTrackPair(sourceTrackHash, targetTrackHash);

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        var hashes = new[] { sourceTrackHash, targetTrackHash };

        var entries = await db.LibraryEntries
            .Where(e => hashes.Contains(e.UniqueHash))
            .ToListAsync()
            .ConfigureAwait(false);
        var byHash = entries.ToDictionary(e => e.UniqueHash, StringComparer.Ordinal);

        var features = await db.AudioFeatures
            .Where(f => hashes.Contains(f.TrackUniqueHash))
            .ToListAsync()
            .ConfigureAwait(false);
        var featuresByHash = features.ToDictionary(f => f.TrackUniqueHash, StringComparer.Ordinal);

        var cues = await db.CuePoints
            .Where(c => hashes.Contains(c.TrackUniqueHash))
            .ToListAsync()
            .ConfigureAwait(false);

        if (byHash.TryGetValue(sourceTrackHash, out var source))
        {
            _sourceFilePath = source.FilePath ?? string.Empty;
            _sourceTitle = source.Title ?? string.Empty;
        }
        if (byHash.TryGetValue(targetTrackHash, out var target))
        {
            _targetFilePath = target.FilePath ?? string.Empty;
            _targetTitle = target.Title ?? string.Empty;
        }
        if (featuresByHash.TryGetValue(sourceTrackHash, out var sourceFeatures))
            _projectBpm = sourceFeatures.Bpm > 0 ? sourceFeatures.Bpm : 120.0;

        var outgoing = BuildTrackWaveformData(sourceTrackHash, byHash, featuresByHash, cues, isOutgoing: true);
        var incoming = BuildTrackWaveformData(targetTrackHash, byHash, featuresByHash, cues, isOutgoing: false);
        WaveformData = new TransitionWaveformData(outgoing, incoming, Template.BarsBeforeMix);

        _sourceTrigger = outgoing.MixAnchorSeconds;
        _targetTrigger = incoming.MixAnchorSeconds;
    }

    private static TrackWaveformData BuildTrackWaveformData(
        string hash,
        IReadOnlyDictionary<string, LibraryEntryEntity> entries,
        IReadOnlyDictionary<string, AudioFeaturesEntity> features,
        IReadOnlyList<CuePointEntity> cues,
        bool isOutgoing)
    {
        entries.TryGetValue(hash, out var entry);
        features.TryGetValue(hash, out var audioFeatures);

        var rmsProfile = WaveformRenderer.ComputeRmsProfile(entry?.RmsData ?? Array.Empty<byte>(), targetBins: 512);
        var duration = audioFeatures?.TrackDuration > 0 ? audioFeatures.TrackDuration : 300.0;
        var bpm = audioFeatures?.Bpm > 0 ? (double)audioFeatures.Bpm : 120.0;
        var beatTicks = BeatGridFitter.ParseTicks(audioFeatures?.BeatGridJson);

        var trackCues = cues.Where(c => c.TrackUniqueHash == hash).ToList();
        var mixAnchor = ResolveMixAnchor(trackCues, duration, audioFeatures?.DownbeatOffsetSeconds, isOutgoing);

        return new TrackWaveformData(rmsProfile, duration, bpm, beatTicks, mixAnchor);
    }

    private static double ResolveMixAnchor(
        IReadOnlyList<CuePointEntity> cues,
        double duration,
        double? downbeatOffsetSeconds,
        bool isOutgoing)
    {
        if (isOutgoing)
        {
            return cues.FirstOrDefault(c => c.Type == CuePointType.MixOut)?.TimestampInSeconds
                ?? cues.FirstOrDefault(c => c.Type == CuePointType.OutroStart)?.TimestampInSeconds
                ?? cues.FirstOrDefault(c => c.Type == CuePointType.Outro)?.TimestampInSeconds
                ?? duration; // absolute boundary fallback
        }

        return cues.FirstOrDefault(c => c.Type == CuePointType.MixIn)?.TimestampInSeconds
            ?? cues.FirstOrDefault(c => c.Type == CuePointType.Intro)?.TimestampInSeconds
            ?? cues.FirstOrDefault(c => c.Type == CuePointType.IntroEnd)?.TimestampInSeconds
            ?? (downbeatOffsetSeconds is > 0 ? downbeatOffsetSeconds.Value : 0.0); // first downbeat / start fallback
    }

    private async Task AuditionAsync()
    {
        if (string.IsNullOrEmpty(_sourceFilePath) || string.IsNullOrEmpty(_targetFilePath))
            return;

        try
        {
            await _previewPlayer.StartTransitionPreviewAsync(
                _sourceTitle, _sourceFilePath, _sourceTrigger,
                _targetTitle, _targetFilePath, _targetTrigger,
                Template.ToTransitionModel(), _projectBpm)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger?.LogWarning(ex, "[TransitionLab] audition failed");
        }
    }

    private async Task SaveTemplateAsync()
    {
        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);
        db.TransitionTemplates.Add(Template.ToEntity("Transition Lab"));
        await db.SaveChangesAsync().ConfigureAwait(false);
    }

    private async Task SaveKnowledgeAsync()
    {
        if (string.IsNullOrEmpty(SourceTrackHash) || string.IsNullOrEmpty(TargetTrackHash))
            return;

        await using var db = await _dbFactory.CreateDbContextAsync().ConfigureAwait(false);

        var edge = await KnowledgeLifecycleRepository
            .GetOrCreateKnowledgeEdgeAsync(db, SourceTrackHash, TargetTrackHash, KnowledgeTransitionType.Blend)
            .ConfigureAwait(false);

        if (Score.UserRating is double rating)
            await KnowledgeLifecycleRepository
                .AddEvidenceAsync(db, edge.Id, TransitionEvidenceSource.ManualRating, rating, 0.9, null)
                .ConfigureAwait(false);

        edge.Verified = Score.Verified;
        await KnowledgeLifecycleRepository.RecomputeAsync(edge.Id, db).ConfigureAwait(false);
    }
}
