using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Background job that orchestrates full structural audio analysis and auto-cue generation for a single track.
///
/// Workflow
/// ========
/// 1. Look up the track's stored <see cref="AudioFeaturesEntity"/> (BPM, duration, energy curve).
/// 2. Run the <see cref="StructuralAnalysisEngine"/> to detect phrase boundaries and drops.
/// 3. Use <see cref="CueGenerationService"/> to map the results into <see cref="CuePointEntity"/> objects.
/// 4. Persist the cue points and publish a <see cref="TrackStructureAnalysisCompletedEvent"/>.
///
/// If any step fails the error is logged and a failure event is published – the job does NOT throw.
/// </summary>
public sealed class AnalyzeTrackStructureJob
{
    private readonly DatabaseService _databaseService;
    private readonly CueGenerationService _cueGenerationService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AnalyzeTrackStructureJob> _logger;

    public AnalyzeTrackStructureJob(
        DatabaseService databaseService,
        CueGenerationService cueGenerationService,
        IEventBus eventBus,
        ILogger<AnalyzeTrackStructureJob> logger)
    {
        _databaseService = databaseService;
        _cueGenerationService = cueGenerationService;
        _eventBus = eventBus;
        _logger = logger;
    }

    /// <summary>
    /// Executes the full structural analysis pipeline for the specified track.
    /// </summary>
    /// <param name="trackUniqueHash">Content hash of the track to analyse.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task ExecuteAsync(string trackUniqueHash, CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("[AnalyzeTrackStructureJob] Starting structural analysis for track {Hash}", trackUniqueHash);

        try
        {
            // Step 1: Fetch audio features from the database
            var features = await _databaseService.GetAudioFeaturesByHashAsync(trackUniqueHash);

            if (features == null)
            {
                _logger.LogWarning(
                    "[AnalyzeTrackStructureJob] No audio features found for track {Hash}. Skipping.", trackUniqueHash);
                PublishCompleted(trackUniqueHash, success: false, error: "No audio features available.");
                return;
            }

            if (features.Bpm <= 0)
            {
                _logger.LogWarning(
                    "[AnalyzeTrackStructureJob] BPM is 0 for track {Hash}. Skipping drop detection.", trackUniqueHash);
            }

            // Step 2: Deserialise the pre-computed energy curve (stored as JSON in AudioFeaturesEntity)
            IReadOnlyList<float>? energyCurve = null;
            if (!string.IsNullOrWhiteSpace(features.EnergyCurveJson) && features.EnergyCurveJson != "[]")
            {
                try
                {
                    energyCurve = JsonSerializer.Deserialize<List<float>>(features.EnergyCurveJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AnalyzeTrackStructureJob] Could not deserialise energy curve for {Hash}", trackUniqueHash);
                }
            }

            // Step 3: Run the structural analysis engine
            var analysisResult = StructuralAnalysisEngine.Analyze(
                bpm: features.Bpm,
                durationSeconds: features.TrackDuration > 0 ? features.TrackDuration : 0,
                energyCurve: energyCurve);

            _logger.LogDebug(
                "[AnalyzeTrackStructureJob] Analysis complete: {PhraseBoundaries} phrase boundaries, {Drops} drops detected",
                analysisResult.PhraseBoundaries.Count, analysisResult.Drops.Count);

            // Step 4: Generate and persist cue points
            var cues = await _cueGenerationService.GenerateDefaultCuesAsync(
                trackUniqueHash, analysisResult, cancellationToken);

            // Step 5: Mark structural analysis version on the features entity
            features.StructuralVersion += 1;
            await _databaseService.UpdateAudioFeaturesAsync(features);

            // Step 6: Publish completion event
            PublishCompleted(trackUniqueHash, success: true);

            _logger.LogInformation(
                "[AnalyzeTrackStructureJob] Finished for track {Hash}: {CueCount} cue points generated.",
                trackUniqueHash, cues.Count);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("[AnalyzeTrackStructureJob] Cancelled for track {Hash}", trackUniqueHash);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[AnalyzeTrackStructureJob] Failed for track {Hash}", trackUniqueHash);
            PublishCompleted(trackUniqueHash, success: false, error: ex.Message);
        }
    }

    private void PublishCompleted(string trackUniqueHash, bool success, string? error = null)
    {
        _eventBus.Publish(new TrackStructureAnalysisCompletedEvent(trackUniqueHash, success, error));
    }
}
