using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Owns Soulseek chat-room membership and messaging: join/leave, send, persists message
/// history, republishes on the app event bus, and tracks each currently-joined room's live
/// roster in memory so the Rooms UI can rehydrate without re-querying the server on every
/// rebind. Room membership itself is never persisted — it's live, server-authoritative state,
/// refetched via JoinRoomAsync/RoomJoined each session (same treatment the app already gives
/// other live state, like transfer progress).
/// </summary>
public sealed class RoomChatService
{
    private readonly ISoulseekAdapter _adapter;
    private readonly DatabaseService _databaseService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<RoomChatService> _logger;
    private readonly ConcurrentDictionary<string, RoomSnapshot> _joinedRooms = new(StringComparer.OrdinalIgnoreCase);

    public RoomChatService(ISoulseekAdapter adapter, DatabaseService databaseService, IEventBus eventBus, ILogger<RoomChatService> logger)
    {
        _adapter = adapter;
        _databaseService = databaseService;
        _eventBus = eventBus;
        _logger = logger;

        _adapter.RoomMessageReceived += OnRoomMessageReceived;
        _adapter.RoomMembershipChanged += OnRoomMembershipChanged;
    }

    public IReadOnlyDictionary<string, RoomSnapshot> JoinedRooms => _joinedRooms;

    public Task<IReadOnlyList<RoomSummary>> GetRoomListAsync() => _adapter.GetRoomListAsync();

    public async Task<RoomSnapshot> JoinRoomAsync(string roomName, bool isPrivate = false)
    {
        var snapshot = await _adapter.JoinRoomAsync(roomName, isPrivate).ConfigureAwait(false);
        _joinedRooms[roomName] = snapshot;
        return snapshot;
    }

    public async Task LeaveRoomAsync(string roomName)
    {
        await _adapter.LeaveRoomAsync(roomName).ConfigureAwait(false);
        _joinedRooms.TryRemove(roomName, out _);
    }

    public async Task SendMessageAsync(string roomName, string message)
    {
        await _adapter.SendRoomMessageAsync(roomName, message).ConfigureAwait(false);

        var entity = new RoomMessageEntity
        {
            RoomName = roomName,
            Username = "You",
            Message = message,
            IsOutgoing = true,
            TimestampUtc = DateTime.UtcNow,
        };

        // Mirrors ChatService.SendMessageAsync — the library doesn't echo your own room
        // messages back through RoomMessageReceived, so persist+publish it here directly.
        await _databaseService.RecordRoomMessageAsync(entity).ConfigureAwait(false);
        _eventBus.Publish(new RoomMessageReceivedEvent(entity.Id, roomName, entity.Username, message, entity.TimestampUtc, IsOutgoing: true));
    }

    public Task<List<RoomMessageEntity>> GetRoomHistoryAsync(string roomName, int limit = 500, DateTime? beforeUtc = null)
        => _databaseService.GetRoomHistoryAsync(roomName, limit, beforeUtc);

    /// <summary>Removes a single message from local room history — local-only, the Soulseek protocol has no message recall.</summary>
    public Task DeleteMessageAsync(Guid id) => _databaseService.DeleteRoomMessageAsync(id);

    private void OnRoomMessageReceived(object? sender, RoomMessageReceivedEventArgs e)
    {
        _ = PersistAndPublishAsync(e);
    }

    private async Task PersistAndPublishAsync(RoomMessageReceivedEventArgs e)
    {
        try
        {
            var entity = new RoomMessageEntity
            {
                RoomName = e.RoomName,
                Username = e.Username,
                Message = e.Message,
                TimestampUtc = e.TimestampUtc,
                IsOutgoing = false,
            };

            await _databaseService.RecordRoomMessageAsync(entity).ConfigureAwait(false);
            _eventBus.Publish(new RoomMessageReceivedEvent(entity.Id, e.RoomName, e.Username, e.Message, e.TimestampUtc, IsOutgoing: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[RoomChat] Failed to persist/publish message in {RoomName}", e.RoomName);
        }
    }

    private void OnRoomMembershipChanged(object? sender, RoomMembershipChangedEventArgs e)
    {
        _eventBus.Publish(new RoomMembershipChangedEvent(e.RoomName, e.Username, e.Joined));
    }
}
