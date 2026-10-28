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

/// <summary>
/// Bundles the metrics with the library-entry existence scan they were computed from, so a
/// caller that also needs the entry list (e.g. to populate a track list) doesn't have to repeat
/// the same full-library-load + File.Exists sweep a second time.
/// </summary>
public sealed record LifecycleSnapshot(
    IReadOnlyList<LibraryEntry> AllEntries,
    IReadOnlyList<LibraryEntry> ExistingEntries,
    LifecycleMetrics Metrics);

public interface ILifecycleProjectionService
{
    Task<LifecycleMetrics> ComputeMetricsAsync(CancellationToken cancellationToken = default);
    Task<LifecycleSnapshot> ComputeSnapshotAsync(CancellationToken cancellationToken = default);
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
        => (await ComputeSnapshotAsync(cancellationToken).ConfigureAwait(false)).Metrics;

    public async Task<LifecycleSnapshot> ComputeSnapshotAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var entries = await _libraryService.LoadAllLibraryEntriesAsync().ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();

        var existingEntries = await FilterByFileExistsAsync(
            entries, e => e.FilePath, cancellationToken).ConfigureAwait(false);

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

        var backlogCandidates = playlistTracks
            .Where(t => t.Status == TrackStatus.Downloaded
                && !string.IsNullOrWhiteSpace(t.ResolvedFilePath)
                && !string.IsNullOrWhiteSpace(t.TrackUniqueHash)
                && !indexedHashes.Contains(t.TrackUniqueHash))
            .ToList();
        var backlogExisting = await FilterByFileExistsAsync(
            backlogCandidates, t => t.ResolvedFilePath, cancellationToken).ConfigureAwait(false);

        var metrics = Normalize(new LifecycleMetrics(
            PhysicalOnDisk: existingEntries.Count,
            IndexedCatalog: entries.Count,
            StaleIndexed: entries.Count - existingEntries.Count,
            IngestionBacklog: backlogExisting.Count,
            DesiredDownloads: desiredCount));

        return new LifecycleSnapshot(entries, existingEntries, metrics);
    }

    /// <summary>
    /// Filters a list down to entries whose file path exists on disk, checking in parallel
    /// (bounded) instead of a sequential synchronous loop — File.Exists is cheap per call but a
    /// sequential sweep over thousands of tracks (worse on network/removable storage) is not.
    /// </summary>
    private static async Task<List<T>> FilterByFileExistsAsync<T>(
        IReadOnlyList<T> items, Func<T, string?> pathSelector, CancellationToken cancellationToken)
    {
        if (items.Count == 0) return new List<T>();

        var exists = new bool[items.Count];
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount * 4),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), options, (i, _) =>
        {
            var path = pathSelector(items[i]);
            exists[i] = !string.IsNullOrWhiteSpace(path) && File.Exists(path);
            return ValueTask.CompletedTask;
        }).ConfigureAwait(false);

        var result = new List<T>(items.Count);
        for (int i = 0; i < items.Count; i++)
        {
            if (exists[i]) result.Add(items[i]);
        }
        return result;
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
