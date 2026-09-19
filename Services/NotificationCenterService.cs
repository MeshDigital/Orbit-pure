using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

/// <summary>
/// Persistent notification history — a bell/side-panel complement to the existing ephemeral
/// toast popups (<see cref="NotificationServiceAdapter"/>/<c>ToastRequestedEvent</c>), which
/// auto-dismiss and keep no record. Listens for track downloads completing, incoming 1:1 chat
/// messages, and incoming room messages, and also fires a toast for each via the existing
/// <see cref="INotificationService"/> so both a transient and a persistent signal are raised
/// from the same event — one system, not two competing ones.
/// </summary>
public sealed class NotificationCenterService : ReactiveObject, IDisposable
{
    private const int MaxNotifications = 200;

    private readonly IEventBus _eventBus;
    private readonly DownloadManager _downloadManager;
    private readonly INotificationService _notificationService;
    private readonly WindowsToastService _windowsToast;
    private readonly ILogger<NotificationCenterService> _logger;
    private readonly CompositeDisposable _disposables = new();
    private string? _activeConversationUsername;
    private string? _activeRoomName;

    public ObservableCollection<NotificationItem> Notifications { get; } = new();

    private int _unreadCount;
    public int UnreadCount
    {
        get => _unreadCount;
        private set => this.RaiseAndSetIfChanged(ref _unreadCount, value);
    }

    public ReactiveCommand<Unit, Unit> MarkAllReadCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearCommand { get; }
    public ReactiveCommand<NotificationItem, Unit> OpenCommand { get; }

    public NotificationCenterService(
        IEventBus eventBus,
        DownloadManager downloadManager,
        INotificationService notificationService,
        WindowsToastService windowsToast,
        ILogger<NotificationCenterService> logger)
    {
        _eventBus = eventBus;
        _downloadManager = downloadManager;
        _notificationService = notificationService;
        _windowsToast = windowsToast;
        _logger = logger;

        eventBus.GetEvent<TrackStateChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => e.State == PlaylistTrackState.Completed)
            .Subscribe(OnTrackDownloadCompleted)
            .DisposeWith(_disposables);

        eventBus.GetEvent<PrivateMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => !e.IsOutgoing)
            .Subscribe(OnPrivateMessageReceived)
            .DisposeWith(_disposables);

        eventBus.GetEvent<RoomMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => !e.IsOutgoing)
            .Subscribe(OnRoomMessageReceived)
            .DisposeWith(_disposables);

        // Mirrors the "isActive" check UsersViewModel/RoomsViewModel already do locally to skip
        // marking a thread unread when it's the one currently open — without this, a message on
        // the thread the user is actively looking at still fired a toast + OS notification + a
        // persistent bell entry, none of which made sense for something already on screen.
        eventBus.GetEvent<ActiveConversationChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => _activeConversationUsername = e.Username)
            .DisposeWith(_disposables);

        eventBus.GetEvent<ActiveRoomChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e => _activeRoomName = e.RoomName)
            .DisposeWith(_disposables);

        MarkAllReadCommand = ReactiveCommand.Create(MarkAllRead);
        ClearCommand = ReactiveCommand.Create(Clear);
        OpenCommand = ReactiveCommand.Create<NotificationItem>(Open);
    }

    /// <summary>
    /// Handles a click on a notification row: marks it read and, for chat/room notifications,
    /// asks the Users page (via <see cref="MainViewModel"/>) to jump straight to that thread.
    /// </summary>
    private void Open(NotificationItem item)
    {
        MarkRead(item);

        if (!string.IsNullOrWhiteSpace(item.NavigationUsername) || !string.IsNullOrWhiteSpace(item.NavigationRoomName))
        {
            _eventBus.Publish(new global::SLSKDONET.Models.OpenConversationRequestedEvent(item.NavigationUsername, item.NavigationRoomName));
        }
    }

    // A track that belongs to multiple playlists gets one DownloadContext (and one
    // TrackStateChangedEvent(Completed)) per playlist when the same physical file finishes —
    // correct for each playlist's own row to update, but it means a single download can fire this
    // handler many times in a tight burst for the same hash, often interleaved with bursts for
    // OTHER tracks completing around the same moment (a single last-hash slot gets clobbered by
    // the interleaving and stops deduplicating either one). Track a per-hash last-notified
    // timestamp instead so the bell gets exactly one entry per actual download regardless of how
    // many playlists it belongs to or what else completes alongside it, while a genuine later
    // re-download of the same track still gets its own entry. Pruned opportunistically so this
    // doesn't grow unbounded over a long-running session.
    private readonly Dictionary<string, DateTime> _recentlyCompletedHashes = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan DuplicateCompletionWindow = TimeSpan.FromSeconds(10);

    private void OnTrackDownloadCompleted(TrackStateChangedEvent e)
    {
        // A "Completed" transition can mean a file was actually just transferred, or it can mean
        // DownloadManager.ProcessTrackAsync found the file already sitting on disk / in the
        // library and silently relinked it (common when re-syncing a playlist: a track marked
        // Failed/OnHold gets requeued, but the song was already downloaded under a sibling
        // PlaylistTrack row). Both used to look identical here and fired the same "download
        // complete" toast — surfacing a success notification for something that was never
        // actually downloaded this session.
        if (e.WasAlreadyPresent)
        {
            return;
        }

        var now = DateTime.UtcNow;

        if (_recentlyCompletedHashes.TryGetValue(e.TrackGlobalId, out var lastAt) && now - lastAt < DuplicateCompletionWindow)
        {
            return;
        }
        _recentlyCompletedHashes[e.TrackGlobalId] = now;

        if (_recentlyCompletedHashes.Count > 256)
        {
            var stale = _recentlyCompletedHashes.Where(kv => now - kv.Value >= DuplicateCompletionWindow).Select(kv => kv.Key).ToList();
            foreach (var key in stale) _recentlyCompletedHashes.Remove(key);
        }

        // Resolve Artist/Title from the in-memory download list rather than the database —
        // avoids a race against DownloadHistoryEntity's own (independently-timed) write.
        var match = _downloadManager.GetAllDownloads()
            .FirstOrDefault(d => string.Equals(d.Model.TrackUniqueHash, e.TrackGlobalId, StringComparison.OrdinalIgnoreCase));

        var title = match.Model is not null && !string.IsNullOrWhiteSpace(match.Model.Artist)
            ? $"{match.Model.Artist} — {match.Model.Title}"
            : "Track downloaded";

        var detail = !string.IsNullOrWhiteSpace(e.PeerName) ? $"from {e.PeerName}" : null;

        Add(new NotificationItem
        {
            Kind = global::SLSKDONET.Models.NotificationKind.DownloadCompleted,
            Title = title,
            Detail = detail,
            NavigationUsername = e.PeerName,
        });

        _notificationService.Show(title, detail ?? "Download completed", NotificationType.Success);
    }

    private void OnPrivateMessageReceived(PrivateMessageReceivedEvent e)
    {
        if (string.Equals(_activeConversationUsername, e.PeerUsername, StringComparison.OrdinalIgnoreCase))
            return;

        Add(new NotificationItem
        {
            Kind = global::SLSKDONET.Models.NotificationKind.PrivateMessage,
            Title = $"Message from {e.PeerUsername}",
            Detail = e.Message,
            NavigationUsername = e.PeerUsername,
        });

        _notificationService.Show($"Message from {e.PeerUsername}", e.Message, NotificationType.Information);
        _windowsToast.ShowIfUnfocused($"Message from {e.PeerUsername}", e.Message, navigateUsername: e.PeerUsername);
    }

    private void OnRoomMessageReceived(RoomMessageReceivedEvent e)
    {
        if (string.Equals(_activeRoomName, e.RoomName, StringComparison.OrdinalIgnoreCase))
            return;

        Add(new NotificationItem
        {
            Kind = global::SLSKDONET.Models.NotificationKind.RoomMessage,
            Title = $"{e.Username} in #{e.RoomName}",
            Detail = e.Message,
            NavigationRoomName = e.RoomName,
        });

        _notificationService.Show($"{e.Username} in #{e.RoomName}", e.Message, NotificationType.Information);
        _windowsToast.ShowIfUnfocused($"{e.Username} in #{e.RoomName}", e.Message, navigateRoomName: e.RoomName);
    }

    private void Add(NotificationItem item)
    {
        Notifications.Insert(0, item);
        while (Notifications.Count > MaxNotifications)
            Notifications.RemoveAt(Notifications.Count - 1);

        UnreadCount++;
    }

    public void MarkAllRead()
    {
        foreach (var item in Notifications)
            item.IsRead = true;

        UnreadCount = 0;
    }

    public void MarkRead(NotificationItem item)
    {
        if (item.IsRead)
            return;

        item.IsRead = true;
        UnreadCount = Math.Max(0, UnreadCount - 1);
    }

    public void Clear()
    {
        Notifications.Clear();
        UnreadCount = 0;
    }

    public void Dispose() => _disposables.Dispose();
}
