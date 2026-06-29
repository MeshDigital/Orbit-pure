using System;
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
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            targets = await db.LibraryEntries
                .AsNoTracking()
                .Where(e => e.FilePath != null && e.FilePath != string.Empty)
                .Select(e => new { e.UniqueHash, e.FilePath, e.Artist, e.Title })
                .ToListAsync(cancellationToken)
                .ContinueWith(
                    t => t.Result.Select(e => (e.UniqueHash, e.FilePath, e.Artist, e.Title)).ToList(),
                    cancellationToken, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }

        _logger.LogInformation("[LibraryScan] Starting corruption scan — {Count} tracks", targets.Count);

        int done = 0, clean = 0, warned = 0, fatal = 0, missing = 0;
        var issues = new List<CorruptFileScanEntry>();

        foreach (var (hash, filePath, artist, title) in targets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            done++;
            progress?.Report((done, targets.Count, Path.GetFileName(filePath)));

            CorruptionStatus status;
            string? details;

            if (!File.Exists(filePath))
            {
                status  = CorruptionStatus.Fatal;
                details = "File not found on disk";
                missing++;
            }
            else
            {
                try
                {
                    (status, details) = await _scanner.ScanAsync(filePath, cancellationToken)
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
                    case CorruptionStatus.Clean:   clean++;   break;
                    case CorruptionStatus.Warning: warned++;  break;
                    case CorruptionStatus.Fatal:   fatal++;   break;
                }

                if (status != CorruptionStatus.Clean)
                    _logger.LogWarning("[LibraryScan] {Status} — {File}: {Details}",
                        status, Path.GetFileName(filePath), details);
            }

            if (status != CorruptionStatus.Clean)
                issues.Add(new CorruptFileScanEntry(hash, filePath, artist, title, status, details));

            await PersistAsync(hash, status, details, cancellationToken).ConfigureAwait(false);
        }

        var result = new LibraryCorruptionScanResult(
            Total:       targets.Count,
            Clean:       clean,
            Warned:      warned,
            Fatal:       fatal,
            Missing:     missing,
            Issues:      issues,
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
            await using var db = await _dbFactory.CreateDbContextAsync(cancellationToken);

            var entity = await db.AudioAnalysis
                .FirstOrDefaultAsync(a => a.TrackUniqueHash == trackHash, cancellationToken);

            if (entity is null)
            {
                entity = new AudioAnalysisEntity { TrackUniqueHash = trackHash };
                db.AudioAnalysis.Add(entity);
            }

            entity.CorruptionStatus    = status;
            entity.CorruptionDetails   = details;
            entity.LastIntegrityScanAt = DateTime.UtcNow;

            await db.SaveChangesAsync(cancellationToken);
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
