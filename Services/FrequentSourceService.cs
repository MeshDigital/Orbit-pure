using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Local-only, opt-in persistence for repeated source peers and folders.
/// No network uploads, no telemetry, and no playlist content storage.
/// </summary>
public sealed class FrequentSourceService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly DatabaseService? _databaseService;
    private readonly AppConfig _appConfig;
    private readonly ILogger<FrequentSourceService> _logger;

    public FrequentSourceService(
        IDbContextFactory<AppDbContext> dbContextFactory,
        DatabaseService? databaseService,
        AppConfig appConfig,
        ILogger<FrequentSourceService> logger)
    {
        _dbContextFactory = dbContextFactory;
        _databaseService = databaseService;
        _appConfig = appConfig;
        _logger = logger;
    }

    public async Task UpsertAsync(string sourceUsername, string folderPath, long bytesDownloaded, DateTime timestampUtc, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return;

        if (string.IsNullOrWhiteSpace(sourceUsername) || string.IsNullOrWhiteSpace(folderPath))
            return;

        var normalizedUsername = NormalizeSourceUsername(sourceUsername);
        var normalizedFolderPath = NormalizeFolderPath(folderPath);

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageReadyAsync(context, cancellationToken).ConfigureAwait(false);
        var existing = await context.FrequentSources
            .FirstOrDefaultAsync(item => item.SourceUsername == normalizedUsername && item.FolderPath == normalizedFolderPath, cancellationToken)
            .ConfigureAwait(false);

        if (existing is null)
        {
            context.FrequentSources.Add(new FrequentSource
            {
                SourceUsername = normalizedUsername,
                FolderPath = normalizedFolderPath,
                DownloadCount = 1,
                LastDownloadedAtUtc = timestampUtc,
                TotalBytesDownloaded = Math.Max(0, bytesDownloaded),
            });
        }
        else
        {
            existing.DownloadCount += 1;
            existing.LastDownloadedAtUtc = timestampUtc > existing.LastDownloadedAtUtc
                ? timestampUtc
                : existing.LastDownloadedAtUtc;
            existing.TotalBytesDownloaded += Math.Max(0, bytesDownloaded);
        }

        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FrequentSource>> GetRankedAsync(int limit = 100, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return Array.Empty<FrequentSource>();

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageReadyAsync(context, cancellationToken).ConfigureAwait(false);
        return await context.FrequentSources
            .OrderByDescending(item => item.IsPinned)
            .ThenByDescending(item => item.IsFriend)
            .ThenByDescending(item => item.DownloadCount)
            .ThenByDescending(item => item.LastDownloadedAtUtc)
            .ThenBy(item => item.SourceUsername)
            .ThenBy(item => item.FolderPath)
            .Take(Math.Max(1, limit))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task ClearAsync(CancellationToken cancellationToken = default)
    {
        if (!IsEnabled())
            return;

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageReadyAsync(context, cancellationToken).ConfigureAwait(false);
        context.FrequentSources.RemoveRange(context.FrequentSources);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task PinAsync(string sourceUsername, string folderPath, bool isPinned, CancellationToken cancellationToken = default)
        => await UpdateFlagsAsync(sourceUsername, folderPath, item => item.IsPinned = isPinned, cancellationToken).ConfigureAwait(false);

    public async Task AddNoteAsync(string sourceUsername, string folderPath, string? note, CancellationToken cancellationToken = default)
        => await UpdateFlagsAsync(sourceUsername, folderPath, item => item.LocalNote = note, cancellationToken).ConfigureAwait(false);

    public async Task SetFriendAsync(string sourceUsername, string folderPath, bool isFriend, CancellationToken cancellationToken = default)
        => await UpdateFlagsAsync(sourceUsername, folderPath, item => item.IsFriend = isFriend, cancellationToken).ConfigureAwait(false);

    private async Task UpdateFlagsAsync(string sourceUsername, string folderPath, Action<FrequentSource> update, CancellationToken cancellationToken)
    {
        if (!IsEnabled())
            return;

        if (string.IsNullOrWhiteSpace(sourceUsername) || string.IsNullOrWhiteSpace(folderPath))
            return;

        var normalizedUsername = NormalizeSourceUsername(sourceUsername);
        var normalizedFolderPath = NormalizeFolderPath(folderPath);

        await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);
        await EnsureStorageReadyAsync(context, cancellationToken).ConfigureAwait(false);
        var item = await context.FrequentSources
            .FirstOrDefaultAsync(value => value.SourceUsername == normalizedUsername && value.FolderPath == normalizedFolderPath, cancellationToken)
            .ConfigureAwait(false);

        if (item is null)
        {
            item = new FrequentSource
            {
                SourceUsername = normalizedUsername,
                FolderPath = normalizedFolderPath,
                DownloadCount = 0,
                LastDownloadedAtUtc = DateTime.UtcNow,
            };
            context.FrequentSources.Add(item);
        }

        update(item);
        await context.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
    }

    private bool IsEnabled()
        => _appConfig.EnableFrequentSources;

    private static string NormalizeSourceUsername(string sourceUsername)
        => sourceUsername.Trim().ToLowerInvariant();

    private static string NormalizeFolderPath(string folderPath)
    {
        var normalized = folderPath.Trim().Replace('/', '\\');
        normalized = CollapseRepeatedSeparators(normalized);
        normalized = normalized.TrimEnd('\\');

        if (normalized.Length == 0)
            return "\\";

        return normalized.ToLowerInvariant();
    }

    private static string CollapseRepeatedSeparators(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;

        var chars = new List<char>(value.Length);
        var lastWasSeparator = false;

        foreach (var current in value)
        {
            var isSeparator = current == '\\';
            if (isSeparator && lastWasSeparator)
                continue;

            chars.Add(current);
            lastWasSeparator = isSeparator;
        }

        return new string(chars.ToArray());
    }

    private async Task EnsureStorageReadyAsync(AppDbContext context, CancellationToken cancellationToken)
    {
        if (_databaseService is not null)
        {
            await _databaseService.InitAsync().ConfigureAwait(false);
            return;
        }

        await context.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);
    }
}