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
using SLSKDONET.Services.Audio;
using SLSKDONET.Services.Library.Maintenance;
using SLSKDONET.Services.Matching;

namespace SLSKDONET.Services.Intake;

public sealed record IntakeScanProgress(int Done, int Total, int Failed, string? CurrentFile);

/// <summary>Files per lane in one session.</summary>
public sealed record IntakeSessionSummary(
    int Total, int Pending, int Good, int BetterCopy, int Duplicate, int Suspicious, int Reject, int Failed, int Applied);

/// <summary>
/// Intake's scan (epic #632): lists a drive or folder, checks every audio file on it and puts each
/// in a review lane with a pre-ticked action. The source is only read — nothing is copied, moved
/// or written next to it — so a large drive needs no staging space; Apply copies only what the
/// user keeps. A scan is resumable: every file is a row from the start, checked rows keep their
/// result, and a restarted scan picks up the files still Pending.
/// </summary>
public sealed class IntakeScanService
{
    private const int InsertBatchSize = 500;
    private const int SaveBatchSize = 50;

    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly IIntakeFileInspector _inspector;
    private readonly ILogger<IntakeScanService> _logger;
    private readonly Func<CancellationToken, Task<IntakeLibraryIndex>> _loadIndex;

    public IntakeScanService(
        IDbContextFactory<AppDbContext> dbFactory,
        IIntakeFileInspector inspector,
        ILogger<IntakeScanService> logger)
        : this(dbFactory, inspector, logger, null) { }

    internal IntakeScanService(
        IDbContextFactory<AppDbContext> dbFactory,
        IIntakeFileInspector inspector,
        ILogger<IntakeScanService> logger,
        Func<CancellationToken, Task<IntakeLibraryIndex>>? loadIndex)
    {
        _dbFactory = dbFactory;
        _inspector = inspector;
        _logger = logger;
        _loadIndex = loadIndex ?? (ct => IntakeLibraryIndex.LoadAsync(dbFactory, ct));
    }

    /// <summary>Files checked at once. The full decode check is CPU-bound, so half the cores, at most 4.</summary>
    public int MaxParallelism { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    /// <summary>
    /// Lists every audio file under <paramref name="sourceRoot"/> into a new session. Non-audio
    /// files, macOS "._" resource forks and hidden/system files are counted as junk and left out.
    /// </summary>
    public async Task<IntakeSessionEntity> CreateSessionAsync(string sourceRoot, bool deepDecodeCheck, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot))
            throw new DirectoryNotFoundException($"The folder {sourceRoot} doesn't exist or isn't connected.");

        var root = Path.GetFullPath(sourceRoot);
        var session = new IntakeSessionEntity
        {
            SourceRoot = root,
            Label = LabelFor(root),
            DeepDecodeCheck = deepDecodeCheck,
            Status = IntakeSessionStatus.Scanning,
        };

        var (files, junk) = await Task.Run(() => ListAudioFiles(root, ct), ct).ConfigureAwait(false);
        session.JunkCount = junk;

        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            db.IntakeSessions.Add(session);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        foreach (var batch in files.Chunk(InsertBatchSize))
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            db.IntakeItems.AddRange(batch.Select(f => new IntakeItemEntity
            {
                SessionId = session.Id,
                SourcePath = f.Path,
                RelativePath = Path.GetRelativePath(root, f.Path).Replace('\\', '/'),
                SizeBytes = f.Size,
                Extension = Path.GetExtension(f.Path).ToLowerInvariant(),
            }));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        _logger.LogInformation("Intake session {Id} for {Root}: {Files} audio files, {Junk} skipped", session.Id, root, files.Count, junk);
        return session;
    }

    /// <summary>
    /// Checks every Pending file of the session, then settles copies of one track found twice on
    /// the source. Cancelling keeps every file already checked; the next call carries on.
    /// </summary>
    public async Task ScanAsync(string sessionId, IProgress<IntakeScanProgress>? progress = null, CancellationToken ct = default)
    {
        IntakeSessionEntity session;
        List<(string Id, string Path)> pending;
        int total;
        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            session = await db.IntakeSessions.AsNoTracking().FirstOrDefaultAsync(s => s.Id == sessionId, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Intake session {sessionId} doesn't exist.");
            total = await db.IntakeItems.CountAsync(i => i.SessionId == sessionId, ct).ConfigureAwait(false);
            pending = (await db.IntakeItems.AsNoTracking()
                    .Where(i => i.SessionId == sessionId && i.Status == IntakeItemStatus.Pending)
                    .Select(i => new { i.Id, i.SourcePath })
                    .ToListAsync(ct).ConfigureAwait(false))
                .Select(i => (i.Id, i.SourcePath))
                .ToList();
        }

        if (pending.Count > 0 && !Directory.Exists(session.SourceRoot))
            throw new DirectoryNotFoundException($"{session.Label} isn't connected. Plug it in to carry on the scan.");

        var index = await _loadIndex(ct).ConfigureAwait(false);
        _logger.LogInformation("Intake scan {Id}: {Pending} of {Total} files to check against {Library} library tracks ({Hashed} hashed)",
            sessionId, pending.Count, total, index.Count, index.HashedCount);

        int done = total - pending.Count;
        int failed = 0;
        var results = new List<IntakeItemEntity>(SaveBatchSize);
        var saveGate = new SemaphoreSlim(1, 1);

        async Task FlushAsync(bool force, CancellationToken token)
        {
            List<IntakeItemEntity> toSave;
            lock (results)
            {
                if (results.Count == 0 || (!force && results.Count < SaveBatchSize)) return;
                toSave = results.ToList();
                results.Clear();
            }
            await saveGate.WaitAsync(token).ConfigureAwait(false);
            try
            {
                await using var db = await _dbFactory.CreateDbContextAsync(token).ConfigureAwait(false);
                db.IntakeItems.UpdateRange(toSave);
                await db.SaveChangesAsync(token).ConfigureAwait(false);
            }
            finally { saveGate.Release(); }
        }

        var itemsById = await LoadItemsAsync(pending.Select(p => p.Id), ct).ConfigureAwait(false);
        try
        {
            await Parallel.ForEachAsync(pending,
                new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, MaxParallelism), CancellationToken = ct },
                async (p, token) =>
                {
                    var item = itemsById[p.Id];
                    await CheckOneAsync(item, session.DeepDecodeCheck, index, token).ConfigureAwait(false);
                    if (item.Status == IntakeItemStatus.Failed) Interlocked.Increment(ref failed);
                    lock (results) results.Add(item);
                    int now = Interlocked.Increment(ref done);
                    progress?.Report(new IntakeScanProgress(now, total, Volatile.Read(ref failed), item.RelativePath));
                    await FlushAsync(force: false, token).ConfigureAwait(false);
                }).ConfigureAwait(false);
        }
        finally
        {
            // Keep what was checked even when cancelled.
            await FlushAsync(force: true, CancellationToken.None).ConfigureAwait(false);
        }

        await SettleInSessionDuplicatesAsync(sessionId, ct).ConfigureAwait(false);

        await using (var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false))
        {
            var s = await db.IntakeSessions.FirstAsync(x => x.Id == sessionId, ct).ConfigureAwait(false);
            s.Status = IntakeSessionStatus.Ready;
            s.ScannedAt = DateTime.UtcNow;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Intake scan {Id} finished: {Done} files, {Failed} couldn't be checked", sessionId, done, failed);
    }

    public async Task<IntakeSessionSummary> GetSummaryAsync(string sessionId, CancellationToken ct = default)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.IntakeItems.AsNoTracking()
            .Where(i => i.SessionId == sessionId)
            .GroupBy(i => new { i.Status, i.Lane })
            .Select(g => new { g.Key.Status, g.Key.Lane, Count = g.Count() })
            .ToListAsync(ct).ConfigureAwait(false);
        int Lane(IntakeLane lane) => rows.Where(r => r.Lane == lane && r.Status == IntakeItemStatus.Scanned).Sum(r => r.Count);
        return new IntakeSessionSummary(
            Total: rows.Sum(r => r.Count),
            Pending: rows.Where(r => r.Status == IntakeItemStatus.Pending).Sum(r => r.Count),
            Good: Lane(IntakeLane.Good),
            BetterCopy: Lane(IntakeLane.BetterCopy),
            Duplicate: Lane(IntakeLane.Duplicate),
            Suspicious: Lane(IntakeLane.Suspicious),
            Reject: Lane(IntakeLane.Reject),
            Failed: rows.Where(r => r.Status == IntakeItemStatus.Failed).Sum(r => r.Count),
            Applied: rows.Where(r => r.Status is IntakeItemStatus.Applied or IntakeItemStatus.Skipped).Sum(r => r.Count));
    }

    private async Task CheckOneAsync(IntakeItemEntity item, bool deep, IntakeLibraryIndex index, CancellationToken ct)
    {
        item.ScannedAt = DateTime.UtcNow;
        if (!File.Exists(item.SourcePath))
        {
            item.Status = IntakeItemStatus.Failed;
            item.Error = "The file is gone from the source.";
            return;
        }

        IntakeInspection inspection;
        try
        {
            inspection = await _inspector.InspectAsync(item.SourcePath, deep, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Intake: checking {Path} failed", item.SourcePath);
            item.Status = IntakeItemStatus.Failed;
            item.Error = ex.Message;
            return;
        }

        var f = inspection.Facts;
        item.Artist = Clean(f?.Artist);
        item.Title = Clean(f?.Title);
        item.Album = Clean(f?.Album);
        item.DurationSeconds = f == null ? 0 : (int)Math.Round(f.Duration.TotalSeconds);
        item.BitrateKbps = f?.BitrateKbps ?? 0;
        item.SampleRate = f?.SampleRate ?? 0;
        item.BitsPerSample = f?.BitsPerSample ?? 0;
        item.ContentHash = inspection.ContentHash;
        item.DecodeErrorCount = inspection.DecodeErrorCount;
        item.DecodeErrorPreview = inspection.DecodeErrorPreview;
        item.SpectralVerdict = inspection.Spectral?.Verdict.ToString();
        item.SpectralCutoffHz = inspection.Spectral?.SpectralCutoffHz;
        item.SpectralConfidence = inspection.Spectral?.Confidence;
        if (inspection.ReadError != null) item.Error = inspection.ReadError;

        var match = f == null ? null : index.FindMatch(item.ContentHash, item.Artist, item.Title, item.DurationSeconds);
        item.MatchKind = match?.Kind ?? IntakeMatchKind.None;
        item.MatchedTrackHash = match?.TrackHash;
        item.MatchedFilePath = match?.FilePath;
        item.MatchedFormat = match?.Format;
        item.MatchedBitrate = match?.Bitrate;

        var verdict = IntakeClassifier.Classify(new IntakeFacts(
            item.Extension, f != null, item.Artist, item.Title, item.DurationSeconds, item.BitrateKbps,
            item.DecodeErrorCount, inspection.Spectral?.Verdict, inspection.Spectral?.Confidence ?? 0,
            inspection.Spectral?.SpectralCutoffHz), match);
        item.Lane = verdict.Lane;
        item.Action = verdict.Action;
        item.Reason = verdict.Reason;
        item.Status = IntakeItemStatus.Scanned;
    }

    private async Task<Dictionary<string, IntakeItemEntity>> LoadItemsAsync(IEnumerable<string> ids, CancellationToken ct)
    {
        var set = ids.ToHashSet(StringComparer.Ordinal);
        var map = new Dictionary<string, IntakeItemEntity>(set.Count, StringComparer.Ordinal);
        foreach (var chunk in set.Chunk(900))
        {
            await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
            foreach (var e in await db.IntakeItems.AsNoTracking().Where(i => chunk.Contains(i.Id)).ToListAsync(ct).ConfigureAwait(false))
                map[e.Id] = e;
        }
        return map;
    }

    private async Task SettleInSessionDuplicatesAsync(string sessionId, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var items = await db.IntakeItems
            .Where(i => i.SessionId == sessionId && i.Status == IntakeItemStatus.Scanned)
            .ToListAsync(ct).ConfigureAwait(false);
        int changed = SettleInSessionDuplicates(items);
        if (changed > 0)
        {
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            _logger.LogInformation("Intake scan {Id}: {Count} extra copies on the source marked as duplicates", sessionId, changed);
        }
    }

    /// <summary>
    /// The same track found more than once on the source (two folders, FLAC and MP3 side by side):
    /// only the best copy keeps its Keep / Replace / Upgrade action; the others become Duplicates
    /// that point at it. Copies are the same track when their audio hash matches, or their artist,
    /// title and version match with durations within 2 seconds. Returns how many items changed.
    /// </summary>
    internal static int SettleInSessionDuplicates(IReadOnlyList<IntakeItemEntity> items)
    {
        var candidates = items.Where(i => i.Lane is IntakeLane.Good or IntakeLane.BetterCopy or IntakeLane.Suspicious).ToList();
        var groups = new List<List<IntakeItemEntity>>();

        foreach (var byHash in candidates.Where(i => IntakeLibraryIndex.Key(i.Artist, i.Title) == null && !string.IsNullOrEmpty(i.ContentHash))
                     .GroupBy(i => i.ContentHash!, StringComparer.Ordinal))
            groups.Add(byHash.ToList());

        foreach (var byKey in candidates.Where(i => IntakeLibraryIndex.Key(i.Artist, i.Title) != null)
                     .GroupBy(i => IntakeLibraryIndex.Key(i.Artist, i.Title) + "|" + (VersionTag.DerivativeVersionTag(i.Title) ?? "").ToLowerInvariant(), StringComparer.Ordinal))
        {
            // Within one name, split by duration: an Extended and a Radio edit are different tracks.
            List<IntakeItemEntity>? cluster = null;
            int last = int.MinValue;
            foreach (var item in byKey.OrderBy(i => i.DurationSeconds))
            {
                if (cluster == null || item.DurationSeconds - last > IntakeLibraryIndex.DurationToleranceSeconds)
                    groups.Add(cluster = new List<IntakeItemEntity>());
                cluster.Add(item);
                last = item.DurationSeconds;
            }
        }

        int changed = 0;
        foreach (var group in groups.Where(g => g.Count > 1))
        {
            var winner = group
                .OrderByDescending(Rank)
                .ThenByDescending(i => i.SizeBytes)
                .ThenBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase)
                .First();
            foreach (var other in group.Where(i => !ReferenceEquals(i, winner)))
            {
                other.Lane = IntakeLane.Duplicate;
                other.Action = IntakeAction.Skip;
                other.Reason = $"Another copy on this drive is used instead: {winner.RelativePath}";
                changed++;
            }
        }
        return changed;
    }

    /// <summary>Suspicious copies rank below any trusted one; otherwise lossless › 320 › lower.</summary>
    private static int Rank(IntakeItemEntity i) =>
        i.Lane == IntakeLane.Suspicious ? 0 : 1 + ExactDuplicateKeeper.QualityRank(IntakeClassifier.Format(i.Extension), i.BitrateKbps);

    private static string? Clean(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private static string LabelFor(string root)
    {
        var name = Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (!string.IsNullOrEmpty(name)) return name;
        try
        {
            var drive = new DriveInfo(root);
            return string.IsNullOrWhiteSpace(drive.VolumeLabel) ? root : $"{drive.VolumeLabel} ({drive.Name.TrimEnd('\\', '/')})";
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return root;
        }
    }

    /// <summary>Audio files to check, and how many other files were skipped.</summary>
    internal static (List<(string Path, long Size)> Files, int Junk) ListAudioFiles(string root, CancellationToken ct)
    {
        var options = new EnumerationOptions
        {
            RecurseSubdirectories = true,
            IgnoreInaccessible = true,
            ReturnSpecialDirectories = false,
            AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
        };
        var files = new List<(string, long)>();
        int junk = 0;
        foreach (var info in new DirectoryInfo(root).EnumerateFiles("*", options))
        {
            ct.ThrowIfCancellationRequested();
            if (IsJunk(info.Name) || !AudioFormats.ImportableExtensions.Contains(info.Extension.ToLowerInvariant()))
            {
                junk++;
                continue;
            }
            files.Add((info.FullName, info.Length));
        }
        files.Sort((a, b) => string.Compare(a.Item1, b.Item1, StringComparison.OrdinalIgnoreCase));
        return (files, junk);
    }

    internal static bool IsJunk(string fileName) =>
        fileName.StartsWith("._", StringComparison.Ordinal)
        || fileName.Equals(".DS_Store", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("Thumbs.db", StringComparison.OrdinalIgnoreCase)
        || fileName.Equals("desktop.ini", StringComparison.OrdinalIgnoreCase);
}
