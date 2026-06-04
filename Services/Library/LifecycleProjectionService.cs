using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Library;

public enum TrackLifecycleState
{
    Unknown = 0,
    DesiredDownload = 1,
    DownloadedAwaitingIndex = 2,
    Indexed = 3,
    StaleIndexed = 4,
}

public sealed record LifecycleMetrics(
    int PhysicalOnDisk,
    int IndexedCatalog,
    int StaleIndexed,
    int IngestionBacklog,
    int DesiredDownloads);

public interface ILifecycleProjectionService
{
    Task<LifecycleMetrics> ComputeMetricsAsync(CancellationToken cancellationToken = default);
    LifecycleMetrics ApplyFileIngestionQueued(LifecycleMetrics current);
    LifecycleMetrics ApplyFileIngestionCompleted(LifecycleMetrics current);
    LifecycleMetrics ApplyFileMissingDetected(LifecycleMetrics current);
    TrackLifecycleState ProjectTrackState(PlaylistTrack track, IReadOnlySet<string>? indexedHashes = null);
}

public sealed class LifecycleProjectionService : ILifecycleProjectionService
{
    private readonly ILibraryService _libraryService;

    public LifecycleProjectionService(ILibraryService libraryService)
    {
        _libraryService = libraryService;
    }

    public async Task<LifecycleMetrics> ComputeMetricsAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _libraryService.LoadAllLibraryEntriesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var existingEntries = entries
            .Where(e => !string.IsNullOrWhiteSpace(e.FilePath) && File.Exists(e.FilePath))
            .ToList();

        var indexedHashes = existingEntries
            .Where(e => !string.IsNullOrWhiteSpace(e.UniqueHash))
            .Select(e => e.UniqueHash)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var playlistTracks = await _libraryService.GetAllPlaylistTracksAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var desiredCount = playlistTracks.Count(t =>
            t.Status == TrackStatus.Missing
            || t.Status == TrackStatus.Pending
            || t.Status == TrackStatus.OnHold
            || t.Status == TrackStatus.Failed);

        var backlogCount = playlistTracks.Count(t =>
            t.Status == TrackStatus.Downloaded
            && !string.IsNullOrWhiteSpace(t.ResolvedFilePath)
            && File.Exists(t.ResolvedFilePath)
            && !string.IsNullOrWhiteSpace(t.TrackUniqueHash)
            && !indexedHashes.Contains(t.TrackUniqueHash));

        return Normalize(new LifecycleMetrics(
            PhysicalOnDisk: existingEntries.Count,
            IndexedCatalog: entries.Count,
            StaleIndexed: entries.Count - existingEntries.Count,
            IngestionBacklog: backlogCount,
            DesiredDownloads: desiredCount));
    }

    public LifecycleMetrics ApplyFileIngestionQueued(LifecycleMetrics current)
        => Normalize(current with { IngestionBacklog = current.IngestionBacklog + 1 });

    public LifecycleMetrics ApplyFileIngestionCompleted(LifecycleMetrics current)
        => Normalize(current with
        {
            IngestionBacklog = current.IngestionBacklog - 1,
            IndexedCatalog = current.IndexedCatalog + 1,
            PhysicalOnDisk = current.PhysicalOnDisk + 1,
        });

    public LifecycleMetrics ApplyFileMissingDetected(LifecycleMetrics current)
        => Normalize(current with { PhysicalOnDisk = current.PhysicalOnDisk - 1 });

    public TrackLifecycleState ProjectTrackState(PlaylistTrack track, IReadOnlySet<string>? indexedHashes = null)
    {
        if (track == null)
            return TrackLifecycleState.Unknown;

        if (track.Status == TrackStatus.Missing
            || track.Status == TrackStatus.Pending
            || track.Status == TrackStatus.OnHold
            || track.Status == TrackStatus.Failed)
        {
            return TrackLifecycleState.DesiredDownload;
        }

        var hasFile = !string.IsNullOrWhiteSpace(track.ResolvedFilePath) && File.Exists(track.ResolvedFilePath);
        if (!hasFile)
            return TrackLifecycleState.Unknown;

        var isIndexed = indexedHashes != null
            && !string.IsNullOrWhiteSpace(track.TrackUniqueHash)
            && indexedHashes.Contains(track.TrackUniqueHash);

        if (isIndexed)
            return TrackLifecycleState.Indexed;

        return track.Status == TrackStatus.Downloaded
            ? TrackLifecycleState.DownloadedAwaitingIndex
            : TrackLifecycleState.StaleIndexed;
    }

    private static LifecycleMetrics Normalize(LifecycleMetrics metrics)
    {
        var indexed = Math.Max(0, metrics.IndexedCatalog);
        var physical = Math.Max(0, Math.Min(metrics.PhysicalOnDisk, indexed));
        var stale = Math.Max(0, indexed - physical);

        return metrics with
        {
            IndexedCatalog = indexed,
            PhysicalOnDisk = physical,
            StaleIndexed = stale,
            IngestionBacklog = Math.Max(0, metrics.IngestionBacklog),
            DesiredDownloads = Math.Max(0, metrics.DesiredDownloads),
        };
    }
}
