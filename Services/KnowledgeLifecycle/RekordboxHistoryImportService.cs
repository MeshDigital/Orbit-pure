using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Rekordbox;

namespace SLSKDONET.Services.KnowledgeLifecycle;

/// <summary>What a history import did, for an honest status line.</summary>
public sealed record RekordboxHistoryImportResult(
    int SessionsImported, int SessionsAlreadyImported, int SessionsWithoutTransitions,
    int TransitionsRecorded, int TracksMatched, int TracksNotInLibrary, string? Error = null)
{
    public bool Failed => Error is not null;

    public string Summary => Failed
        ? Error!
        : SessionsImported == 0 && SessionsAlreadyImported > 0
            ? $"Nothing new: all {SessionsAlreadyImported} Rekordbox sessions were imported before."
            : $"Imported {SessionsImported} Rekordbox session{(SessionsImported == 1 ? "" : "s")}: {TransitionsRecorded} played transitions recorded " +
              $"({TracksMatched} tracks matched, {TracksNotInLibrary} not in your ORBIT library)" +
              (SessionsAlreadyImported > 0 ? $", {SessionsAlreadyImported} already imported" : "") + ".";
}

/// <summary>
/// "Played live" evidence from Rekordbox's play history (rebuild plan P6.1, #207). Each history
/// playlist becomes a <see cref="PerformanceSessionEntity"/>; every two tracks played back to back
/// (both in the ORBIT library) become a live transition, recorded through the same path the
/// Performance Analyst agent uses (<see cref="PerformanceAnalystAgent.RecordEvidenceAsync"/>).
/// Runs on request and directly, not through the opt-in agent swarm. Idempotent: a session is keyed
/// by its Rekordbox history id and imported once. Reads Rekordbox read-only.
/// </summary>
public sealed class RekordboxHistoryImportService
{
    /// <summary>Marks an imported session in <see cref="PerformanceSessionEntity.Notes"/>.</summary>
    public const string NotesPrefix = "rekordbox-history:";

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly Func<CancellationToken, Task<RekordboxHistoryResult>> _readHistory;
    private readonly ILogger<RekordboxHistoryImportService> _logger;

    public RekordboxHistoryImportService(IDbContextFactory<AppDbContext> dbFactory, RekordboxLibraryService rekordbox,
        ILogger<RekordboxHistoryImportService> logger)
        : this(dbFactory, rekordbox.ReadHistoryAsync, logger) { }

    /// <summary>Tests pass the history directly.</summary>
    internal RekordboxHistoryImportService(IDbContextFactory<AppDbContext> dbFactory,
        Func<CancellationToken, Task<RekordboxHistoryResult>> readHistory, ILogger<RekordboxHistoryImportService> logger)
    {
        _dbFactory = dbFactory;
        _readHistory = readHistory;
        _logger = logger;
    }

    public async Task<RekordboxHistoryImportResult> ImportAsync(CancellationToken ct = default)
    {
        var read = await _readHistory(ct).ConfigureAwait(false);
        if (!read.Success) return new RekordboxHistoryImportResult(0, 0, 0, 0, 0, 0, read.Message);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var library = await db.LibraryEntries.AsNoTracking()
            .Where(e => e.FilePath != null && e.FilePath != "")
            .Select(e => new { e.UniqueHash, e.FilePath })
            .ToListAsync(ct).ConfigureAwait(false);
        var byPath = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var e in library) byPath.TryAdd(RekordboxLibraryService.NormalizePath(e.FilePath), e.UniqueHash);

        var imported = (await db.PerformanceSessions.AsNoTracking()
                .Where(s => s.Notes != null && s.Notes.StartsWith(NotesPrefix))
                .Select(s => s.Notes!)
                .ToListAsync(ct).ConfigureAwait(false))
            .ToHashSet(StringComparer.Ordinal);

        int sessions = 0, already = 0, empty = 0, transitions = 0, matched = 0, missing = 0;
        foreach (var history in read.Histories)
        {
            ct.ThrowIfCancellationRequested();
            var key = NotesPrefix + history.Id;
            if (imported.Contains(key)) { already++; continue; }

            var session = BuildSession(history, byPath, out var m, out var miss);
            matched += m;
            missing += miss;
            var audibles = session.Tracks.Count(t => t.AudibleChange);
            if (audibles == 0) { empty++; continue; }

            session.Notes = key;
            db.PerformanceSessions.Add(session);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            transitions += await PerformanceAnalystAgent.RecordEvidenceAsync(db, session.Id).ConfigureAwait(false);
            sessions++;
        }

        var result = new RekordboxHistoryImportResult(sessions, already, empty, transitions, matched, missing);
        _logger.LogInformation("[RekordboxHistory] {Summary}", result.Summary);
        return result;
    }

    /// <summary>
    /// The session for one history playlist. Tracks not in the ORBIT library are left out, and the
    /// track after such a gap gets no transition into it: Rekordbox played something in between, so
    /// the two ORBIT tracks were never mixed into each other.
    /// </summary>
    internal static PerformanceSessionEntity BuildSession(RekordboxHistory history, IReadOnlyDictionary<string, string> hashByNormalizedPath,
        out int matched, out int notInLibrary)
    {
        var started = ParseCreated(history.Created);
        var session = new PerformanceSessionEntity
        {
            Venue = history.Name,
            StartedAtUtc = started,
            EndedAtUtc = started,
        };

        matched = 0;
        notInLibrary = 0;
        var previousMatched = false;
        string? previousHash = null;
        var sequence = 0;
        foreach (var track in history.Tracks.OrderBy(t => t.TrackNo))
        {
            if (track.Path is null || !hashByNormalizedPath.TryGetValue(RekordboxLibraryService.NormalizePath(track.Path), out var hash))
            {
                notInLibrary++;
                previousMatched = false;
                previousHash = null;
                continue;
            }

            matched++;
            session.Tracks.Add(new PerformanceSessionTrackEntity
            {
                SessionId = session.Id,
                TrackId = hash,
                SequenceOrder = sequence++,
                Planned = false,
                // Every back-to-back pair was a choice the DJ made live; there was no plan to deviate from.
                // (A track loaded twice in a row is not a transition.)
                AudibleChange = previousMatched && !string.Equals(previousHash, hash, StringComparison.Ordinal),
            });
            previousMatched = true;
            previousHash = hash;
        }
        return session;
    }

    private static DateTime ParseCreated(string? created) =>
        DateTime.TryParse(created, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal | DateTimeStyles.AdjustToUniversal, out var t)
            ? t
            : DateTime.UtcNow;
}
