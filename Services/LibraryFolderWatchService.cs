using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Keeps a live <see cref="FileSystemWatcher"/> on every <c>LibraryFolders</c> row flagged
/// <c>IsWatched</c>, auto-importing new audio files shortly after they appear instead of requiring
/// the user to open Manage Sources and click "Scan All". Debounces per-path so a file still being
/// written/copied isn't scanned mid-write, and rebuilds its watcher set whenever
/// <see cref="LibraryFoldersChangedEvent"/> fires (a folder added/removed, or its watch flag
/// toggled).
/// </summary>
public sealed class LibraryFolderWatchService : IDisposable
{
    private static readonly HashSet<string> AudioExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp3", ".flac", ".wav", ".m4a", ".aac", ".ogg", ".opus", ".aiff", ".aif", ".wma", ".ape",
    };

    private const int SettleDelayMs = 3000;

    private readonly ILogger<LibraryFolderWatchService> _logger;
    private readonly IDbContextFactory<AppDbContext> _dbFactory;
    private readonly LibraryFolderScannerService _scannerService;
    private readonly IEventBus _eventBus;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<string, System.Timers.Timer> _debounceTimers = new(StringComparer.OrdinalIgnoreCase);
    private IDisposable? _foldersChangedSubscription;
    private bool _disposed;

    public LibraryFolderWatchService(
        ILogger<LibraryFolderWatchService> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        LibraryFolderScannerService scannerService,
        IEventBus eventBus)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _scannerService = scannerService;
        _eventBus = eventBus;
    }

    public async Task StartAsync()
    {
        await RebuildWatchersAsync();
        _foldersChangedSubscription = _eventBus.GetEvent<LibraryFoldersChangedEvent>()
            .Subscribe(evt => { _ = RebuildWatchersAsync(); });
    }

    private async Task RebuildWatchersAsync()
    {
        try
        {
            await using var context = _dbFactory.CreateDbContext();
            var watchedFolders = await context.LibraryFolders
                .Where(f => f.IsWatched && f.IsEnabled)
                .ToListAsync();

            lock (_lock)
            {
                var currentIds = watchedFolders.Select(f => f.Id).ToHashSet();
                foreach (var staleId in _watchers.Keys.Where(id => !currentIds.Contains(id)).ToList())
                {
                    _watchers[staleId].Dispose();
                    _watchers.Remove(staleId);
                    _logger.LogInformation("Stopped watching library folder (no longer watched): {Id}", staleId);
                }

                foreach (var folder in watchedFolders)
                {
                    if (_watchers.ContainsKey(folder.Id)) continue;

                    if (!Directory.Exists(folder.FolderPath))
                    {
                        _logger.LogWarning("Cannot watch missing folder: {Path}", folder.FolderPath);
                        continue;
                    }

                    try
                    {
                        var watcher = new FileSystemWatcher(folder.FolderPath)
                        {
                            IncludeSubdirectories = true,
                            NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                        };
                        var folderId = folder.Id;
                        watcher.Created += (_, e) => OnFileEvent(folderId, e.FullPath);
                        watcher.Renamed += (_, e) => OnFileEvent(folderId, e.FullPath);
                        watcher.Error += (_, e) => OnWatcherError(folderId, folder.FolderPath, e);
                        watcher.EnableRaisingEvents = true;

                        _watchers[folder.Id] = watcher;
                        _logger.LogInformation("Watching library folder for auto-import: {Path}", folder.FolderPath);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Failed to start watching folder: {Path}", folder.FolderPath);
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rebuild library folder watchers");
        }
    }

    private void OnFileEvent(Guid folderId, string fullPath)
    {
        if (!AudioExtensions.Contains(Path.GetExtension(fullPath)))
            return;

        lock (_lock)
        {
            if (_disposed) return;

            if (_debounceTimers.TryGetValue(fullPath, out var existingTimer))
            {
                // Restart the settle timer — the file is still being written/copied.
                existingTimer.Stop();
                existingTimer.Start();
                return;
            }

            var timer = new System.Timers.Timer(SettleDelayMs) { AutoReset = false };
            timer.Elapsed += (_, _) =>
            {
                lock (_lock) { _debounceTimers.Remove(fullPath); }
                timer.Dispose();
                _ = ScanFolderSafeAsync(folderId, fullPath);
            };
            _debounceTimers[fullPath] = timer;
            timer.Start();
        }
    }

    private async Task ScanFolderSafeAsync(Guid folderId, string triggeringPath)
    {
        try
        {
            _logger.LogInformation("Watch-folder auto-import triggered by {Path}", triggeringPath);
            await _scannerService.ScanFolderAsync(folderId);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch-folder auto-import scan failed for folder {FolderId} (triggered by {Path})", folderId, triggeringPath);
        }
    }

    /// <summary>
    /// FileSystemWatcher's internal buffer can overflow under a very large burst of events. Rather
    /// than crash or silently stop working, drop that one folder's watch — the user still has
    /// manual Scan All as a fallback — and log clearly so it's diagnosable.
    /// </summary>
    private void OnWatcherError(Guid folderId, string path, System.IO.ErrorEventArgs e)
    {
        _logger.LogError(e.GetException(), "FileSystemWatcher error for '{Path}' — disabling auto-import for this folder until the app restarts.", path);

        lock (_lock)
        {
            if (_watchers.TryGetValue(folderId, out var watcher))
            {
                watcher.Dispose();
                _watchers.Remove(folderId);
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _foldersChangedSubscription?.Dispose();

        lock (_lock)
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();

            foreach (var timer in _debounceTimers.Values) timer.Dispose();
            _debounceTimers.Clear();
        }
    }
}
