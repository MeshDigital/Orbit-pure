using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Engine.Snapping;
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
    public float[] EnergyCurve { get; set; } = Array.Empty<float>();
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
    private readonly ILogger<AnalysisPipeline> _logger;

    public AnalysisPipeline(
        AudioIngestionPipeline ingestionPipeline,
        ILogger<AnalysisPipeline> logger)
    {
        _ingestionPipeline = ingestionPipeline ?? throw new ArgumentNullException(nameof(ingestionPipeline));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        
        _transientEngine = new TransientClusteringEngine();
        _harmonicTracker = new HarmonicPhaseTracker();
        _drumEngine = new DrumPatternFingerprintEngine();
        _energyNormalizer = new EnergyCurveNormalizer();
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

            _logger.LogInformation($"[AnalysisPipeline] Completed curation analysis. Found {transients.Count} transients, {harmonicResets.Count} harmonic resets, {drumMismatches.Count} pattern switches.");

            return new AnalysisPipelineResult
            {
                Bpm = estimatedBpm,
                DurationSeconds = duration,
                Transients = transients,
                HarmonicResets = harmonicResets,
                DrumSignatureChanges = drumMismatches,
                EnergyCurve = energyCurve
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
