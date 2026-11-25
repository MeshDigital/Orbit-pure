using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Models;
using SLSKDONET.Services.Models;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.IO; // Added for Path
using SLSKDONET.Data.Entities;

namespace SLSKDONET.Services;

public partial class DatabaseService
{
    private readonly ILogger<DatabaseService> _logger;
    private readonly SchemaMigratorService _schemaMigrator;
    private readonly Repositories.ITrackRepository _trackRepository;
    private readonly Services.IO.IFileWriteService _fileWriteService;
    private readonly Configuration.AppConfig? _appConfig;
    private readonly IFilePathResolverService? _filePathResolver;

    // Semaphore to serialize database write operations and prevent SQLite locking issues
    private static readonly SemaphoreSlim _writeSemaphore = new SemaphoreSlim(1, 1);
    private readonly SemaphoreSlim _initSemaphore = new SemaphoreSlim(1, 1);
    private bool _isInitialized;

    // Short TTL cache for the Users page's per-peer download summary — this query previously ran
    // an unindexed full-table GROUP BY (fixed separately, see SchemaMigratorService patch #28), but
    // the Refresh button re-runs LoadAsync on demand, so a brief cache absorbs repeated clicks
    // without needing an invalidation hook wired into every download-completion path. 20s balances
    // "don't hammer the DB on a double-click" against "new downloads should show up reasonably soon".
    private List<UserDownloadSummary>? _downloadedUsersSummaryCache;
    private DateTime _downloadedUsersSummaryCachedAtUtc = DateTime.MinValue;
    private static readonly TimeSpan DownloadedUsersSummaryCacheTtl = TimeSpan.FromSeconds(20);

    public DatabaseService(
        ILogger<DatabaseService> logger,
        SchemaMigratorService schemaMigrator,
        Repositories.ITrackRepository trackRepository,
        Services.IO.IFileWriteService fileWriteService,
        Configuration.AppConfig? appConfig = null,
        IFilePathResolverService? filePathResolver = null)
    {
        _logger = logger;
        _schemaMigrator = schemaMigrator;
        _trackRepository = trackRepository;
        _fileWriteService = fileWriteService;
        _appConfig = appConfig;
        _filePathResolver = filePathResolver;
    }

    private Microsoft.EntityFrameworkCore.Storage.IDbContextTransaction? _currentTransaction;
    private AppDbContext? _transactionContext;

    public async Task BeginTransactionAsync()
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            _transactionContext = new AppDbContext();
            _currentTransaction = await _transactionContext.Database.BeginTransactionAsync();
        }
        catch (Exception ex)
        {
            _writeSemaphore.Release();
            _logger.LogError(ex, "Failed to begin database transaction");
            throw;
        }
    }

    public async Task CommitTransactionAsync()
    {
        if (_currentTransaction == null || _transactionContext == null)
            throw new InvalidOperationException("No active transaction to commit");

        try
        {
            await _transactionContext.SaveChangesAsync();
            await _currentTransaction.CommitAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to commit database transaction");
            throw;
        }
        finally
        {
            await CleanupTransactionAsync();
            _writeSemaphore.Release();
        }
    }

    public async Task RollbackTransactionAsync()
    {
        if (_currentTransaction == null) return;

        try
        {
            await _currentTransaction.RollbackAsync();
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to rollback database transaction");
        }
        finally
        {
            await CleanupTransactionAsync();
            _writeSemaphore.Release();
        }
    }

    private async Task CleanupTransactionAsync()
    {
        if (_currentTransaction != null)
        {
            await _currentTransaction.DisposeAsync();
            _currentTransaction = null;
        }
        if (_transactionContext != null)
        {
            await _transactionContext.DisposeAsync();
            _transactionContext = null;
        }
    }

    public async Task RunInTransactionAsync(Func<Task> action)
    {
        await BeginTransactionAsync();
        try
        {
            await action();
            await CommitTransactionAsync();
        }
        catch
        {
            await RollbackTransactionAsync();
            throw;
        }
    }

    public async Task InitAsync()
    {
        if (_isInitialized)
            return;

        await _initSemaphore.WaitAsync();
        try
        {
            if (_isInitialized)
                return;

            await _schemaMigrator.InitializeDatabaseAsync();
            await EnsureDownloadQueueTableAsync();
            await EnsureFrequentSourcesTablesAsync();
            _isInitialized = true;
        }
        finally
        {
            _initSemaphore.Release();
        }
    }

    private async Task EnsureDownloadQueueTableAsync()
    {
        using var context = new AppDbContext();

        const string sql = @"
            CREATE TABLE IF NOT EXISTS DownloadQueueItems (
                Id TEXT NOT NULL PRIMARY KEY,
                PlaylistTrackId TEXT NOT NULL,
                QueuePosition INTEGER NOT NULL,
                EnqueuedAt TEXT NOT NULL
            );

            CREATE UNIQUE INDEX IF NOT EXISTS IX_DownloadQueueItems_PlaylistTrackId
            ON DownloadQueueItems (PlaylistTrackId);

            CREATE INDEX IF NOT EXISTS IX_DownloadQueueItems_QueuePosition
            ON DownloadQueueItems (QueuePosition);
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    private async Task EnsureFrequentSourcesTablesAsync()
    {
        using var context = new AppDbContext();

        const string sql = @"
            CREATE TABLE IF NOT EXISTS FrequentSources (
                SourceUsername TEXT NOT NULL,
                FolderPath TEXT NOT NULL,
                DownloadCount INTEGER NOT NULL,
                LastDownloadedAtUtc TEXT NOT NULL,
                TotalBytesDownloaded INTEGER NOT NULL,
                LocalNote TEXT NULL,
                IsFriend INTEGER NOT NULL,
                IsPinned INTEGER NOT NULL,
                PRIMARY KEY (SourceUsername, FolderPath)
            );

            CREATE INDEX IF NOT EXISTS IX_FrequentSources_Rank
            ON FrequentSources (IsPinned, IsFriend, DownloadCount, LastDownloadedAtUtc);

            CREATE TABLE IF NOT EXISTS PrefetchQueueItems (
                Id TEXT NOT NULL PRIMARY KEY,
                SourceUsername TEXT NOT NULL,
                RemotePath TEXT NOT NULL,
                LocalStagingPath TEXT NOT NULL,
                Status INTEGER NOT NULL,
                EnqueuedAtUtc TEXT NOT NULL,
                CompletedAtUtc TEXT NULL,
                BytesDownloaded INTEGER NOT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_PrefetchQueue_Source_Status
            ON PrefetchQueueItems (SourceUsername, Status);

            CREATE INDEX IF NOT EXISTS IX_PrefetchQueue_EnqueuedAt
            ON PrefetchQueueItems (EnqueuedAtUtc);
        ";

        await context.Database.ExecuteSqlRawAsync(sql);
    }

    // ===== PendingOrchestration Methods =====

    public async Task AddPendingOrchestrationAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var sql = "INSERT OR IGNORE INTO PendingOrchestrations (GlobalId, AddedAt) VALUES (@id, @now)";
            await context.Database.ExecuteSqlRawAsync(sql, 
                new SqliteParameter("@id", globalId),
                new SqliteParameter("@now", DateTime.UtcNow.ToString("o")));
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task RemovePendingOrchestrationAsync(string globalId)
    {
        await _writeSemaphore.WaitAsync();
        try
        {
            using var context = new AppDbContext();
            var sql = "DELETE FROM PendingOrchestrations WHERE GlobalId = @id";
            await context.Database.ExecuteSqlRawAsync(sql, new SqliteParameter("@id", globalId));
        }
        finally
        {
            _writeSemaphore.Release();
        }
    }

    public async Task<List<string>> GetPendingOrchestrationsAsync()
    {
        using var context = new AppDbContext();
        var ids = new List<string>();
        var connection = context.Database.GetDbConnection();
        await connection.OpenAsync();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT GlobalId FROM PendingOrchestrations";
        using var reader = await cmd.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            ids.Add(reader.GetString(0));
        }
        return ids;
    }

    // ===== Track Methods =====

    public async Task<List<TrackEntity>> LoadTracksAsync()
    {
        return await _trackRepository.LoadTracksAsync();
    }

    public async Task<TrackEntity?> FindTrackAsync(string globalId)
    {
        return await _trackRepository.FindTrackAsync(globalId);
    }

    public async Task SaveTrackAsync(TrackEntity track)
    {
        await _trackRepository.SaveTrackAsync(track);
    }
    
    public async Task UpdateTrackFilePathAsync(string globalId, string filePath)
    {
        await _trackRepository.UpdateTrackFilePathAsync(globalId, filePath);
    }

    public async Task RemoveTrackAsync(string globalId)
    {
        await _trackRepository.RemoveTrackAsync(globalId);
    }

    public async Task DeleteLibraryEntryAsync(Guid id)
    {
        using var context = new AppDbContext();
        var entry = await context.LibraryEntries.FindAsync(id);
        if (entry != null)
        {
            context.LibraryEntries.Remove(entry);
            await context.SaveChangesAsync();
        }
    }

    /// <summary>
    /// Deletes a LibraryEntries row by its real primary key. LibraryEntryEntity's <c>[Key]</c> is
    /// UniqueHash (a string) — its Guid Id field is a secondary convenience field only, per the
    /// entity's own doc comment — so <see cref="DeleteLibraryEntryAsync"/>'s FindAsync(Guid) can
    /// never match a row here and silently no-ops. Use this whenever the caller has the track's
    /// hash (the common case) rather than that Guid.
    /// </summary>
    public async Task DeleteLibraryEntryByHashAsync(string uniqueHash)
    {
        using var context = new AppDbContext();
        var entry = await context.LibraryEntries.FindAsync(uniqueHash);
        if (entry != null)
        {
            context.LibraryEntries.Remove(entry);
            await context.SaveChangesAsync();
        }
    }

    // Helper to bulk save if needed
    public async Task SaveAllAsync(IEnumerable<TrackEntity> tracks)
    {
        using var context = new AppDbContext();
        foreach(var t in tracks)
        {
            if (!await context.Tracks.AnyAsync(x => x.GlobalId == t.GlobalId))
            {
                await context.Tracks.AddAsync(t);
            }
        }
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Updates the status of a track across all playlists that contain it,
    /// then recalculates the progress counts for those playlists.
    /// Phase 3D: High-Efficiency Core - handles both Master and Playlist updates in one transaction.
    /// </summary>
    public async Task<List<Guid>> UpdatePlaylistTrackStatusAndRecalculateJobsAsync(
        string trackUniqueHash, 
        TrackStatus newStatus, 
        string? resolvedPath, 
        int searchRetryCount = 0, 
        int notFoundRestartCount = 0,
        string? state = null,
        string? error = null,
        DateTime? completedAt = null,
        string? stalledReason = null)
    {
        return await _trackRepository.UpdatePlaylistTrackStatusAndRecalculateJobsAsync(
            trackUniqueHash, newStatus, resolvedPath, searchRetryCount, notFoundRestartCount,
            state, error, completedAt, stalledReason);
    }

    public async Task<List<Guid>> BulkUpdatePlaylistTrackStatusAsync(
        IReadOnlyList<string> trackUniqueHashes, TrackStatus? newStatus, string? state = null,
        bool? isUserPaused = null, bool clearRetryState = false, bool? isClearedFromDownloadCenter = null,
        int? priority = null)
        => await _trackRepository.BulkUpdatePlaylistTrackStatusAsync(
            trackUniqueHashes, newStatus, state, isUserPaused, clearRetryState, isClearedFromDownloadCenter, priority);

    // ===== LibraryEntry Methods =====

    public async Task<LibraryEntryEntity?> FindLibraryEntryAsync(string uniqueHash)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Include(e => e.AudioFeatures)
            .FirstOrDefaultAsync(e => e.UniqueHash == uniqueHash);
    }

    /// <summary>Finds a library entry whose tag-independent audio content hash matches (issue #239).</summary>
    public async Task<LibraryEntryEntity?> FindLibraryEntryByContentHashAsync(string contentHash)
    {
        if (string.IsNullOrWhiteSpace(contentHash))
            return null;
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .FirstOrDefaultAsync(e => e.AudioContentHash == contentHash);
    }



    public async Task SaveLibraryEntryAsync(LibraryEntryEntity entry)
    {
        using var context = new AppDbContext();
        
        // Robust Upsert Pattern: Check existence first to avoid DbUpdateConcurrencyException
        var existing = await context.LibraryEntries.FindAsync(entry.UniqueHash);
        
        if (existing == null)
        {
            // It doesn't exist, so we ADD it.
            context.LibraryEntries.Add(entry);
        }
        else
        {
            // It exists, so we UPDATE it.
            // Since 'existing' is tracked and 'entry' is detached, we use SetValues.
            context.Entry(existing).CurrentValues.SetValues(entry);
            
            // Ensure LastUsedAt is updated on the tracked entity
            existing.LastUsedAt = DateTime.UtcNow; 
        }
        
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Finds an enriched track by artist and title.
    /// Used by SpotifyEnrichmentService for cache-first lookups.
    /// </summary>
    public async Task<LibraryEntryEntity?> FindEnrichedTrackAsync(string artist, string title)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Where(e => e.Artist.ToLower() == artist.ToLower() && 
                       e.Title.ToLower() == title.ToLower() &&
                       e.IsEnriched)
            .FirstOrDefaultAsync();
    }

    /// <summary>
    /// Gets a library entry by its Guid Id.
    /// </summary>
    public async Task<LibraryEntryEntity?> GetLibraryEntryAsync(Guid id)
    {
        using var context = new AppDbContext();
        return await context.LibraryEntries
            .Include(e => e.AudioFeatures)
            .FirstOrDefaultAsync(e => e.Id == id);
    }

    /// <summary>
    /// Gets a library entry by its unique hash.
    /// </summary>
    public async Task<LibraryEntryEntity?> GetLibraryEntryAsync(string uniqueHash)
    {
        return await FindLibraryEntryAsync(uniqueHash);
    }

    /// <summary>
    /// Finds an enriched track in the global Tracks table by its GlobalId.
    /// Used by enrichment services for cache lookups.
    /// </summary>
    public async Task<TrackEntity?> FindEnrichedTrackAsync(string globalId)
    {
        return await _trackRepository.FindTrackAsync(globalId);
    }

    /// <summary>
    /// Searches library entries and returns enrichment status.
    /// Used by Mission Control for status lookups.
    /// </summary>
    public async Task<List<LibraryEntryEntity>> SearchLibraryEntriesWithStatusAsync(string query, int limit = 50)
    {
        using var context = new AppDbContext();
        var lowerQuery = query.ToLower();
        return await context.LibraryEntries
            .Where(e => e.Artist.ToLower().Contains(lowerQuery) || e.Title.ToLower().Contains(lowerQuery))
            .OrderByDescending(e => e.AddedAt)
            .Take(limit)
            .ToListAsync();
    }


    public async Task<List<LibraryEntryEntity>> GetLibraryEntriesNeedingEnrichmentAsync(int limit)
    {
        return await _trackRepository.GetLibraryEntriesNeedingEnrichmentAsync(limit);
    }

    public async Task UpdateLibraryEntryEnrichmentAsync(string uniqueHash, TrackEnrichmentResult result)
    {
        await _trackRepository.UpdateLibraryEntryEnrichmentAsync(uniqueHash, result);
    }

    public async Task<List<PlaylistTrackEntity>> GetPlaylistTracksNeedingEnrichmentAsync(int limit)
    {
        return await _trackRepository.GetPlaylistTracksNeedingEnrichmentAsync(limit);
    }

    public async Task UpdatePlaylistTrackEnrichmentAsync(Guid id, TrackEnrichmentResult result)
    {
        await _trackRepository.UpdatePlaylistTrackEnrichmentAsync(id, result);
    }



}

/// <summary>Per-peer download counts/last-seen, grouped from DownloadHistory.</summary>
public readonly record struct UserDownloadSummary(
    string Username,
    int TotalDownloads,
    int CompletedDownloads,
    DateTime LastDownloadedAtUtc);

/// <summary>A 1:1 conversation's most recent message — powers the Users page's Conversations list.</summary>
public readonly record struct ConversationSummary(
    string Username,
    string LastMessage,
    DateTime LastMessageUtc,
    bool LastMessageWasOutgoing,
    bool HasUnread);



