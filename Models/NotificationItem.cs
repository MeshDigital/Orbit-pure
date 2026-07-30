using System;
using ReactiveUI;

namespace SLSKDONET.Models;

public enum NotificationKind
{
    DownloadCompleted,
    PrivateMessage,
    RoomMessage
}

/// <summary>
/// A single entry in the persistent notification history (bell/side panel) — distinct from the
/// existing ephemeral <c>ToastRequestedEvent</c>/toast popups, which auto-dismiss and keep no
/// history. <see cref="IsRead"/> is mutable so the panel can mark items read without replacing them.
/// </summary>
public sealed class NotificationItem : ReactiveObject
{
    public Guid Id { get; } = Guid.NewGuid();
    public NotificationKind Kind { get; init; }
    public string Title { get; init; } = string.Empty;
    public string? Detail { get; init; }
    public DateTime TimestampUtc { get; init; } = DateTime.UtcNow;

    /// <summary>Soulseek username to jump to (Users page) when this notification is clicked, if applicable.</summary>
    public string? NavigationUsername { get; init; }

    /// <summary>Room name to jump to (Users page → Rooms) when this notification is clicked, if applicable.</summary>
    public string? NavigationRoomName { get; init; }

    private bool _isRead;
    public bool IsRead
    {
        get => _isRead;
        set => this.RaiseAndSetIfChanged(ref _isRead, value);
    }

    public string KindIcon => Kind switch
    {
        NotificationKind.DownloadCompleted => "📥",
        NotificationKind.PrivateMessage => "💬",
        NotificationKind.RoomMessage => "👥",
        _ => "🔔"
    };
}
