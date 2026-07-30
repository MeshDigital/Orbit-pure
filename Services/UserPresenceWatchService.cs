using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Reference-counted wrapper around <see cref="ISoulseekAdapter.WatchUserAsync"/>/<see cref="ISoulseekAdapter.UnwatchUserAsync"/>.
/// Calling <see cref="WatchAsync"/> only issues a real watch request on the 0-&gt;1 transition for a
/// username, and only unwatches on the 1-&gt;0 transition — so multiple open profile views on the
/// same user share one underlying watch instead of stacking redundant server requests, and closing
/// one view doesn't kill presence updates for another still-open view of the same user.
/// </summary>
public sealed class UserPresenceWatchService
{
    private readonly ISoulseekAdapter _adapter;
    private readonly IEventBus _eventBus;
    private readonly ILogger<UserPresenceWatchService> _logger;
    private readonly ConcurrentDictionary<string, int> _refCounts = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Last known watch snapshot (speed/share counts/country) per username. The rich data only
    /// arrives on the actual 0-&gt;1 WatchUserAsync call, not on later UserStatusChanged events —
    /// cached here so a second caller watching an already-watched user still gets something,
    /// rather than nothing, without triggering a redundant watch request.
    /// </summary>
    private readonly ConcurrentDictionary<string, UserWatchSnapshot> _lastSnapshots = new(StringComparer.OrdinalIgnoreCase);

    public UserPresenceWatchService(ISoulseekAdapter adapter, IEventBus eventBus, ILogger<UserPresenceWatchService> logger)
    {
        _adapter = adapter;
        _eventBus = eventBus;
        _logger = logger;

        _adapter.UserStatusChanged += OnUserStatusChanged;
    }

    private void OnUserStatusChanged(object? sender, UserStatusChangedEventArgs e)
    {
        _eventBus.Publish(new UserPresenceChangedEvent(e.Username, e.Presence, e.IsPrivileged));
    }

    public async Task<(IDisposable Handle, UserWatchSnapshot? Snapshot)> WatchAsync(string username, CancellationToken ct = default)
    {
        var count = _refCounts.AddOrUpdate(username, 1, (_, existing) => existing + 1);
        if (count == 1)
        {
            try
            {
                var snapshot = await _adapter.WatchUserAsync(username, ct).ConfigureAwait(false);
                _lastSnapshots[username] = snapshot;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to watch user {Username}", username);
            }
        }

        _lastSnapshots.TryGetValue(username, out var cached);
        return (new WatchHandle(this, username), cached);
    }

    private void Release(string username)
    {
        var count = _refCounts.AddOrUpdate(username, 0, (_, existing) => Math.Max(0, existing - 1));
        if (count != 0)
            return;

        _refCounts.TryRemove(username, out _);
        _ = UnwatchSafeAsync(username);
    }

    private async Task UnwatchSafeAsync(string username)
    {
        try
        {
            await _adapter.UnwatchUserAsync(username).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unwatch user {Username}", username);
        }
    }

    private sealed class WatchHandle : IDisposable
    {
        private readonly UserPresenceWatchService _owner;
        private readonly string _username;
        private int _disposed;

        public WatchHandle(UserPresenceWatchService owner, string username)
        {
            _owner = owner;
            _username = username;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0)
                return;

            _owner.Release(_username);
        }
    }
}
