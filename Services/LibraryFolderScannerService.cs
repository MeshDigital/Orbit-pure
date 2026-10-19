using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace SLSKDONET.Services;

/// <summary>Per-folder outcome of the legacy single-folder scan API (Library Health "Scan now").</summary>
public class ScanResult
{
    public Guid FolderId { get; set; }
    public string FolderPath { get; set; } = string.Empty;
    public int TotalFilesFound { get; set; }
    public int FilesImported { get; set; }
    public int FilesSkipped { get; set; }
    public int FilesDuplicateByPath { get; set; }
    public int FilesDuplicateByHash { get; set; }
    public int FilesMetadataFailed { get; set; }
    public int FilesAutoUpgraded { get; set; }
    public int FilesMarkedForRemoval { get; set; }
    public List<Guid> ImportedLibraryEntryIds { get; set; } = new();
}

internal sealed class ExistingEntryInfo
{
    public string UniqueHash { get; init; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public string Format { get; set; } = string.Empty;
    public int Bitrate { get; set; }
    public int QualityScore { get; set; }
}

/// <summary>
/// Brings the music in Library Sources (the LibraryFolders table) into the library.
/// <para>
/// <see cref="ImportFoldersAsync"/> is the one import pipeline: scan the folders, read tags and
/// check each new file, then add them in batched transactions with path and track-identity
/// de-duplication. It runs off the UI thread, reports throttled progress, and publishes a single
/// <see cref="LibraryImportCompletedEvent"/> at the end, so views refresh once instead of once
/// per file. While a folder is being imported, <see cref="IsFolderImporting"/> is true for it and
/// the folder watcher holds back its own events for that folder.
/// </para>
/// </summary>
public class LibraryFolderScannerService
{
    private readonly ILogger<LibraryFolderScannerService> _logger;
    private readonly DatabaseService _databaseService;
    private readonly LibraryService _libraryService;
    private readonly IEventBus? _eventBus;
    private const int EnsureDefaultFolderMaxAttempts = 3;

    private readonly object _activeLock = new();
    private readonly Dictionary<Guid, int> _activeFolders = new();

    public LibraryFolderScannerService(
        ILogger<LibraryFolderScannerService> logger,
        DatabaseService databaseService,
        LibraryService libraryService,
        IEventBus eventBus)
    {
        _logger = logger;
        _databaseService = databaseService;
        _libraryService = libraryService;
        _eventBus = eventBus;
    }

    /// <summary>True while an import is reading or writing this folder.</summary>
    public bool IsFolderImporting(Guid folderId)
    {
        lock (_activeLock) return _activeFolders.ContainsKey(folderId);
    }

    /// <summary>True while any folder import is running.</summary>
    public bool IsAnyImportRunning
    {
        get { lock (_activeLock) return _activeFolders.Count > 0; }
    }

    private void MarkActive(IEnumerable<Guid> folderIds)
    {
        lock (_activeLock)
        {
            foreach (var id in folderIds)
                _activeFolders[id] = _activeFolders.TryGetValue(id, out var n) ? n + 1 : 1;
        }
    }

    private void ReleaseActive(IEnumerable<Guid> folderIds)
    {
        lock (_activeLock)
        {
            foreach (var id in folderIds)
            {
                if (!_activeFolders.TryGetValue(id, out var n)) continue;
                if (n <= 1) _activeFolders.Remove(id);
                else _activeFolders[id] = n - 1;
            }
        }
    }

    /// <summary>
    /// Registers <paramref name="path"/> as a Library Source (or re-enables the existing row) and
    /// returns its id. <paramref name="watch"/> null leaves an existing row's watch flag alone.
    /// </summary>
    public async Task<(Guid Id, bool IsNew)> RegisterFolderAsync(string path, bool? watch = null)
    {
        var normalized = NormalizePath(path);
        var comparer = GetPathComparer();
        using var context = new AppDbContext();
        var rows = await context.LibraryFolders.ToListAsync();
        var match = rows.FirstOrDefault(r => comparer.Equals(NormalizePath(r.FolderPath), normalized));
        if (match != null)
        {
            var changed = false;
            if (!match.IsEnabled) { match.IsEnabled = true; changed = true; }
            if (watch.HasValue && match.IsWatched != watch.Value) { match.IsWatched = watch.Value; changed = true; }
            if (changed) await context.SaveChangesAsync();
            return (match.Id, false);
        }

        var entity = new LibraryFolderEntity
        {
            Id = Guid.NewGuid(),
            FolderPath = normalized,
            IsEnabled = true,
            IsWatched = watch ?? false,
            AddedAt = DateTime.UtcNow,
        };
        context.LibraryFolders.Add(entity);
        await context.SaveChangesAsync();
        _logger.LogInformation("Registered library folder: {Path}", normalized);
        return (entity.Id, true);
    }

    /// <summary>
    /// The import pipeline. Never throws for file or database problems: they are counted in the
    /// result (and an unexpected failure sets <see cref="LibraryImportResult.Error"/>). Cancelling
    /// while tags are being read still adds the files already checked; cancelling while adding
    /// stops after the current batch. Everything already written stays in the library.
    /// </summary>
    public async Task<LibraryImportResult> ImportFoldersAsync(
        LibraryImportRequest request,
        IProgress<LibraryImportProgress>? progress = null,
        CancellationToken ct = default)
    {
        var stopwatch = Stopwatch.StartNew();
        var result = new LibraryImportResult { Trigger = request.Trigger };
        var state = new ImportCounters();
        var reporter = new ThrottledProgressReporter(progress, request.ProgressInterval);
        List<LibraryFolderEntity> folders = new();

        try
        {
            folders = await LoadFoldersAsync(request.FolderIds, result, ct).ConfigureAwait(false);
            MarkActive(folders.Select(f => f.Id));
            await Task.Run(() => RunPipelineAsync(request, folders, result, state, reporter, ct), CancellationToken.None)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            result.WasCancelled = true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Library import failed");
            result.Error = ex.Message;
            result.Issues.Add(new LibraryImportIssue(LibraryImportIssueKind.Failed, string.Join("; ", result.FolderPaths), ex.Message));
        }
        finally
        {
            ReleaseActive(folders.Select(f => f.Id));
        }

        result.WasCancelled |= ct.IsCancellationRequested;
        result.Elapsed = stopwatch.Elapsed;
        state.CopyTo(result);
        reporter.Report(() => state.Snapshot(LibraryImportPhase.Done, string.Empty), force: true);

        if (request.QueueAnalysis && _eventBus != null)
        {
            foreach (var hash in result.AddedTrackHashes)
                _eventBus.Publish(new TrackAnalysisRequestedEvent(hash));
        }

        _logger.LogInformation(
            "Library import ({Trigger}) {Outcome} in {Ms} ms: {Found} found, {Added} added, {Known} already in library, {Dup} duplicates ({Upgraded} upgraded), {Unsupported} unsupported, {Unreadable} unreadable, {Failed} not saved",
            request.Trigger, result.WasCancelled ? "cancelled" : result.Error != null ? "failed" : "finished",
            (long)result.Elapsed.TotalMilliseconds, result.FilesFound, result.Added, result.AlreadyInLibrary,
            result.Duplicates, result.Upgraded, result.Unsupported, result.Unreadable, result.Failed);

        _eventBus?.Publish(new LibraryImportCompletedEvent(result));
        return result;
    }

    private async Task<List<LibraryFolderEntity>> LoadFoldersAsync(IReadOnlyList<Guid>? folderIds, LibraryImportResult result, CancellationToken ct)
    {
        using var context = new AppDbContext();
        var query = context.LibraryFolders.AsNoTracking().Where(f => f.IsEnabled);
        var rows = await query.ToListAsync(ct).ConfigureAwait(false);
        if (folderIds != null)
        {
            var wanted = folderIds.ToHashSet();
            rows = rows.Where(f => wanted.Contains(f.Id)).ToList();
        }

        var usable = new List<LibraryFolderEntity>();
        foreach (var folder in rows)
        {
            result.FolderIds.Add(folder.Id);
            result.FolderPaths.Add(folder.FolderPath);
            if (Directory.Exists(folder.FolderPath))
            {
                usable.Add(folder);
            }
            else
            {
                _logger.LogWarning("Library folder is missing, skipped: {Path}", folder.FolderPath);
                result.Issues.Add(new LibraryImportIssue(LibraryImportIssueKind.FolderMissing, folder.FolderPath,
                    "The folder can't be found. Reconnect the drive or remove it from Library folders."));
            }
        }
        return usable;
    }

    private async Task RunPipelineAsync(
        LibraryImportRequest request,
        List<LibraryFolderEntity> folders,
        LibraryImportResult result,
        ImportCounters state,
        ThrottledProgressReporter reporter,
        CancellationToken ct)
    {
        // ── Phase 1: scanning folders ──────────────────────────────────────────
        reporter.Report(() => state.Snapshot(LibraryImportPhase.Scanning, string.Empty), force: true);
        var discovered = new List<(string Path, int Folder)>();
        var perFolderFound = new int[folders.Count];

        if (request.Files != null)
        {
            foreach (var file in request.Files.Distinct(GetPathComparer()))
            {
                if (ct.IsCancellationRequested) break;
                if (!File.Exists(file)) continue;
                var folderIndex = folders.FindIndex(f => IsUnder(file, f.FolderPath));
                if (folderIndex < 0) continue;
                ClassifyDiscovered(file, folderIndex, discovered, perFolderFound, result, state);
            }
        }
        else
        {
            var options = new EnumerationOptions
            {
                RecurseSubdirectories = true,
                IgnoreInaccessible = true,
                MatchCasing = MatchCasing.CaseInsensitive,
                ReturnSpecialDirectories = false,
                AttributesToSkip = FileAttributes.System | FileAttributes.Hidden,
            };

            for (var i = 0; i < folders.Count && !ct.IsCancellationRequested; i++)
            {
                try
                {
                    foreach (var file in Directory.EnumerateFiles(folders[i].FolderPath, "*", options))
                    {
                        if (ct.IsCancellationRequested) break;
                        ClassifyDiscovered(file, i, discovered, perFolderFound, result, state);
                        reporter.Report(() => state.Snapshot(LibraryImportPhase.Scanning, Path.GetFileName(file)));
                    }
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
                {
                    _logger.LogWarning(ex, "Error scanning folder {Path}", folders[i].FolderPath);
                    result.Issues.Add(new LibraryImportIssue(LibraryImportIssueKind.Failed, folders[i].FolderPath,
                        $"Part of the folder couldn't be read: {ex.Message}"));
                }
            }
        }

        reporter.Report(() => state.Snapshot(LibraryImportPhase.Scanning, string.Empty), force: true);
        if (ct.IsCancellationRequested) return; // nothing has been checked yet, so nothing to keep

        // ── Phase 2: reading tags & checking files ─────────────────────────────
        var pathComparer = GetPathComparer();
        HashSet<string> existingPaths;
        Dictionary<string, ExistingEntryInfo> existingByHash;
        using (var readContext = new AppDbContext())
        {
            var existingEntries = await readContext.LibraryEntries
                .AsNoTracking()
                .Select(e => new { e.FilePath, e.UniqueHash, e.Format, e.Bitrate })
                .ToListAsync(ct).ConfigureAwait(false);

            existingPaths = existingEntries
                .Select(e => NormalizePath(e.FilePath))
                .Where(p => !string.IsNullOrWhiteSpace(p))
                .ToHashSet(pathComparer);

            existingByHash = existingEntries
                .Where(e => !string.IsNullOrWhiteSpace(e.UniqueHash))
                .GroupBy(e => e.UniqueHash, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.First())
                .ToDictionary(
                    e => e.UniqueHash,
                    e => new ExistingEntryInfo
                    {
                        UniqueHash = e.UniqueHash,
                        FilePath = e.FilePath,
                        Format = e.Format ?? string.Empty,
                        Bitrate = e.Bitrate,
                        QualityScore = ComputeQualityScore(e.Format, e.Bitrate, 0, 0)
                    },
                    StringComparer.OrdinalIgnoreCase);
        }

        // The same file is never read twice: path matches are settled before any tag is opened.
        var candidates = new List<string>(discovered.Count);
        foreach (var (path, _) in discovered)
        {
            if (existingPaths.Contains(NormalizePath(path))) state.AlreadyInLibrary++;
            else candidates.Add(path);
        }

        state.Total = candidates.Count;
        state.Processed = 0;
        reporter.Report(() => state.Snapshot(LibraryImportPhase.Reading, string.Empty), force: true);

        var read = new ReadOutcome?[candidates.Count];
        var parallel = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(Environment.ProcessorCount / 2, 1, 4) };
        Parallel.For(0, candidates.Count, parallel, (i, loop) =>
        {
            if (ct.IsCancellationRequested) { loop.Stop(); return; }
            var path = candidates[i];
            var entry = TryReadEntry(path, out var error);
            read[i] = new ReadOutcome(path, entry, error);
            state.CountRead(entry == null);
            reporter.Report(() => state.Snapshot(LibraryImportPhase.Reading, Path.GetFileName(path)));
        });

        foreach (var outcome in read)
        {
            if (outcome is { Entry: null } bad)
                result.Issues.Add(new LibraryImportIssue(LibraryImportIssueKind.Unreadable, bad.Path, bad.Error ?? "The file couldn't be read."));
        }
        reporter.Report(() => state.Snapshot(LibraryImportPhase.Reading, string.Empty), force: true);

        // ── Phase 3: adding to the library ─────────────────────────────────────
        // A cancel during reading still adds what was checked; a cancel from here on stops after
        // the batch in flight.
        var keepCheckedAfterCancel = ct.IsCancellationRequested;
        var toAdd = read.Where(o => o is { Entry: not null }).Select(o => o!.Entry!).ToList();
        state.Total = toAdd.Count;
        state.Processed = 0;
        reporter.Report(() => state.Snapshot(LibraryImportPhase.Adding, string.Empty), force: true);

        var batch = new List<LibraryEntryEntity>(request.BatchSize);
        foreach (var entry in toAdd)
        {
            if (!keepCheckedAfterCancel && ct.IsCancellationRequested) break;

            if (existingByHash.TryGetValue(entry.UniqueHash, out var existing))
            {
                var candidateScore = TryExtractScore(entry.QualityDetails) ?? ComputeQualityScore(entry.Format, entry.Bitrate, 0, 0);
                if (candidateScore > existing.QualityScore && await TryUpgradeExistingEntryAsync(existing, entry, candidateScore, CancellationToken.None).ConfigureAwait(false))
                {
                    state.Upgraded++;
                    existing.FilePath = entry.FilePath;
                    existing.Format = entry.Format;
                    existing.Bitrate = entry.Bitrate;
                    existing.QualityScore = candidateScore;
                }
                state.Duplicates++;
                state.Processed++;
                reporter.Report(() => state.Snapshot(LibraryImportPhase.Adding, Path.GetFileName(entry.FilePath)));
                continue;
            }

            existingByHash[entry.UniqueHash] = new ExistingEntryInfo
            {
                UniqueHash = entry.UniqueHash,
                FilePath = entry.FilePath,
                Format = entry.Format,
                Bitrate = entry.Bitrate,
                QualityScore = TryExtractScore(entry.QualityDetails) ?? ComputeQualityScore(entry.Format, entry.Bitrate, 0, 0)
            };
            batch.Add(entry);

            if (batch.Count >= request.BatchSize)
            {
                await FlushBatchAsync(batch, result, state).ConfigureAwait(false);
                reporter.Report(() => state.Snapshot(LibraryImportPhase.Adding, Path.GetFileName(entry.FilePath)));
            }
        }

        if (batch.Count > 0)
            await FlushBatchAsync(batch, result, state).ConfigureAwait(false);

        // Folder bookkeeping: what a full walk found, and when it last completed.
        if (request.Files == null)
            await UpdateFolderMetadataAsync(folders, perFolderFound, completed: !ct.IsCancellationRequested).ConfigureAwait(false);
    }

    private static void ClassifyDiscovered(
        string file, int folderIndex, List<(string Path, int Folder)> discovered, int[] perFolderFound,
        LibraryImportResult result, ImportCounters state)
    {
        switch (LibraryAudioFormats.Classify(file))
        {
            case LibraryAudioFormats.Kind.Supported:
                discovered.Add((file, folderIndex));
                perFolderFound[folderIndex]++;
                state.FilesFound++;
                break;
            case LibraryAudioFormats.Kind.Unsupported:
                state.Unsupported++;
                result.Issues.Add(new LibraryImportIssue(LibraryImportIssueKind.Unsupported, file,
                    $"{Path.GetExtension(file).TrimStart('.').ToUpperInvariant()} files can't be imported yet."));
                break;
        }
    }

    private async Task FlushBatchAsync(List<LibraryEntryEntity> batch, LibraryImportResult result, ImportCounters state)
    {
        try
        {
            using var context = new AppDbContext();
            context.LibraryEntries.AddRange(batch);
            await context.SaveChangesAsync().ConfigureAwait(false);

            foreach (var entry in batch)
            {
                result.AddedEntryIds.Add(entry.Id);
                if (!string.IsNullOrWhiteSpace(entry.UniqueHash)) result.AddedTrackHashes.Add(entry.UniqueHash);
            }
            state.Added += batch.Count;
        }
        catch (Exception ex)
        {
            // Reported as "not saved", never as added.
            _logger.LogError(ex, "Failed to save a batch of {Count} library entries", batch.Count);
            state.Failed += batch.Count;
            foreach (var entry in batch)
                result.Issues.Add(new LibraryImportIssue(LibraryImportIssueKind.SaveFailed, entry.FilePath, ex.GetBaseException().Message));
        }
        state.Processed += batch.Count;
        batch.Clear();
    }

    private async Task UpdateFolderMetadataAsync(List<LibraryFolderEntity> folders, int[] perFolderFound, bool completed)
    {
        if (folders.Count == 0) return;
        try
        {
            using var context = new AppDbContext();
            var ids = folders.Select(f => f.Id).ToList();
            var rows = await context.LibraryFolders.Where(f => ids.Contains(f.Id)).ToListAsync().ConfigureAwait(false);
            foreach (var row in rows)
            {
                var index = folders.FindIndex(f => f.Id == row.Id);
                if (index < 0) continue;
                row.TracksFound = perFolderFound[index];
                if (completed) row.LastScannedAt = DateTime.UtcNow;
            }
            await context.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to update library folder scan metadata");
        }
    }

    private static bool IsUnder(string file, string folder)
    {
        var f = NormalizePath(folder) + Path.DirectorySeparatorChar;
        return NormalizePath(file).StartsWith(f, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
    }

    /// <summary>
    /// Imports one Library Source the way Library Health's "Scan now" always has (analysis is
    /// queued for what's added). Prefer the import sheet (<c>ILibraryImportLauncher</c>) for
    /// anything the user starts.
    /// </summary>
    public async Task<ScanResult> ScanFolderAsync(Guid folderId, CancellationToken ct = default)
    {
        var import = await ImportFoldersAsync(new LibraryImportRequest
        {
            FolderIds = new[] { folderId },
            QueueAnalysis = true,
            Trigger = LibraryImportTrigger.Maintenance,
        }, null, ct).ConfigureAwait(false);

        return new ScanResult
        {
            FolderId = folderId,
            FolderPath = import.FolderPaths.FirstOrDefault() ?? string.Empty,
            TotalFilesFound = import.FilesFound,
            FilesImported = import.Added,
            FilesSkipped = import.AlreadyInLibrary + import.Duplicates + import.Unreadable,
            FilesDuplicateByPath = import.AlreadyInLibrary,
            FilesDuplicateByHash = import.Duplicates,
            FilesMetadataFailed = import.Unreadable,
            FilesAutoUpgraded = import.Upgraded,
            FilesMarkedForRemoval = import.Upgraded,
            ImportedLibraryEntryIds = import.AddedEntryIds.ToList(),
        };
    }

    /// <summary>
    /// Reads one file into a library entry. Returns null (with <paramref name="error"/>) when the
    /// file is empty or its audio/tags can't be parsed, i.e. it is corrupt or not really audio.
    /// </summary>
    internal LibraryEntryEntity? TryReadEntry(string filePath, out string? error)
    {
        error = null;
        try
        {
            var fileInfo = new FileInfo(filePath);
            if (!fileInfo.Exists)
            {
                error = "The file disappeared before it could be read.";
                return null;
            }
            if (fileInfo.Length == 0)
            {
                error = "Empty file (0 bytes).";
                return null;
            }

            string artist;
            string title;
            string album;
            int duration, bitrate, sampleRate, bitsPerSample;
            double? tagBpm = null;

            try
            {
                using var file = TagLib.File.Create(filePath);
                artist = string.IsNullOrWhiteSpace(file.Tag.FirstPerformer) ? "Unknown Artist" : file.Tag.FirstPerformer;
                title = string.IsNullOrWhiteSpace(file.Tag.Title) ? Path.GetFileNameWithoutExtension(filePath) : file.Tag.Title;
                album = file.Tag.Album ?? string.Empty;
                duration = (int)file.Properties.Duration.TotalSeconds;
                bitrate = file.Properties.AudioBitrate;
                sampleRate = file.Properties.AudioSampleRate;
                bitsPerSample = file.Properties.BitsPerSample;

                // Trust a file-embedded BPM tag (written by Rekordbox/Serato/etc.) over ORBIT's own
                // Essentia analysis, which has a quantization bias on breakbeat/DNB content.
                // Sanity-bounded to BpmDetectionService's [60,220] range so a placeholder tag isn't trusted.
                var rawTagBpm = file.Tag.BeatsPerMinute;
                if (rawTagBpm is >= 60 and <= 220)
                    tagBpm = rawTagBpm;
            }
            catch (Exception ex) when (ex is TagLib.CorruptFileException or TagLib.UnsupportedFormatException or IOException or ArgumentException or IndexOutOfRangeException or NullReferenceException or OverflowException or InvalidOperationException)
            {
                _logger.LogDebug(ex, "Unreadable audio file skipped: {Path}", filePath);
                error = ex is TagLib.UnsupportedFormatException
                    ? "Not a recognised audio file."
                    : $"Corrupt or unreadable audio ({ex.Message}).";
                return null;
            }

            var format = Path.GetExtension(filePath).TrimStart('.').ToLowerInvariant();
            var qualityScore = ComputeQualityScore(format, bitrate, sampleRate, bitsPerSample);

            return new LibraryEntryEntity
            {
                Id = Guid.NewGuid(),
                UniqueHash = BuildLibraryUniqueHash(artist, title, duration, filePath),
                Artist = artist,
                Title = title,
                Album = album,
                DurationSeconds = duration,
                FilePath = filePath,
                AddedAt = DateTime.UtcNow,
                Bitrate = bitrate,
                Format = format,
                AvailabilityState = TrackAvailabilityState.LocalUnanalyzed,
                QualityDetails = $"scanner:v2;score={qualityScore};format={format};bitrate={bitrate};samplerate={sampleRate};bitdepth={bitsPerSample}",
                TagBPM = tagBpm,
                // Seed the primary BPM from the tag; analysis won't overwrite it once TagBPM is set.
                BPM = tagBpm
            };
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to create entry for {Path}", filePath);
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Creates a library entry for one file, or null when it can't be read.</summary>
    private LibraryEntryEntity? CreateLibraryEntry(string filePath) => TryReadEntry(filePath, out _);

    private sealed record ReadOutcome(string Path, LibraryEntryEntity? Entry, string? Error);

    /// <summary>Running counters, written by the pipeline and snapshotted for progress reports.</summary>
    private sealed class ImportCounters
    {
        private int _processed;
        private int _unreadable;
        public int FilesFound;
        public int Total;
        public int AlreadyInLibrary;
        public int Duplicates;
        public int Upgraded;
        public int Unsupported;
        public int Added;
        public int Failed;

        public int Processed
        {
            get => Volatile.Read(ref _processed);
            set => Volatile.Write(ref _processed, value);
        }

        public int Unreadable => Volatile.Read(ref _unreadable);

        public void CountRead(bool unreadable)
        {
            Interlocked.Increment(ref _processed);
            if (unreadable) Interlocked.Increment(ref _unreadable);
        }

        public LibraryImportProgress Snapshot(LibraryImportPhase phase, string currentFile) => new(
            phase,
            FilesFound,
            phase == LibraryImportPhase.Scanning ? FilesFound : Processed,
            phase == LibraryImportPhase.Scanning ? 0 : Total,
            currentFile,
            Added, AlreadyInLibrary, Duplicates, Upgraded, Unsupported, Unreadable, Failed);

        public void CopyTo(LibraryImportResult result)
        {
            result.FilesFound = FilesFound;
            result.Added = Added;
            result.AlreadyInLibrary = AlreadyInLibrary;
            result.Duplicates = Duplicates;
            result.Upgraded = Upgraded;
            result.Unsupported = Unsupported;
            result.Unreadable = Unreadable;
            result.Failed = Failed;
        }
    }

    /// <summary>
    /// Ensures that the specified path is registered as a library folder.
    /// Uses the existing folder if found, or creates a new one.
    /// </summary>
    public async Task EnsureDefaultFolderAsync(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path)) return;
        var normalizedPath = NormalizePath(path);
        var pathComparer = GetPathComparer();

        for (var attempt = 1; attempt <= EnsureDefaultFolderMaxAttempts; attempt++)
        {
            try
            {
                using var context = new AppDbContext();
                var existingRows = await context.LibraryFolders
                    .Where(f => !string.IsNullOrWhiteSpace(f.FolderPath))
                    .ToListAsync();

                var matches = existingRows
                    .Where(row => pathComparer.Equals(NormalizePath(row.FolderPath), normalizedPath))
                    .OrderBy(row => row.AddedAt)
                    .ToList();

                if (matches.Count > 0)
                {
                    var primary = matches[0];
                    var shouldSave = false;

                    if (!pathComparer.Equals(NormalizePath(primary.FolderPath), normalizedPath))
                    {
                        primary.FolderPath = normalizedPath;
                        shouldSave = true;
                    }

                    if (!primary.IsEnabled)
                    {
                        primary.IsEnabled = true;
                        shouldSave = true;
                    }

                    if (matches.Count > 1)
                    {
                        context.LibraryFolders.RemoveRange(matches.Skip(1));
                        shouldSave = true;
                    }

                    if (shouldSave)
                    {
                        _logger.LogInformation("Consolidating duplicate library folder rows for {Path}", normalizedPath);
                        await context.SaveChangesAsync();
                    }

                    return;
                }

                _logger.LogInformation("Registering new default library folder: {Path}", normalizedPath);

                context.LibraryFolders.Add(new LibraryFolderEntity
                {
                    FolderPath = normalizedPath,
                    IsEnabled = true,
                    AddedAt = DateTime.UtcNow
                });

                await context.SaveChangesAsync();
                return;
            }
            catch (Exception ex) when (IsTransientFolderRegistrationException(ex) && attempt < EnsureDefaultFolderMaxAttempts)
            {
                var delayMs = 75 * attempt;
                _logger.LogWarning(ex,
                    "Transient folder registration failure for {Path} on attempt {Attempt}/{MaxAttempts}; retrying after {DelayMs}ms",
                    normalizedPath,
                    attempt,
                    EnsureDefaultFolderMaxAttempts,
                    delayMs);
                await Task.Delay(delayMs);
            }
        }
    }

    private static bool IsTransientFolderRegistrationException(Exception ex)
    {
        if (ex is DbUpdateException || ex is TimeoutException || ex is IOException)
        {
            return true;
        }

        for (var current = ex; current is not null; current = current.InnerException)
        {
            var message = current.Message;
            if (string.IsNullOrWhiteSpace(message))
            {
                continue;
            }

            if (message.Contains("database is locked", StringComparison.OrdinalIgnoreCase) ||
                message.Contains("database is busy", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static StringComparer GetPathComparer()
    {
        return OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal;
    }

    private static string NormalizePath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return string.Empty;

        return Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private static string BuildLibraryUniqueHash(string artist, string title, int durationSeconds, string filePath)
    {
        static string NormalizeToken(string value)
        {
            return new string((value ?? string.Empty)
                .ToLowerInvariant()
                .Where(c => !char.IsWhiteSpace(c))
                .ToArray());
        }

        var artistToken = NormalizeToken(artist);
        var titleToken = NormalizeToken(title);

        // Align with Track.UniqueHash identity format where possible.
        if (!string.IsNullOrWhiteSpace(artistToken) || !string.IsNullOrWhiteSpace(titleToken))
        {
            return $"{artistToken}-{titleToken}".Trim('-');
        }

        // Fallback for tag-less files: stable hash from path + duration.
        var canonical = $"{NormalizePath(filePath).ToLowerInvariant()}|{durationSeconds}";
        using var sha1 = SHA1.Create();
        var bytes = sha1.ComputeHash(Encoding.UTF8.GetBytes(canonical));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static int ComputeQualityScore(string? format, int bitrate, int sampleRate, int bitsPerSample)
    {
        var fmt = (format ?? string.Empty).Trim().ToLowerInvariant();

        int score = fmt switch
        {
            "flac" => 500,
            "wav" => 470,
            "aiff" => 460,
            "aif" => 460,
            "m4a" => 260,
            "aac" => 250,
            "ogg" => 240,
            "mp3" => 200,
            _ => 150
        };

        score += Math.Clamp(bitrate, 0, 1600) / 4;

        if (sampleRate >= 96000) score += 120;
        else if (sampleRate >= 48000) score += 40;

        if (bitsPerSample >= 24) score += 80;

        return score;
    }

    private static int? TryExtractScore(string? qualityDetails)
    {
        if (string.IsNullOrWhiteSpace(qualityDetails)) return null;

        const string marker = "score=";
        var idx = qualityDetails.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return null;

        var start = idx + marker.Length;
        var end = qualityDetails.IndexOf(';', start);
        var token = end >= 0 ? qualityDetails[start..end] : qualityDetails[start..];

        return int.TryParse(token, out var value) ? value : null;
    }

    private async Task<bool> TryUpgradeExistingEntryAsync(ExistingEntryInfo existing, LibraryEntryEntity candidate, int candidateScore, CancellationToken ct)
    {
        try
        {
            using var context = new AppDbContext();
            var entry = await context.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == candidate.UniqueHash, ct);
            if (entry == null) return false;

            var currentPath = NormalizePath(entry.FilePath);
            var candidatePath = NormalizePath(candidate.FilePath);

            if (GetPathComparer().Equals(currentPath, candidatePath)) return false;

            var previousPath = entry.FilePath;

            entry.FilePath = candidate.FilePath;
            entry.Format = candidate.Format;
            entry.Bitrate = Math.Max(candidate.Bitrate, entry.Bitrate);
            if (candidate.DurationSeconds.HasValue && candidate.DurationSeconds.Value > 0)
            {
                entry.DurationSeconds = candidate.DurationSeconds;
            }

            if (string.IsNullOrWhiteSpace(entry.OriginalFilePath))
            {
                entry.OriginalFilePath = previousPath;
            }

            entry.FilePathUpdatedAt = DateTime.UtcNow;
            entry.QualityDetails = $"scanner-upgrade:v1;score={candidateScore};from={previousPath};to={candidate.FilePath}";
            entry.Comments = AppendRemovalCandidateComment(entry.Comments, previousPath);

            context.LibraryActionLogs.Add(new LibraryActionLogEntity
            {
                BatchId = Guid.NewGuid(),
                ActionType = LibraryActionType.Consolidate,
                SourcePath = previousPath,
                DestinationPath = candidate.FilePath,
                Timestamp = DateTime.Now,
                TrackArtist = entry.Artist,
                TrackTitle = entry.Title
            });

            await context.SaveChangesAsync(ct);

            _logger.LogInformation(
                "Auto-upgraded quality for {Hash}: {From} -> {To} (score {Score})",
                entry.UniqueHash,
                previousPath,
                candidate.FilePath,
                candidateScore);

            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to auto-upgrade duplicate {Hash}", candidate.UniqueHash);
            return false;
        }
    }

    private static string AppendRemovalCandidateComment(string? existing, string pathToRemove)
    {
        var marker = $"AUTO_REMOVE_CANDIDATE:{pathToRemove}";
        if (!string.IsNullOrWhiteSpace(existing) && existing.Contains(marker, StringComparison.OrdinalIgnoreCase))
        {
            return existing;
        }

        return string.IsNullOrWhiteSpace(existing)
            ? marker
            : $"{existing} | {marker}";
    }
}

