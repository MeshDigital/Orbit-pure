using System;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Probes every completed download for its real audio duration and persists it.
///
/// Duration was, in practice, the least reliable piece of basic track metadata: Bitrate and
/// Format get set from the Soulseek search result and the filename the moment a download starts
/// (DownloadManager.DownloadFileAsync), but nothing ever probed the actual downloaded file for
/// its real duration afterward — a direct DB check found ~70% of library tracks with a real file
/// on disk had a NULL/zero duration, versus <10% for bitrate and ~0% for format. TagLib-based
/// duration extraction already existed, but only on the manual folder-scan import path
/// (LibraryFolderScannerService.CreateLibraryEntry), which most tracks — downloaded via Soulseek,
/// not imported from a folder — never go through.
///
/// Deliberately NOT built as an extension of PostDownloadSpectralScanService: that service is
/// gated behind AppConfig.EnableVbrFraudDetection and only scans lossless formats, neither of
/// which is appropriate for basic duration capture, which should apply unconditionally to every
/// format. Runs unconditionally, format-agnostic, and skips files that already have a duration.
/// </summary>
public sealed class PostDownloadDurationCaptureService : IDisposable
{
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _probedPaths =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly DatabaseService _databaseService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<PostDownloadDurationCaptureService> _logger;
    private readonly System.Reactive.Disposables.CompositeDisposable _disposables = new();

    public PostDownloadDurationCaptureService(
        DatabaseService databaseService,
        IEventBus eventBus,
        ILogger<PostDownloadDurationCaptureService> logger)
    {
        _databaseService = databaseService;
        _eventBus = eventBus;
        _logger = logger;

        var subscription = _eventBus.GetEvent<TrackStateChangedEvent>()
            .Subscribe(evt =>
            {
                // See PostDownloadSpectralScanService's identical guard: WasAlreadyPresent means
                // this is the same file re-linked, not a fresh transfer, so it's already been
                // probed once and re-probing on every playlist resync is wasted work.
                if (evt.State == PlaylistTrackState.Completed && !evt.WasAlreadyPresent)
                    _ = ProbeAsync(evt);
            });
        _disposables.Add(subscription);
    }

    private async Task ProbeAsync(TrackStateChangedEvent evt)
    {
        try
        {
            var entity = await _databaseService.GetPlaylistTrackByHashAsync(evt.ProjectId, evt.TrackGlobalId);
            if (entity == null) return;

            var filePath = entity.ResolvedFilePath;
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

            if (!_probedPaths.TryAdd(filePath, 0)) return; // already probed this session

            int durationSeconds;
            try
            {
                using var file = TagLib.File.Create(filePath);
                durationSeconds = (int)file.Properties.Duration.TotalSeconds;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Duration probe failed for '{Path}'", filePath);
                return;
            }

            if (durationSeconds <= 0) return;

            var canonicalMs = entity.CanonicalDuration;
            if (canonicalMs is not > 0)
            {
                // No canonical duration yet — backfill it (existing behaviour).
                await _databaseService.UpdateDurationAsync(evt.TrackGlobalId, durationSeconds);
                _eventBus.Publish(new TrackMetadataUpdatedEvent(evt.TrackGlobalId));

                _logger.LogInformation("Captured duration {Seconds}s for '{Title}'", durationSeconds, entity.Title);
                return;
            }

            // Canonical duration is already known (from search/Spotify metadata). Compare the real
            // file length: a >5% gap means this file is a slowed/sped edit, an excerpt, or a
            // mis-matched download. Surface it so the user isn't left wondering why the file's
            // actual length differs from the matched track (e.g. a "101 BPM" slowed mix of a
            // "174 BPM" track).
            var canonicalSeconds = canonicalMs.Value / 1000.0;
            var drift = Math.Abs(durationSeconds - canonicalSeconds) / canonicalSeconds;
            if (drift > 0.05)
            {
                _logger.LogWarning(
                    "Duration mismatch for '{Title}': file is {Actual}s but the matched track is {Canonical}s ({Drift:P0}) — likely a mix/edit or mis-matched download",
                    entity.Title, durationSeconds, canonicalSeconds, drift);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Duration capture failed for track {Hash}", evt.TrackGlobalId);
        }
    }

    public void Dispose() => _disposables.Dispose();
}
