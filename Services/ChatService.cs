using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;

namespace SLSKDONET.Services;

/// <summary>
/// Owns 1:1 Soulseek private messaging: persists incoming/outgoing messages and republishes
/// them on the app event bus. Deliberately separate from <see cref="RoomChatService"/> and
/// <see cref="UserPresenceWatchService"/> — three independently-lifecycled concerns with
/// different persistence needs, matching the codebase's existing small-focused-service
/// convention (PeerReliabilityService, FrequentSourceService) rather than one god-service.
/// </summary>
public sealed class ChatService
{
    private readonly ISoulseekAdapter _adapter;
    private readonly DatabaseService _databaseService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<ChatService> _logger;

    // Guards against re-publishing a server-replayed message that arrives again after a
    // reconnect while a conversation thread is already open and displaying it. The DB's unique
    // index on SoulseekMessageId already prevents a duplicate row, but that write is effectively
    // fire-and-forget relative to this in-memory publish, so a separate guard is needed here too.
    // Pruned opportunistically (same pattern as NotificationCenterService._recentlyCompletedHashes)
    // so a long-running session doesn't grow this unbounded — a replay only ever happens shortly
    // after a reconnect, so anything older than the retention window could never legitimately need
    // to be looked up again.
    private readonly ConcurrentDictionary<int, DateTime> _seenIncomingMessageIds = new();
    private static readonly TimeSpan SeenMessageIdRetention = TimeSpan.FromHours(24);

    public ChatService(ISoulseekAdapter adapter, DatabaseService databaseService, IEventBus eventBus, ILogger<ChatService> logger)
    {
        _adapter = adapter;
        _databaseService = databaseService;
        _eventBus = eventBus;
        _logger = logger;

        _adapter.PrivateMessageReceived += OnPrivateMessageReceived;
    }

    public async Task SendMessageAsync(string username, string message)
    {
        await _adapter.SendPrivateMessageAsync(username, message).ConfigureAwait(false);

        var entity = new PrivateMessageEntity
        {
            PeerUsername = username,
            IsOutgoing = true,
            Message = message,
            TimestampUtc = DateTime.UtcNow,
        };

        // The library doesn't echo your own sent messages back through PrivateMessageReceived,
        // so the outgoing copy is persisted and published here, immediately.
        await _databaseService.RecordPrivateMessageAsync(entity).ConfigureAwait(false);
        _eventBus.Publish(new PrivateMessageReceivedEvent(entity.Id, username, message, entity.TimestampUtc, IsOutgoing: true));
    }

    public Task<List<PrivateMessageEntity>> GetConversationAsync(string peerUsername, int limit = 500, DateTime? beforeUtc = null)
        => _databaseService.GetConversationAsync(peerUsername, limit, beforeUtc);

    public Task<List<ConversationSummary>> GetRecentConversationsAsync()
        => _databaseService.GetRecentConversationsAsync();

    /// <summary>Removes a single message from local history — a local-only action (the Soulseek protocol has no message recall), it never affects what the peer has.</summary>
    public Task DeleteMessageAsync(Guid id) => _databaseService.DeletePrivateMessageAsync(id);

    /// <summary>Wipes an entire conversation's local history — for spam/gate-bot peers you just want gone.</summary>
    public Task DeleteConversationAsync(string peerUsername) => _databaseService.DeleteConversationAsync(peerUsername);

    private void OnPrivateMessageReceived(object? sender, PrivateMessageReceivedEventArgs e)
    {
        var now = DateTime.UtcNow;
        if (!_seenIncomingMessageIds.TryAdd(e.Id, now))
        {
            _logger.LogDebug("[Chat] Ignoring duplicate/replayed message {Id} from {Username}", e.Id, e.Username);
            return;
        }

        if (_seenIncomingMessageIds.Count > 256)
        {
            var stale = _seenIncomingMessageIds.Where(kv => now - kv.Value >= SeenMessageIdRetention).Select(kv => kv.Key).ToList();
            foreach (var key in stale)
                _seenIncomingMessageIds.TryRemove(key, out _);
        }

        _ = PersistAndPublishAsync(e);
    }

    private async Task PersistAndPublishAsync(PrivateMessageReceivedEventArgs e)
    {
        try
        {
            var entity = new PrivateMessageEntity
            {
                SoulseekMessageId = e.Id,
                PeerUsername = e.Username,
                IsOutgoing = false,
                Message = e.Message,
                TimestampUtc = e.TimestampUtc,
                WasReplayed = e.Replayed,
                IsRead = false,
            };

            await _databaseService.RecordPrivateMessageAsync(entity).ConfigureAwait(false);
            _eventBus.Publish(new PrivateMessageReceivedEvent(entity.Id, e.Username, e.Message, e.TimestampUtc, IsOutgoing: false));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[Chat] Failed to persist/publish incoming message from {Username}", e.Username);
        }
    }
}
