using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.AudioAnalysis;
using SLSKDONET.Services.Embeddings;

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
    private readonly IPhraseAlignmentService _phraseAlignmentService;
    private readonly IEmbeddingExtractionService _embeddingExtractionService;
    private readonly EnergyAnalysisService _energyAnalysisService;
    private readonly IEdmFormerService? _edmFormer;
    private readonly IEventBus _eventBus;
    private readonly ILogger<AnalyzeTrackStructureJob> _logger;

    public AnalyzeTrackStructureJob(
        DatabaseService databaseService,
        CueGenerationService cueGenerationService,
        IPhraseAlignmentService phraseAlignmentService,
        IEmbeddingExtractionService embeddingExtractionService,
        EnergyAnalysisService energyAnalysisService,
        IEventBus eventBus,
        ILogger<AnalyzeTrackStructureJob> logger,
        IEdmFormerService? edmFormerService = null)
    {
        _databaseService = databaseService;
        _cueGenerationService = cueGenerationService;
        _phraseAlignmentService = phraseAlignmentService;
        _embeddingExtractionService = embeddingExtractionService;
        _energyAnalysisService = energyAnalysisService;
        _edmFormer = edmFormerService;
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
                "[AnalyzeTrackStructureJob] Analysis complete: {PhraseBoundaries} phrase boundaries, {Drops} drops detected, {Sections} sections inferred",
                analysisResult.PhraseBoundaries.Count, analysisResult.Drops.Count, analysisResult.Sections.Count);

            // Step 4: Snap raw structure into genre-aware sections and persist section embeddings.
            var rawBoundaries = BuildRawBoundaries(analysisResult);
            var alignedPhrases = await _phraseAlignmentService.AlignPhrasesAsync(
                rawBoundaries,
                features.Bpm > 0 ? features.Bpm : analysisResult.Bpm,
                !string.IsNullOrWhiteSpace(features.DetectedSubGenre) ? features.DetectedSubGenre : features.ElectronicSubgenre,
                trackUniqueHash,
                cancellationToken);

            var sections = alignedPhrases.Count > 0
                ? await EnrichAlignedPhrasesAsync(alignedPhrases, trackUniqueHash, features, analysisResult, cancellationToken)
                : await BuildPhraseEntitiesAsync(trackUniqueHash, features, analysisResult, cancellationToken);

            // Bridge the real rule-based phrase analysis into PhraseSegmentsJson. This is the
            // same field Cue Forge's phrase map and GenerateDefaultCuesAsync's ML path read,
            // but it was previously only ever populated by the optional EDMFormer microservice
            // (Step 5.5 below), which requires a user to manually run a separate Python service.
            // Without this bridge, phrase-aware cue generation and the Cue Forge phrase map were
            // silently empty for everyone who hasn't set EDMFormer up — even though this same
            // rule-based structural analysis already ran for every track and produced real data,
            // just under a different table (TrackPhrases) that nothing downstream consulted.
            if (sections.Count >= 2 && (string.IsNullOrWhiteSpace(features.PhraseSegmentsJson) || features.PhraseSegmentsJson == "[]"))
            {
                features.PhraseSegmentsJson = JsonSerializer.Serialize(ToPhraseSegments(sections, features.Bpm));
            }

            var energyProfile = _energyAnalysisService.BuildEnergyProfile(
                analysisResult.EnergyCurve,
                analysisResult.EnergyWindowSeconds,
                sections);
            ApplyEnergyProfile(features, sections, energyProfile);

            if (sections.Count > 0)
                await _databaseService.SavePhrasesAsync(sections);

            // Step 5: Generate and persist cue points
            // Use EDMFormer ML phrase segments when available, otherwise heuristic
            IReadOnlyList<PhraseSegment>? phraseSegments = null;
            if (!string.IsNullOrWhiteSpace(features.PhraseSegmentsJson) && features.PhraseSegmentsJson != "[]")
            {
                try
                {
                    phraseSegments = JsonSerializer.Deserialize<List<PhraseSegment>>(features.PhraseSegmentsJson);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AnalyzeTrackStructureJob] Could not deserialize PhraseSegmentsJson for {Hash}", trackUniqueHash);
                }
            }

            var cues = await _cueGenerationService.GenerateDefaultCuesAsync(
                trackUniqueHash, analysisResult, phraseSegments, cancellationToken);

            // Step 5.5: EDMFormer ML phrase detection (optional — requires local Python service)
            if (_edmFormer?.IsAvailable == true)
            {
                try
                {
                    var audioPath = await _databaseService.GetLocalFilePathByHashAsync(trackUniqueHash);
                    if (!string.IsNullOrEmpty(audioPath))
                    {
                        var edmSegments = await _edmFormer.AnalyzeAsync(audioPath, cancellationToken);
                        if (edmSegments is { Count: > 0 })
                        {
                            features.PhraseSegmentsJson = JsonSerializer.Serialize(edmSegments);
                            _logger.LogInformation(
                                "[AnalyzeTrackStructureJob] EDMFormer produced {n} phrase segments for {Hash}",
                                edmSegments.Count, trackUniqueHash);
                        }
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[AnalyzeTrackStructureJob] EDMFormer call failed for {Hash}, continuing without ML phrases", trackUniqueHash);
                }
            }

            // Step 6: Mark structural analysis version on the features entity
            features.StructuralVersion += 1;
            await _databaseService.UpdateAudioFeaturesAsync(features);

            // Step 6.5: Sync the track embedding for Similarity search
            try
            {
                await _embeddingExtractionService.SyncEmbeddingAsync(trackUniqueHash, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[AnalyzeTrackStructureJob] Failed to sync embedding for {Hash}", trackUniqueHash);
            }

            // Step 7: Publish completion event
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

    private List<RawBoundary> BuildRawBoundaries(StructuralAnalysisResult analysisResult)
    {
        if (analysisResult.Sections.Count > 0)
        {
            return analysisResult.Sections
                .OrderBy(s => s.OrderIndex)
                .Select(s => new RawBoundary
                {
                    StartTimeSeconds = s.StartSeconds,
                    EndTimeSeconds = s.EndSeconds,
                    EnergyLevel = s.EnergyLevel,
                    Confidence = s.Confidence,
                    Label = s.Label,
                    SuggestedType = s.Type,
                })
                .ToList();
        }

        return analysisResult.PhraseBoundaries
            .Select((boundary, index) => new RawBoundary
            {
                StartTimeSeconds = boundary,
                EndTimeSeconds = index < analysisResult.PhraseBoundaries.Count - 1 ? analysisResult.PhraseBoundaries[index + 1] : analysisResult.DurationSeconds,
                EnergyLevel = EstimateEnergyAt(boundary, analysisResult),
                Confidence = 0.45f,
                Label = $"Phrase {index + 1}",
                SuggestedType = InferTypeFromPosition(boundary, analysisResult.DurationSeconds)
            })
            .ToList();
    }

    /// <summary>
    /// Converts rule-based structural sections into the same <see cref="PhraseSegment"/> shape
    /// EDMFormer produces, so both writers of PhraseSegmentsJson are interchangeable to readers.
    /// </summary>
    private static List<PhraseSegment> ToPhraseSegments(IReadOnlyList<TrackPhraseEntity> sections, float bpm)
    {
        double beatSeconds = bpm > 0 ? 60.0 / bpm : 0;
        double barSeconds = beatSeconds * 4;

        return sections
            .OrderBy(s => s.OrderIndex)
            .Select(s => new PhraseSegment
            {
                Label = string.IsNullOrWhiteSpace(s.Label) ? s.Type.ToString() : s.Label,
                Start = s.StartTimeSeconds,
                Duration = s.DurationSeconds,
                Bars = barSeconds > 0 ? (int)Math.Round(s.DurationSeconds / barSeconds) : 0,
                Beats = beatSeconds > 0 ? (int)Math.Round(s.DurationSeconds / beatSeconds) : 0,
                Confidence = Math.Clamp(s.Confidence, 0f, 1f),
            })
            .ToList();
    }

    private async Task<List<TrackPhraseEntity>> BuildPhraseEntitiesAsync(
        string trackUniqueHash,
        AudioFeaturesEntity features,
        StructuralAnalysisResult analysisResult,
        CancellationToken cancellationToken)
    {
        if (analysisResult.Sections.Count == 0)
            return new List<TrackPhraseEntity>();

        var phrases = analysisResult.Sections
            .OrderBy(s => s.OrderIndex)
            .Select(section => new TrackPhraseEntity
            {
                TrackUniqueHash = trackUniqueHash,
                Type = section.Type,
                StartTimeSeconds = (float)section.StartSeconds,
                EndTimeSeconds = (float)section.EndSeconds,
                EnergyLevel = Math.Clamp(section.EnergyLevel, 0f, 1f),
                Confidence = Math.Clamp(section.Confidence, 0f, 1f),
                OrderIndex = section.OrderIndex,
                Label = section.Label,
            })
            .ToList();

        return await EnrichAlignedPhrasesAsync(phrases, trackUniqueHash, features, analysisResult, cancellationToken);
    }

    private async Task<List<TrackPhraseEntity>> EnrichAlignedPhrasesAsync(
        IReadOnlyList<TrackPhraseEntity> phrases,
        string trackUniqueHash,
        AudioFeaturesEntity features,
        StructuralAnalysisResult analysisResult,
        CancellationToken cancellationToken)
    {
        var enriched = new List<TrackPhraseEntity>(phrases.Count);

        foreach (var phrase in phrases.OrderBy(p => p.OrderIndex))
        {
            cancellationToken.ThrowIfCancellationRequested();

            var localWindows = SliceEnergyWindows(phrase.StartTimeSeconds, phrase.EndTimeSeconds, analysisResult);
            try
            {
                var embedding = await _embeddingExtractionService.ExtractSectionEmbeddingAsync(
                    features,
                    phrase.Type,
                    phrase.StartTimeSeconds,
                    phrase.EndTimeSeconds,
                    localWindows,
                    cancellationToken);

                phrase.TrackUniqueHash = trackUniqueHash;
                phrase.SectionEmbeddingJson = embedding is { Length: > 0 } ? JsonSerializer.Serialize(embedding) : phrase.SectionEmbeddingJson;
                phrase.EmbeddingMagnitude = ComputeMagnitude(embedding ?? Array.Empty<float>());
                phrase.EmbeddingModel = embedding is { Length: > 0 } ? "orbit-section-v2" : phrase.EmbeddingModel;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "[AnalyzeTrackStructureJob] Section embedding extraction failed for {Hash} {PhraseType} {Start:F1}-{End:F1}; continuing without section vector.",
                    trackUniqueHash,
                    phrase.Type,
                    phrase.StartTimeSeconds,
                    phrase.EndTimeSeconds);
            }

            enriched.Add(phrase);
        }

        return enriched;
    }

    private static IReadOnlyList<float> SliceEnergyWindows(float startSeconds, float endSeconds, StructuralAnalysisResult analysisResult)
    {
        if (analysisResult.EnergyCurve.Count == 0)
            return Array.Empty<float>();

        double windowSize = analysisResult.EnergyWindowSeconds > 0 ? analysisResult.EnergyWindowSeconds : 1.0;
        int startIndex = Math.Clamp((int)Math.Floor(startSeconds / windowSize), 0, analysisResult.EnergyCurve.Count - 1);
        int endIndex = Math.Clamp((int)Math.Ceiling(endSeconds / windowSize), startIndex + 1, analysisResult.EnergyCurve.Count);

        return analysisResult.EnergyCurve.Skip(startIndex).Take(Math.Max(1, endIndex - startIndex)).ToArray();
    }

    private void ApplyEnergyProfile(
        AudioFeaturesEntity features,
        IReadOnlyList<TrackPhraseEntity> sections,
        EnergyProfile energyProfile)
    {
        features.Energy = energyProfile.OverallEnergy;
        features.EnergyScore = energyProfile.OverallEnergyScore;
        features.SegmentedEnergyJson = JsonSerializer.Serialize(
            energyProfile.Segments.OrderBy(s => s.OrderIndex).Select(s => s.EnergyScore).ToList());

        if (string.IsNullOrWhiteSpace(features.EnergyCurveJson) || features.EnergyCurveJson == "[]")
        {
            features.EnergyCurveJson = JsonSerializer.Serialize(
                energyProfile.Segments.OrderBy(s => s.OrderIndex).Select(s => s.AverageEnergy).ToList());
        }

        foreach (var phrase in sections)
        {
            var segment = energyProfile.Segments.FirstOrDefault(s => s.OrderIndex == phrase.OrderIndex);
            if (segment is null)
                continue;

            phrase.EnergyLevel = segment.AverageEnergy;
            phrase.Label = string.IsNullOrWhiteSpace(phrase.Label) ? segment.Label : phrase.Label;
        }
    }

    private static float EstimateEnergyAt(double timeSeconds, StructuralAnalysisResult analysisResult)
    {
        if (analysisResult.EnergyCurve.Count == 0)
            return 0.5f;

        double windowSize = analysisResult.EnergyWindowSeconds > 0 ? analysisResult.EnergyWindowSeconds : 1.0;
        int index = Math.Clamp((int)Math.Round(timeSeconds / windowSize), 0, analysisResult.EnergyCurve.Count - 1);
        return analysisResult.EnergyCurve[index];
    }

    private static PhraseType InferTypeFromPosition(double timeSeconds, double durationSeconds)
    {
        if (durationSeconds <= 0)
            return PhraseType.Unknown;

        double ratio = timeSeconds / durationSeconds;
        if (ratio <= 0.15d) return PhraseType.Intro;
        if (ratio >= 0.85d) return PhraseType.Outro;
        if (ratio >= 0.55d && ratio <= 0.75d) return PhraseType.Drop;
        return PhraseType.Build;
    }

    private static float ComputeMagnitude(float[] values)
    {
        if (values.Length == 0)
            return 0f;

        double sum = 0d;
        foreach (var value in values)
            sum += value * value;

        return (float)Math.Sqrt(sum);
    }

    private void PublishCompleted(string trackUniqueHash, bool success, string? error = null)
    {
        _eventBus.Publish(new TrackStructureAnalysisCompletedEvent(trackUniqueHash, success, error));
    }
}
