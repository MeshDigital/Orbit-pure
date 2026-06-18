using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;
using SLSKDONET.Services.Library;

namespace SLSKDONET.Services.Export;

public enum ExportMode { XmlOnly, FilesAndXml }

public record ExportProgress(int Total, int Copied, int Skipped, string CurrentFile, bool IsComplete);

/// <summary>
/// Orchestrates playlist export to USB/folder:
///   Phase A — copy audio files to destination
///   Phase B — write Rekordbox XML with destination paths
///
/// For USB export the XML is written to {dest}/PIONEER/rekordbox.xml,
/// which Pioneer CDJs detect automatically when the drive is inserted.
/// </summary>
public sealed class UsbExportOrchestrator
{
    private readonly PlaylistExportService _exportService;
    private readonly ILogger<UsbExportOrchestrator> _logger;

    public UsbExportOrchestrator(
        PlaylistExportService exportService,
        ILogger<UsbExportOrchestrator> logger)
    {
        _exportService = exportService;
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
        CancellationToken ct = default)
    {
        _logger.LogInformation(
            "Starting {Mode} export of '{Playlist}' ({Count} tracks) → {Dest}",
            mode, playlistName, tracks.Count, destinationRoot);

        if (mode == ExportMode.FilesAndXml)
            await ExportFilesAndXmlAsync(playlistName, tracks, destinationRoot, progress, ct);
        else
            await ExportXmlOnlyAsync(playlistName, tracks, destinationRoot, progress, ct);
    }

    // ─── FilesAndXml ─────────────────────────────────────────────────────────

    private async Task ExportFilesAndXmlAsync(
        string playlistName,
        IReadOnlyList<PlaylistTrack> tracks,
        string usbRoot,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        string audioDir = Path.Combine(usbRoot, "OrbitAudio", Sanitize(playlistName));
        Directory.CreateDirectory(audioDir);

        // Phase A: copy files, building a map from local path → destination path
        var pathMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        int copied = 0, skipped = 0;

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

            var ext = Path.GetExtension(track.ResolvedFilePath);
            var safeName = Sanitize($"{track.Artist} - {track.Title}") + ext;
            // Deduplicate file names (two tracks from different artists might share a name)
            var destPath = UniqueDestPath(audioDir, safeName);

            progress?.Report(new(tracks.Count, copied, skipped, safeName, false));

            await Task.Run(() => File.Copy(track.ResolvedFilePath, destPath, overwrite: true), ct);
            pathMap[track.ResolvedFilePath] = destPath;
            copied++;

            _logger.LogDebug("Copied {Src} → {Dest}", track.ResolvedFilePath, destPath);
        }

        // Phase B: write XML with dest paths instead of local paths
        var pioneerDir = Path.Combine(usbRoot, "PIONEER");
        Directory.CreateDirectory(pioneerDir);
        var xmlPath = Path.Combine(pioneerDir, "rekordbox.xml");

        progress?.Report(new(tracks.Count, copied, skipped, "Writing rekordbox.xml…", false));
        await _exportService.ExportToRekordboxXmlAsync(playlistName, tracks, xmlPath, pathMap);

        progress?.Report(new(tracks.Count, copied, skipped, "Done", true));
        _logger.LogInformation(
            "FilesAndXml export complete: {Copied} copied, {Skipped} skipped. XML → {Xml}",
            copied, skipped, xmlPath);
    }

    // ─── XmlOnly ─────────────────────────────────────────────────────────────

    private async Task ExportXmlOnlyAsync(
        string playlistName,
        IReadOnlyList<PlaylistTrack> tracks,
        string xmlFilePath,
        IProgress<ExportProgress>? progress,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        progress?.Report(new(tracks.Count, 0, 0, "Writing rekordbox.xml…", false));
        await _exportService.ExportToRekordboxXmlAsync(playlistName, tracks, xmlFilePath);
        progress?.Report(new(tracks.Count, tracks.Count, 0, "Done", true));
    }

    // ─── Helpers ─────────────────────────────────────────────────────────────

    private static readonly char[] _invalidChars = Path.GetInvalidFileNameChars();

    private static string Sanitize(string name) =>
        string.Concat(name.Select(c => _invalidChars.Contains(c) ? '_' : c)).Trim();

    private static string UniqueDestPath(string dir, string safeName)
    {
        var candidate = Path.Combine(dir, safeName);
        if (!File.Exists(candidate)) return candidate;

        var nameOnly = Path.GetFileNameWithoutExtension(safeName);
        var ext = Path.GetExtension(safeName);
        int n = 2;
        while (File.Exists(candidate))
        {
            candidate = Path.Combine(dir, $"{nameOnly} ({n}){ext}");
            n++;
        }
        return candidate;
    }
}
