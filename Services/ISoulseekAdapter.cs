using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

public interface ISoulseekAdapter
{
    bool IsConnected { get; }
    bool IsLoggedIn { get; }
    int SharedFileCount { get; }
    Task ConnectAsync(string? password = null, CancellationToken ct = default);
    Task<bool> ApplyRuntimeNetworkConfigurationAsync(CancellationToken ct = default);
    Task DisconnectAsync();
    void Disconnect();
    Task RefreshShareStateAsync(CancellationToken ct = default);
    IAsyncEnumerable<Track> StreamResultsAsync(
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        DownloadMode mode,
        SearchExecutionProfile? executionProfile = null,
        CancellationToken ct = default,
        SearchScopeKind scopeKind = SearchScopeKind.Network);

    /// <summary>
    /// Targeted, short-timeout search scoped to a single known peer (Soulseek protocol's
    /// SearchScope.User) — used to fast-path re-downloads/upgrades of tracks with a known-good
    /// source without paying the cost of a full network-wide search first.
    /// </summary>
    Task<List<Track>> SearchUserForTrackAsync(
        string username,
        string query,
        IEnumerable<string>? formatFilter,
        (int? Min, int? Max) bitrateFilter,
        int timeoutMs,
        CancellationToken ct = default);

    Task<bool> DownloadAsync(
        string username,
        string filename,
        string outputPath,
        long? size = null,
        IProgress<double>? progress = null,
        Action<TransferLifecycleUpdate>? lifecycleUpdate = null,
        CancellationToken ct = default,
        long startOffset = 0);

    Task<IEnumerable<Track>> GetUserSharesAsync(
        string username,
        CancellationToken ct = default);

    // ── Presence ─────────────────────────────────────────────────────────
    /// <summary>
    /// Starts server-side presence tracking for <paramref name="username"/>. The server bundles a
    /// one-time snapshot (speed/share counts/country) with the ack — return it rather than
    /// discarding it, since it's otherwise a wasted free read.
    /// </summary>
    Task<UserWatchSnapshot> WatchUserAsync(string username, CancellationToken ct = default);
    Task UnwatchUserAsync(string username, CancellationToken ct = default);
    Task<UserStatusSnapshot> GetUserStatusAsync(string username, CancellationToken ct = default);
    Task<UserProfileSnapshot> GetUserInfoAsync(string username, CancellationToken ct = default);
    /// <summary>Sets this client's own presence (Online/Away) as seen by anyone watching it.</summary>
    Task SetStatusAsync(UserPresenceState status, CancellationToken ct = default);

    // ── 1:1 chat ─────────────────────────────────────────────────────────
    Task SendPrivateMessageAsync(string username, string message, CancellationToken ct = default);

    // ── Rooms ────────────────────────────────────────────────────────────
    Task<IReadOnlyList<RoomSummary>> GetRoomListAsync(CancellationToken ct = default);
    Task<RoomSnapshot> JoinRoomAsync(string roomName, bool isPrivate = false, CancellationToken ct = default);
    Task LeaveRoomAsync(string roomName, CancellationToken ct = default);
    Task SendRoomMessageAsync(string roomName, string message, CancellationToken ct = default);

    event EventHandler<DownloadProgressEventArgs>? DownloadProgressChanged;
    event EventHandler<DownloadCompletedEventArgs>? DownloadCompleted;
    event EventHandler<UserStatusChangedEventArgs>? UserStatusChanged;
    event EventHandler<PrivateMessageReceivedEventArgs>? PrivateMessageReceived;
    event EventHandler<RoomMessageReceivedEventArgs>? RoomMessageReceived;
    event EventHandler<RoomMembershipChangedEventArgs>? RoomMembershipChanged;
}

/// <summary>Presence state for a Soulseek user, independent of the underlying library's enum.</summary>
public enum UserPresenceState
{
    Unknown,
    Offline,
    Away,
    Online
}

public sealed record UserStatusSnapshot(string Username, UserPresenceState Presence, bool IsPrivileged);

/// <summary>
/// The one-time bundle the Soulseek server includes with a successful watch acknowledgement —
/// share/speed stats and country, on top of presence. Cheaper than a full browse or a separate
/// GetUserInfoAsync round-trip, and it comes back "for free" with WatchUserAsync's own response.
/// </summary>
public sealed record UserWatchSnapshot(
    string Username,
    UserPresenceState Presence,
    int AverageSpeed,
    int DirectoryCount,
    int FileCount,
    int? SlotsFree,
    long UploadCount,
    string? CountryCode);

public sealed record UserProfileSnapshot(
    string Username,
    string? Description,
    bool HasPicture,
    byte[]? Picture,
    bool HasFreeUploadSlot,
    int UploadSlots,
    int QueueLength);

public sealed record RoomSummary(string Name, int UserCount, bool IsPrivate);

public sealed record RoomMemberSnapshot(
    string Username,
    UserPresenceState Presence,
    int AverageSpeed,
    int FileCount,
    int DirectoryCount,
    int? SlotsFree);

public sealed record RoomSnapshot(string Name, bool IsPrivate, string? Owner, IReadOnlyList<RoomMemberSnapshot> Members);

public class UserStatusChangedEventArgs : EventArgs
{
    public string Username { get; }
    public UserPresenceState Presence { get; }
    public bool IsPrivileged { get; }

    public UserStatusChangedEventArgs(string username, UserPresenceState presence, bool isPrivileged)
    {
        Username = username;
        Presence = presence;
        IsPrivileged = isPrivileged;
    }
}

public class PrivateMessageReceivedEventArgs : EventArgs
{
    public int Id { get; }
    public string Username { get; }
    public string Message { get; }
    public DateTime TimestampUtc { get; }
    public bool Replayed { get; }

    public PrivateMessageReceivedEventArgs(int id, string username, string message, DateTime timestampUtc, bool replayed)
    {
        Id = id;
        Username = username;
        Message = message;
        TimestampUtc = timestampUtc;
        Replayed = replayed;
    }
}

public class RoomMessageReceivedEventArgs : EventArgs
{
    public string RoomName { get; }
    public string Username { get; }
    public string Message { get; }
    /// <summary>Locally-stamped receipt time — the Soulseek protocol does not include a server timestamp for room messages.</summary>
    public DateTime TimestampUtc { get; }

    public RoomMessageReceivedEventArgs(string roomName, string username, string message, DateTime timestampUtc)
    {
        RoomName = roomName;
        Username = username;
        Message = message;
        TimestampUtc = timestampUtc;
    }
}

public class RoomMembershipChangedEventArgs : EventArgs
{
    public string RoomName { get; }
    public string Username { get; }
    public bool Joined { get; }

    public RoomMembershipChangedEventArgs(string roomName, string username, bool joined)
    {
        RoomName = roomName;
        Username = username;
        Joined = joined;
    }
}

public enum TransferLifecyclePhase
{
    RemoteQueued,
    Transferring,
    /// <summary>
    /// Fires when the peer reports a new queue position. Allows consumers to implement
    /// Queue Velocity tracking (detect zombie peers whose position never improves).
    /// </summary>
    QueuePositionUpdate
}

public sealed record TransferLifecycleUpdate(
    TransferLifecyclePhase Phase,
    string? Detail = null,
    /// <summary>Peer-reported queue position. Only populated for QueuePositionUpdate events.</summary>
    int? QueuePosition = null);

public class DownloadProgressEventArgs : EventArgs
{
    public string Filename { get; }
    public string Username { get; }
    public double Progress { get; }
    public long BytesReceived { get; }
    public long TotalBytes { get; }

    public DownloadProgressEventArgs(string filename, string username, double progress, long bytesReceived, long totalBytes)
    {
        Filename = filename;
        Username = username;
        Progress = progress;
        BytesReceived = bytesReceived;
        TotalBytes = totalBytes;
    }
}

public class DownloadCompletedEventArgs : EventArgs
{
    public string Filename { get; }
    public string Username { get; }
    public bool Success { get; }
    public string? Error { get; }

    public DownloadCompletedEventArgs(string filename, string username, bool success, string? error = null)
    {
        Filename = filename;
        Username = username;
        Success = success;
        Error = error;
    }
}
