using System.Diagnostics;
using System.IO;
using System.Reactive.Subjects;
using Microsoft.Extensions.Logging;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using Soulseek;
using System.Linq;
using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using System.Collections.ObjectModel;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Open.Nat;

namespace SLSKDONET.Services;


/// <summary>
/// Soulseek file transfers, user shares, presence/watch, direct messages, and chat rooms (#307).
/// </summary>
public partial class SoulseekAdapter
{
    public async Task<bool> DownloadAsync(
        string username,
        string filename,
        string outputPath,
        long? size = null,
        IProgress<double>? progress = null,
        Action<TransferLifecycleUpdate>? lifecycleUpdate = null,
        CancellationToken ct = default,
        long startOffset = 0,  // Phase 2.5: Add resume support
        bool suppressCompletionEventOnFailure = false)
    {
        if (this._client == null)
        {
            throw new InvalidOperationException("Not connected to Soulseek");
        }

        try
        {
            this._logger.LogInformation("Downloading {Filename} from {Username} to {OutputPath} (offset: {Offset})", 
                filename, username, outputPath, startOffset);
            
            // Check if already cancelled
            ct.ThrowIfCancellationRequested();

            var directory = Path.GetDirectoryName(outputPath);
            if (directory != null)
                System.IO.Directory.CreateDirectory(directory);

            // Track state for timeout logic
            DateTime lastActivity = DateTime.UtcNow;
            long lastBytes = startOffset;  // Start from existing bytes
            bool isQueued = false;
            bool transferStartedOrQueued = false;
            TransferLifecyclePhase? lastPhase = null;
            DateTime queueStartTime = DateTime.UtcNow; // Phase 3D: Detect zombie peers stuck in queue
            var connectFailFastSeconds = Math.Clamp(_config.PeerConnectFailFastSeconds, 5, 30);
            var stallTimeoutSeconds = Math.Clamp(_config.TransferStallTimeoutSeconds, 15, 180);

            // Queue Velocity: zombie detection based on position *stagnation*, not elapsed time.
            // A popular peer with 500 people in queue is healthy; a dead peer whose position
            // never moves is a zombie. We give the peer an initial grace period to report any
            // position at all, then track whether the position actually improves over time.
            int lastKnownQueuePosition = -1;                    // -1 = never reported
            DateTime queuePositionLastChanged = DateTime.UtcNow; // Reset whenever position improves
            // Initial grace: peer gets up to MaxQueueWaitMinutes * 60 seconds total, but must show
            // *some* queue progress within QUEUE_STAGNATION_WINDOW_SECONDS.
            // e.g. 15 min stagnation = zombie. A 500-deep queue should move every few minutes.
            var maxQueueWaitSeconds = Math.Max(300, _config.MaxQueueWaitTimeMinutes * 60);
            const int QUEUE_INITIAL_GRACE_SECONDS = 120;  // Allow 2 min before any position is required
            const int QUEUE_STAGNATION_WINDOW_SECONDS = 300; // was 900 — 5 min stagnation = zombie; re-discover faster instead of waiting 15 min on dead peers

            var downloadOptions = new TransferOptions(
                stateChanged: (args) =>
                {
                    // Update queued status
                    if (args.Transfer.State.HasFlag(TransferStates.Queued))
                    {
                        if (!isQueued)
                        {
                            queueStartTime = DateTime.UtcNow; // Phase 3D: Record when queue started
                            queuePositionLastChanged = DateTime.UtcNow; // Velocity clock starts now
                        }
                        isQueued = true;
                        transferStartedOrQueued = true;
                        if (lastPhase != TransferLifecyclePhase.RemoteQueued)
                        {
                            lastPhase = TransferLifecyclePhase.RemoteQueued;
                            lifecycleUpdate?.Invoke(new TransferLifecycleUpdate(
                                TransferLifecyclePhase.RemoteQueued,
                                "Queued remotely by peer"));
                        }
                    }
                    else if (args.Transfer.State.HasFlag(TransferStates.InProgress))
                    {
                        isQueued = false;
                        transferStartedOrQueued = true;
                        if (lastPhase != TransferLifecyclePhase.Transferring)
                        {
                            lastPhase = TransferLifecyclePhase.Transferring;
                            lifecycleUpdate?.Invoke(new TransferLifecycleUpdate(
                                TransferLifecyclePhase.Transferring,
                                startOffset > 0 ? "Transfer resumed" : "Transfer started"));
                        }
                        
                        // Check for progress activity
                        if (args.Transfer.BytesTransferred > lastBytes)
                        {
                            lastBytes = args.Transfer.BytesTransferred;
                            lastActivity = DateTime.UtcNow;
                        }

                        if (size.HasValue && size.Value > 0)
                        {
                            double percentage = (double)args.Transfer.BytesTransferred / size.Value;
                            progress?.Report(percentage);
                            
                            DownloadProgressChanged?.Invoke(this, new DownloadProgressEventArgs(
                                filename, username, percentage, args.Transfer.BytesTransferred, size.Value));
                        }
                    }
                });

            // Phase 2.5: Use Append mode if resuming, Create if starting fresh
            var fileMode = startOffset > 0 ? FileMode.Append : FileMode.Create;
            FileStream? fileStream = null;
            using var downloadCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            Task? downloadTask = null;
            var transferCompleted = false;
            
            try
            {
                fileStream = new FileStream(outputPath, fileMode, FileAccess.Write, FileShare.None, 8192, useAsync: true);
                
                // We wrap the Soulseek DownloadAsync in our own task to enforce our custom timeout logic
                // The underlying client has some timeout logic, but we want granular control over "Stalled vs Queued"
                downloadTask = this._client.DownloadAsync(
                    username,
                    filename,
                    () => Task.FromResult((Stream)fileStream),
                    size,
                    startOffset: startOffset,  // Pass the offset to Soulseek client
                    options: downloadOptions,
                    cancellationToken: downloadCts.Token);

                // Download completion/failure already has dedicated events (TransferFinishedEvent/
                // TransferFailedEvent below); this just marks dispatch for the network activity feed —
                // the stall-detection loop below can cancel/retry independently of the underlying
                // call, so there's no single "call finished" point to wrap with TrackNetworkCallAsync.
                if (_config.EnableNetworkActivityMonitor)
                {
                    _eventBus.Publish(new NetworkActivityEvent(DateTime.UtcNow, "Soulseek", "Download", $"{username}: {filename}", null, true));
                }

                // Monitoring Loop
                while (!downloadTask.IsCompleted)
                {
                    var idleSeconds = (DateTime.UtcNow - lastActivity).TotalSeconds;
                    if (!transferStartedOrQueued && idleSeconds > connectFailFastSeconds)
                    {
                        downloadCts.Cancel();
                        throw new TimeoutException($"Peer did not respond within {connectFailFastSeconds} seconds.");
                    }

                    // Check if we should time out
                    // Modified: Only timeout if NOT queued and no activity for configured stall window
                    if (!isQueued && idleSeconds > stallTimeoutSeconds)
                    {
                        // STALLED: Not queued, but no bytes moved for configured timeout
                        downloadCts.Cancel();
                        throw new TimeoutException($"Transfer stalled for {stallTimeoutSeconds} seconds (0 bytes received).");
                    }
                    
                    // Phase 3D: ZOMBIE DETECTION via Queue Velocity.
                    // We do NOT use a static elapsed-time timeout because healthy popular peers
                    // can legitimately have 500+ queued users (45min+ waits are normal on Soulseek).
                    // Instead, we check whether the peer-reported queue POSITION has improved
                    // within a reasonable stagnation window. No movement = dead peer.
                    if (isQueued)
                    {
                        var totalQueueSeconds = (DateTime.UtcNow - queueStartTime).TotalSeconds;
                        var stagnationSeconds = (DateTime.UtcNow - queuePositionLastChanged).TotalSeconds;
                        
                        // Absolute cap: never wait beyond MaxQueueWaitTimeMinutes config
                        if (totalQueueSeconds > maxQueueWaitSeconds)
                        {
                            downloadCts.Cancel();
                            throw new TimeoutException(
                                $"Queue exceeded maximum configured wait of {_config.MaxQueueWaitTimeMinutes} minutes. Dropping peer.");
                        }
                        
                        // After the initial grace period, require that position has moved at least once.
                        // If position is still -1 (never reported), the peer is likely dead.
                        if (totalQueueSeconds > QUEUE_INITIAL_GRACE_SECONDS && lastKnownQueuePosition == -1)
                        {
                            downloadCts.Cancel();
                            throw new TimeoutException(
                                $"Peer never reported a queue position after {QUEUE_INITIAL_GRACE_SECONDS}s grace. Dropping zombie peer.");
                        }
                        
                        // Position stagnation check: if we have a known position and it hasn't
                        // improved in QUEUE_STAGNATION_WINDOW_SECONDS, the peer is a zombie.
                        if (lastKnownQueuePosition > 0 && stagnationSeconds > QUEUE_STAGNATION_WINDOW_SECONDS)
                        {
                            downloadCts.Cancel();
                            throw new TimeoutException(
                                $"Queue position stagnant at #{lastKnownQueuePosition} for {stagnationSeconds:0}s (zombie peer). Dropping peer.");
                        }
                    }
                    
                    // If we are queued but not stuck, we wait for user cancellation
                    // Queue timeout above prevents indefinite hanging

                    await Task.WhenAny(downloadTask, Task.Delay(1000, ct));
                }

                await downloadTask; // Propagate exceptions/completion
                transferCompleted = true;
                
                this._logger.LogInformation("Download completed: {Filename}", filename);
                progress?.Report(1.0);
                _eventBus.Publish(new TransferFinishedEvent(filename, username));

                try
                {
                    await TryRecordFrequentSourceDownloadAsync(
                        _frequentSourceService,
                        username,
                        filename,
                        size,
                        lastBytes,
                        DateTime.UtcNow,
                        ct).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Frequent Sources hook failed for {Username}/{Filename}", username, filename);
                }
                
                DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, true));
                
                return true;
            }
            finally
            {
                if (!transferCompleted && downloadTask != null)
                {
                    try
                    {
                        if (!downloadCts.IsCancellationRequested)
                        {
                            downloadCts.Cancel();
                        }

                        await downloadTask.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (downloadCts.IsCancellationRequested)
                    {
                    }
                    catch (TimeoutException)
                    {
                        this._logger.LogWarning("Timed out while awaiting terminal transfer task cleanup for {Filename}", filename);
                    }
                    catch (Exception ex) when (
                        downloadCts.IsCancellationRequested ||
                        ex.Message.Contains("Transfer complete", StringComparison.OrdinalIgnoreCase))
                    {
                        this._logger.LogDebug(ex, "Ignoring terminal download task exception during cleanup for {Filename}", filename);
                    }
                }

                // CRITICAL: Ensure FileStream is always closed properly, even on exception
                // This prevents "file in use" errors on retry attempts
                if (fileStream != null)
                {
                    try
                    {
                        await fileStream.DisposeAsync().ConfigureAwait(false);
                        this._logger.LogDebug("FileStream closed for {Filename}", filename);
                    }
                    catch (Exception disposeEx)
                    {
                        this._logger.LogWarning(disposeEx, "Error disposing stream for {Filename}", filename);
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            this._logger.LogWarning("Download cancelled: {Filename}", filename);
            _eventBus.Publish(new TransferCancelledEvent(filename, username));

            // Not gated by suppressCompletionEventOnFailure: this rethrows, so it always ends the
            // same-peer retry loop immediately (no later attempt to consolidate with) — it's the
            // one and only signal for this episode regardless of which attempt it landed on.
            DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Cancelled"));

            throw;
        }
        catch (TimeoutException ex)
        {
            this._logger.LogWarning("Download timeout: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection timeout"));

            // Same-peer retry loop (DownloadManager) suppresses this on all but the final attempt
            // of a "give this peer a couple more shots" episode, so one flaky transfer doesn't
            // dock the peer's reliability score once per attempt.
            if (!suppressCompletionEventOnFailure)
                DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Timeout"));

            return false;
        }
        catch (IOException ex)
        {
            this._logger.LogError(ex, "I/O error during download: {Filename} from {Username}", filename, username);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "I/O error: " + ex.Message));

            if (!suppressCompletionEventOnFailure)
                DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "I/O Error: " + ex.Message));

            return false;
        }
        catch (Exception ex) when (ex.Message.Contains("refused") || ex.Message.Contains("aborted") || ex.Message.Contains("Unable to read"))
        {
            this._logger.LogWarning("Network error during download: {Filename} from {Username} - {Message}", filename, username, ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, "Connection failed"));

            if (!suppressCompletionEventOnFailure)
                DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Connection Failed"));

            return false;
        }
        catch (Soulseek.TransferRejectedException ex)
        {
             // RETHROW: "Too many files" or "Banned"
             // This allows DownloadManager to catch it and trigger Exponential Backoff / Retry.
             // Not gated by suppressCompletionEventOnFailure — same reasoning as the Cancelled
             // catch above: rethrows immediately, so it's always the episode's one true signal,
             // and an explicit rejection deserves to be reported, not treated as a transient blip.
             DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, "Rejected: " + ex.Message));
             throw;
        }
        catch (Exception ex)
        {
            this._logger.LogError(ex, "Download failed: {Message}", ex.Message);
            _eventBus.Publish(new TransferFailedEvent(filename, username, ex.Message));

            if (!suppressCompletionEventOnFailure)
                DownloadCompleted?.Invoke(this, new DownloadCompletedEventArgs(filename, username, false, ex.Message));

            return false;
        }
    }

    public async Task<IEnumerable<Track>> GetUserSharesAsync(string username, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            _logger.LogInformation("Browsing shares for user: {Username}", username);
            
            var response = await TrackNetworkCallAsync("Browse", username, () => _client.BrowseAsync(username, cancellationToken: ct));
            
            var tracks = new List<Track>();
            var allFiles = response.Directories
                .Concat(response.LockedDirectories)
                .SelectMany(directory => directory.Files.Select(file => new SearchResponse(
                    username,
                    0,
                    false,
                    0,
                    0,
                    new[]
                    {
                        new Soulseek.File(
                            file.Code,
                            $"{directory.Name.TrimEnd('\\')}\\{file.Filename}",
                            file.Size,
                            file.Extension,
                            file.Attributes)
                    })));

            foreach (var responseItem in allFiles)
            {
                var file = responseItem.Files.First();
                var track = ParseTrackFromFile(file, responseItem);
                if (track != null) tracks.Add(track);
            }
            
            _logger.LogInformation("Found {Count} files in {Username}'s shares", tracks.Count, username);
            return tracks;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to browse user shares for {Username}: {Message}", username, ex.Message);
            return Enumerable.Empty<Track>();
        }
    }

    // ── Social: presence ─────────────────────────────────────────────────

    public async Task<UserWatchSnapshot> WatchUserAsync(string username, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            var data = await TrackNetworkCallAsync("WatchUser", username, () => _client.WatchUserAsync(username, cancellationToken: ct));
            return new UserWatchSnapshot(
                data.Username,
                MapPresence(data.Status),
                data.AverageSpeed,
                data.DirectoryCount,
                data.FileCount,
                data.SlotsFree,
                data.UploadCount,
                data.CountryCode);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to watch user {Username}: {Message}", username, ex.Message);
            return new UserWatchSnapshot(username, UserPresenceState.Unknown, 0, 0, 0, null, 0, null);
        }
    }

    public async Task SetStatusAsync(UserPresenceState status, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            await TrackNetworkCallAsync("SetStatus", status.ToString(), () => _client.SetStatusAsync(MapPresenceToLibrary(status), cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set own status to {Status}: {Message}", status, ex.Message);
        }
    }

    public async Task UnwatchUserAsync(string username, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            return;

        try
        {
            await TrackNetworkCallAsync("UnwatchUser", username, () => _client.UnwatchUserAsync(username, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to unwatch user {Username}: {Message}", username, ex.Message);
        }
    }

    public async Task<UserStatusSnapshot> GetUserStatusAsync(string username, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            var status = await TrackNetworkCallAsync("GetUserStatus", username, () => _client.GetUserStatusAsync(username, cancellationToken: ct));
            return new UserStatusSnapshot(status.Username, MapPresence(status.Presence), status.IsPrivileged);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get status for {Username}: {Message}", username, ex.Message);
            return new UserStatusSnapshot(username, UserPresenceState.Unknown, false);
        }
    }

    public async Task<UserProfileSnapshot> GetUserInfoAsync(string username, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            var info = await TrackNetworkCallAsync("GetUserInfo", username, () => _client.GetUserInfoAsync(username, cancellationToken: ct));
            return new UserProfileSnapshot(
                username,
                info.Description,
                info.HasPicture,
                info.Picture,
                info.HasFreeUploadSlot,
                info.UploadSlots,
                info.QueueLength);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get user info for {Username}: {Message}", username, ex.Message);
            return new UserProfileSnapshot(username, null, false, null, false, 0, 0);
        }
    }

    private static UserPresenceState MapPresence(UserPresence presence) => presence switch
    {
        UserPresence.Online => UserPresenceState.Online,
        UserPresence.Away => UserPresenceState.Away,
        UserPresence.Offline => UserPresenceState.Offline,
        _ => UserPresenceState.Unknown
    };

    private static UserPresence MapPresenceToLibrary(UserPresenceState state) => state switch
    {
        UserPresenceState.Online => UserPresence.Online,
        UserPresenceState.Away => UserPresence.Away,
        UserPresenceState.Offline => UserPresence.Offline,
        _ => UserPresence.Online
    };

    // ── Social: 1:1 chat ─────────────────────────────────────────────────

    public async Task SendPrivateMessageAsync(string username, string message, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        await WaitForMessageTokenAsync(ct).ConfigureAwait(false);
        await TrackNetworkCallAsync("SendMessage", username, () => _client.SendPrivateMessageAsync(username, message, cancellationToken: ct));
    }

    // ── Social: rooms ────────────────────────────────────────────────────

    public async Task<IReadOnlyList<RoomSummary>> GetRoomListAsync(CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            var roomList = await TrackNetworkCallAsync("GetRoomList", "server", () => _client.GetRoomListAsync(cancellationToken: ct));
            return roomList.Public.Select(r => new RoomSummary(r.Name, r.UserCount, IsPrivate: false))
                .Concat(roomList.Private.Select(r => new RoomSummary(r.Name, r.UserCount, IsPrivate: true)))
                .ToList();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to get room list: {Message}", ex.Message);
            return Array.Empty<RoomSummary>();
        }
    }

    public async Task<RoomSnapshot> JoinRoomAsync(string roomName, bool isPrivate = false, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        try
        {
            var room = await TrackNetworkCallAsync("JoinRoom", roomName, () => _client.JoinRoomAsync(roomName, isPrivate, cancellationToken: ct));
            var members = (room.Users ?? Enumerable.Empty<UserData>())
                .Select(u => new RoomMemberSnapshot(u.Username, MapPresence(u.Status), u.AverageSpeed, u.FileCount, u.DirectoryCount, u.SlotsFree))
                .ToList();
            return new RoomSnapshot(room.Name, room.IsPrivate, room.Owner, members);
        }
        catch (RoomJoinForbiddenException ex)
        {
            _logger.LogWarning(ex, "Server rejected join request for room {RoomName}", roomName);
            throw new InvalidOperationException($"The server rejected joining \"{roomName}\" — some rooms require sharing files or meeting other conditions.", ex);
        }
        catch (Exception ex) when (ex is TimeoutException or NoResponseException)
        {
            _logger.LogWarning(ex, "Join room {RoomName} timed out / no response", roomName);
            throw new InvalidOperationException($"Joining \"{roomName}\" timed out — the room may be very large or the server may be slow to respond.", ex);
        }
    }

    public async Task LeaveRoomAsync(string roomName, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            return;

        try
        {
            await TrackNetworkCallAsync("LeaveRoom", roomName, () => _client.LeaveRoomAsync(roomName, cancellationToken: ct));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to leave room {RoomName}: {Message}", roomName, ex.Message);
        }
    }

    public async Task SendRoomMessageAsync(string roomName, string message, CancellationToken ct = default)
    {
        if (_client == null || !_client.State.HasFlag(SoulseekClientStates.Connected))
            throw new InvalidOperationException("Not connected to Soulseek");

        await WaitForMessageTokenAsync(ct).ConfigureAwait(false);
        await TrackNetworkCallAsync("SendRoomMessage", roomName, () => _client.SendRoomMessageAsync(roomName, message, cancellationToken: ct));
    }

}
