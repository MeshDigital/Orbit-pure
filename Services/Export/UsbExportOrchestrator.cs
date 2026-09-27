using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.IO;
using SLSKDONET.Services.Library;

namespace SLSKDONET.Services.Export;

public enum ExportMode { XmlOnly, FilesAndXml }

public record ExportProgress(int Total, int Copied, int Skipped, string CurrentFile, bool IsComplete);

/// <summary>
/// Orchestrates playlist export to USB/folder:
///   Phase A — copy audio files to destination
///   Phase B — write Rekordbox XML with destination paths
///
/// The XML is written to {dest}/PIONEER/rekordbox.xml — matching where Rekordbox itself places
/// device-exported metadata, so it's easy to find, but this is NOT a CDJ-native database. CDJ
/// hardware reads Pioneer's own binary PDB+ANLZ device library (written only by Rekordbox
/// desktop's own "Export to Device" feature) — it does not parse XML directly, and writing that
/// binary format is a deliberate non-goal here (see DOCS/REKORDBOX_EXPORT_ARCHITECTURE.md — an
/// encrypted, reverse-engineered format with no .NET precedent). The real, required workflow is:
/// this export → open Rekordbox desktop → Preferences → Advanced → rekordbox xml → import this
/// file → then use Rekordbox's own device-export feature to actually prepare a CDJ-ready USB. A
/// drive containing only what this class writes will not play on a CDJ if plugged in directly.
/// </summary>
public sealed class UsbExportOrchestrator
{
    private readonly PlaylistExportService _exportService;
    private readonly IFileWriteService _fileWriteService;
    private readonly ILogger<UsbExportOrchestrator> _logger;

    public UsbExportOrchestrator(
        PlaylistExportService exportService,
        IFileWriteService fileWriteService,
        ILogger<UsbExportOrchestrator> logger)
    {
        _exportService = exportService;
        _fileWriteService = fileWriteService;
        _logger = logger;
    }

    /// <param name="playlistName">Human-readable playlist name used in the XML and folder.</param>
    /// <param name="tracks">Tracks to export (resolved paths may be empty for undownloaded tracks).</param>
    /// <param name="destinationRoot">
    ///   FilesAndXml mode → root of USB/folder (e.g. D:\ or /Volumes/USB1).
    ///   XmlOnly mode → full target XML file path.
    /// </param>
    public async Task ExportAsync(
        string playlistName,
        IReadOnlyList<PlaylistTrack> tracks,
        string destinationRoot,
        ExportMode mode,
        IProgress<ExportProgress>? progress = null,
        CancellationToken ct = default,
        Guid? folderId = null)
    {
        _logger.LogInformation(
            "Starting {Mode} export of '{Playlist}' ({Count} tracks) → {Dest}",
            mode, playlistName, tracks.Count, destinationRoot);

        if (mode == ExportMode.FilesAndXml)
            await ExportFilesAndXmlAsync(playlistName, tracks, destinationRoot, progress, ct, folderId);
        else
            await ExportXmlOnlyAsync(playlistName, tracks, destinationRoot, progress, ct, folderId);
    }

    // ─── FilesAndXml ─────────────────────────────────────────────────────────

    private async Task ExportFilesAndXmlAsync(
        string playlistName,
        IReadOnlyList<PlaylistTrack> tracks,
        string usbRoot,
        IProgress<ExportProgress>? progress,
        CancellationToken ct,
        Guid? folderId = null)
    {
        string audioDir = Path.Combine(usbRoot, "OrbitAudio", Sanitize(playlistName));
        Directory.CreateDirectory(audioDir);

        // Pre-flight: estimate total size vs. free space so a large export warns up front instead
        // of failing silently partway through a long copy with no indication of why. Also detects
        // FAT32 (still required by some older CDJ models) to catch its hard 4GB single-file limit
        // per-track below, rather than letting File-copy fail with an opaque IOException.
        long totalBytes = 0;
        foreach (var t in tracks)
        {
            if (string.IsNullOrEmpty(t.ResolvedFilePath) || !File.Exists(t.ResolvedFilePath)) continue;
            try { totalBytes += new FileInfo(t.ResolvedFilePath).Length; } catch { /* counted during the real copy below instead */ }
        }
        string driveFormat = "";
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(usbRoot)) ?? usbRoot);
            driveFormat = drive.DriveFormat;
            if (drive.AvailableFreeSpace < totalBytes)
            {
                _logger.LogWarning(
                    "Export destination may not have enough free space: need ~{NeedMb}MB, {FreeMb}MB available on {Root}.",
                    totalBytes / 1024 / 1024, drive.AvailableFreeSpace / 1024 / 1024, usbRoot);
                progress?.Report(new(tracks.Count, 0, 0,
                    $"Warning: ~{totalBytes / 1024 / 1024}MB needed, only {drive.AvailableFreeSpace / 1024 / 1024}MB free", false));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not check free space / filesystem type for {Root}", usbRoot);
        }
        bool isFat32 = string.Equals(driveFormat, "FAT32", StringComparison.OrdinalIgnoreCase);
        const long Fat32MaxFileSize = 4_294_967_295L; // 4GiB - 1 byte, FAT32's hard per-file limit

        // Phase A: copy files, building a map from local path → destination path
        var pathMap = new ConcurrentDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, skipped = 0, reused = 0;

        // Cheap, sequential pre-pass: missing-file/FAT32-oversize skips and the same-size-already-
        // there reuse fast path are all local stat checks (no actual copy I/O), so there's nothing
        // to gain from parallelizing them — only the tracks that genuinely need copying go into
        // toCopy below.
        var toCopy = new List<(PlaylistTrack Track, string DestPath)>();
        foreach (var track in tracks)
        {
            ct.ThrowIfCancellationRequested();

            if (string.IsNullOrEmpty(track.ResolvedFilePath) || !File.Exists(track.ResolvedFilePath))
            {
                skipped++;
                progress?.Report(new(tracks.Count, copied, skipped, track.Title ?? "—", false));
                _logger.LogWarning("Skipping track with missing file: {Title}", track.Title);
                continue;
            }

            var sourceInfo = new FileInfo(track.ResolvedFilePath);

            if (isFat32 && sourceInfo.Length > Fat32MaxFileSize)
            {
                skipped++;
                progress?.Report(new(tracks.Count, copied, skipped, track.Title ?? "—", false));
                _logger.LogWarning(
                    "Skipping '{Title}' ({SizeMb}MB) — exceeds FAT32's 4GB file size limit on {Root}. Reformat the drive as exFAT to include it (check your CDJ model's supported filesystems first).",
                    track.Title, sourceInfo.Length / 1024 / 1024, usbRoot);
                continue;
            }

            var destPath = BuildDeterministicDestPath(audioDir, track);

            // Re-export fast path: a same-size file already at the deterministic destination is
            // treated as already correctly copied — skips re-writing the entire playlist's worth
            // of files (and the USB wear that goes with it) on every re-export after new downloads.
            if (File.Exists(destPath) && new FileInfo(destPath).Length == sourceInfo.Length)
            {
                pathMap[track.ResolvedFilePath] = destPath;
                reused++;
                progress?.Report(new(tracks.Count, copied, skipped, Path.GetFileName(destPath), false));
                continue;
            }

            toCopy.Add((track, destPath));
        }

        // Copy the files that actually need it with bounded parallelism. USB/removable media
        // write throughput is the real bottleneck (SafeWriteService funnels every write through
        // one shared background writer), but each copy's read + crash-journal checkpoint + post-
        // copy verification + atomic rename can now overlap across files instead of one file's
        // entire copy completing before the next one's even starts reading.
        const int MaxConcurrentCopies = 4;
        using var copySemaphore = new SemaphoreSlim(MaxConcurrentCopies);
        var copyTasks = toCopy.Select(async item =>
        {
            await copySemaphore.WaitAsync(ct);
            try
            {
                ct.ThrowIfCancellationRequested();
                progress?.Report(new(tracks.Count, copied, skipped, Path.GetFileName(item.DestPath), false));

                bool ok = await _fileWriteService.CopyFileAtomicAsync(item.Track.ResolvedFilePath!, item.DestPath, preserveTimestamps: true, ct);
                if (!ok)
                {
                    // CopyFileAtomicAsync already logged the specific cause (disk full, access
                    // denied, verification failure, etc.) — one bad file must not abort the export.
                    Interlocked.Increment(ref skipped);
                    progress?.Report(new(tracks.Count, copied, skipped, item.Track.Title ?? "—", false));
                    _logger.LogWarning("Failed to copy '{Title}' to {Dest} — skipping, see prior log entry for cause.", item.Track.Title, item.DestPath);
                    return;
                }

                pathMap[item.Track.ResolvedFilePath!] = item.DestPath;
                Interlocked.Increment(ref copied);
                _logger.LogDebug("Copied {Src} → {Dest}", item.Track.ResolvedFilePath, item.DestPath);
            }
            finally
            {
                copySemaphore.Release();
            }
        });

        await Task.WhenAll(copyTasks);

        // Phase B: write XML with dest paths instead of local paths
        var pioneerDir = Path.Combine(usbRoot, "PIONEER");
        Directory.CreateDirectory(pioneerDir);
        var xmlPath = Path.Combine(pioneerDir, "rekordbox.xml");

        progress?.Report(new(tracks.Count, copied, skipped, "Writing rekordbox.xml…", false));
        await _exportService.ExportToRekordboxXmlAsync(playlistName, tracks, xmlPath, pathMap, folderId);

        progress?.Report(new(tracks.Count, copied, skipped, "Done", true));
        _logger.LogInformation(
            "FilesAndXml export complete: {Copied} copied, {Reused} already up to date, {Skipped} skipped. XML → {Xml}",
            copied, reused, skipped, xmlPath);
    }

    // ─── XmlOnly ─────────────────────────────────────────────────────────────

    private async Task ExportXmlOnlyAsync(
        string playlistName,
        IReadOnlyList<PlaylistTrack> tracks,
        string xmlFilePath,
        IProgress<ExportProgress>? progress,
        CancellationToken ct,
        Guid? folderId = null)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(tracks.Count, 0, 0, "Writing rekordbox.xml…", false));
        await _exportService.ExportToRekordboxXmlAsync(playlistName, tracks, xmlFilePath, folderId: folderId);
        progress?.Report(new(tracks.Count, tracks.Count, 0, "Done", true));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static readonly char[] _invalidChars = Path.GetInvalidFileNameChars();

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => _invalidChars.Contains(c) ? '_' : c)).Trim();

    /// <summary>
    /// Deterministic per-track destination filename, stable across repeated exports of the same
    /// playlist — a short suffix from the track's own content hash keeps two same-named tracks
    /// apart without depending on "does a file with this name already exist." A prior version did
    /// depend on that (appending "(2)", "(3)"... to the first available name), which meant every
    /// re-export of the same playlist to the same destination piled a fresh duplicate copy of
    /// every track on top of the previous export's files, since those files already existed —
    /// orphaning the old copies on the drive forever instead of updating them in place.
    /// Internal (not private) so UsbExportOrchestratorTests can verify determinism directly.
    /// </summary>
    internal static string BuildDeterministicDestPath(string audioDir, PlaylistTrack track)
    {
        var ext = Path.GetExtension(track.ResolvedFilePath ?? "");
        var stableSuffix = string.IsNullOrEmpty(track.TrackUniqueHash)
            ? ""
            : " [" + track.TrackUniqueHash.Substring(0, Math.Min(8, track.TrackUniqueHash.Length)) + "]";
        var safeName = Sanitize($"{track.Artist} - {track.Title}{stableSuffix}") + ext;
        return Path.Combine(audioDir, safeName);
    }

}
