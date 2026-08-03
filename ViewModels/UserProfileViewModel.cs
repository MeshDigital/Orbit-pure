using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Data.Entities;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Per-user profile: Overview (presence + stats + Soulseek user info), Browse Shares (reuses a
/// fresh transient <see cref="UserCollectionViewModel"/>), Download History, and Chat. Opened
/// per-profile from <see cref="UsersViewModel"/>; disposed when the profile is closed, which
/// releases the presence watch handle.
/// </summary>
public class UserProfileViewModel : ReactiveObject, IDisposable
{
    private readonly ISoulseekAdapter _adapter;
    private readonly DatabaseService _databaseService;
    private readonly UserPresenceWatchService _presenceWatch;
    private readonly ChatService _chatService;
    private readonly ChatAttachmentService _chatAttachments;
    private readonly IFileInteractionService _fileInteraction;
    private readonly IDialogService _dialogService;
    private readonly IEventBus _eventBus;
    private readonly ILogger<UserProfileViewModel> _logger;
    private readonly CompositeDisposable _disposables = new();
    private IDisposable? _presenceWatchHandle;
    private bool _disposed;
    private bool _browserLoadStarted;

    private const int MessagePageSize = 50;

    // Bounds how large Messages can grow from live incoming/outgoing traffic during a single
    // long-lived session (a profile left open for days can otherwise accumulate thousands of
    // rows, degrading ChatGroupingHelper's O(n) full-rescan on every new message). Explicit
    // "Load earlier" pagination is exempt — that growth is user-initiated and bounded by clicks.
    private const int MaxLiveMessages = 500;

    public UserCollectionViewModel Browser { get; }
    public ObservableCollection<DownloadHistoryEntity> DownloadHistory { get; } = new();
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    private string _username = string.Empty;
    public string Username
    {
        get => _username;
        private set => this.RaiseAndSetIfChanged(ref _username, value);
    }

    private UserPresenceState _presence = UserPresenceState.Unknown;
    public UserPresenceState Presence
    {
        get => _presence;
        private set => this.RaiseAndSetIfChanged(ref _presence, value);
    }

    private UserProfileSnapshot? _info;
    public UserProfileSnapshot? Info
    {
        get => _info;
        private set => this.RaiseAndSetIfChanged(ref _info, value);
    }

    private PeerUserSnapshot? _reliabilitySnapshot;
    public PeerUserSnapshot? ReliabilitySnapshot
    {
        get => _reliabilitySnapshot;
        private set => this.RaiseAndSetIfChanged(ref _reliabilitySnapshot, value);
    }

    /// <summary>
    /// Speed/share-count/country data bundled with the server's watch acknowledgement — cheaper
    /// than a full browse and populated as soon as the profile opens, before any tab is clicked.
    /// </summary>
    private UserWatchSnapshot? _watchSnapshot;
    public UserWatchSnapshot? WatchSnapshot
    {
        get => _watchSnapshot;
        private set => this.RaiseAndSetIfChanged(ref _watchSnapshot, value);
    }

    private string _messageInput = string.Empty;
    public string MessageInput
    {
        get => _messageInput;
        set => this.RaiseAndSetIfChanged(ref _messageInput, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private bool _isInfoPanelOpen;
    /// <summary>Whether the secondary Overview/Browse Shares/Download History drawer is open — chat is the primary surface, this is opt-in.</summary>
    public bool IsInfoPanelOpen
    {
        get => _isInfoPanelOpen;
        set => this.RaiseAndSetIfChanged(ref _isInfoPanelOpen, value);
    }

    private bool _isLoadingOlderMessages;
    public bool IsLoadingOlderMessages
    {
        get => _isLoadingOlderMessages;
        private set => this.RaiseAndSetIfChanged(ref _isLoadingOlderMessages, value);
    }

    private bool _hasMoreHistory = true;
    public bool HasMoreHistory
    {
        get => _hasMoreHistory;
        private set => this.RaiseAndSetIfChanged(ref _hasMoreHistory, value);
    }

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SendImageCommand { get; }
    public ReactiveCommand<Unit, Unit> ToggleInfoPanelCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadOlderMessagesCommand { get; }
    public ReactiveCommand<ChatMessageViewModel, Unit> DeleteMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> ClearConversationCommand { get; }

    private string? _imageSendError;
    public string? ImageSendError
    {
        get => _imageSendError;
        private set => this.RaiseAndSetIfChanged(ref _imageSendError, value);
    }

    private string? _messageSendError;
    public string? MessageSendError
    {
        get => _messageSendError;
        private set => this.RaiseAndSetIfChanged(ref _messageSendError, value);
    }

    public UserProfileViewModel(
        ISoulseekAdapter adapter,
        DatabaseService databaseService,
        UserPresenceWatchService presenceWatch,
        ChatService chatService,
        ChatAttachmentService chatAttachments,
        IFileInteractionService fileInteraction,
        IDialogService dialogService,
        PeerReliabilityService peerReliability,
        IEventBus eventBus,
        UserCollectionViewModel browser,
        ILogger<UserProfileViewModel> logger)
    {
        _adapter = adapter;
        _databaseService = databaseService;
        _presenceWatch = presenceWatch;
        _chatService = chatService;
        _chatAttachments = chatAttachments;
        _fileInteraction = fileInteraction;
        _dialogService = dialogService;
        _eventBus = eventBus;
        _logger = logger;
        Browser = browser;

        var canSend = this.WhenAnyValue(x => x.MessageInput, text => !string.IsNullOrWhiteSpace(text));
        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
        SendImageCommand = ReactiveCommand.CreateFromTask(SendImageAsync);
        ToggleInfoPanelCommand = ReactiveCommand.Create(() =>
        {
            IsInfoPanelOpen = !IsInfoPanelOpen;
            if (IsInfoPanelOpen)
                _ = EnsureBrowserLoadedAsync();
        });

        var canLoadOlder = this.WhenAnyValue(x => x.IsLoadingOlderMessages, x => x.HasMoreHistory, (loading, hasMore) => !loading && hasMore);
        LoadOlderMessagesCommand = ReactiveCommand.CreateFromTask(LoadOlderMessagesAsync, canLoadOlder);

        DeleteMessageCommand = ReactiveCommand.CreateFromTask<ChatMessageViewModel>(DeleteMessageAsync);
        ClearConversationCommand = ReactiveCommand.CreateFromTask(ClearConversationAsync);

        _eventBus.GetEvent<PrivateMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => string.Equals(e.PeerUsername, Username, StringComparison.OrdinalIgnoreCase))
            .Subscribe(e =>
            {
                Messages.Add(new ChatMessageViewModel(e.Id, e.IsOutgoing ? "You" : e.PeerUsername, e.Message, e.TimestampUtc, e.IsOutgoing, _chatAttachments, Username));
                ChatGroupingHelper.Apply(Messages);
                TrimLiveMessagesIfNeeded();
            })
            .DisposeWith(_disposables);

        _eventBus.GetEvent<UserPresenceChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => string.Equals(e.Username, Username, StringComparison.OrdinalIgnoreCase))
            .Subscribe(e => Presence = e.Presence)
            .DisposeWith(_disposables);

        _peerReliabilityRef = peerReliability;
    }

    private readonly PeerReliabilityService _peerReliabilityRef;

    public async Task LoadUserAsync(string username)
    {
        Username = username;
        IsLoading = true;
        HasMoreHistory = false; // avoid a flash of "Load earlier" before the first page has actually loaded
        try
        {
            ReliabilitySnapshot = _peerReliabilityRef.GetSnapshot(username);

            // Chat is the primary surface, and everything it needs (message history, download
            // history) is local DB data — load and render it first, without waiting on anything
            // that has to round-trip to the peer over the network. Browse Shares is deliberately
            // not loaded here at all (see EnsureBrowserLoadedAsync); presence/status/info are
            // network calls that can be slow or hang against an unresponsive peer, so they're
            // fetched afterward in the background instead of blocking chat on them (see
            // LoadPresenceAndInfoAsync below) — the header just updates in place whenever they land.
            var historyTask = _databaseService.GetDownloadHistoryForUserAsync(username);
            var conversationTask = _chatService.GetConversationAsync(username, MessagePageSize);
            await Task.WhenAll(historyTask, conversationTask).ConfigureAwait(true);

            DownloadHistory.Clear();
            foreach (var entry in await historyTask)
                DownloadHistory.Add(entry);

            var conversation = await conversationTask;
            Messages.Clear();
            foreach (var message in conversation)
                Messages.Add(new ChatMessageViewModel(message.Id, message.IsOutgoing ? "You" : username, message.Message, message.TimestampUtc, message.IsOutgoing, _chatAttachments, username));
            ChatGroupingHelper.Apply(Messages);
            HasMoreHistory = conversation.Count >= MessagePageSize;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load profile for {Username}", username);
        }
        finally
        {
            IsLoading = false;
        }

        _ = LoadPresenceAndInfoAsync(username);
    }

    private async Task LoadPresenceAndInfoAsync(string username)
    {
        try
        {
            _presenceWatchHandle?.Dispose();
            _presenceWatchHandle = null;
            var (handle, snapshot) = await _presenceWatch.WatchAsync(username).ConfigureAwait(true);
            if (_disposed)
            {
                // The profile was closed/replaced while the watch request was in flight — this
                // instance's handle would otherwise never be released, permanently leaking the
                // ref-count for this username (see UserPresenceWatchService.WatchAsync).
                handle.Dispose();
                return;
            }
            _presenceWatchHandle = handle;
            WatchSnapshot = snapshot;

            Presence = (await SafeGetStatusAsync(username).ConfigureAwait(true)).Presence;
            Info = await SafeGetInfoAsync(username).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to load presence/status for {Username}", username);
        }
    }

    /// <summary>Loads Browse Shares on first demand (opening the Info drawer) rather than eagerly with the rest of the profile — see the comment in <see cref="LoadUserAsync"/>.</summary>
    private async Task EnsureBrowserLoadedAsync()
    {
        if (_browserLoadStarted || string.IsNullOrWhiteSpace(Username))
            return;

        _browserLoadStarted = true;
        await Browser.LoadUserAsync(Username).ConfigureAwait(true);
    }

    /// <summary>Drops the oldest messages once live traffic pushes the collection past <see cref="MaxLiveMessages"/>. Re-opens "Load earlier" since the trimmed rows are still in the DB.</summary>
    private void TrimLiveMessagesIfNeeded()
    {
        if (Messages.Count <= MaxLiveMessages) return;

        var excess = Messages.Count - MaxLiveMessages;
        for (var i = 0; i < excess; i++)
            Messages.RemoveAt(0);

        HasMoreHistory = true;
        ChatGroupingHelper.Apply(Messages);
    }

    private async Task LoadOlderMessagesAsync()
    {
        if (Messages.Count == 0 || string.IsNullOrWhiteSpace(Username))
            return;

        IsLoadingOlderMessages = true;
        try
        {
            var oldestLoaded = Messages[0].TimestampUtc;
            var older = await _chatService.GetConversationAsync(Username, MessagePageSize, oldestLoaded).ConfigureAwait(true);
            HasMoreHistory = older.Count >= MessagePageSize;

            for (var i = older.Count - 1; i >= 0; i--)
            {
                var message = older[i];
                Messages.Insert(0, new ChatMessageViewModel(message.Id, message.IsOutgoing ? "You" : Username, message.Message, message.TimestampUtc, message.IsOutgoing, _chatAttachments, Username));
            }
            ChatGroupingHelper.Apply(Messages);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load older messages for {Username}", Username);
        }
        finally
        {
            IsLoadingOlderMessages = false;
        }
    }

    /// <summary>Removes a single message from local history only — the Soulseek protocol has no message recall, so the peer's own copy is unaffected.</summary>
    private async Task DeleteMessageAsync(ChatMessageViewModel message)
    {
        try
        {
            await _chatService.DeleteMessageAsync(message.Id).ConfigureAwait(true);
            Messages.Remove(message);
            ChatGroupingHelper.Apply(Messages); // group boundaries/date separators may shift once the removed message's neighbors are adjacent
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete message {Id}", message.Id);
        }
    }

    /// <summary>Wipes the entire local history with this peer — e.g. for a spam/gate-bot conversation you just want gone. Confirmed first since it can't be undone.</summary>
    private async Task ClearConversationAsync()
    {
        if (string.IsNullOrWhiteSpace(Username))
            return;

        var confirmed = await _dialogService.ConfirmAsync(
            "Clear Conversation",
            $"Delete your entire message history with {Username}? This only removes your own local copy — it can't be undone.",
            confirmLabel: "Delete",
            cancelLabel: "Cancel").ConfigureAwait(true);
        if (!confirmed)
            return;

        try
        {
            await _chatService.DeleteConversationAsync(Username).ConfigureAwait(true);
            Messages.Clear();
            _eventBus.Publish(new ConversationClearedEvent(Username));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to clear conversation with {Username}", Username);
        }
    }

    private async Task<UserStatusSnapshot> SafeGetStatusAsync(string username)
    {
        try
        {
            return await _adapter.GetUserStatusAsync(username).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get status for {Username}", username);
            return new UserStatusSnapshot(username, UserPresenceState.Unknown, false);
        }
    }

    private async Task<UserProfileSnapshot> SafeGetInfoAsync(string username)
    {
        try
        {
            return await _adapter.GetUserInfoAsync(username).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to get user info for {Username}", username);
            return new UserProfileSnapshot(username, null, false, null, false, 0, 0);
        }
    }

    private async Task SendMessageAsync()
    {
        var text = MessageInput.Trim();
        if (string.IsNullOrWhiteSpace(text) || string.IsNullOrWhiteSpace(Username))
            return;

        MessageSendError = null;
        MessageInput = string.Empty;
        try
        {
            await _chatService.SendMessageAsync(Username, text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to {Username}", Username);
            MessageInput = text; // restore the typed text so it isn't silently lost
            MessageSendError = $"Couldn't send: {ex.Message}";
        }
    }

    private async Task SendImageAsync()
    {
        if (string.IsNullOrWhiteSpace(Username))
            return;

        ImageSendError = null;

        var filters = new[] { new FileDialogFilter("Images", new List<string> { "png", "jpg", "jpeg", "gif", "bmp", "webp" }) };
        var picked = await _fileInteraction.OpenFileDialogAsync("Send an image", filters).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(picked))
            return;

        try
        {
            var offer = _chatAttachments.PrepareOutgoingImage(picked, Username);
            await _chatService.SendMessageAsync(Username, offer).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send image to {Username}", Username);
            ImageSendError = ex.Message;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _presenceWatchHandle?.Dispose();
        _disposables.Dispose();
    }
}
