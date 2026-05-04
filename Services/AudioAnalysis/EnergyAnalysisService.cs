using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Computes Mixed In Key-style track and phrase energy values from audio or precomputed energy windows.
/// The service prefers deterministic, normalized output so the same track always maps to the same score.
/// </summary>
public sealed class EnergyAnalysisService
{
    private readonly ILogger<EnergyAnalysisService> _logger;

    public EnergyAnalysisService(ILogger<EnergyAnalysisService> logger)
    {
        _logger = logger;
    }

    /// <summary>
    /// Builds an <see cref="EnergyProfile"/> from an existing set of normalized energy windows.
    /// This is the fast path used by the structural analysis pipeline after waveform / Essentia extraction.
    /// </summary>
    public EnergyProfile BuildEnergyProfile(
        IReadOnlyList<float>? energyWindows,
        double windowSeconds = 1.0,
        IReadOnlyList<TrackPhraseEntity>? phrases = null)
    {
        if (energyWindows == null || energyWindows.Count == 0)
            return EnergyProfile.Neutral;

        var sanitized = energyWindows
            .Select(v => float.IsNaN(v) || float.IsInfinity(v) ? 0f : Math.Clamp(v, 0f, 1f))
            .ToArray();

        if (sanitized.Length == 0)
            return EnergyProfile.Neutral;

        var normalized = Normalize(sanitized);
        var average = normalized.Average();
        var peak = normalized.Max();
        var p80 = Percentile(normalized, 0.80);
        var overall = Math.Clamp((average * 0.55f) + (p80 * 0.30f) + (peak * 0.15f), 0f, 1f);
        var segments = BuildSegments(normalized, windowSeconds, phrases);

        return new EnergyProfile(overall, ToEnergyScore(overall), segments);
    }

    /// <summary>
    /// Computes a normalized energy profile directly from an audio file using RMS windows.
    /// This is a fallback path for analysis stages where a persisted energy curve is not yet available.
    /// </summary>
    public Task<EnergyProfile> ComputeTrackEnergyAsync(
        string filePath,
        IReadOnlyList<TrackPhraseEntity>? phrases = null,
        CancellationToken cancellationToken = default)
        => ComputeTrackEnergyLevelAsync(filePath, phrases, cancellationToken);

    /// <summary>
    /// Backward-compatible entry point retained for callers that still use the older naming.
    /// </summary>
    public async Task<EnergyProfile> ComputeTrackEnergyLevelAsync(
        string filePath,
        IReadOnlyList<TrackPhraseEntity>? phrases = null,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            _logger.LogWarning("[EnergyAnalysis] File not found for energy analysis: {FilePath}", filePath);
            return EnergyProfile.Neutral;
        }

        try
        {
            var windows = await Task.Run(() => ReadRmsWindows(filePath, cancellationToken), cancellationToken)
                .ConfigureAwait(false);
            return BuildEnergyProfile(windows, 1.0, phrases);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[EnergyAnalysis] Failed to compute energy profile for {FilePath}", filePath);
            return EnergyProfile.Neutral;
        }
    }

    private static List<EnergySegment> BuildSegments(
        IReadOnlyList<float> normalizedWindows,
        double windowSeconds,
        IReadOnlyList<TrackPhraseEntity>? phrases)
    {
        if (phrases == null || phrases.Count == 0)
            return new List<EnergySegment>();

        var segments = new List<EnergySegment>(phrases.Count);

        foreach (var phrase in phrases.OrderBy(p => p.OrderIndex))
        {
            int startIndex = Math.Clamp((int)Math.Floor(phrase.StartTimeSeconds / Math.Max(0.01, windowSeconds)), 0, Math.Max(0, normalizedWindows.Count - 1));
            int endIndex = Math.Clamp((int)Math.Ceiling(phrase.EndTimeSeconds / Math.Max(0.01, windowSeconds)), startIndex + 1, normalizedWindows.Count);

            var slice = normalizedWindows.Skip(startIndex).Take(Math.Max(1, endIndex - startIndex)).ToArray();
            var average = slice.Length > 0 ? slice.Average() : 0.5f;

            segments.Add(new EnergySegment(
                phrase.OrderIndex,
                phrase.Label ?? phrase.Type.ToString(),
                phrase.StartTimeSeconds,
                phrase.EndTimeSeconds,
                average,
                ToEnergyScore(average)));
        }

        return segments;
    }

    private static List<float> ReadRmsWindows(string filePath, CancellationToken cancellationToken)
    {
        using var reader = new NAudio.Wave.AudioFileReader(filePath);

        int sampleRate = Math.Max(1, reader.WaveFormat.SampleRate);
        int channels = Math.Max(1, reader.WaveFormat.Channels);
        int samplesPerWindow = sampleRate * channels;
        var buffer = new float[samplesPerWindow];
        var windows = new List<float>();

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
                break;

            double sumSquares = 0d;
            for (int i = 0; i < read; i++)
                sumSquares += buffer[i] * buffer[i];

            var rms = read > 0 ? (float)Math.Sqrt(sumSquares / read) : 0f;
            windows.Add(rms);
        }

        return windows;
    }

    private static float[] Normalize(IReadOnlyList<float> values)
    {
        if (values.Count == 0)
            return Array.Empty<float>();

        var min = values.Min();
        var max = values.Max();
        var range = max - min;

        if (range < 0.0001f)
            return values.Select(_ => 0.5f).ToArray();

        return values.Select(v => Math.Clamp((v - min) / range, 0f, 1f)).ToArray();
    }

    private static float Percentile(IReadOnlyList<float> values, double percentile)
    {
        if (values.Count == 0)
            return 0.5f;

        var ordered = values.OrderBy(v => v).ToArray();
        int index = Math.Clamp((int)Math.Round((ordered.Length - 1) * percentile), 0, ordered.Length - 1);
        return ordered[index];
    }

    private static int ToEnergyScore(float normalizedEnergy)
        => Math.Clamp((int)Math.Round(1 + (Math.Clamp(normalizedEnergy, 0f, 1f) * 9f)), 1, 10);
}

/// <summary>
/// Whole-track energy summary plus per-section values used by the creative cockpit and flow builder.
/// </summary>
public sealed record EnergyProfile(
    float OverallEnergy,
    int OverallEnergyScore,
    IReadOnlyList<EnergySegment> Segments)
{
    public static EnergyProfile Neutral { get; } = new(0.5f, 5, Array.Empty<EnergySegment>());
}

/// <summary>
/// Energy readout for a structural section such as Intro, Build, Drop, or Outro.
/// </summary>
public sealed record EnergySegment(
    int OrderIndex,
    string Label,
    float StartSeconds,
    float EndSeconds,
    float AverageEnergy,
    int EnergyScore);
