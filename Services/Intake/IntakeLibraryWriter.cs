using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;
using SLSKDONET.Services.Library.Files;

namespace SLSKDONET.Services.Intake;

/// <summary>What Apply needs from the library (a seam so Apply can be tested without the real library).</summary>
public interface IIntakeLibraryWriter
{
    /// <summary>The folder kept files are filed under (the library's download folder).</summary>
    string LibraryRoot { get; }

    /// <summary>Where the user's path template files this track (no directories created).</summary>
    string TargetPathFor(IntakeItemEntity item);

    /// <summary>Adds files already copied under <see cref="LibraryRoot"/> to the library and queues their analysis.</summary>
    Task<int> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct);

    /// <summary>Swaps the library track's file for <paramref name="newFile"/> (moved in), keeping cues and grid. Returns the new path.</summary>
    Task<string> ReplaceAsync(IntakeItemEntity item, string newFile, CancellationToken ct);

    /// <summary>Queues a Soulseek search for each track. Returns how many were queued.</summary>
    Task<int> QueueUpgradesAsync(string title, IReadOnlyList<IntakeItemEntity> items, CancellationToken ct);
}

/// <summary>
/// The real writer: the path template (<see cref="PathProviderService"/>), the library import
/// pipeline (<see cref="LibraryFolderScannerService"/>, files only), the quality upgrade swap
/// (<see cref="QualityUpgradeService"/>: old file to ORBIT's trash, cues and beatgrid shifted by the
/// measured decoder offset) and the download queue for upgrades.
/// </summary>
public sealed class IntakeLibraryWriter : IIntakeLibraryWriter
{
    private readonly AppConfig _config;
    private readonly PathProviderService _paths;
    private readonly LibraryFolderScannerService _scanner;
    private readonly QualityUpgradeService _upgrade;
    private readonly AudioContentHasher _hasher;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly Func<DownloadManager?> _downloads;
    private readonly ILogger<IntakeLibraryWriter> _logger;

    public IntakeLibraryWriter(
        AppConfig config,
        PathProviderService paths,
        LibraryFolderScannerService scanner,
        QualityUpgradeService upgrade,
        AudioContentHasher hasher,
        IDbContextFactory<AppDbContext> dbFactory,
        Func<DownloadManager?> downloads,
        ILogger<IntakeLibraryWriter> logger)
    {
        _config = config;
        _paths = paths;
        _scanner = scanner;
        _upgrade = upgrade;
        _hasher = hasher;
        _dbFactory = dbFactory;
        _downloads = downloads;
        _logger = logger;
    }

    public string LibraryRoot => Path.GetFullPath(_config.DownloadDirectory ?? "Downloads");

    public string TargetPathFor(IntakeItemEntity item) => _paths.ResolveTrackPath(new Track
    {
        Artist = item.Artist ?? "Unknown Artist",
        Title = item.Title ?? Path.GetFileNameWithoutExtension(item.SourcePath),
        Album = item.Album,
        Filename = Path.GetFileName(item.SourcePath),
        Format = IntakeClassifier.Format(item.Extension),
        Length = item.DurationSeconds,
        Bitrate = item.BitrateKbps,
    });

    public async Task<int> ImportFilesAsync(IReadOnlyList<string> paths, CancellationToken ct)
    {
        if (paths.Count == 0) return 0;
        // Files are only imported when they sit under a Library Source; the download folder is one.
        await _scanner.EnsureDefaultFolderAsync(LibraryRoot).ConfigureAwait(false);
        var result = await _scanner.ImportFoldersAsync(new LibraryImportRequest
        {
            Files = paths,
            QueueAnalysis = true,
            Trigger = LibraryImportTrigger.Manual,
        }, null, ct).ConfigureAwait(false);
        if (result.Error != null)
            throw new IOException($"Adding the files to the library failed: {result.Error}");
        return result.AddedTrackHashes.Count;
    }

    public async Task<string> ReplaceAsync(IntakeItemEntity item, string newFile, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(item.MatchedTrackHash) || string.IsNullOrEmpty(item.MatchedFilePath))
            throw new InvalidOperationException("This file isn't matched to a library track.");

        var result = await _upgrade.ReplaceWithOffsetEstimationAsync(
            item.MatchedTrackHash, newFile, item.MatchedFilePath, LibraryRoot, allowProtectedOverride: false, ct).ConfigureAwait(false);

        // The swap moves the file and shifts cues; the row still describes the old file.
        var hash = await _hasher.ComputeAsync(result.NewPath, ct).ConfigureAwait(false);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == item.MatchedTrackHash, ct).ConfigureAwait(false);
        if (entry != null)
        {
            entry.Format = IntakeClassifier.Format(item.Extension);
            entry.Bitrate = item.BitrateKbps;
            if (item.DurationSeconds > 0) entry.DurationSeconds = item.DurationSeconds;
            entry.AudioContentHash = hash?.Value;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }
        _logger.LogInformation("Intake: replaced {Old} with {New} (offset {Offset:0.0} ms, old file in {Trash})",
            item.MatchedFilePath, result.NewPath, result.MeasuredOffsetSeconds * 1000, result.TrashedOldPath);
        return result.NewPath;
    }

    public async Task<int> QueueUpgradesAsync(string title, IReadOnlyList<IntakeItemEntity> items, CancellationToken ct)
    {
        var withNames = items.Where(i => !string.IsNullOrWhiteSpace(i.Artist) && !string.IsNullOrWhiteSpace(i.Title)).ToList();
        if (withNames.Count == 0) return 0;
        var downloads = _downloads() ?? throw new InvalidOperationException("The download manager isn't available.");

        var job = new PlaylistJob
        {
            Id = Guid.NewGuid(),
            SourceTitle = title,
            SourceType = "Intake",
            DestinationFolder = string.Empty,
            CreatedAt = DateTime.UtcNow,
            DateUpdated = DateTime.UtcNow,
        };
        foreach (var i in withNames)
            job.OriginalTracks.Add(new Track { Artist = i.Artist, Title = i.Title, Album = i.Album, Length = i.DurationSeconds });

        var r = await downloads.QueueProject(job, userInitiated: true).ConfigureAwait(false);
        return r.QueuedForDownload;
    }
}
