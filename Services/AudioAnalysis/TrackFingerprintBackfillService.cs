using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// A10.1 non-UI hook for rebuilding fingerprints from current persisted analysis data.
/// Intended for test fixtures and small backfill runs.
/// </summary>
public sealed class TrackFingerprintBackfillService
{
    private readonly DatabaseService _databaseService;
    private readonly TrackFingerprintBuilderService _builder;
    private readonly TrackFingerprintStore _store;
    private readonly ILogger<TrackFingerprintBackfillService> _logger;

    public TrackFingerprintBackfillService(
        DatabaseService databaseService,
        TrackFingerprintBuilderService builder,
        TrackFingerprintStore store,
        ILogger<TrackFingerprintBackfillService> logger)
    {
        _databaseService = databaseService;
        _builder = builder;
        _store = store;
        _logger = logger;
    }

    public async Task<TrackFingerprint?> BuildForTrackAsync(string trackHash, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(trackHash))
            return null;

        var features = await _databaseService.GetAudioFeaturesByHashAsync(trackHash).ConfigureAwait(false);
        if (features is null)
        {
            _logger.LogWarning("TrackFingerprintBackfill: no audio features for {TrackHash}", trackHash);
            return null;
        }

        var phrases = await _databaseService.GetPhrasesByHashAsync(trackHash).ConfigureAwait(false);
        var fingerprint = _builder.Build(trackHash, features, essentiaOutput: null, phrases);
        await _store.SaveAsync(fingerprint, ct).ConfigureAwait(false);
        return fingerprint;
    }

    public async Task<int> BuildForTracksAsync(IEnumerable<string> trackHashes, CancellationToken ct = default)
    {
        var built = 0;
        foreach (var hash in trackHashes.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct())
        {
            ct.ThrowIfCancellationRequested();

            try
            {
                if (await BuildForTrackAsync(hash, ct).ConfigureAwait(false) is not null)
                    built++;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "TrackFingerprintBackfill: failed for {TrackHash}", hash);
            }
        }

        return built;
    }
}