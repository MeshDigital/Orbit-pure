using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

/// <summary>
/// Keeps a live <see cref="FileSystemWatcher"/> on every <c>LibraryFolders</c> row flagged
/// <c>IsWatched</c> and imports files that appear later as small background imports, with a toast
/// ("3 new tracks added from Watch folder X").
/// <para>
/// Events are batched per folder: every new event restarts that folder's settle timer, so a copy
/// of 200 files becomes one import of 200 files, not 200 folder scans. While the folder is under
/// an import (the initial import sheet, or another watch import) its events are held back, and
/// replayed when <see cref="LibraryImportCompletedEvent"/> says that import ended. Two imports never
/// ingest the same folder at once.
/// </para>
/// </summary>
public sealed class LibraryFolderWatchService : IDisposable
{
    private const int DefaultSettleDelayMs = 3000;

    private readonly ILogger<LibraryFolderWatchService> _logger;
    private readonly IDbContextFactory<AppDbContext>? _dbFactory;
    private readonly LibraryFolderScannerService _scannerService;
    private readonly IEventBus _eventBus;
    private readonly int _settleDelayMs;

    private readonly object _lock = new();
    private readonly Dictionary<Guid, FileSystemWatcher> _watchers = new();
    private readonly Dictionary<Guid, string> _folderPaths = new();
    private readonly Dictionary<Guid, PendingFolder> _pending = new();
    private readonly Dictionary<Guid, HashSet<string>> _deferred = new();
    private IDisposable? _foldersChangedSubscription;
    private IDisposable? _importCompletedSubscription;
    private bool _disposed;

    public LibraryFolderWatchService(
        ILogger<LibraryFolderWatchService> logger,
        IDbContextFactory<AppDbContext> dbFactory,
        LibraryFolderScannerService scannerService,
        IEventBus eventBus)
        : this(logger, dbFactory, scannerService, eventBus, DefaultSettleDelayMs)
    {
    }

    internal LibraryFolderWatchService(
        ILogger<LibraryFolderWatchService> logger,
        IDbContextFactory<AppDbContext>? dbFactory,
        LibraryFolderScannerService scannerService,
        IEventBus eventBus,
        int settleDelayMs)
    {
        _logger = logger;
        _dbFactory = dbFactory;
        _scannerService = scannerService;
        _eventBus = eventBus;
        _settleDelayMs = Math.Max(1, settleDelayMs);

        // Subscribed from construction (not StartAsync) so events deferred before the watchers
        // start are still replayed.
        _importCompletedSubscription = _eventBus.GetEvent<LibraryImportCompletedEvent>()
            .Subscribe(evt => OnImportCompleted(evt.Result));
    }

    public async Task StartAsync()
    {
        await RebuildWatchersAsync();
        _foldersChangedSubscription = _eventBus.GetEvent<LibraryFoldersChangedEvent>()
            .Subscribe(evt => { _ = RebuildWatchersAsync(); });
    }

    /// <summary>Files held back for <paramref name="folderId"/> because an import was running.</summary>
    internal int DeferredCount(Guid folderId)
    {
        lock (_lock) return _deferred.TryGetValue(folderId, out var set) ? set.Count : 0;
    }

    /// <summary>Files waiting for <paramref name="folderId"/>'s settle timer.</summary>
    internal int PendingCount(Guid folderId)
    {
        lock (_lock) return _pending.TryGetValue(folderId, out var p) ? p.Paths.Count : 0;
    }

    internal void SetFolderPathForTests(Guid folderId, string path)
    {
        lock (_lock) _folderPaths[folderId] = path;
    }

    private async Task RebuildWatchersAsync()
    {
        if (_dbFactory == null) return;
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
                    _folderPaths.Remove(staleId);
                    _logger.LogInformation("Stopped watching library folder (no longer watched): {Id}", staleId);
                }

                foreach (var folder in watchedFolders)
                {
                    _folderPaths[folder.Id] = folder.FolderPath;
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
                        // Changed only keeps an already-pending file's settle timer running while it is
                        // still being copied; ORBIT's own tag writes must not trigger imports.
                        watcher.Changed += (_, e) => OnFileEvent(folderId, e.FullPath, isChange: true);
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

    /// <summary>A file appeared (or, with <paramref name="isChange"/>, was written to) under a watched folder.</summary>
    internal void OnFileEvent(Guid folderId, string fullPath, bool isChange = false)
    {
        if (LibraryAudioFormats.Classify(fullPath) != LibraryAudioFormats.Kind.Supported)
            return;

        lock (_lock)
        {
            if (_disposed) return;

            if (isChange)
            {
                if (_pending.TryGetValue(folderId, out var waiting) && waiting.Paths.Contains(fullPath))
                {
                    waiting.Timer.Stop();
                    waiting.Timer.Start();
                }
                return;
            }

            // The folder is being imported right now: that import may or may not have seen this
            // file already, so hold the event until it finishes instead of ingesting in parallel.
            if (_scannerService.IsFolderImporting(folderId))
            {
                if (!_deferred.TryGetValue(folderId, out var held))
                    _deferred[folderId] = held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                held.Add(fullPath);
                return;
            }

            QueueLocked(folderId, new[] { fullPath });
        }
    }

    private void QueueLocked(Guid folderId, IEnumerable<string> paths)
    {
        if (!_pending.TryGetValue(folderId, out var pending))
        {
            var timer = new System.Timers.Timer(_settleDelayMs) { AutoReset = false };
            pending = new PendingFolder(timer);
            timer.Elapsed += (_, _) => OnSettled(folderId);
            _pending[folderId] = pending;
        }

        foreach (var path in paths) pending.Paths.Add(path);

        // Restart the settle timer: files are still arriving or being written.
        pending.Timer.Stop();
        pending.Timer.Start();
    }

    private void OnSettled(Guid folderId)
    {
        List<string> files;
        lock (_lock)
        {
            if (_disposed || !_pending.TryGetValue(folderId, out var pending)) return;

            if (_scannerService.IsFolderImporting(folderId))
            {
                // An import started in the meantime; hand these to the deferred set.
                if (!_deferred.TryGetValue(folderId, out var held))
                    _deferred[folderId] = held = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                held.UnionWith(pending.Paths);
                pending.Paths.Clear();
                return;
            }

            files = pending.Paths.ToList();
            pending.Paths.Clear();
        }

        if (files.Count > 0)
            _ = ImportSafeAsync(folderId, files);
    }

    private void OnImportCompleted(LibraryImportResult result)
    {
        lock (_lock)
        {
            if (_disposed) return;
            foreach (var folderId in result.FolderIds)
            {
                if (_scannerService.IsFolderImporting(folderId)) continue;
                if (!_deferred.Remove(folderId, out var held) || held.Count == 0) continue;
                _logger.LogInformation("Replaying {Count} watch-folder event(s) held during an import of folder {FolderId}", held.Count, folderId);
                QueueLocked(folderId, held);
            }
        }
    }

    private async Task ImportSafeAsync(Guid folderId, List<string> files)
    {
        try
        {
            _logger.LogInformation("Watch-folder auto-import: {Count} file(s) in folder {FolderId}", files.Count, folderId);
            var result = await _scannerService.ImportFoldersAsync(new LibraryImportRequest
            {
                FolderIds = new[] { folderId },
                Files = files,
                QueueAnalysis = true,
                Trigger = LibraryImportTrigger.Watch,
            });

            if (result.Added > 0)
            {
                string folderName;
                lock (_lock)
                    folderName = _folderPaths.TryGetValue(folderId, out var p) ? DisplayName(p) : "a watched folder";
                var noun = result.Added == 1 ? "new track" : "new tracks";
                _eventBus.Publish(new ToastRequestedEvent(
                    "Watch folder",
                    $"{SLSKDONET.Utils.CountFormat.N(result.Added)} {noun} added from Watch folder {folderName}.",
                    NotificationType.Success,
                    TimeSpan.FromSeconds(5)));
            }
            else if (result.Unreadable > 0 || result.Failed > 0)
            {
                _eventBus.Publish(new ToastRequestedEvent(
                    "Watch folder",
                    $"{SLSKDONET.Utils.CountFormat.N(result.Unreadable + result.Failed)} new file(s) couldn't be added. They may be corrupt or still copying.",
                    NotificationType.Warning,
                    TimeSpan.FromSeconds(6)));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Watch-folder auto-import failed for folder {FolderId}", folderId);
        }
    }

    private static string DisplayName(string path)
    {
        var name = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        return string.IsNullOrEmpty(name) ? path : name;
    }

    /// <summary>
    /// FileSystemWatcher's internal buffer can overflow under a very large burst of events. Rather
    /// than crash or silently stop working, drop that one folder's watch (manual scan still works)
    /// and log clearly so it's diagnosable.
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
        _importCompletedSubscription?.Dispose();

        lock (_lock)
        {
            foreach (var watcher in _watchers.Values) watcher.Dispose();
            _watchers.Clear();

            foreach (var pending in _pending.Values) pending.Timer.Dispose();
            _pending.Clear();
            _deferred.Clear();
        }
    }

    private sealed class PendingFolder
    {
        public PendingFolder(System.Timers.Timer timer) => Timer = timer;
        public System.Timers.Timer Timer { get; }
        public HashSet<string> Paths { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
