using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Services.Enrichment;

namespace SLSKDONET.Services.Library.Maintenance;

/// <summary>Serialized choices for a run, persisted to <c>MaintenanceRuns.OptionsJson</c>.</summary>
public sealed record MaintenanceRunOptions(string? OptionsJson = null);

public sealed record TagApplyReport(
    int Applied,
    int FileWritten,
    int DbOnly,
    int Failed,
    IReadOnlyList<string> Errors);

public sealed record TagRevertReport(
    int Reverted,
    int Skipped,
    IReadOnlyList<string> Skips);

public sealed record MaintenanceRunSummary(int Applied, int FileWritten, int DbOnly, int Failed);

/// <summary>
/// Reversible maintenance tag writes. A run journals every field change before writing, writes the
/// file first (so a failed file write never touches the DB for that track), and can revert the whole
/// run without overwriting edits the user made afterwards.
/// </summary>
public sealed class TagChangeService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ITaggerService _tagger;
    private readonly TrackMetadataWriter _writer;
    private readonly EnrichmentStore _enrichment;
    private readonly ILogger<TagChangeService> _logger;

    public TagChangeService(
        IDbContextFactory<AppDbContext> dbFactory,
        ITaggerService tagger,
        TrackMetadataWriter writer,
        EnrichmentStore enrichment,
        ILogger<TagChangeService> logger)
    {
        _dbFactory = dbFactory;
        _tagger = tagger;
        _writer = writer;
        _enrichment = enrichment;
        _logger = logger;
    }

    public async Task<Guid> BeginRunAsync(MaintenanceRunOptions options, CancellationToken ct = default)
    {
        var id = Guid.NewGuid();
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.MaintenanceRuns.Add(new MaintenanceRunEntity
        {
            Id = id.ToString(),
            StartedAt = DateTime.UtcNow,
            Status = "Scanning",
            OptionsJson = options?.OptionsJson,
        });
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Started maintenance run {RunId}", id);
        return id;
    }

    public async Task<TagApplyReport> ApplyAsync(Guid runId, IReadOnlyList<ProposedTagChange> approved, IProgress<int>? progress = null, CancellationToken ct = default)
    {
        var runIdStr = runId.ToString();
        int applied = 0, fileWritten = 0, dbOnly = 0, failed = 0;
        var errors = new List<string>();

        for (var i = 0; i < approved.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var change = approved[i];
            progress?.Report(i + 1);

            var filePresent = !string.IsNullOrWhiteSpace(change.FilePath) && File.Exists(change.FilePath);

            // 1. Journal rows first (Applied=0) so a crash mid-run still leaves a revertable log.
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

            var journal = new List<TagChangeLogEntity>();
            foreach (var field in change.Changes)
            {
                var oldValue = filePresent
                    ? await _tagger.ReadFieldValueAsync(change.FilePath!, field.Field, ct).ConfigureAwait(false)
                    : await ReadDbFieldAsync(db, change.Hash, field.Field, ct).ConfigureAwait(false);

                journal.Add(new TagChangeLogEntity
                {
                    RunId = runIdStr,
                    TrackUniqueHash = change.Hash,
                    FilePath = change.FilePath,
                    Field = field.Field.ToString(),
                    OldValue = oldValue,
                    NewValue = field.Value,
                    Rule = change.Rule,
                    Applied = 0,
                    FileWritten = 0,
                });
            }
            db.TagChangeLog.AddRange(journal);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            // 2. Write the file first. If it fails, the DB is not changed for this track.
            var fileOk = false;
            if (filePresent)
            {
                var patchResult = await _tagger.ApplyTagPatchAsync(change.FilePath!, new TagPatch(change.Changes), ct).ConfigureAwait(false);
                fileOk = patchResult.Success;
                if (!fileOk)
                {
                    failed++;
                    errors.Add($"{change.Hash}: {patchResult.Error}");
                    foreach (var row in journal)
                        row.Error = patchResult.Error;
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    _logger.LogWarning("Maintenance run {RunId}: file write failed for {Hash}: {Error}", runId, change.Hash, patchResult.Error);
                    continue;
                }
            }

            // 3. DB sync (and provenance) for the fields that have a DB column. AlbumArtist/Remixer
            //    are file-only and skipped here.
            var dbChanges = change.Changes
                .Where(f => HasDbColumn(f.Field))
                .Select(f => new TrackMetadataWriter.Change(f.Field, f.Value))
                .ToList();

            if (dbChanges.Count > 0)
            {
                await _writer.ApplyAsync(db, change.Hash, dbChanges, ct).ConfigureAwait(false);
                var provenanceFields = dbChanges
                    .Select(c => FieldToProvenance(c.Field))
                    .Where(f => f is not null)
                    .Select(f => f!)
                    .ToList();
                if (provenanceFields.Count > 0)
                    await _enrichment.MarkUserEditedAsync(new[] { change.Hash }, provenanceFields, ct, MetadataMerger.MaintenanceSource).ConfigureAwait(false);
            }

            // 4. Mark the journal rows applied.
            foreach (var row in journal)
            {
                row.Applied = 1;
                row.FileWritten = filePresent && fileOk ? 1 : 0;
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            applied++;
            if (filePresent) fileWritten++; else dbOnly++;
        }

        _logger.LogInformation("Maintenance run {RunId}: applied {Applied} (file {FileWritten}, db-only {DbOnly}), failed {Failed}",
            runId, applied, fileWritten, dbOnly, failed);

        return new TagApplyReport(applied, fileWritten, dbOnly, failed, errors);
    }

    public async Task<TagRevertReport> RevertRunAsync(Guid runId, CancellationToken ct = default)
    {
        var runIdStr = runId.ToString();
        int reverted = 0, skipped = 0;
        var skips = new List<string>();

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // Reverse apply order (newest first) so overlapping fields restore correctly. Already-reverted
        // rows (RevertedAt set) are skipped, so revert is safe to run again.
        var rows = await db.TagChangeLog
            .Where(r => r.RunId == runIdStr && r.Applied == 1 && r.RevertedAt == null)
            .OrderByDescending(r => r.Id)
            .ToListAsync(ct).ConfigureAwait(false);

        foreach (var row in rows)
        {
            ct.ThrowIfCancellationRequested();
            var field = Enum.Parse<TagField>(row.Field);

            // Read the current value from the file AND the DB.
            string? fileValue = null;
            if (row.FileWritten == 1 && !string.IsNullOrWhiteSpace(row.FilePath) && File.Exists(row.FilePath))
                fileValue = await _tagger.ReadFieldValueAsync(row.FilePath, field, ct).ConfigureAwait(false);

            var dbValue = await ReadDbFieldAsync(db, row.TrackUniqueHash, field, ct).ConfigureAwait(false);

            var fileStillNew = EqualOrNull(fileValue, row.NewValue);
            var dbStillNew = EqualOrNull(dbValue, row.NewValue);

            // Restore only if both still equal NewValue; otherwise the user edited it after the run.
            if (!fileStillNew || !dbStillNew)
            {
                skipped++;
                row.RevertSkipReason = "edited since";
                skips.Add($"{row.TrackUniqueHash}.{row.Field}");
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            // Restore the file tag.
            if (row.FileWritten == 1 && !string.IsNullOrWhiteSpace(row.FilePath) && File.Exists(row.FilePath))
            {
                var patchResult = await _tagger.ApplyTagPatchAsync(
                    row.FilePath, new TagPatch(new[] { new TagPatchField(field, row.OldValue) }), ct).ConfigureAwait(false);
                if (!patchResult.Success)
                {
                    skipped++;
                    row.RevertSkipReason = $"file restore failed: {patchResult.Error}";
                    skips.Add($"{row.TrackUniqueHash}.{row.Field}");
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                    continue;
                }
            }

            // Restore the DB.
            if (HasDbColumn(field))
                await _writer.ApplyAsync(db, row.TrackUniqueHash, new[] { new TrackMetadataWriter.Change(field, row.OldValue) }, ct).ConfigureAwait(false);

            row.RevertedAt = DateTime.UtcNow;
            row.RevertSkipReason = null;
            reverted++;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var run = await db.MaintenanceRuns.FirstOrDefaultAsync(r => r.Id == runIdStr, ct).ConfigureAwait(false);
        if (run is not null)
        {
            run.RevertedAt = DateTime.UtcNow;
            run.Status = skipped > 0 ? "PartiallyReverted" : "Reverted";
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Maintenance run {RunId}: reverted {Reverted}, skipped {Skipped}", runId, reverted, skipped);
        return new TagRevertReport(reverted, skipped, skips);
    }

    public async Task CompleteRunAsync(Guid runId, MaintenanceRunSummary summary, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var run = await db.MaintenanceRuns.FirstOrDefaultAsync(r => r.Id == runId.ToString(), ct).ConfigureAwait(false);
        if (run is null) return;

        run.CompletedAt = DateTime.UtcNow;
        run.Status = "Completed";
        run.SummaryJson = JsonSerializer.Serialize(summary);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Completed maintenance run {RunId}", runId);
    }

    private static bool EqualOrNull(string? a, string? b) =>
        a == null ? b == null : string.Equals(a, b, StringComparison.Ordinal);

    private static bool HasDbColumn(TagField field) =>
        field is TagField.Title or TagField.Artist or TagField.Album or TagField.Genre or TagField.Label;

    private static string? FieldToProvenance(TagField field) => field switch
    {
        TagField.Title => MetadataMerger.Fields.Title,
        TagField.Artist => MetadataMerger.Fields.Artist,
        TagField.Album => MetadataMerger.Fields.Album,
        TagField.Genre => MetadataMerger.Fields.Genre,
        TagField.Label => MetadataMerger.Fields.Label,
        _ => null,
    };

    private static async Task<string?> ReadDbFieldAsync(AppDbContext db, string uniqueHash, TagField field, CancellationToken ct)
    {
        var entry = await db.LibraryEntries.AsNoTracking()
            .FirstOrDefaultAsync(e => e.UniqueHash == uniqueHash, ct).ConfigureAwait(false);
        return field switch
        {
            TagField.Title => entry?.Title,
            TagField.Artist => entry?.Artist,
            TagField.Album => entry?.Album,
            TagField.Genre => entry?.PrimaryGenre,
            TagField.Label => entry?.Label,
            _ => null,
        };
    }
}
