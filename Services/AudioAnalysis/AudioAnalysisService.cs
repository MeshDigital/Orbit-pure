using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Data.Essentia;

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
        DatabaseService db,
        ILogger<AudioAnalysisService> logger)
    {
        _ingestion = ingestion;
        _waveform  = waveformExtraction;
        _bpm       = bpmDetection;
        _key       = keyDetection;
        _beatgrid  = beatgridDetection;
        _cues      = cuePointDetection;
        _essentia  = essentiaRunner;
        _db        = db;
        _logger    = logger;
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
                _logger.LogWarning(ex, "[AudioAnalysis] FFmpeg decode failed for {File}", filePath);
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

                    _logger.LogDebug("[AudioAnalysis] Essentia OK — BPM={Bpm} Key={Key}",
                        features.Bpm, features.Key);
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
                catch { /* best-effort cleanup */ }
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
}
