using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Engine.Snapping;
using SLSKDONET.Models;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.AudioAnalysis;

namespace SLSKDONET.Engine.Analysis;

/// <summary>
/// Compiled result of the curation analysis pipeline.
/// </summary>
public sealed class AnalysisPipelineResult
{
    public float Bpm { get; set; }
    public double DurationSeconds { get; set; }
    public IReadOnlyList<TransientDataPoint> Transients { get; set; } = Array.Empty<TransientDataPoint>();
    public IReadOnlyList<double> HarmonicResets { get; set; } = Array.Empty<double>();
    public IReadOnlyList<double> DrumSignatureChanges { get; set; } = Array.Empty<double>();

    // ── Tier-1: RMS energy curve (1s windows, 3-tier normalized) ──────────
    public float[] EnergyCurve { get; set; } = Array.Empty<float>();

    // ── Tier-2: Spectral Flux Novelty (half-wave rectified STFT delta) ────
    // High values = sudden spectral change → build peaks and drop onsets.
    // Resolution: one value per hopSize/sampleRate seconds (~11ms at 512/44100).
    public float[] SpectralFluxNovelty { get; set; } = Array.Empty<float>();

    // ── Tier-3: Sub-bass energy curve (20–120 Hz, 0.5s windows) ──────────
    // Critical for DnB: bass dropouts before drops are the most stable drop signature.
    public float[] SubBassEnergyCurve { get; set; } = Array.Empty<float>();

    // ── Detected structural events (from new engines) ─────────────────────
    /// <summary>Timestamps where sub-bass energy drops to <25% of track mean (start of breakdown).</summary>
    public IReadOnlyList<double> SubBassDropoutTimestamps { get; set; } = Array.Empty<double>();

    /// <summary>Timestamps where sub-bass energy returns to >60% of track mean after a dropout (drop hit).</summary>
    public IReadOnlyList<double> SubBassReturnTimestamps { get; set; } = Array.Empty<double>();

    /// <summary>Spectral flux novelty peaks with build confirmation — high-confidence drop candidates.</summary>
    public IReadOnlyList<(double DropSeconds, double BuildStartSeconds, float Strength)> NoveltyDropSignatures { get; set; }
        = Array.Empty<(double, double, float)>();

    // ── Essentia AI signals (fed in from EssentiaOutput when available) ───
    /// <summary>0–1. From Essentia HighLevel voice_instrumental model. >0.7 = likely instrumental section.</summary>
    public float EssentiaInstrumentalProbability { get; set; } = 0.5f;

    /// <summary>0–1. From Essentia HighLevel mood_aggressive model. High = likely drop/build energy.</summary>
    public float EssentiaAggressiveProbability { get; set; } = 0f;

    /// <summary>0–1. From Essentia HighLevel danceability model.</summary>
    public float EssentiaDanceability { get; set; } = 0.5f;

    /// <summary>
    /// ML-grade structural phrase segments from EDMFormer (Intro/Build/Drop/Breakdown/Outro).
    /// Empty when the EDMFormer microservice is unavailable; populated otherwise.
    /// </summary>
    public IReadOnlyList<PhraseSegment> PhraseSegments { get; set; } = Array.Empty<PhraseSegment>();
}

/// <summary>
/// Coordinates the multi-tiered structural analysis pipeline.
/// </summary>
public sealed class AnalysisPipeline
{
    private readonly AudioIngestionPipeline _ingestionPipeline;
    private readonly TransientClusteringEngine _transientEngine;
    private readonly HarmonicPhaseTracker _harmonicTracker;
    private readonly DrumPatternFingerprintEngine _drumEngine;
    private readonly EnergyCurveNormalizer _energyNormalizer;
    private readonly SpectralFluxNoveltyEngine _noveltyEngine;
    private readonly SubBassDropoutEngine _subBassEngine;
    private readonly IEdmFormerService? _edmFormer;
    private readonly ILogger<AnalysisPipeline> _logger;

    public AnalysisPipeline(
        AudioIngestionPipeline ingestionPipeline,
        ILogger<AnalysisPipeline> logger,
        IEdmFormerService? edmFormerService = null)
    {
        _ingestionPipeline = ingestionPipeline ?? throw new ArgumentNullException(nameof(ingestionPipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _edmFormer = edmFormerService;

        _transientEngine = new TransientClusteringEngine();
        _harmonicTracker = new HarmonicPhaseTracker();
        _drumEngine = new DrumPatternFingerprintEngine();
        _energyNormalizer = new EnergyCurveNormalizer();
        _noveltyEngine = new SpectralFluxNoveltyEngine();
        _subBassEngine = new SubBassDropoutEngine();
    }

    /// <summary>
    /// Executes the full multi-tier analysis pipeline on the given audio file.
    /// </summary>
    public async Task<AnalysisPipelineResult> AnalyzeAsync(
        string filePath, 
        float estimatedBpm, 
        string genre = "General", 
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            throw new FileNotFoundException("Audio file not found.", filePath);

        _logger.LogInformation($"[AnalysisPipeline] Starting curation analysis on: {Path.GetFileName(filePath)} (BPM: {estimatedBpm}, Genre: {genre})");

        string? tempWav = null;
        try
        {
            // 1. Decode audio to temporary WAV file
            var source = new TrackAudioSource(filePath);
            tempWav = await _ingestionPipeline.DecodeToTempWavAsync(source, ct).ConfigureAwait(false);

            // 2. Read PCM float samples (interleaved)
            var (interleavedSamples, sampleRate, channels) = AudioIngestionPipeline.ReadPcmFloat(tempWav);
            if (interleavedSamples == null || interleavedSamples.Length == 0)
            {
                throw new InvalidOperationException("Failed to decode audio samples.");
            }

            // 3. Convert to Mono signal
            float[] monoSamples;
            if (channels > 1)
            {
                int numFrames = interleavedSamples.Length / channels;
                monoSamples = new float[numFrames];
                for (int i = 0; i < numFrames; i++)
                {
                    float sum = 0f;
                    for (int c = 0; c < channels; c++)
                    {
                        sum += interleavedSamples[i * channels + c];
                    }
                    monoSamples[i] = sum / channels;
                }
            }
            else
            {
                monoSamples = interleavedSamples;
            }

            double duration = (double)monoSamples.Length / sampleRate;

            // 4. In-Process Core DSP Pipeline Passes
            
            // Pass A: Transient Clustering
            _logger.LogDebug("[AnalysisPipeline] Running Transient Detection & K-Means clustering...");
            var transientTimes = _transientEngine.DetectTransients(monoSamples, sampleRate);
            var mfccs = transientTimes.Select(t => _transientEngine.ExtractMfccWindow(monoSamples, sampleRate, t)).ToList();
            var transients = _transientEngine.ClusterTransients(transientTimes, mfccs);

            // Pass B: Harmonic Phase Tracking (Chroma Vectors & Resets)
            _logger.LogDebug("[AnalysisPipeline] Running Harmonic Phase Tracking...");
            double harmonicHopSecs = 0.25;
            var chromaVectors = _harmonicTracker.ComputeChromaVectors(monoSamples, sampleRate, windowSeconds: 0.5, hopSeconds: harmonicHopSecs);
            var harmonicResets = _harmonicTracker.DetectHarmonicResets(chromaVectors, harmonicHopSecs);

            // Pass C: Drum Pattern Fingerprinting (HPSS Percussive extraction)
            _logger.LogDebug("[AnalysisPipeline] Running HPSS Percussive isolation...");
            double drumHopSecs = 0.02;
            var percussiveEnergy = _drumEngine.IsolatePercussiveSignal(monoSamples, sampleRate, frameSizeSecs: 0.04, hopSizeSecs: drumHopSecs);
            var drumMismatches = _drumEngine.DetectPatternMismatches(percussiveEnergy, drumHopSecs, estimatedBpm);

            // Pass D: Energy Curve Normalization (3-tier)
            _logger.LogDebug("[AnalysisPipeline] Normalizing 3-Tier Energy Curve...");
            // Generate a raw RMS energy curve to normalize (e.g. 1.0 second windows)
            double energyWindowSecs = 1.0;
            int energyWindowsCount = (int)Math.Ceiling(duration / energyWindowSecs);
            var rawEnergyCurve = new float[Math.Max(1, energyWindowsCount)];
            int samplesPerWindow = (int)(energyWindowSecs * sampleRate);

            for (int i = 0; i < rawEnergyCurve.Length; i++)
            {
                int startIdx = i * samplesPerWindow;
                int endIdx = Math.Min(monoSamples.Length, startIdx + samplesPerWindow);
                if (endIdx <= startIdx) continue;

                double sumSq = 0.0;
                for (int s = startIdx; s < endIdx; s++)
                {
                    sumSq += monoSamples[s] * monoSamples[s];
                }
                rawEnergyCurve[i] = (float)Math.Sqrt(sumSq / (endIdx - startIdx));
            }

            var energyCurve = _energyNormalizer.NormalizeEnergyCurve(rawEnergyCurve, estimatedBpm, energyWindowSecs, genre);

            // Pass E: Spectral Flux Novelty (EDM drop detection — replaces pure RMS delta)
            _logger.LogDebug("[AnalysisPipeline] Computing Spectral Flux Novelty curve...");
            var spectralFluxNovelty = _noveltyEngine.ComputeNoveltyFunction(monoSamples, sampleRate);
            var noveltyDropSignatures = _noveltyEngine.DetectDropSignatures(spectralFluxNovelty, sampleRate, 512);

            // Pass F: Sub-Bass Dropout Detection (DnB-specific drop signature)
            _logger.LogDebug("[AnalysisPipeline] Running Sub-Bass Dropout detection...");
            var subBassEnergyCurve = _subBassEngine.ComputeSubBassEnergyCurve(monoSamples, sampleRate);
            var (subBassDropouts, subBassReturns) = _subBassEngine.DetectDropoutEvents(subBassEnergyCurve);

            // Pass G: EDMFormer ML phrase detection (optional — requires local microservice)
            IReadOnlyList<PhraseSegment> phraseSegments = Array.Empty<PhraseSegment>();
            if (_edmFormer?.IsAvailable == true)
            {
                _logger.LogDebug("[AnalysisPipeline] Running EDMFormer phrase detection...");
                // Pass the original filePath — librosa handles format resampling internally
                var edm = await _edmFormer.AnalyzeAsync(filePath, ct).ConfigureAwait(false);
                if (edm is { Count: > 0 })
                {
                    phraseSegments = edm;
                    _logger.LogInformation("[AnalysisPipeline] EDMFormer detected {n} phrase segments", edm.Count);
                }
            }

            _logger.LogInformation(
                "[AnalysisPipeline] Completed. Transients={T}, HarmonicResets={H}, DrumSwitches={D}, " +
                "NoveltyDrops={N}, SubBassDropouts={SBD}, SubBassReturns={SBR}, Phrases={P}",
                transients.Count, harmonicResets.Count, drumMismatches.Count,
                noveltyDropSignatures.Count, subBassDropouts.Count, subBassReturns.Count, phraseSegments.Count);

            return new AnalysisPipelineResult
            {
                Bpm = estimatedBpm,
                DurationSeconds = duration,
                Transients = transients,
                HarmonicResets = harmonicResets,
                DrumSignatureChanges = drumMismatches,
                EnergyCurve = energyCurve,
                SpectralFluxNovelty = spectralFluxNovelty,
                SubBassEnergyCurve = subBassEnergyCurve,
                SubBassDropoutTimestamps = subBassDropouts,
                SubBassReturnTimestamps = subBassReturns,
                NoveltyDropSignatures = noveltyDropSignatures,
                PhraseSegments = phraseSegments,
            };
        }
        finally
        {
            if (tempWav != null && File.Exists(tempWav))
            {
                try { File.Delete(tempWav); } catch { /* Ignore cleanup issues */ }
            }
        }
    }
}
