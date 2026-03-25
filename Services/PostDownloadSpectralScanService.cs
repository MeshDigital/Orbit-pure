using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Background service that automatically runs spectral integrity analysis on every
/// completed download and surfaces the verdict back through the UI.
///
/// Pipeline position
/// -----------------
///   Download completes
///       ↓  DownloadManager publishes TrackStateChangedEvent(Completed)
///       ↓  THIS SERVICE listens
///       ↓  Runs IAudioIntegrityService.AnalyseAsync() on the resolved file path
///       ↓  Writes IsTranscoded, FrequencyCutoff, Integrity, QualityDetails to the database
///       ↓  Publishes TrackMetadataUpdatedEvent
///       ↓  UnifiedTrackViewModel.OnMetadataUpdated() picks it up
///       ↓  Spectral verdict badge in StandardTrackRow updates immediately
///
/// The analysis runs on a dedicated background thread so it never blocks the UI or
/// the download engine.  Each file gets at most one scan per application session
/// (tracked via a concurrent dictionary to avoid redundant work on retries).
/// </summary>
public sealed class PostDownloadSpectralScanService : IDisposable
{
    // Tracks file paths already scanned in this session to prevent redundant analysis.
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _scannedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly System.Collections.Generic.HashSet<string> LosslessExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".aiff", ".aif", ".ape", ".wv" };

    private readonly IAudioIntegrityService _integrityService;
    private readonly DatabaseService _databaseService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<PostDownloadSpectralScanService> _logger;
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();

    public PostDownloadSpectralScanService(
        IAudioIntegrityService integrityService,
        DatabaseService databaseService,
        IEventBus eventBus,
        ILogger<PostDownloadSpectralScanService> logger)
    {
        _integrityService = integrityService;
        _databaseService = databaseService;
        _eventBus = eventBus;
        _logger = logger;

        // Subscribe to download-completion events.  Fire-and-forget: the analysis is
        // CPU-intensive and runs on a dedicated thread pool thread.
        var subscription = _eventBus.GetEvent<TrackStateChangedEvent>()
            .Subscribe(evt =>
            {
                if (evt.State == PlaylistTrackState.Completed)
                    _ = ScanAsync(evt);
            });
        _disposables.Add(subscription);
    }

    // ── core analysis ─────────────────────────────────────────────────────────

    private async Task ScanAsync(TrackStateChangedEvent evt)
    {
        try
        {
            // Resolve the PlaylistTrackEntity using the track hash + project ID.
            // TrackGlobalId is the TrackUniqueHash; ProjectId is the PlaylistId.
            var entity = await _databaseService.GetPlaylistTrackByHashAsync(
                evt.ProjectId, evt.TrackGlobalId);

            if (entity == null)
            {
                _logger.LogDebug("Spectral scan skipped: track {Hash} not found in project {Project}",
                    evt.TrackGlobalId, evt.ProjectId);
                return;
            }

            var filePath = entity.ResolvedFilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                _logger.LogDebug("Spectral scan skipped: no resolved file for {Hash}", evt.TrackGlobalId);
                return;
            }

            // Only scan lossless formats — MP3/AAC are always lossy by definition.
            var ext = Path.GetExtension(filePath);
            if (!LosslessExtensions.Contains(ext))
                return;

            // De-duplicate: skip if already scanned in this session.
            if (!_scannedPaths.TryAdd(filePath, 0))
            {
                _logger.LogDebug("Spectral scan skipped: already scanned '{Path}' this session", filePath);
                return;
            }

            _logger.LogInformation("Spectral scan starting for '{Title}' — {Path}",
                entity.Title, filePath);

            var result = await _integrityService.AnalyseAsync(filePath);

            // ── map verdict to storage fields ──────────────────────────────────
            var isTranscoded = !result.IsGenuineLossless &&
                               result.Verdict != AudioAuthenticityVerdict.Unknown;

            var integrityLevel = result.Verdict switch
            {
                AudioAuthenticityVerdict.GenuineLossless         => IntegrityLevel.Gold,
                AudioAuthenticityVerdict.TranscodedHighBitrate   => IntegrityLevel.Suspicious,
                AudioAuthenticityVerdict.TranscodedMediumBitrate => IntegrityLevel.Suspicious,
                AudioAuthenticityVerdict.TranscodedLowBitrate    => IntegrityLevel.Suspicious,
                _                                                => IntegrityLevel.None
            };

            var cutoffKhz = result.SpectralCutoffHz > 0
                ? $"{result.SpectralCutoffHz / 1000.0:F1} kHz"
                : "unknown";

            // QualityDetails is read by UnifiedTrackViewModel for the forensic HUD and tooltip.
            var qualityDetails = result.Verdict == AudioAuthenticityVerdict.Unknown
                ? result.Reason
                : $"{result.Verdict} | cutoff: {cutoffKhz} | confidence: {result.Confidence:P0}";

            var frequencyCutoffHz = result.SpectralCutoffHz > 0
                ? (int?)result.SpectralCutoffHz
                : null;

            // dBFS values are negative by definition — use a sentinel of −120.0 (defined as
            // the floor in ComputeDynamics) to detect "not computed" vs. a real near-zero measurement.
            const double NoDataSentinel = -120.0;

            // ── persist all spectral forensics data to database ────────────────
            await _databaseService.UpdateSpectralVerdictAsync(
                entity.Id,
                isTranscoded,
                frequencyCutoffHz,
                integrityLevel,
                qualityDetails,
                sampleRateHz:     result.FileSampleRateHz > 0    ? result.FileSampleRateHz : null,
                bitDepth:         result.FileBitDepth > 0        ? result.FileBitDepth : null,
                rolloffSteepness: result.RolloffSteepnessDpkHz > 0 ? result.RolloffSteepnessDpkHz : null,
                midBandEnergy:    result.MidBandEnergyDbfs > NoDataSentinel ? result.MidBandEnergyDbfs : null,
                highBandEnergy:   result.HighBandEnergyDbfs > NoDataSentinel ? result.HighBandEnergyDbfs : null,
                rmsDbfs:          result.RmsLevelDbfs > NoDataSentinel  ? result.RmsLevelDbfs : null,
                crestFactorDb:    result.CrestFactorDb > 0              ? result.CrestFactorDb : null,
                noiseFloorDbfs:   result.NoiseFloorDbfs > NoDataSentinel ? result.NoiseFloorDbfs : null);

            // ── notify the ViewModel so the badge refreshes immediately ────────
            _eventBus.Publish(new TrackMetadataUpdatedEvent(evt.TrackGlobalId));

            _logger.LogInformation(
                "Spectral verdict for '{Title}': {Verdict} (cutoff: {Cutoff}, conf: {Conf:P0})",
                entity.Title, result.Verdict, cutoffKhz, result.Confidence);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Spectral scan failed for track {Hash}", evt.TrackGlobalId);
        }
    }

    public void Dispose() => _disposables.Dispose();
}
