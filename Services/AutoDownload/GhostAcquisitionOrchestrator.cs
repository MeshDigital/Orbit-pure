using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services.AutoDownload;

namespace SLSKDONET.Services.AutoDownload;

/// <summary>
/// Background service that scans the database for Ghost tracks,
/// searches Soulseek via AutoSearchService, validates with SearchResultMatcher,
/// and queues downloads if confidence >= 95%.
/// </summary>
public class GhostAcquisitionOrchestrator : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AutoSearchService _autoSearchService;
    private readonly SearchResultMatcher _searchResultMatcher;
    private readonly DownloadManager _downloadManager;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<GhostAcquisitionOrchestrator> _logger;
    private readonly Random _random = new();

    public GhostAcquisitionOrchestrator(
        IDbContextFactory<AppDbContext> dbContextFactory,
        AutoSearchService autoSearchService,
        SearchResultMatcher searchResultMatcher,
        DownloadManager downloadManager,
        ILibraryService libraryService,
        IEventBus eventBus,
        ILogger<GhostAcquisitionOrchestrator> logger)
    {
        _dbContextFactory = dbContextFactory;
        _autoSearchService = autoSearchService;
        _searchResultMatcher = searchResultMatcher;
        _downloadManager = downloadManager;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("GhostAcquisitionOrchestrator background service started.");

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Wait/check delay between database sweeps
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                // Check if Soulseek is connected. If not, skip this round.
                if (!_downloadManager.SoulseekConnected)
                {
                    _logger.LogDebug("Soulseek not logged in. Skipping acquisition sweep.");
                    continue;
                }

                // Query database for Ghost tracks with Status == Missing and SearchRetryCount < 3
                List<PlaylistTrack> ghostTracks = new();
                await using (var context = await _dbContextFactory.CreateDbContextAsync(stoppingToken))
                {
                    var entities = await context.PlaylistTracks
                        .Where(t => t.AvailabilityState == TrackAvailabilityState.Ghost &&
                                    t.Status == TrackStatus.Missing &&
                                    t.SearchRetryCount < 3)
                        .OrderBy(t => t.Priority)
                        .ThenBy(t => t.AddedAt)
                        .ToListAsync(stoppingToken);

                    foreach (var entity in entities)
                    {
                        ghostTracks.Add(new PlaylistTrack
                        {
                            Id = entity.Id,
                            PlaylistId = entity.PlaylistId,
                            Artist = entity.Artist,
                            Title = entity.Title,
                            Album = entity.Album,
                            TrackUniqueHash = entity.TrackUniqueHash,
                            Status = entity.Status,
                            AvailabilityState = entity.AvailabilityState,
                            SpotifyPlaylistId = entity.SpotifyPlaylistId,
                            SpotifyUri = entity.SpotifyUri,
                            TrackNumber = entity.TrackNumber,
                            AddedAt = entity.AddedAt,
                            Priority = entity.Priority,
                            SpotifyTrackId = entity.SpotifyTrackId,
                            SpotifyAlbumId = entity.SpotifyAlbumId,
                            SpotifyArtistId = entity.SpotifyArtistId,
                            AlbumArtUrl = entity.AlbumArtUrl,
                            Genres = entity.Genres,
                            Popularity = entity.Popularity,
                            CanonicalDuration = entity.CanonicalDuration,
                            ReleaseDate = entity.ReleaseDate
                        });
                    }
                }

                if (ghostTracks.Count > 0)
                {
                    _logger.LogInformation("Found {Count} ghost tracks needing acquisition.", ghostTracks.Count);

                    foreach (var track in ghostTracks)
                    {
                        if (stoppingToken.IsCancellationRequested)
                            break;

                        // Double check: if already active in download manager, skip
                        if (_downloadManager.IsTrackAlreadyQueued(track.SpotifyTrackId, track.Artist, track.Title))
                        {
                            _logger.LogDebug("Track '{Artist} - {Title}' already in download queue, skipping search.", track.Artist, track.Title);
                            continue;
                        }

                        _logger.LogInformation("Attempting ghost acquisition for: {Artist} - {Title}", track.Artist, track.Title);

                        // Find best match using AutoSearchService
                        var (bestMatch, diagnostics) = await _autoSearchService.FindBestMatchAsync(track, stoppingToken);

                        if (bestMatch != null)
                        {
                            // Score the match
                            var score = _searchResultMatcher.CalculateScore(track, bestMatch);
                            _logger.LogInformation("Match found for '{Artist} - {Title}' with score {Score}", track.Artist, track.Title, score);

                            if (score >= 95)
                            {
                                _logger.LogInformation("Match score {Score} >= 95. Triggering acquisition for track {Id}.", score, track.Id);

                                // Update availability state to QueuedForDownload in DB
                                await using (var context = await _dbContextFactory.CreateDbContextAsync(stoppingToken))
                                {
                                    var dbTrack = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == track.Id, stoppingToken);
                                    if (dbTrack != null)
                                    {
                                        dbTrack.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                                        await context.SaveChangesAsync(stoppingToken);
                                    }
                                }

                                // Queue track in download manager
                                track.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                                _downloadManager.QueueTracks(new List<PlaylistTrack> { track });

                                // Find context in download manager and set override candidate
                                var active = _downloadManager.ActiveDownloads;
                                var ctx = active.FirstOrDefault(d => d.Model.Id == track.Id || d.GlobalId == track.TrackUniqueHash);
                                if (ctx != null)
                                {
                                    ctx.OverrideCandidate = bestMatch;
                                    _logger.LogInformation("OverrideCandidate successfully assigned to download context for '{Artist} - {Title}'.", track.Artist, track.Title);
                                }
                            }
                            else
                            {
                                _logger.LogInformation("Match score {Score} < 95. Match rejected.", score);
                                await IncrementRetryCountAsync(track.Id, stoppingToken);
                            }
                        }
                        else
                        {
                            _logger.LogInformation("No match found for '{Artist} - {Title}'.", track.Artist, track.Title);
                            await IncrementRetryCountAsync(track.Id, stoppingToken);
                        }

                        // Apply 15-second throttle delay with +/- 2s jitter
                        var delayMs = 15000 + _random.Next(-2000, 2000);
                        _logger.LogDebug("Throttling: Waiting {Seconds}s before next search.", delayMs / 1000.0);
                        await Task.Delay(delayMs, stoppingToken);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error occurred in GhostAcquisitionOrchestrator loop.");
            }
        }

        _logger.LogInformation("GhostAcquisitionOrchestrator stopped.");
    }

    private async Task IncrementRetryCountAsync(Guid trackId, CancellationToken cancellationToken)
    {
        try
        {
            await using var context = await _dbContextFactory.CreateDbContextAsync(cancellationToken);
            var dbTrack = await context.PlaylistTracks.FirstOrDefaultAsync(t => t.Id == trackId, cancellationToken);
            if (dbTrack != null)
            {
                dbTrack.SearchRetryCount++;
                if (dbTrack.SearchRetryCount >= 3)
                {
                    dbTrack.Status = TrackStatus.OnHold;
                    _logger.LogWarning("Track {Id} ({Artist} - {Title}) failed search 3 times. Bumping to OnHold state.", dbTrack.Id, dbTrack.Artist, dbTrack.Title);
                    
                    // Publish event so UI can display OnHold state immediately
                    _eventBus.Publish(new TrackStateChangedEvent(
                        dbTrack.TrackUniqueHash,
                        dbTrack.PlaylistId,
                        PlaylistTrackState.Paused,
                        DownloadFailureReason.NoSearchResults,
                        "Search failed 3 times. Put on hold."));
                }
                await context.SaveChangesAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to increment retry count for track {Id}", trackId);
        }
    }
}
