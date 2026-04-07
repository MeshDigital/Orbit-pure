using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services.Library;

/// <summary>
/// Task 9.1 — Extensions to the Rekordbox XML export workflow:
///
/// 1. <see cref="TranslateToUsbPath"/> — converts absolute local paths to
///    USB-drive-relative paths (e.g. D:\Music\… → /Volumes/USB_DRIVE/Music/…).
///
/// 2. <see cref="AutoExportWatcher"/> — file-system watcher that triggers a
///    Rekordbox XML re-export whenever the tracked playlist/session changes.
///
/// These are additive extensions that compose with the existing
/// <see cref="PlaylistExportService"/>.
/// </summary>
public sealed class RekordboxExportExtensions : IDisposable
{
    private readonly PlaylistExportService _exporter;
    private readonly ILogger<RekordboxExportExtensions> _logger;

    private FileSystemWatcher? _watcher;
    private bool _disposed;

    public RekordboxExportExtensions(
        PlaylistExportService exporter,
        ILogger<RekordboxExportExtensions> logger)
    {
        _exporter = exporter;
        _logger   = logger;
    }

    // ── USB path translation (Task 9.1 §1) ───────────────────────────────

    /// <summary>
    /// Translates a local absolute file path to a USB-mount-relative path
    /// suitable for Rekordbox on a Pioneer CDJ.
    ///
    /// Examples:
    /// <code>
    /// // Windows  D:\Music\track.mp3  →  /MUSIC/track.mp3     (usbRoot="D:\")
    /// // macOS    /Volumes/MUSIC/...  →  /MUSIC/...
    /// // Linux    /media/usb/...      →  /Music/...
    /// </code>
    /// The Pioneer CDJ2000NXS2 expects paths starting with a forward slash
    /// that match the root layout of the USB drive.
    /// </summary>
    /// <param name="localPath">Absolute local file path.</param>
    /// <param name="localUsbRoot">
    /// Root of the USB drive on the local machine, e.g. <c>D:\</c> (Windows)
    /// or <c>/Volumes/USBDRIVE</c> (macOS).
    /// </param>
    /// <returns>Forward-slash path relative to USB root, e.g. <c>/Music/artist/track.mp3</c>.</returns>
    public static string TranslateToUsbPath(string localPath, string localUsbRoot)
    {
        if (string.IsNullOrWhiteSpace(localPath))
            return localPath;

        // Normalise both paths to use the same directory separator
        string normLocal = localPath.Replace('\\', '/');
        string normRoot  = localUsbRoot.TrimEnd('\\', '/').Replace('\\', '/');

        if (!normLocal.StartsWith(normRoot, StringComparison.OrdinalIgnoreCase))
            return "/" + normLocal.TrimStart('/'); // already relative or on different drive

        string relative = normLocal[normRoot.Length..].TrimStart('/');
        return "/" + relative;
    }

    /// <summary>
    /// Rewrites the <c>Location</c> attribute of each track in a Rekordbox XML
    /// file so that all absolute paths are translated to USB-relative paths.
    /// Modifies the file in-place.
    /// </summary>
    public async Task TranslateXmlLocationsAsync(
        string xmlFilePath,
        string localUsbRoot,
        CancellationToken ct = default)
    {
        if (!File.Exists(xmlFilePath))
            throw new FileNotFoundException("Rekordbox XML not found", xmlFilePath);

        // Read, replace, write — simple regex approach avoids full XML re-parse
        // so original formatting and comments are preserved.
        string xml = await File.ReadAllTextAsync(xmlFilePath, ct);
        string replaced = System.Text.RegularExpressions.Regex.Replace(
            xml,
            @"Location=""file://localhost/([^""]+)""",
            m =>
            {
                string localUri = Uri.UnescapeDataString(m.Groups[1].Value.Replace('/', Path.DirectorySeparatorChar));
                string usbPath  = TranslateToUsbPath(localUri, localUsbRoot);
                return $"Location=\"{usbPath}\"";
            });

        await File.WriteAllTextAsync(xmlFilePath, replaced, System.Text.Encoding.UTF8, ct);
        _logger.LogInformation("USB path translation complete for {XmlFile}", xmlFilePath);
    }

    // ── Auto-export watcher (Task 9.1 §2) ────────────────────────────────

    /// <summary>
    /// Starts watching <paramref name="watchDirectory"/> for changes to files
    /// matching <paramref name="filter"/> (e.g. <c>*.m3u</c>, <c>*.json</c>).
    ///
    /// When a change is detected the watcher waits <paramref name="debounceSecs"/>
    /// seconds (to coalesce rapid saves) then calls <paramref name="onChangedAsync"/>.
    /// The callback receives the file path that triggered the export.
    /// </summary>
    public void StartAutoExportWatcher(
        string watchDirectory,
        string filter,
        Func<string, CancellationToken, Task> onChangedAsync,
        double debounceSecs = 2.0)
    {
        StopAutoExportWatcher();

        if (!Directory.Exists(watchDirectory))
        {
            _logger.LogWarning("AutoExport watcher: directory does not exist: {Dir}", watchDirectory);
            return;
        }

        _watcher = new FileSystemWatcher(watchDirectory, filter)
        {
            NotifyFilter           = NotifyFilters.LastWrite | NotifyFilters.FileName,
            EnableRaisingEvents    = true,
            IncludeSubdirectories  = false
        };

        Timer? debounceTimer = null;
        string lastPath = string.Empty;
        var lockObj = new object();

        void OnChanged(object _, FileSystemEventArgs e)
        {
            lock (lockObj)
            {
                lastPath = e.FullPath;
                debounceTimer?.Dispose();
                debounceTimer = new Timer(_ =>
                {
                    string path;
                    lock (lockObj) { path = lastPath; }
                    _logger.LogInformation("AutoExport triggered by change in {File}", path);
                    // Fire-and-forget on thread-pool; caller owns exception handling
                    _ = Task.Run(() => onChangedAsync(path, CancellationToken.None));
                }, null, TimeSpan.FromSeconds(debounceSecs), Timeout.InfiniteTimeSpan);
            }
        }

        _watcher.Changed += OnChanged;
        _watcher.Created += OnChanged;
        _watcher.Renamed += (s, e) => OnChanged(s, e);

        _logger.LogInformation(
            "Rekordbox auto-export watcher started on {Dir} (filter: {Filter})",
            watchDirectory, filter);
    }

    /// <summary>Stops and disposes the file-system watcher.</summary>
    public void StopAutoExportWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        StopAutoExportWatcher();
    }
}
