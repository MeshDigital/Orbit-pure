using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;

namespace SLSKDONET.Services.Library.Files;

/// <summary>
/// LF6 (#348): replaces a lower-quality library file with a better one in place. The new file moves
/// into the old file's folder under the old base name with the new extension, the old file goes to
/// <see cref="OrbitTrash"/>, and the three path columns are repointed at the new path.
/// </summary>
public sealed class QualityUpgradeService
{
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly ILogger<QualityUpgradeService> _logger;

    public QualityUpgradeService(IDbContextFactory<AppDbContext> dbFactory, ILogger<QualityUpgradeService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<string> ReplaceAsync(string hash, string newFile, string oldFile, string downloadDir, CancellationToken ct = default)
    {
        var target = Path.Combine(
            Path.GetDirectoryName(oldFile)!,
            Path.GetFileNameWithoutExtension(oldFile) + Path.GetExtension(newFile));

        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Move(newFile, target);
        OrbitTrash.TrashFile(oldFile, downloadDir);

        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        var entry = await db.LibraryEntries.FirstOrDefaultAsync(e => e.UniqueHash == hash, ct).ConfigureAwait(false);
        if (entry != null)
        {
            entry.FilePath = target;
            entry.FilePathUpdatedAt = DateTime.UtcNow;
        }

        var playlistTracks = await db.PlaylistTracks.Where(t => t.TrackUniqueHash == hash).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in playlistTracks)
            t.ResolvedFilePath = target;

        var tracks = await db.Tracks.Where(t => t.GlobalId == hash).ToListAsync(ct).ConfigureAwait(false);
        foreach (var t in tracks)
            t.LocalFilePath = target;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        _logger.LogInformation("Upgraded {Hash}: {Old} -> {New}", hash, oldFile, target);
        return target;
    }
}
