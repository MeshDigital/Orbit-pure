using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Data;
using SLSKDONET.Events;
using SLSKDONET.Models;
using SLSKDONET.Services.AutoDownload;

namespace SLSKDONET.Services.AutoDownload;

/// <summary>
/// Background service that scans the database for tracks not yet downloaded (Missing, Failed,
/// or OnHold), searches Soulseek via AutoSearchService, validates with SearchResultMatcher, and
/// queues downloads if confidence >= 95%. Processes the most promising (least-tried) tracks
/// first within each sweep; tracks that have already failed repeatedly are retried last rather
/// than excluded, so nothing is permanently stuck once it starts failing.
/// </summary>
public class GhostAcquisitionOrchestrator : BackgroundService
{
    private readonly IDbContextFactory<AppDbContext> _dbContextFactory;
    private readonly AutoSearchService _autoSearchService;
    private readonly SearchResultMatcher _searchResultMatcher;
    private readonly DownloadManager _downloadManager;
    private readonly ILibraryService _libraryService;
    private readonly IEventBus _eventBus;
    private readonly AppConfig _config;
    private readonly ILogger<GhostAcquisitionOrchestrator> _logger;
    private readonly Random _random = new();

    public GhostAcquisitionOrchestrator(
        IDbContextFactory<AppDbContext> dbContextFactory,
        AutoSearchService autoSearchService,
        SearchResultMatcher searchResultMatcher,
        DownloadManager downloadManager,
        ILibraryService libraryService,
        IEventBus eventBus,
        AppConfig config,
        ILogger<GhostAcquisitionOrchestrator> logger)
    {
        _dbContextFactory = dbContextFactory;
        _autoSearchService = autoSearchService;
        _searchResultMatcher = searchResultMatcher;
        _downloadManager = downloadManager;
        _libraryService = libraryService;
        _eventBus = eventBus;
        _config = config;
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

                // Idle-gate: don't start a background sweep while the user's own searches/downloads
                // are in flight — this competes for the same rate-limited search dispatch otherwise.
                // EnableIdleGhostAcquisition is a safety valve to restore the old always-on behavior.
                if (_config.EnableIdleGhostAcquisition && !_downloadManager.IsQueueIdle)
                {
                    _logger.LogDebug("Download queue is active. Skipping acquisition sweep until idle.");
                    continue;
                }

                // Every track not yet downloaded is in scope — Missing, Failed, and OnHold alike.
                // Previously this only matched AvailabilityState==Ghost && Status==Missing &&
                // SearchRetryCount<3, which was a one-way trapdoor: a track that failed search 3
                // times got flipped to OnHold and then permanently excluded from every future
                // sweep (nothing ever reset it back), while tracks that reached Status==Failed
                // through the main DownloadManager pipeline were never in scope at all. Verified
                // live: this left the eligible pool at literally zero while ~1,900 "wanted"
                // tracks sat stuck, unreachable by this background service.
                //
                // No candidate is excluded anymore — instead, ordering does the throttling: worst
                // state last. A track that has never been attempted (Missing) is tried well before
                // one that has already failed search repeatedly (Failed, then OnHold), so a full
                // sweep spends its limited time budget on the most promising candidates first and
                // only reaches the stubborn ones once everything better has had a turn.
                List<PlaylistTrack> ghostTracks = new();
                await using (var context = await _dbContextFactory.CreateDbContextAsync(stoppingToken))
                {
                    var entities = await context.PlaylistTracks
                        .Where(t => t.Status == TrackStatus.Missing ||
                                    t.Status == TrackStatus.Failed ||
                                    t.Status == TrackStatus.OnHold)
                        .OrderBy(t => t.Status == TrackStatus.Missing ? 0
                                    : t.Status == TrackStatus.Failed ? 1
                                    : 2) // OnHold — already exhausted retries, worst state, tried last
                        .ThenBy(t => t.SearchRetryCount)
                        .ThenBy(t => t.Priority)
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
                    _logger.LogInformation("Found {Count} tracks needing acquisition (Missing/Failed/OnHold, worst-state-last order).", ghostTracks.Count);

                    foreach (var track in ghostTracks)
                    {
                        if (stoppingToken.IsCancellationRequested)
                            break;

                        // Re-check mid-sweep: back off immediately if the user started something
                        // since this sweep began, rather than blasting through the whole batch.
                        if (_config.EnableIdleGhostAcquisition && !_downloadManager.IsQueueIdle)
                        {
                            _logger.LogDebug("Download queue became active mid-sweep. Stopping this batch early.");
                            break;
                        }

                        // Double check: if already active in download manager, skip
                        if (_downloadManager.IsTrackAlreadyQueued(track.SpotifyTrackId, track.Artist, track.Title))
                        {
                            _logger.LogDebug("Track '{Artist} - {Title}' already in download queue, skipping search.", track.Artist, track.Title);
                            continue;
                        }

                        _logger.LogInformation("Attempting ghost acquisition for: {Artist} - {Title}", track.Artist, track.Title);

                        // Find best match using AutoSearchService. Background ghost acquisition has
                        // no interactive user waiting — use SearchScope.Wishlist (the protocol's own
                        // low-priority scope, gentler server-side rate limiting) rather than Network.
                        var (bestMatch, diagnostics) = await _autoSearchService.FindBestMatchAsync(track, stoppingToken, isBackgroundScan: true);

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

                                // Queue track in download manager — awaited (this is a background
                                // service, not the UI thread) since the OverrideCandidate lookup
                                // right below needs the freshly-created DownloadContext to already
                                // exist in ActiveDownloads.
                                track.AvailabilityState = TrackAvailabilityState.QueuedForDownload;
                                await _downloadManager.QueueTracksAsync(new List<PlaylistTrack> { track });

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
