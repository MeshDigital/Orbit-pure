using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

/// <summary>
/// Scans every locally available library track for file-level corruption.
/// Results are persisted to <see cref="AudioAnalysisEntity.CorruptionStatus"/>
/// and returned as a detailed <see cref="LibraryCorruptionScanResult"/> for UI remediation.
/// </summary>
public sealed class LibraryCorruptionScanService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly AudioCorruptionScannerService _scanner;
    private readonly ILogger<LibraryCorruptionScanService> _logger;

    public LibraryCorruptionScanService(
        IDbContextFactory<AppDbContext> dbFactory,
        AudioCorruptionScannerService scanner,
        ILogger<LibraryCorruptionScanService> logger)
    {
        _dbFactory = dbFactory;
        _scanner = scanner;
        _logger = logger;
    }

    /// <summary>
    /// Scans all library entries that have a non-empty local file path.
    /// Updates <see cref="AudioAnalysisEntity"/> with the detected corruption status.
    /// Returns full per-file issue list so the UI can offer targeted remediation.
    /// </summary>
    public async Task<LibraryCorruptionScanResult> ScanLibraryAsync(
        IProgress<(int Done, int Total, string CurrentFile)>? progress = null,
        CancellationToken cancellationToken = default)
    {
        List<(string Hash, string FilePath, string Artist, string Title)> targets;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false))
        {
            targets = await db.LibraryEntries
                .AsNoTracking()
                .Where(e => e.FilePath != null && e.FilePath != string.Empty)
                .Select(e => new { e.UniqueHash, e.FilePath, e.Artist, e.Title })
                .ToListAsync(cancellationToken)
                .ContinueWith(
                    t => t.Result.Select(e => (e.UniqueHash, e.FilePath, e.Artist, e.Title)).ToList(),
                    cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default)
                .ConfigureAwait(false);
        }

        _logger.LogInformation("[LibraryScan] Starting corruption scan — {Count} tracks", targets.Count);

        int done = 0, clean = 0, warned = 0, fatal = 0, missing = 0;
        var issues = new ConcurrentBag<CorruptFileScanEntry>();

        // Bounded parallelism: each scan spawns an external FFmpeg process and does its own DB
        // round-trip, so a fully serial foreach here meant a multi-thousand-track library took a
        // linear chain of process spawns — tens of minutes to hours. Mirrors AnalysisQueueService's
        // default worker sizing rather than reinventing a concurrency scheme.
        var options = new ParallelOptions
        {
            MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
            CancellationToken = cancellationToken,
        };

        await Parallel.ForEachAsync(targets, options, async (target, ct) =>
        {
            var (hash, filePath, artist, title) = target;
            CorruptionStatus status;
            string? details;

            if (!File.Exists(filePath))
            {
                status  = CorruptionStatus.Fatal;
                details = "File not found on disk";
                Interlocked.Increment(ref missing);
            }
            else
            {
                try
                {
                    (status, details) = await _scanner.ScanAsync(filePath, ct)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    status  = CorruptionStatus.Warning;
                    details = ex.Message;
                    _logger.LogError(ex, "[LibraryScan] Unexpected error scanning {File}", filePath);
                }

                switch (status)
                {
                    case CorruptionStatus.Clean:   Interlocked.Increment(ref clean);   break;
                    case CorruptionStatus.Warning: Interlocked.Increment(ref warned);  break;
                    case CorruptionStatus.Fatal:   Interlocked.Increment(ref fatal);   break;
                }

                if (status != CorruptionStatus.Clean)
                    _logger.LogWarning("[LibraryScan] {Status} — {File}: {Details}",
                        status, Path.GetFileName(filePath), details);
            }

            if (status != CorruptionStatus.Clean)
                issues.Add(new CorruptFileScanEntry(hash, filePath, artist, title, status, details));

            await PersistAsync(hash, status, details, ct).ConfigureAwait(false);

            int doneNow = Interlocked.Increment(ref done);
            progress?.Report((doneNow, targets.Count, Path.GetFileName(filePath)));
        }).ConfigureAwait(false);

        var result = new LibraryCorruptionScanResult(
            Total:       targets.Count,
            Clean:       clean,
            Warned:      warned,
            Fatal:       fatal,
            Missing:     missing,
            // Parallel completion order isn't meaningful — sort for a stable, scannable list.
            Issues:      issues.OrderBy(i => i.Artist, StringComparer.OrdinalIgnoreCase)
                                .ThenBy(i => i.Title, StringComparer.OrdinalIgnoreCase)
                                .ToList(),
            CompletedAt: DateTime.UtcNow);

        _logger.LogInformation(
            "[LibraryScan] Done — Clean:{C} Warning:{W} Fatal:{F} Missing:{M} / {T}",
            clean, warned, fatal, missing, targets.Count);

        return result;
    }

    private async Task PersistAsync(
        string trackHash,
        CorruptionStatus status,
        string? details,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

            var entity = await db.AudioAnalysis
                .FirstOrDefaultAsync(a => a.TrackUniqueHash == trackHash, cancellationToken).ConfigureAwait(false);

            if (entity is null)
            {
                entity = new AudioAnalysisEntity { TrackUniqueHash = trackHash };
                db.AudioAnalysis.Add(entity);
            }

            entity.CorruptionStatus    = status;
            entity.CorruptionDetails   = details;
            entity.LastIntegrityScanAt = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[LibraryScan] Failed to persist result for {Hash}", trackHash);
        }
    }
}

/// <summary>Per-file issue entry returned by the library scan.</summary>
public record CorruptFileScanEntry(
    string Hash,
    string FilePath,
    string Artist,
    string Title,
    CorruptionStatus Status,
    string? Details)
{
    public bool IsMissing  => Status == CorruptionStatus.Fatal && Details == "File not found on disk";
    public bool IsCorrupt  => Status == CorruptionStatus.Fatal && !IsMissing;
    public bool IsWarning  => Status == CorruptionStatus.Warning;
    public string DisplayLabel => $"{Artist} — {Title}";
    public string StatusBadge  => IsMissing ? "Missing" : IsCorrupt ? "Corrupt" : "Warning";
}

public record LibraryCorruptionScanResult(
    int Total,
    int Clean,
    int Warned,
    int Fatal,
    int Missing,
    IReadOnlyList<CorruptFileScanEntry> Issues,
    DateTime CompletedAt);
