using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Services;
using SLSKDONET.Views;

namespace SLSKDONET.Services.AudioAnalysis;

/// <summary>
/// Published by <see cref="PlaylistPrepCoordinator"/> whenever a playlist "Prep for Mix" run
/// starts, advances, finishes, fails, or is cancelled — drives the top-bar Analysis status pill
/// (Epic #517 / #527).
/// </summary>
public sealed record PlaylistPrepStatusChangedEvent(
    bool IsRunning,
    Guid PlaylistId,
    string PlaylistTitle,
    int Completed,
    int Total,
    int Prepared,
    int Skipped,
    int Failed,
    bool WasCancelled,
    string StatusText);

/// <summary>
/// Coordinates single-active-run playlist prep ("Prep for Mix") across every entry point
/// (playlist right-click menu, playlist readiness badge flyout) and exposes live cancellation
/// for the top-bar Analysis status pill.
/// </summary>
public interface IPlaylistPrepCoordinator
{
    bool IsRunning { get; }
    Guid ActivePlaylistId { get; }
    string ActivePlaylistTitle { get; }
    int Completed { get; }
    int Total { get; }

    Task<PlaylistPrepResult> RunAsync(Guid playlistId, string playlistTitle);
    void Cancel();
}

/// <summary>
/// Singleton coordinator over <see cref="IPlaylistPrepService"/>. Non-throwing constructor.
/// </summary>
public sealed class PlaylistPrepCoordinator : IPlaylistPrepCoordinator, IDisposable
{
    private readonly IPlaylistPrepService _prepService;
    private readonly IEventBus? _eventBus;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<PlaylistPrepCoordinator>? _logger;
    private readonly object _gate = new();
    private CancellationTokenSource? _activeCts;

    public bool IsRunning { get; private set; }
    public Guid ActivePlaylistId { get; private set; }
    public string ActivePlaylistTitle { get; private set; } = string.Empty;
    public int Completed { get; private set; }
    public int Total { get; private set; }

    public PlaylistPrepCoordinator(
        IPlaylistPrepService prepService,
        IEventBus? eventBus = null,
        INotificationService? notificationService = null,
        ILogger<PlaylistPrepCoordinator>? logger = null)
    {
        _prepService = prepService ?? throw new ArgumentNullException(nameof(prepService));
        _eventBus = eventBus;
        _notificationService = notificationService;
        _logger = logger;
    }

    public async Task<PlaylistPrepResult> RunAsync(Guid playlistId, string playlistTitle)
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            cts = new CancellationTokenSource();
            _activeCts = cts;
            IsRunning = true;
            ActivePlaylistId = playlistId;
            ActivePlaylistTitle = string.IsNullOrWhiteSpace(playlistTitle) ? "Playlist" : playlistTitle;
            Completed = 0;
            Total = 0;
        }

        Publish(isRunning: true, playlistId, ActivePlaylistTitle, 0, 0, 0, 0, 0, false, $"Prepping '{ActivePlaylistTitle}'…");

        try
        {
            var progress = new Progress<PlaylistPrepProgress>(p =>
            {
                Completed = p.Completed;
                Total = p.Total;
                Publish(true, playlistId, ActivePlaylistTitle, p.Completed, p.Total, p.Prepared, p.Skipped, p.Failed, false, p.StatusText);
            });

            var result = await _prepService.PrepareAsync(playlistId, progress, cts.Token).ConfigureAwait(false);

            Completed = result.Prepared + result.Skipped + result.Failed;
            Total = result.TotalEligible;

            if (result.WasCancelled)
            {
                _notificationService?.Show(
                    "Prep for Mix cancelled",
                    $"Stopped '{ActivePlaylistTitle}' — {result.Prepared} prepped, {result.Skipped} already ready.",
                    Views.NotificationType.Warning);
            }
            else if (result.Failed > 0)
            {
                var firstError = result.Errors.Count > 0 ? $" ({result.Errors[0]})" : string.Empty;
                _notificationService?.Show(
                    "Prep for Mix finished with issues",
                    $"'{ActivePlaylistTitle}': {result.Prepared} prepped, {result.Skipped} already ready, {result.Failed} failed{firstError}.",
                    Views.NotificationType.Warning);
            }
            else if (result.TotalEligible == 0)
            {
                _notificationService?.Show(
                    "Prep for Mix",
                    $"'{ActivePlaylistTitle}' has no local downloaded tracks to prepare yet.",
                    Views.NotificationType.Information);
            }
            else
            {
                _notificationService?.Show(
                    "Prep for Mix complete",
                    $"'{ActivePlaylistTitle}': {result.Prepared} prepped, {result.Skipped} already ready.",
                    Views.NotificationType.Success);
            }

            lock (_gate)
            {
                if (ReferenceEquals(_activeCts, cts))
                {
                    IsRunning = false;
                }
            }

            Publish(
                isRunning: false,
                playlistId,
                ActivePlaylistTitle,
                Completed,
                Total,
                result.Prepared,
                result.Skipped,
                result.Failed,
                result.WasCancelled,
                result.WasCancelled ? "Prep cancelled" : "Prep complete");

            return result;
        }
        catch (Exception ex)
        {
            _logger?.LogError(ex, "[PlaylistPrepCoordinator] Prep failed for playlist {PlaylistId}", playlistId);
            _notificationService?.Show(
                "Prep for Mix failed",
                $"Could not prepare '{ActivePlaylistTitle}': {ex.Message}",
                Views.NotificationType.Error);

            lock (_gate)
            {
                if (ReferenceEquals(_activeCts, cts))
                {
                    IsRunning = false;
                }
            }

            Publish(false, playlistId, ActivePlaylistTitle, Completed, Total, 0, 0, 1, false, ex.Message);
            return new PlaylistPrepResult(Total, 0, 0, 1, 0, false, new[] { ex.Message });
        }
    }

    public void Cancel()
    {
        lock (_gate)
        {
            _activeCts?.Cancel();
        }
    }

    private void Publish(
        bool isRunning,
        Guid playlistId,
        string playlistTitle,
        int completed,
        int total,
        int prepared,
        int skipped,
        int failed,
        bool wasCancelled,
        string statusText)
    {
        _eventBus?.Publish(new PlaylistPrepStatusChangedEvent(
            isRunning, playlistId, playlistTitle, completed, total, prepared, skipped, failed, wasCancelled, statusText));
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _activeCts?.Cancel();
            _activeCts?.Dispose();
            _activeCts = null;
        }
    }
}
