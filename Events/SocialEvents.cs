using System;
using SLSKDONET.Services;

namespace SLSKDONET.Models;

// Social: presence, 1:1 chat, chat rooms — republished from ISoulseekAdapter's raw C# events
// onto the app's own event bus so ViewModels can subscribe without holding a direct adapter
// reference, matching the existing AnalysisEvents.cs pattern.

public record UserPresenceChangedEvent(string Username, UserPresenceState Presence, bool IsPrivileged);

public record PrivateMessageReceivedEvent(Guid Id, string PeerUsername, string Message, DateTime TimestampUtc, bool IsOutgoing);

public record RoomMessageReceivedEvent(Guid Id, string RoomName, string Username, string Message, DateTime TimestampUtc, bool IsOutgoing);

public record RoomMembershipChangedEvent(string RoomName, string Username, bool Joined);

/// <summary>Published when the user clicks a chat/room notification — asks the Users page to open that thread.</summary>
public record OpenConversationRequestedEvent(string? Username, string? RoomName);

/// <summary>Published when a whole 1:1 conversation is cleared, so the Users page can drop it from the Conversations list without a full reload.</summary>
public record ConversationClearedEvent(string PeerUsername);
