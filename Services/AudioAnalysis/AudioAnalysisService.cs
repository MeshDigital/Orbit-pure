using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;
using SLSKDONET.Engine.Analysis;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Orchestrates the full audio analysis pipeline for a single track:
/// FFmpeg decode → Waveform extraction → optional Essentia (BPM/key/beatgrid) →
/// CuePoint detection → DatabaseService persist.
///
/// If the Essentia binary is not installed the pipeline still succeeds —
/// waveform bands and structural cue points based on existing metadata are saved.
/// </summary>
public sealed class AudioAnalysisService : IAudioAnalysisService
{
    private readonly AudioIngestionPipeline _ingestion;
    private readonly WaveformExtractionService _waveform;
    private readonly BpmDetectionService _bpm;
    private readonly KeyDetectionService _key;
    private readonly BeatgridDetectionService _beatgrid;
    private readonly CuePointDetectionService _cues;
    private readonly EssentiaRunner _essentia;
    private readonly EnergyScoringService _energyScoring;
    private readonly EnergyAnalysisService _energyAnalysis;
    private readonly SubBassDropoutEngine _subBassEngine = new();
    private readonly SpectralFluxNoveltyEngine _noveltyEngine = new();
    private readonly TrackFingerprintBuilderService _trackFingerprintBuilder;
    private readonly TrackFingerprintStore _trackFingerprintStore;
    private readonly DatabaseService _db;
    private readonly ILogger<AudioAnalysisService> _logger;

    public AudioAnalysisService(
        AudioIngestionPipeline ingestion,
        WaveformExtractionService waveformExtraction,
        BpmDetectionService bpmDetection,
        KeyDetectionService keyDetection,
        BeatgridDetectionService beatgridDetection,
        CuePointDetectionService cuePointDetection,
        EssentiaRunner essentiaRunner,
        EnergyScoringService energyScoring,
        EnergyAnalysisService energyAnalysis,
        TrackFingerprintBuilderService trackFingerprintBuilder,
        TrackFingerprintStore trackFingerprintStore,
        DatabaseService db,
        ILogger<AudioAnalysisService> logger)
    {
        _ingestion     = ingestion;
        _waveform      = waveformExtraction;
        _bpm           = bpmDetection;
        _key           = keyDetection;
        _beatgrid      = beatgridDetection;
        _cues          = cuePointDetection;
        _essentia      = essentiaRunner;
        _energyScoring = energyScoring;
        _energyAnalysis = energyAnalysis;
        _trackFingerprintBuilder = trackFingerprintBuilder;
        _trackFingerprintStore = trackFingerprintStore;
        _db            = db;
        _logger        = logger;

        ValidateGenreModelFiles();
    }

    /// <inheritdoc/>
    public async Task<AudioAnalysisEntity?> AnalyzeFileAsync(
        string filePath,
        string trackUniqueHash,
        IProgress<(int Percent, string Step)>? progress = null,
        string? correlationId = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath))
        {
            _logger.LogWarning("[AudioAnalysis] FilePath is null or empty for hash {Hash}", trackUniqueHash);
            return null;
        }

        if (!File.Exists(filePath))
        {
            _logger.LogWarning("[AudioAnalysis] File not found: {Path}", filePath);
            return null;
        }

        _logger.LogInformation("[AudioAnalysis] Starting analysis for {Hash} ({File})",
            trackUniqueHash, Path.GetFileName(filePath));

        string? tempWav = null;
        try
        {
            // ── Step 1: Decode to 44.1 kHz stereo WAV ────────────────────
            progress?.Report((5, "Decoding audio..."));
            var source = new TrackAudioSource(filePath) { TrackUniqueHash = trackUniqueHash };

            try
            {
                tempWav = await _ingestion.DecodeToTempWavAsync(source, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Full detail already logged by AudioIngestionPipeline; log just file + summary here.
                _logger.LogWarning("[AudioAnalysis] Decode failed for {File}: {Msg}",
                    Path.GetFileName(filePath), ex.Message);
                return null;
            }

            // ── Step 2: Create or load the features entity ────────────────
            var features = await _db.GetAudioFeaturesByHashAsync(trackUniqueHash).ConfigureAwait(false)
                           ?? new AudioFeaturesEntity { TrackUniqueHash = trackUniqueHash };

            // ── Step 3: Measure track duration from the decoded WAV ───────
            double durationSeconds = MeasureDuration(tempWav);
            if (durationSeconds > 0)
                features.TrackDuration = durationSeconds;

            // ── Step 4: Extract waveform bands ────────────────────────────
            progress?.Report((25, "Extracting waveform..."));
            await _waveform.ExtractAsync(tempWav, features, cancellationToken).ConfigureAwait(false);

            // ── Step 4b: Real per-second RMS energy curve ─────────────────
            // Independent of Essentia, so drop/phrase detection (CuePointDetectionService,
            // AnalyzeTrackStructureJob) gets a genuine time-series signal to work with even
            // when the optional Essentia binary isn't installed. Previously EnergyCurveJson
            // was never populated at analysis time, so those detectors ran against a flat
            // placeholder curve and could only ever "find" a drop at its artificial edges.
            try
            {
                var rmsCurve = await _energyAnalysis.ComputeRawEnergyCurveAsync(tempWav, cancellationToken)
                    .ConfigureAwait(false);
                if (rmsCurve.Count > 0)
                    features.EnergyCurveJson = JsonSerializer.Serialize(rmsCurve);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "[AudioAnalysis] RMS energy curve computation failed for {File}; drop/phrase detection will fall back to a flat estimate.",
                    Path.GetFileName(filePath));
            }

            // ── Step 4c: Real multi-candidate drop signals ────────────────
            // Feeds Engine.Cueing.CueGenerationService's DSP drop-scoring path with actual
            // candidate timestamps (every sub-bass dropout/return, every build-confirmed
            // novelty peak) instead of the single collapsed DropTimeSeconds/DropConfidence
            // float pair it previously had to work with.
            try
            {
                var (pcmSamples, pcmSampleRate, pcmChannels) = AudioIngestionPipeline.ReadPcmFloat(tempWav);
                if (pcmSamples.Length > 0)
                {
                    var mono = ToMono(pcmSamples, pcmChannels);

                    var subBassCurve = _subBassEngine.ComputeSubBassEnergyCurve(mono, pcmSampleRate);
                    var (dropouts, returns) = _subBassEngine.DetectDropoutEvents(subBassCurve);
                    features.SubBassDropoutTimestampsJson = JsonSerializer.Serialize(dropouts);
                    features.SubBassReturnTimestampsJson = JsonSerializer.Serialize(returns);

                    var novelty = _noveltyEngine.ComputeNoveltyFunction(mono, pcmSampleRate);
                    var dropSignatures = _noveltyEngine.DetectDropSignatures(novelty, pcmSampleRate, 512);
                    features.NoveltyDropSignaturesJson = JsonSerializer.Serialize(dropSignatures
                        .Select(d => new NoveltyDropSignatureDto(d.DropSeconds, d.BuildStartSeconds, d.DropStrength))
                        .ToList());
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "[AudioAnalysis] Drop-signal detection failed for {File}; DSP drop scoring will fall back to a single collapsed timestamp.",
                    Path.GetFileName(filePath));
            }

            // ── Step 5: Essentia BPM / Key / Beatgrid (optional) ─────────
            EssentiaOutput? essentiaOutput = null;
            if (_essentia.IsAvailable)
            {
                progress?.Report((45, "Running BPM/key analysis..."));
                essentiaOutput = await _essentia.RunAsync(tempWav, cancellationToken)
                    .ConfigureAwait(false);

                if (essentiaOutput is not null)
                {
                    _bpm.Detect(essentiaOutput, features);
                    _key.Detect(essentiaOutput, features);
                    _beatgrid.Detect(essentiaOutput, features);

                    if (essentiaOutput.LowLevel?.AverageLoudness is > 0 and var loudness)
                        features.LoudnessLUFS = loudness;
                    if (essentiaOutput.Rhythm?.Danceability is > 0 and var dance)
                        features.Danceability = dance;

                    // ── Spectral & rhythm quick-reads ──────────────────────────
                    if (essentiaOutput.LowLevel?.SpectralCentroid?.Mean is > 0 and var sc)
                        features.SpectralCentroid = sc;
                    if (essentiaOutput.LowLevel?.SpectralComplexity?.Mean is > 0 and var scx)
                        features.SpectralComplexity = scx;
                    if (essentiaOutput.Rhythm?.OnsetRate is > 0 and var onsetRate)
                        features.OnsetRate = onsetRate;

                    // ── Voice / Instrumental ───────────────────────────────────
                    var voicePred = essentiaOutput.HighLevel?.VoiceInstrumental;
                    if (voicePred?.All?.Instrumental is > 0 and var instrProb)
                        features.InstrumentalProbability = Math.Clamp(instrProb, 0f, 1f);
                    else if ("instrumental".Equals(voicePred?.Value, StringComparison.OrdinalIgnoreCase))
                        features.InstrumentalProbability = Math.Clamp(voicePred!.Probability, 0.5f, 1f);

                    // ── Emotion: emomusic regression → Arousal + Valence ──────
                    // emomusic outputs values on the Russell circumplex (1–9);
                    // normalise to 0–1 so all consumers use a consistent scale.
                    var emoClasses = essentiaOutput.HighLevel?.EmoMusic?.All;
                    if (emoClasses is not null)
                    {
                        if (emoClasses.Arousal > 0f)
                            features.Arousal = Math.Clamp((emoClasses.Arousal - 1f) / 8f, 0f, 1f);
                        if (emoClasses.Valence > 0f)
                            features.Valence = Math.Clamp((emoClasses.Valence - 1f) / 8f, 0f, 1f);
                    }

                    // ── Tonality / DJ-tool detection ───────────────────────────
                    var tonalPred = essentiaOutput.HighLevel?.TonalAtonal;
                    if (tonalPred?.All is { } tonalClasses)
                    {
                        features.TonalProbability = Math.Clamp(tonalClasses.Tonal, 0f, 1f);
                        features.IsDjTool = tonalClasses.Tonal < 0.35f;
                    }
                    else if (!string.IsNullOrWhiteSpace(tonalPred?.Value))
                    {
                        bool isAtonal = "atonal".Equals(tonalPred.Value, StringComparison.OrdinalIgnoreCase);
                        features.IsDjTool         = isAtonal;
                        features.TonalProbability = isAtonal ? 1f - tonalPred.Probability : tonalPred.Probability;
                    }

                    // ── Mood: winner-takes-all across 6 classifiers ────────────
                    ApplyMoodClassification(essentiaOutput.HighLevel, features);

                    // ── Energy score (incorporates Arousal set above) ──────────
                    _energyScoring.Score(essentiaOutput, features);

                    _logger.LogDebug("[AudioAnalysis] Essentia OK — BPM={Bpm} Key={Key} Mood={Mood} Arousal={Arousal:F1}",
                        features.Bpm, features.Key, features.MoodTag, features.Arousal);
                }
                else
                {
                    _logger.LogDebug("[AudioAnalysis] Essentia returned no output for {File}", filePath);
                }
            }
            else
            {
                _logger.LogDebug("[AudioAnalysis] Essentia not available — skipping BPM/key detection");
            }

            // ── Step 5b: Genre inference + canonical normalisation ──────
            await InferAndApplyGenreAsync(essentiaOutput, features, cancellationToken).ConfigureAwait(false);

            // ── Step 6: Cue point detection ───────────────────────────────
            if (features.Bpm > 0 && features.TrackDuration > 0)
            {
                progress?.Report((70, "Detecting cue points..."));
                var cues = await _cues
                    .DetectAndPersistAsync(trackUniqueHash, features, essentiaOutput, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug("[AudioAnalysis] {Count} cue points detected", cues.Count);
            }

            // ── Step 7: Timestamp and save ────────────────────────────────
            progress?.Report((90, "Saving to database..."));
            features.AnalyzedAt = DateTime.UtcNow;
            await _db.SaveAudioFeaturesAsync(features).ConfigureAwait(false);

            // ── Step 8: A10.1 Track fingerprint build/persist (fail-open) ─
            try
            {
                var phrases = await _db.GetPhrasesByHashAsync(trackUniqueHash).ConfigureAwait(false);
                var fingerprint = _trackFingerprintBuilder.Build(trackUniqueHash, features, essentiaOutput, phrases);
                await _trackFingerprintStore.SaveAsync(fingerprint, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // A10 is additive: never block or alter A9-era analysis completion behavior.
                _logger.LogWarning(ex, "[AudioAnalysis] Fingerprint generation failed for {Hash}; continuing without fingerprint.", trackUniqueHash);
            }

            progress?.Report((100, "Done"));
            _logger.LogInformation("[AudioAnalysis] Completed {Hash}", trackUniqueHash);

            return new AudioAnalysisEntity
            {
                TrackUniqueHash = trackUniqueHash,
                AnalyzedAt      = features.AnalyzedAt,
            };
        }
        catch (OperationCanceledException)
        {
            _logger.LogDebug("[AudioAnalysis] Cancelled for {Hash}", trackUniqueHash);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AudioAnalysis] Unhandled error for {Hash}", trackUniqueHash);
            return null;
        }
        finally
        {
            if (tempWav is not null)
            {
                try { File.Delete(tempWav); }
                catch (IOException ex) { _logger.LogDebug(ex, "Best-effort temp WAV cleanup failed for {Path}", tempWav); }
            }
        }
    }

    /// <inheritdoc/>
    public async Task<AudioAnalysisEntity?> GetAnalysisAsync(string trackUniqueHash)
    {
        if (string.IsNullOrWhiteSpace(trackUniqueHash))
            return null;

        // Look up from features table; project into a minimal AudioAnalysisEntity
        var features = await _db.GetAudioFeaturesByHashAsync(trackUniqueHash).ConfigureAwait(false);
        if (features is null) return null;

        return new AudioAnalysisEntity
        {
            TrackUniqueHash = trackUniqueHash,
            AnalyzedAt      = features.AnalyzedAt,
        };
    }

    // ── helpers ───────────────────────────────────────────────────────────

    private static float[] ToMono(float[] interleaved, int channels)
    {
        if (channels <= 1) return interleaved;

        int numFrames = interleaved.Length / channels;
        var mono = new float[numFrames];
        for (int i = 0; i < numFrames; i++)
        {
            float sum = 0f;
            for (int c = 0; c < channels; c++)
                sum += interleaved[i * channels + c];
            mono[i] = sum / channels;
        }
        return mono;
    }

    private static double MeasureDuration(string wavPath)
    {
        try
        {
            using var reader = new NAudio.Wave.AudioFileReader(wavPath);
            return reader.TotalTime.TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    private static readonly string[] MusicnnElectronicModelKeys =
    [
        "genreelectronicmusicnnmsd2",
        "genreelectronic",
        "genre_electronic"
    ];

    private static readonly string[] JamendoModelKeys =
    [
        "mtgjamendogenrediscogseffnet1",
        "mtgjamendogenre",
        "jamendogenre",
        "mtg_jamendo_genre"
    ];

    private static readonly string[] Discogs400ModelKeys =
    [
        "genrediscogs400discogseffnet1",
        "genrediscogs400",
        "genre_discogs400"
    ];

    private async Task InferAndApplyGenreAsync(
        EssentiaOutput? output,
        AudioFeaturesEntity features,
        CancellationToken cancellationToken)
    {
        var candidates = new List<GenreInferenceCandidate>();

        // 1) Explicit model heads from Essentia output
        var extension = output?.HighLevel?.ExtensionData;
        if (extension is not null)
        {
            foreach (var kvp in extension)
            {
                var normalizedKey = NormalizeModelKey(kvp.Key);

                if (MusicnnElectronicModelKeys.Any(k => normalizedKey.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var pair in ParseLabelProbabilities(kvp.Value))
                    {
                        var canonical = CanonicalizeGenre(pair.Label);
                        if (string.IsNullOrWhiteSpace(canonical)) continue;
                        candidates.Add(new GenreInferenceCandidate(canonical, pair.Probability, "musicnn", 1.00f));
                    }
                    continue;
                }

                if (JamendoModelKeys.Any(k => normalizedKey.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var pair in ParseLabelProbabilities(kvp.Value))
                    {
                        var canonical = MapJamendoToElectronicGenre(pair.Label);
                        if (string.IsNullOrWhiteSpace(canonical)) continue;
                        candidates.Add(new GenreInferenceCandidate(canonical, pair.Probability, "jamendo", 0.75f));
                    }
                    continue;
                }

                if (Discogs400ModelKeys.Any(k => normalizedKey.Contains(k, StringComparison.OrdinalIgnoreCase)))
                {
                    foreach (var pair in ParseLabelProbabilities(kvp.Value))
                    {
                        var canonical = MapDiscogs400ToElectronicGenre(pair.Label);
                        if (string.IsNullOrWhiteSpace(canonical)) continue;
                        candidates.Add(new GenreInferenceCandidate(canonical, pair.Probability, "discogs400", 0.65f));
                    }
                }
            }
        }

        // 2) Embedding prior from user style definitions (if vectors + style centroids exist)
        var embeddingPrior = await InferGenreFromStyleCentroidsAsync(features, cancellationToken).ConfigureAwait(false);
        if (!string.IsNullOrWhiteSpace(embeddingPrior.Label))
        {
            candidates.Add(new GenreInferenceCandidate(embeddingPrior.Label, embeddingPrior.Confidence, "embedding", 0.60f));
        }

        // 3) Fallback heuristic only when model-derived candidates are missing
        if (candidates.Count == 0)
        {
            var heuristic = InferGenreHeuristic(features.Bpm);
            if (!string.IsNullOrWhiteSpace(heuristic.Label))
            {
                candidates.Add(new GenreInferenceCandidate(heuristic.Label, heuristic.Confidence, "heuristic", 0.35f));
            }
        }

        if (candidates.Count == 0)
        {
            return;
        }

        // 4) Fuse candidate scores with guardrails + DnB calibration
        var fusedScores = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        var maxConfidence = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate.Label) || candidate.Confidence <= 0f)
            {
                continue;
            }

            var guarded = ApplyBpmGuardrails(candidate.Label, candidate.Confidence, features.Bpm);
            if (guarded <= 0f)
            {
                continue;
            }

            var score = guarded * candidate.Weight;
            if (fusedScores.TryGetValue(candidate.Label, out var current))
            {
                fusedScores[candidate.Label] = current + score;
            }
            else
            {
                fusedScores[candidate.Label] = score;
            }

            if (maxConfidence.TryGetValue(candidate.Label, out var existingMax))
            {
                maxConfidence[candidate.Label] = Math.Max(existingMax, guarded);
            }
            else
            {
                maxConfidence[candidate.Label] = guarded;
            }
        }

        if (fusedScores.Count == 0)
        {
            return;
        }

        var ranked = fusedScores
            .OrderByDescending(kv => kv.Value)
            .ToList();

        var winner = ranked[0];
        var runnerUpScore = ranked.Count > 1 ? ranked[1].Value : 0f;
        var margin = Math.Max(0f, winner.Value - runnerUpScore);
        var winnerConfidence = maxConfidence.TryGetValue(winner.Key, out var conf) ? conf : 0.5f;
        var finalConfidence = Math.Clamp(winnerConfidence + Math.Min(0.20f, margin), 0f, 0.99f);

        features.ElectronicSubgenre = winner.Key;
        features.ElectronicSubgenreConfidence = finalConfidence;
        features.DetectedSubGenre = winner.Key;
        features.SubGenreConfidence = finalConfidence;
        features.PredictedVibe = winner.Key;
        features.PredictionConfidence = finalConfidence;

        var distribution = fusedScores
            .OrderByDescending(kv => kv.Value)
            .Take(6)
            .ToDictionary(kv => kv.Key, kv => Math.Round(kv.Value, 4));
        features.GenreDistributionJson = JsonSerializer.Serialize(distribution);
    }

    private static string NormalizeModelKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            return string.Empty;
        }

        return new string(key
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
    }

    private static IEnumerable<(string Label, float Probability)> ParseLabelProbabilities(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            yield break;
        }

        // Shape: { "all": { "label": score, ... } }
        if (element.TryGetProperty("all", out var allProp) && allProp.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in allProp.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetSingle(out var score))
                {
                    yield return (prop.Name, Math.Clamp(score, 0f, 1f));
                }
            }

            yield break;
        }

        // Shape: { "value": "label", "probability": 0.9 }
        if (element.TryGetProperty("value", out var valueProp) && valueProp.ValueKind == JsonValueKind.String)
        {
            var label = valueProp.GetString() ?? string.Empty;
            var probability = 0f;
            if (element.TryGetProperty("probability", out var probProp) && probProp.TryGetSingle(out var p))
            {
                probability = Math.Clamp(p, 0f, 1f);
            }

            if (!string.IsNullOrWhiteSpace(label))
            {
                yield return (label, probability);
            }
        }
    }

    private static (string Label, float Confidence) InferGenreHeuristic(float bpm)
    {
        if (bpm >= 160 && bpm <= 182)
        {
            return ("Drum and Bass", 0.62f);
        }

        if (bpm >= 120 && bpm <= 132)
        {
            return ("House", 0.56f);
        }

        if (bpm >= 125 && bpm <= 142)
        {
            return ("Techno", 0.53f);
        }

        return (string.Empty, 0f);
    }

    private async Task<(string Label, float Confidence)> InferGenreFromStyleCentroidsAsync(
        AudioFeaturesEntity features,
        CancellationToken cancellationToken)
    {
        var embedding = features.DeepTextureEmbedding ?? features.VectorEmbedding;
        if (embedding is not { Length: > 0 })
        {
            return (string.Empty, 0f);
        }

        var styles = await _db.LoadAllStyleDefinitionsAsync().ConfigureAwait(false);
        if (styles.Count == 0)
        {
            return (string.Empty, 0f);
        }

        string bestGenre = string.Empty;
        var bestSimilarity = 0f;

        foreach (var style in styles)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var centroid = style.Centroid;
            if (centroid.Count == 0)
            {
                continue;
            }

            var similarity = CosineSimilarity(embedding, centroid);
            if (similarity > bestSimilarity)
            {
                var proposed = !string.IsNullOrWhiteSpace(style.ParentGenre) ? style.ParentGenre : style.Name;
                bestGenre = CanonicalizeGenre(proposed);
                bestSimilarity = similarity;
            }
        }

        if (string.IsNullOrWhiteSpace(bestGenre) || bestSimilarity < 0.55f)
        {
            return (string.Empty, 0f);
        }

        var confidence = Math.Clamp((bestSimilarity + 1f) * 0.5f, 0f, 1f);
        return (bestGenre, confidence);
    }

    private static float ApplyBpmGuardrails(string genre, float confidence, float bpm)
    {
        var adjusted = confidence;

        if (genre.Equals("Drum and Bass", StringComparison.OrdinalIgnoreCase))
        {
            if (bpm >= 160 && bpm <= 182) adjusted += 0.10f; // calibration bias for DnB
            if (bpm < 150) adjusted -= 0.15f;
        }

        if (genre.Equals("House", StringComparison.OrdinalIgnoreCase))
        {
            if (bpm >= 118 && bpm <= 132) adjusted += 0.08f;
            if (bpm >= 155) adjusted -= 0.12f;
        }

        if (genre.Equals("Techno", StringComparison.OrdinalIgnoreCase))
        {
            if (bpm >= 124 && bpm <= 145) adjusted += 0.08f;
            if (bpm >= 165) adjusted -= 0.10f;
        }

        return Math.Clamp(adjusted, 0f, 1f);
    }

    private static float CosineSimilarity(float[] a, List<float> b)
    {
        var len = Math.Min(a.Length, b.Count);
        if (len == 0)
        {
            return 0f;
        }

        double dot = 0d;
        double magA = 0d;
        double magB = 0d;
        for (var i = 0; i < len; i++)
        {
            var va = a[i];
            var vb = b[i];
            dot += va * vb;
            magA += va * va;
            magB += vb * vb;
        }

        if (magA <= 0 || magB <= 0)
        {
            return 0f;
        }

        return (float)(dot / (Math.Sqrt(magA) * Math.Sqrt(magB)));
    }

    private static void ApplyMoodClassification(HighLevelData? hl, AudioFeaturesEntity features)
    {
        if (hl is null) return;

        static float ClassProb(ModelPrediction? pred, Func<ModelClasses, float> selector)
            => pred?.All is { } c ? selector(c) : (pred?.Probability ?? 0f);

        (string Tag, float Score)[] candidates =
        [
            ("Happy",      ClassProb(hl.MoodHappy,      c => c.Happy)),
            ("Aggressive", ClassProb(hl.MoodAggressive, c => c.Aggressive)),
            ("Sad",        ClassProb(hl.MoodSad,        c => c.Sad)),
            ("Relaxed",    ClassProb(hl.MoodRelaxed,    c => c.Relaxed)),
            ("Party",      ClassProb(hl.MoodParty,      c => c.Party)),
            ("Electronic", ClassProb(hl.MoodElectronic, c => c.Electronic)),
        ];

        var winner = candidates.MaxBy(c => c.Score);
        if (winner.Score > 0.35f)
        {
            features.MoodTag        = winner.Tag;
            features.MoodConfidence = Math.Clamp(winner.Score, 0f, 1f);
        }

        // Persist raw sadness score for downstream mood filtering
        float sadScore = ClassProb(hl.MoodSad, c => c.Sad);
        if (sadScore > 0.1f)
            features.Sadness = sadScore;
    }

    private static string? MapDiscogs400ToElectronicGenre(string label)
    {
        // Discogs-400 uses "Parent---Subgenre" path notation; only keep Electronic sub-genres
        if (label.IndexOf("Electronic", StringComparison.OrdinalIgnoreCase) < 0)
            return null;

        var parts    = label.Split("---", 2, StringSplitOptions.TrimEntries);
        var subgenre = parts.Length > 1 ? parts[1] : label.Trim();
        return CanonicalizeGenre(subgenre);
    }

    private static string MapJamendoToElectronicGenre(string label)
    {
        var canonical = CanonicalizeGenre(label);
        if (!string.IsNullOrWhiteSpace(canonical) &&
            (canonical.Equals("Drum and Bass", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("House", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Techno", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Trance", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Ambient", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Dubstep", StringComparison.OrdinalIgnoreCase) ||
             canonical.Equals("Trap", StringComparison.OrdinalIgnoreCase)))
        {
            return canonical;
        }

        var normalized = new string(label
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (normalized.Contains("drumnbass") || normalized.Contains("breakbeat")) return "Drum and Bass";
        if (normalized.Contains("deephouse") || normalized.Contains("club")) return "House";
        if (normalized.Contains("minimal") || normalized.Equals("idm", StringComparison.OrdinalIgnoreCase)) return "Techno";

        return string.Empty;
    }

    private void ValidateGenreModelFiles()
    {
        var expected = new[]
        {
            Path.Combine("Tools", "Essentia", "models", "genre_electronic-musicnn-msd-2.pb"),
            Path.Combine("Tools", "Essentia", "models", "mtg_jamendo_genre-discogs-effnet-1.pb"),
            Path.Combine("Tools", "Essentia", "models", "discogs-effnet-bs64-1.pb")
        };

        var missing = expected
            .Select(p => Path.Combine(AppContext.BaseDirectory, p))
            .Where(p => !File.Exists(p))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogWarning("[AudioAnalysis] Genre models missing: {Missing}", string.Join(" | ", missing));
        }
        else
        {
            _logger.LogInformation("[AudioAnalysis] Genre models detected (Musicnn + MTG-Jamendo + DiscogsEffnet).");
        }
    }

    private readonly record struct GenreInferenceCandidate(string Label, float Confidence, string Source, float Weight);

    private static string CanonicalizeGenre(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return string.Empty;
        }

        var normalized = new string(raw
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        if (normalized.Contains("dnb") || normalized.Contains("drumandbass"))
        {
            return "Drum and Bass";
        }

        if (normalized.Contains("techhouse"))
        {
            return "Tech House";
        }

        if (normalized.Contains("melodictechno"))
        {
            return "Melodic Techno";
        }

        if (normalized.Contains("techno"))
        {
            return "Techno";
        }

        if (normalized.Contains("house"))
        {
            return "House";
        }

        if (normalized.Contains("trance"))
        {
            return "Trance";
        }

        if (normalized.Contains("dubstep"))
        {
            return "Dubstep";
        }

        if (normalized.Contains("trap"))
        {
            return "Trap";
        }

        if (normalized.Contains("ambient"))
        {
            return "Ambient";
        }

        return raw.Trim();
    }
}
