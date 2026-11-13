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
using SLSKDONET.Engine.Analysis.GenreFusion;
using SLSKDONET.Models;
using SLSKDONET.Services.Similarity;

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
    private readonly InProcessTempoKeyEngine _inProcessEngine;
    private readonly CuePointDetectionService _cues;
    private readonly EssentiaRunner _essentia;
    private readonly EnergyScoringService _energyScoring;
    private readonly EnergyAnalysisService _energyAnalysis;
    private readonly SubBassDropoutEngine _subBassEngine = new();
    private readonly SpectralFluxNoveltyEngine _noveltyEngine = new();
    private readonly Engine.Analysis.StructuralStrippingEngine _structuralStrippingEngine = new(new SubBassDropoutEngine());
    private readonly DiscogsEffnetEmbeddingExtractor _effnetEmbedding;
    private readonly EffnetClassifierHeadService _effnetClassifier;
    private readonly DiscogsStyleLabels? _styleLabels;
    private readonly TrackFingerprintBuilderService _trackFingerprintBuilder;
    private readonly TrackFingerprintStore _trackFingerprintStore;
    private readonly BreakbeatAnalysisStrategy _breakbeatStrategy;
    private readonly FourOnTheFloorAnalysisStrategy _fourOnFloorStrategy;
    private readonly BassMusicAnalysisStrategy _bassMusicStrategy;
    private readonly DatabaseService _db;
    private readonly ILogger<AudioAnalysisService> _logger;
    private readonly Rekordbox.IRekordboxPssiService? _rekordbox;

    public AudioAnalysisService(
        AudioIngestionPipeline ingestion,
        WaveformExtractionService waveformExtraction,
        BpmDetectionService bpmDetection,
        KeyDetectionService keyDetection,
        BeatgridDetectionService beatgridDetection,
        InProcessTempoKeyEngine inProcessEngine,
        CuePointDetectionService cuePointDetection,
        EssentiaRunner essentiaRunner,
        EnergyScoringService energyScoring,
        EnergyAnalysisService energyAnalysis,
        DiscogsEffnetEmbeddingExtractor effnetEmbedding,
        EffnetClassifierHeadService effnetClassifier,
        TrackFingerprintBuilderService trackFingerprintBuilder,
        TrackFingerprintStore trackFingerprintStore,
        BreakbeatAnalysisStrategy breakbeatStrategy,
        FourOnTheFloorAnalysisStrategy fourOnFloorStrategy,
        BassMusicAnalysisStrategy bassMusicStrategy,
        DatabaseService db,
        ILogger<AudioAnalysisService> logger,
        Rekordbox.IRekordboxPssiService? rekordbox = null,
        DiscogsStyleLabels? styleLabels = null)
    {
        _rekordbox = rekordbox;
        _ingestion     = ingestion;
        _waveform      = waveformExtraction;
        _bpm           = bpmDetection;
        _key           = keyDetection;
        _beatgrid      = beatgridDetection;
        _inProcessEngine = inProcessEngine;
        _cues          = cuePointDetection;
        _essentia      = essentiaRunner;
        _energyScoring = energyScoring;
        _energyAnalysis = energyAnalysis;
        _effnetEmbedding = effnetEmbedding;
        _effnetClassifier = effnetClassifier;
        _styleLabels = styleLabels;
        _trackFingerprintBuilder = trackFingerprintBuilder;
        _trackFingerprintStore = trackFingerprintStore;
        _breakbeatStrategy = breakbeatStrategy;
        _fourOnFloorStrategy = fourOnFloorStrategy;
        _bassMusicStrategy = bassMusicStrategy;
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
        AnalysisEngine engines = AnalysisEngine.Pipeline,
        AnalysisAudioEngine audioEngine = AnalysisAudioEngine.Essentia,
        CancellationToken cancellationToken = default)
    {
        // Nothing requested means nothing to do: the caller decides which engines a run is for.
        if (engines == AnalysisEngine.None)
        {
            _logger.LogDebug("[AudioAnalysis] No engines requested for {Hash}; skipping.", trackUniqueHash);
            return null;
        }

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
        Task<EssentiaOutput?>? essentiaTask = null;
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

            // ── Start Essentia extraction now (BPM/key/beat-grid) ─────────
            // Essentia only needs the decoded WAV produced above, so start it before the waveform
            // and in-process DSP/ONNX passes and let it run concurrently with them; its result is
            // awaited at the BPM/key step. RunAsync is fail-open (null on any error) and honours
            // the cancellation token, so a failure in the interim steps is handled at the await.
            // The in-process NWaves/NAudio profile skips this entirely — it analyses the PCM buffer
            // the in-process passes below already have in memory.
            // Mood runs the in-process ONNX path only — it must not start the external Essentia
            // process (a Mood-only backfill is meant to be cheap).
            var needsEssentia = (engines & (AnalysisEngine.Tempo | AnalysisEngine.Key)) != 0;
            if (audioEngine != AnalysisAudioEngine.InProcess && _essentia.IsAvailable && needsEssentia)
            {
                essentiaTask = _essentia.RunAsync(tempWav, cancellationToken);
            }

            // ── Step 2: Create or load the features entity ────────────────
            var features = await _db.GetAudioFeaturesByHashAsync(trackUniqueHash).ConfigureAwait(false)
                           ?? new AudioFeaturesEntity { TrackUniqueHash = trackUniqueHash };

            // ── Step 3: Measure track duration from the decoded WAV ───────
            double durationSeconds = MeasureDuration(tempWav);
            if (durationSeconds > 0)
                features.TrackDuration = durationSeconds;

            // ── Step 4: Extract waveform bands ────────────────────────────
            var waveformExtracted = false;
            if (engines.Has(AnalysisEngine.Waveform))
            {
                progress?.Report((25, "Extracting waveform..."));
                waveformExtracted = await _waveform.ExtractAsync(tempWav, features, cancellationToken).ConfigureAwait(false);
            }

            // ── Step 4b/4c: Decode once, feed both the RMS energy curve and the ─
            // ── multi-candidate drop-signal engines ─────────────────────────
            // Previously each of these decoded the same tempWav file independently (once via
            // NAudio streaming RMS windows, once via a full PCM float read) — doubling I/O/CPU
            // decode cost per analyzed track for no benefit. Decoding once here and reusing the
            // buffer for both consumers keeps each consumer's own failure isolated (RMS failure
            // must not block drop-signal detection and vice versa) while paying for the decode
            // only once.
            float[] pcmSamples = Array.Empty<float>();
            int pcmSampleRate = 44100;
            int pcmChannels = 2;
            GenreMoodPrediction? onnxGenrePrediction = null;
            var needsPcm = (engines & (AnalysisEngine.Loudness | AnalysisEngine.Structure |
                                       AnalysisEngine.Cues | AnalysisEngine.Tempo | AnalysisEngine.Mood)) != 0;
            if (needsPcm)
            {
                progress?.Report((32, "Measuring loudness & energy..."));
                try
                {
                    (pcmSamples, pcmSampleRate, pcmChannels) = AudioIngestionPipeline.ReadPcmFloat(tempWav);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "[AudioAnalysis] PCM decode failed for {File}; PCM-backed engine output will be skipped.",
                        Path.GetFileName(filePath));
                }
            }

            IReadOnlyList<float> rmsCurve = Array.Empty<float>();
            float[] mono = Array.Empty<float>();
            // Partial runs need these signals when either output was requested. Has() requires
            // both bits and made Structure-only and Cues-only Fill Missing runs reuse stale data.
            var needsDropSignals = engines.HasAny(AnalysisEngine.Structure | AnalysisEngine.Cues);
            if (pcmSamples.Length > 0)
            {
                // Integrated loudness (BS.1770 / EBU R128) for the inspector and loudness-normalised
                // playback. 0 = not measured (the column is NOT NULL); readers treat ≥ 0 as unknown.
                try
                {
                    if (engines.Has(AnalysisEngine.Loudness))
                    {
                        features.LoudnessLUFS = (float)(IntegratedLoudnessMeter.MeasureIntegratedLufs(pcmSamples, pcmSampleRate, pcmChannels) ?? IntegratedLoudnessMeter.SilentMarker);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex, "[AudioAnalysis] Loudness measurement failed for {File}; loudness stays unknown.",
                        Path.GetFileName(filePath));
                }

                // Real per-second (1.0s window) RMS energy curve — independent of Essentia, so
                // drop/phrase detection (CuePointDetectionService, AnalyzeTrackStructureJob) gets
                // a genuine time-series signal to work with even when the optional Essentia binary
                // isn't installed. Previously EnergyCurveJson was never populated at analysis time,
                // so those detectors ran against a flat placeholder curve and could only ever "find"
                // a drop at its artificial edges. Kept in an outer-scoped variable so the novelty
                // gating step below can reuse it instead of recomputing.
                if (needsDropSignals)
                try
                {
                    rmsCurve = _energyAnalysis.ComputeRawEnergyCurveFromPcm(pcmSamples, pcmSampleRate, pcmChannels);
                    if (rmsCurve.Count > 0 && engines.Has(AnalysisEngine.Structure))
                        features.EnergyCurveJson = JsonSerializer.Serialize(rmsCurve);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "[AudioAnalysis] RMS energy curve computation failed for {File}; drop/phrase detection will fall back to a flat estimate.",
                        Path.GetFileName(filePath));
                }

                // Real multi-candidate drop signals — feeds Engine.Cueing.CueGenerationService's
                // DSP drop-scoring path with actual candidate timestamps (every sub-bass
                // dropout/return, every build-confirmed novelty peak) instead of the single
                // collapsed DropTimeSeconds/DropConfidence float pair it previously had to work with.
                // Mono is also used by tempo downbeat validation and Mood's ONNX classifier.
                if (needsDropSignals || engines.HasAny(AnalysisEngine.Tempo | AnalysisEngine.Mood))
                    mono = ToMono(pcmSamples, pcmChannels);

                // Music start (issue #224): first audible sound after silence/run-in. A user-placed
                // marker is never overwritten by re-analysis (same protection rule as the grid).
                if (mono.Length > 0 && !string.Equals(features.MusicStartSource, "User", StringComparison.Ordinal))
                {
                    try
                    {
                        double threshold = features.LoudnessLUFS is > -120 and var lufs
                            ? MusicStartDetector.ThresholdDbFs(lufs)
                            : MusicStartDetector.DefaultThresholdDbFs;
                        features.MusicStartSeconds = MusicStartDetector.Detect(mono, pcmSampleRate, threshold) is { } ms ? (float)ms : null;
                        features.MusicStartSource = features.MusicStartSeconds.HasValue ? "Analysis" : null;
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "[AudioAnalysis] Music-start detection failed for {File}", Path.GetFileName(filePath));
                    }
                }

                if (needsDropSignals)
                try
                {
                    var subBassCurve = _subBassEngine.ComputeSubBassEnergyCurve(mono, pcmSampleRate);
                    var (dropouts, returns) = _subBassEngine.DetectDropoutEvents(subBassCurve);
                    features.SubBassDropoutTimestampsJson = JsonSerializer.Serialize(dropouts);
                    features.SubBassReturnTimestampsJson = JsonSerializer.Serialize(returns);

                    // House/Techno-family breakdown signature — genuinely different from a
                    // sub-bass energy dropout (see StructuralStrippingEngine's own doc comment).
                    // Genre isn't known yet at this point in analysis, so this runs unconditionally
                    // for every track exactly like sub-bass dropout does; genre-gating happens
                    // downstream in CueGenerationService's DSP path.
                    var (strippingStarts, strippingReturns) = _structuralStrippingEngine.DetectStructuralStripping(mono, pcmSampleRate);
                    features.StructuralStrippingStartTimestampsJson = JsonSerializer.Serialize(strippingStarts);
                    features.StructuralStrippingReturnTimestampsJson = JsonSerializer.Serialize(strippingReturns);

                    const int noveltyHopSize = 512;
                    var novelty = _noveltyEngine.ComputeNoveltyFunction(mono, pcmSampleRate);

                    // Track-wide baseline gating: without this, a riser/snare-roll/hi-hat rush
                    // right before a real drop can out-score the drop's own transient, since
                    // SubtractLocalMean only ever compares novelty to a ±1s local window. Scale
                    // by how the broadband RMS at each point compares to the track's own mean —
                    // a spectrally-busy-but-quiet moment can no longer win against a loud one.
                    novelty = SpectralFluxNoveltyEngine.ApplyGlobalEnergyGate(
                        novelty, (double)noveltyHopSize / pcmSampleRate, rmsCurve.ToArray(), energyCurveWindowSeconds: 1.0);

                    var dropSignatures = _noveltyEngine.DetectDropSignatures(novelty, pcmSampleRate, noveltyHopSize);
                    features.NoveltyDropSignaturesJson = JsonSerializer.Serialize(dropSignatures
                        .Select(d => new NoveltyDropSignatureDto(d.DropSeconds, d.BuildStartSeconds, d.DropStrength))
                        .ToList());
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "[AudioAnalysis] Drop-signal detection failed for {File}; DSP drop scoring will fall back to a single collapsed timestamp.",
                        Path.GetFileName(filePath));
                }

                // Real DiscogsEffnet embedding + genre/mood classification via ONNX Runtime, run
                // in-process. ORBIT's bundled Essentia CLI binary was found to silently produce no
                // TensorFlow-model output at all (confirmed empirically — its `highlevel` section
                // comes back empty regardless of the profile.yaml's tensorflow_models directive),
                // so genre classification and mood scoring were previously running on a weak
                // BPM-only heuristic and a dead 0-value fallback respectively, for every track.
                // Feeds InferAndApplyGenreAsync (genre) and mood fields directly below.
                if (engines.Has(AnalysisEngine.Mood))
                try
                {
                    progress?.Report((40, "Running AI genre/mood..."));
                    var mono16k = EffnetMelSpectrogramExtractor.ResampleTo16k(mono, pcmSampleRate);
                    var embeddingResult = await _effnetEmbedding.ExtractAsync(mono16k, cancellationToken).ConfigureAwait(false);
                    if (embeddingResult is not null)
                    {
                        features.Embedding = embeddingResult.Value.Embedding;
                        features.EmbeddingModelTag = _effnetEmbedding.ModelTag;

                        // Persist the 400 Discogs style scores (previously discarded here) plus the
                        // top style, so style playlists and genre fill can use the explainable label.
                        if (_styleLabels is { IsUsable: true })
                        {
                            features.StyleScores = embeddingResult.Value.StylePredictions;
                            features.StyleModelTag = _effnetEmbedding.ModelTag;
                            var top = _styleLabels.TopN(embeddingResult.Value.StylePredictions, 1);
                            if (top.Count > 0)
                            {
                                features.TopStyle = top[0].Key;
                                features.TopStyleConfidence = Math.Clamp(top[0].Score, 0f, 1f);
                            }
                        }

                        var prediction = _effnetClassifier.Classify(embeddingResult.Value.Embedding);
                        onnxGenrePrediction = prediction;

                        // Winner-takes-all across the 5 independent binary mood classifiers,
                        // matching the (previously dead) ApplyMoodClassification's own convention.
                        (string Tag, float Score)[] moodCandidates =
                        [
                            ("Happy", prediction.MoodHappy), ("Aggressive", prediction.MoodAggressive),
                            ("Sad", prediction.MoodSad), ("Relaxed", prediction.MoodRelaxed),
                            ("Party", prediction.MoodParty),
                        ];
                        var moodWinner = moodCandidates.MaxBy(c => c.Score);
                        if (moodWinner.Score > 0.35f)
                        {
                            features.MoodTag = moodWinner.Tag;
                            features.MoodConfidence = Math.Clamp(moodWinner.Score, 0f, 1f);
                        }
                        if (prediction.MoodSad > 0.1f)
                            features.Sadness = prediction.MoodSad;

                        // Full per-dimension breakdown (unlike the winner-takes-all fields above,
                        // stored unthresholded — the Track Inspector shows these as raw confidence
                        // bars, so even a low score is meaningful information, not noise to hide).
                        features.MoodHappy = Math.Clamp(prediction.MoodHappy, 0f, 1f);
                        features.MoodRelaxed = Math.Clamp(prediction.MoodRelaxed, 0f, 1f);
                        features.MoodParty = Math.Clamp(prediction.MoodParty, 0f, 1f);
                        features.MoodAggressive = Math.Clamp(prediction.MoodAggressive, 0f, 1f);
                    }
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogWarning(ex,
                        "[AudioAnalysis] ONNX embedding/genre/mood classification failed for {File}; genre inference will fall back to BPM heuristics.",
                        Path.GetFileName(filePath));
                }
            }

            // ── Step 5: BPM / Key / Beatgrid (backend per the selected analysis profile) ──
            EssentiaOutput? essentiaOutput = null;
            if (essentiaTask is not null)
            {
                progress?.Report((45, "Running BPM/key analysis..."));
                essentiaOutput = await essentiaTask.ConfigureAwait(false);
            }
            else if (audioEngine == AnalysisAudioEngine.InProcess && needsEssentia && mono.Length > 0)
            {
                progress?.Report((45, "Analysing BPM/key in-process..."));
                essentiaOutput = _inProcessEngine.Analyze(mono, pcmSampleRate, durationSeconds);
            }

            // A grid the DJ owns must survive re-analysis byte-identical. Snapshot it so the raw
            // beat-tracker write, downbeat pick and fit below can be rolled back; the fitted/rekordbox
            // suggestion is kept in AnalysedGridJson by GridWriteRules instead of being applied.
            bool userOwnsGrid = features.GridSource == GridWriteRules.SourceUser || features.GridVerified;
            float userBpm = features.Bpm;
            string userBeatGridJson = features.BeatGridJson;
            double userDownbeat = features.DownbeatOffsetSeconds;

            if (essentiaOutput is not null)
            {
                // One BPM/key pass (external Essentia or in-process NWaves/NAudio) yields tempo, key
                // and the high-level descriptors together, so asking for one runs the pass; only the
                // requested outputs are kept.
                if (engines.Has(AnalysisEngine.Tempo)) _bpm.Detect(essentiaOutput, features);
                if (engines.Has(AnalysisEngine.Key)) _key.Detect(essentiaOutput, features);
                if (engines.Has(AnalysisEngine.Tempo)) _beatgrid.Detect(essentiaOutput, features);

                // Downbeat-phase validation: BeatgridDetectionService anchors on the *first*
                // detected beat tick, which carries no bar-phase information from a generic
                // beat tracker — it's not necessarily beat 1 of a bar. Every downstream
                // SnapToBar call in CueGenerationService anchors off this value, so a wrong
                // bar-phase produces a systematic, consistent-per-track offset in every cue —
                // exactly the "close but way off" symptom. Pick whichever of the first few
                // ticks actually carries the strongest kick/sub-bass energy instead.
                if (engines.Has(AnalysisEngine.Tempo) && mono.Length > 0 && !string.IsNullOrWhiteSpace(features.BeatGridJson))
                {
                    try
                    {
                        var beats = JsonSerializer.Deserialize<double[]>(features.BeatGridJson);
                        if (beats is { Length: >= 2 })
                        {
                            int strongestIndex = SubBassDropoutEngine.FindStrongestBeatIndex(mono, pcmSampleRate, beats, candidateCount: 4);
                            if (strongestIndex > 0)
                                features.DownbeatOffsetSeconds = beats[strongestIndex];
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _logger.LogDebug(ex, "[AudioAnalysis] Downbeat-phase validation failed for {File}; keeping the beat tracker's first tick.", Path.GetFileName(filePath));
                    }
                }

                if (engines.Has(AnalysisEngine.Mood))
                {
                    // LoudnessLUFS is measured from the PCM above (IntegratedLoudnessMeter). Essentia's
                    // lowlevel.average_loudness is a 0–1 dynamic-range descriptor, not LUFS; storing it
                    // here showed "0.8 LUFS" and made loudness normalisation cut every track ~15 dB.
                    if (essentiaOutput.Rhythm?.Danceability is > 0 and var dance)
                        features.Danceability = dance;

                    // ── Spectral & rhythm quick-reads ───────────────────────
                    if (essentiaOutput.LowLevel?.SpectralCentroid?.Mean is > 0 and var sc)
                        features.SpectralCentroid = sc;
                    if (essentiaOutput.LowLevel?.SpectralComplexity?.Mean is > 0 and var scx)
                        features.SpectralComplexity = scx;
                    if (essentiaOutput.Rhythm?.OnsetRate is > 0 and var onsetRate)
                        features.OnsetRate = onsetRate;

                    // ── Voice / Instrumental ────────────────────────────────
                    var voicePred = essentiaOutput.HighLevel?.VoiceInstrumental;
                    if (voicePred?.All?.Instrumental is > 0 and var instrProb)
                        features.InstrumentalProbability = Math.Clamp(instrProb, 0f, 1f);
                    else if ("instrumental".Equals(voicePred?.Value, StringComparison.OrdinalIgnoreCase))
                        features.InstrumentalProbability = Math.Clamp(voicePred!.Probability, 0.5f, 1f);

                    // ── Emotion: emomusic regression → Arousal + Valence ────
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

                    // ── Tonality / DJ-tool detection ─────────────────────────
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

                    // ── Mood: winner-takes-all across 6 classifiers ──────────
                    ApplyMoodClassification(essentiaOutput.HighLevel, features);

                    // ── Energy score (incorporates Arousal set above) ────────
                    _energyScoring.Score(essentiaOutput, features);
                }

                _logger.LogDebug("[AudioAnalysis] BPM/key OK — BPM={Bpm} Key={Key} Mood={Mood} Arousal={Arousal:F1}",
                    features.Bpm, features.Key, features.MoodTag, features.Arousal);
            }
            else
            {
                _logger.LogDebug("[AudioAnalysis] BPM/key analysis produced no output for {File}; skipping BPM/key detection", filePath);
            }

            // ── Step 5b: Genre inference + canonical normalisation ──────
            if (engines.Has(AnalysisEngine.Mood))
            {
                // GA6 (#251): the drums' signature for the rhythm & tempo gate. Uses this run's BPM and
                // ticks, or the stored ones on a Mood-only run (the features row carries them).
                var rhythm = AnalyzeRhythm(mono, pcmSampleRate, features, rmsCurve);
                InferAndApplyGenre(features, onnxGenrePrediction, rhythm);
            }

            // ── Step 5c: Fit a precise constant grid to the beat ticks ───
            // Runs after genre inference because that step can still change the BPM's octave. Replaces
            // the integer/half-time-quantised BPM with a fitted one and re-picks the downbeat from the
            // sub-bass structure events — see BeatGridFitter for the measured before/after.
            var rawTicks = BeatGridFitter.ParseTicks(features.BeatGridJson);
            if (engines.Has(AnalysisEngine.Tempo) &&
                BeatGridFitter.ApplyTo(features, rawTicks, BeatGridFitter.StructuralEventsFrom(features)))
                _logger.LogDebug("[AudioAnalysis] Beat grid fitted for {File}: {Bpm:F2} BPM, downbeat {Downbeat:F3}s",
                    Path.GetFileName(filePath), features.Bpm, features.DownbeatOffsetSeconds);

            // If Rekordbox has analysed this exact audio, its grid (real downbeats included) wins.
            if (engines.Has(AnalysisEngine.Tempo)
                && _rekordbox?.IsAvailable == true
                && await _rekordbox.GetBeatGridAsync(filePath, cancellationToken).ConfigureAwait(false) is { } rbGrid
                && BeatGridFitter.ApplyRekordboxGrid(features, rbGrid, rawTicks))
                _logger.LogDebug("[AudioAnalysis] Using Rekordbox's beat grid for {File}: {Bpm:F2} BPM", Path.GetFileName(filePath), features.Bpm);

            // Restore a user-owned grid that the raw detection/fit above was not allowed to change.
            if (userOwnsGrid)
            {
                features.Bpm = userBpm;
                features.BeatGridJson = userBeatGridJson;
                features.DownbeatOffsetSeconds = userDownbeat;
                features.GridSource = GridWriteRules.SourceUser;
                features.GridVerified = true;
            }

            // ── Step 6: Cue point detection ───────────────────────────────
            if (engines.Has(AnalysisEngine.Structure) && features.Bpm > 0 && features.TrackDuration > 0)
            {
                progress?.Report((72, "Detecting cue points..."));
                var cues = await _cues
                    .DetectAndPersistAsync(trackUniqueHash, features, essentiaOutput, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogDebug("[AudioAnalysis] {Count} cue points detected", cues.Count);
            }

            // ── Step 7: Timestamp and save ────────────────────────────────
            progress?.Report((90, "Saving to database..."));
            features.AnalyzedAt = DateTime.UtcNow;
            // AnalysisVersion is the waveform's version (Cue Forge and the coverage card read it to
            // flag old waveforms). Only stamp it when this run actually re-extracted the waveform —
            // a key- or mood-only repair run, or a failed extraction that left the old blob in
            // place, must not make an old waveform look current.
            if (waveformExtracted)
                features.AnalysisVersion = WaveformExtractionService.CurrentWaveformVersion;
            await _db.SaveAudioFeaturesAsync(features).ConfigureAwait(false);

            // ── Step 8: A10.1 Track fingerprint build/persist (fail-open) ─
            if (engines.Has(AnalysisEngine.Fingerprint))
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

            // A repair run must not report success when its requested outputs are still absent.
            // Partial results stay saved, but the queue receives an honest failure instead of
            // letting Fill Missing complete repeatedly with no change (notably when Essentia is
            // unavailable or a model cannot run). Structure/cues are verified by their queue jobs.
            var verifiableOutputs = engines & (AnalysisEngine.Waveform | AnalysisEngine.Loudness |
                AnalysisEngine.Tempo | AnalysisEngine.Key | AnalysisEngine.Mood);
            var presentOutputs = AnalysisCoverageAnalyzer.Detect(features);
            var missingOutputs = verifiableOutputs & ~presentOutputs;
            if (missingOutputs != AnalysisEngine.None)
            {
                var hint = !_essentia.IsAvailable && (missingOutputs & (AnalysisEngine.Tempo | AnalysisEngine.Key)) != 0
                    ? " The Essentia extractor is not installed, so BPM and key cannot be produced."
                    : string.Empty;
                throw new InvalidOperationException(
                    $"Analysis did not produce requested outputs: {AnalysisEngines.Describe(missingOutputs)}.{hint} " +
                    "Check dependency health and the analysis logs; partial results were preserved.");
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
            // A pre-started Essentia extraction may still be running if an earlier step failed
            // (it is only awaited at the BPM/key step). Observe its outcome so a late cancellation
            // is never left unobserved; the temp-WAV delete below is best-effort and simply fails
            // (and logs) if the extraction still holds the file open.
            if (essentiaTask is { } essentia)
            {
                _ = essentia.ContinueWith(_ => { }, CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }

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

    /// <summary>
    /// Runs <see cref="RhythmSignatureAnalyzer"/> and persists the result. Never throws: rhythm is
    /// evidence, and without it the gate simply does nothing.
    /// </summary>
    private RhythmSignature? AnalyzeRhythm(float[] mono, int sampleRate, AudioFeaturesEntity features, IReadOnlyList<float> rmsCurve)
    {
        if (mono.Length == 0) return null;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var ticks = BeatGridFitter.ParseTicks(features.BeatGridJson);
            var signature = RhythmSignatureAnalyzer.Analyze(mono, sampleRate, features.Bpm, ticks, rmsCurve.Count > 0 ? rmsCurve : null);
            _logger.LogDebug("[AudioAnalysis] Rhythm signature in {Ms} ms: 4x4 {FourOnFloor:F2}, backbeat {Backbeat:F2}, half {Half:F2}, break {Break:F2}, {Low:F0}/{High:F0} BPM, reliability {Reliability:F2}",
                sw.ElapsedMilliseconds, signature.FourOnFloorScore, signature.BackbeatScore, signature.HalfTimeScore,
                signature.BreakIrregularity, signature.TempoLow, signature.TempoHigh, signature.Reliability);
            features.RhythmSignatureJson = JsonSerializer.Serialize(signature);
            return signature;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[AudioAnalysis] Rhythm signature failed; genre fuses without the rhythm gate");
            return null;
        }
    }

    private void InferAndApplyGenre(AudioFeaturesEntity features, GenreMoodPrediction? onnxPrediction, RhythmSignature? rhythm)
    {
        var jamendo = onnxPrediction is { } prediction
            ? prediction.JamendoGenres.Select(g => new GenreLabelScore(g.Label, g.Probability)).ToArray()
            : Array.Empty<GenreLabelScore>();

        // GA5 (#250): the 400 Discogs style sigmoids from the same EffNet run. Rows analysed before
        // they were persisted, or with a labels/model mismatch, fuse on Jamendo alone.
        IReadOnlyList<GenreLabelScore>? discogs = null;
        if (features.StyleScores is { } styleScores && _styleLabels is { IsUsable: true } labels &&
            styleScores.Length == labels.All.Count)
        {
            var all = labels.All;
            var votes = new GenreLabelScore[styleScores.Length];
            for (var i = 0; i < votes.Length; i++) votes[i] = new GenreLabelScore(all[i].Key, styleScores[i]);
            discogs = votes;
        }

        var result = GenreFusionEngine.Fuse(new GenreFusionInput(jamendo, features.Bpm, discogs, rhythm));
        if (result is null)
        {
            return;
        }

        features.ElectronicSubgenre = result.WinnerLabel;
        features.ElectronicSubgenreConfidence = result.Confidence;
        features.DetectedSubGenre = result.WinnerLabel;
        features.SubGenreConfidence = result.Confidence;
        features.PredictedVibe = result.WinnerLabel;
        features.PredictionConfidence = result.Confidence;

        // Genre-family BPM refinement — this is the one place both a final genre string and BPM
        // are both settled, the correct seam for a genre-corroborated bracket correction (unlike
        // BpmDetectionService's genre-agnostic half-time correction, which runs before genre is
        // known and can't use it). See BreakbeatAnalysisStrategy/FourOnTheFloorAnalysisStrategy.
        var classification = GenreFamilyClassifier.Classify(result.WinnerLabel, features.Bpm, result.Confidence, GenreSource.ModelInferred);
        IGenreFamilyAnalysisStrategy? strategy = classification.Family switch
        {
            GenreFamily.Breakbeat => _breakbeatStrategy,
            GenreFamily.FourOnTheFloor => _fourOnFloorStrategy,
            GenreFamily.Dubstep => _bassMusicStrategy,
            _ => null
        };
        if (strategy != null)
        {
            var (correctedBpm, confidencePenalty, anomalyNote) = strategy.RefineBpm(classification, features.Bpm, features.BpmConfidence);
            if (correctedBpm != features.Bpm && anomalyNote != null)
            {
                RecordAnomaly(features, anomalyNote);
                features.Bpm = correctedBpm;
                features.BpmConfidence *= confidencePenalty;
            }
        }

        features.GenreDistributionJson = JsonSerializer.Serialize(result.Distribution);
    }

    /// <summary>Appends a short note to <see cref="AudioFeaturesEntity.AnomaliesJson"/> for later
    /// auditing. Mirrors <c>BpmDetectionService.RecordAnomaly</c> — duplicated rather than shared
    /// since it's a trivial JSON-append and sharing it would add a cross-class coupling for
    /// something this small.</summary>
    private static void RecordAnomaly(AudioFeaturesEntity target, string note)
    {
        List<string> anomalies;
        try
        {
            anomalies = JsonSerializer.Deserialize<List<string>>(target.AnomaliesJson) ?? new List<string>();
        }
        catch (JsonException)
        {
            anomalies = new List<string>();
        }

        anomalies.Add(note);
        target.AnomaliesJson = JsonSerializer.Serialize(anomalies);
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

        // Runs after the ONNX classifier head (see AnalyzeFileAsync) already had a chance to set
        // MoodTag/MoodConfidence for this same track. Essentia's TensorFlow layer is currently
        // confirmed non-functional in ORBIT's bundled binary (empty highlevel output), so this is
        // a no-op today — but if a future working binary produces real scores, only let it win
        // when it's actually more confident, rather than unconditionally overwriting the ONNX result.
        var winner = candidates.MaxBy(c => c.Score);
        if (winner.Score > 0.35f && winner.Score > features.MoodConfidence)
        {
            features.MoodTag        = winner.Tag;
            features.MoodConfidence = Math.Clamp(winner.Score, 0f, 1f);
        }

        // Persist raw sadness score for downstream mood filtering (same precedence rule)
        float sadScore = ClassProb(hl.MoodSad, c => c.Sad);
        if (sadScore > 0.1f && sadScore > (features.Sadness ?? 0f))
            features.Sadness = sadScore;
    }

    private void ValidateGenreModelFiles()
    {
        // The models the ONNX genre path actually loads (the DiscogsEffnet embedding, the
        // MTG-Jamendo classifier head) plus the Discogs-400 label JSON — not the unused .pb
        // TensorFlow checkpoints the legacy Essentia path referenced.
        var expected = new[]
        {
            Path.Combine("Tools", "Essentia", "models", "discogs-effnet-bsdynamic-1.onnx"),
            Path.Combine("Tools", "Essentia", "models", "mtg_jamendo_genre-discogs-effnet-1.onnx"),
            Path.Combine("Tools", "Essentia", "models", "genre_discogs400-discogs-effnet-1.json"),
        };

        var missing = expected
            .Select(p => Path.Combine(AppContext.BaseDirectory, p))
            .Where(p => !File.Exists(p))
            .ToList();

        if (missing.Count > 0)
        {
            _logger.LogWarning("[AudioAnalysis] Genre model files missing: {Missing}", string.Join(" | ", missing));
        }
        else
        {
            _logger.LogInformation("[AudioAnalysis] Genre models detected (DiscogsEffnet embedding + MTG-Jamendo head + Discogs-400 labels).");
        }
    }
}
