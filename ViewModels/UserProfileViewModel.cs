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
    private readonly IEventBus _eventBus;
    private readonly ILogger<UserProfileViewModel> _logger;
    private readonly CompositeDisposable _disposables = new();
    private IDisposable? _presenceWatchHandle;

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

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SendImageCommand { get; }

    private string? _imageSendError;
    public string? ImageSendError
    {
        get => _imageSendError;
        private set => this.RaiseAndSetIfChanged(ref _imageSendError, value);
    }

    public UserProfileViewModel(
        ISoulseekAdapter adapter,
        DatabaseService databaseService,
        UserPresenceWatchService presenceWatch,
        ChatService chatService,
        ChatAttachmentService chatAttachments,
        IFileInteractionService fileInteraction,
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
        _eventBus = eventBus;
        _logger = logger;
        Browser = browser;

        var canSend = this.WhenAnyValue(x => x.MessageInput, text => !string.IsNullOrWhiteSpace(text));
        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
        SendImageCommand = ReactiveCommand.CreateFromTask(SendImageAsync);

        _eventBus.GetEvent<PrivateMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => string.Equals(e.PeerUsername, Username, StringComparison.OrdinalIgnoreCase))
            .Subscribe(e => Messages.Add(new ChatMessageViewModel(e.IsOutgoing ? "You" : e.PeerUsername, e.Message, e.TimestampUtc, e.IsOutgoing, _chatAttachments, Username)))
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
        try
        {
            ReliabilitySnapshot = _peerReliabilityRef.GetSnapshot(username);

            _presenceWatchHandle?.Dispose();
            var (handle, snapshot) = await _presenceWatch.WatchAsync(username).ConfigureAwait(true);
            _presenceWatchHandle = handle;
            WatchSnapshot = snapshot;

            var browseTask = Browser.LoadUserAsync(username);
            var historyTask = _databaseService.GetDownloadHistoryForUserAsync(username);
            var conversationTask = _chatService.GetConversationAsync(username);
            var statusTask = SafeGetStatusAsync(username);
            var infoTask = SafeGetInfoAsync(username);

            await Task.WhenAll(browseTask, historyTask, conversationTask, statusTask, infoTask).ConfigureAwait(true);

            DownloadHistory.Clear();
            foreach (var entry in await historyTask)
                DownloadHistory.Add(entry);

            Messages.Clear();
            foreach (var message in await conversationTask)
                Messages.Add(new ChatMessageViewModel(message.IsOutgoing ? "You" : username, message.Message, message.TimestampUtc, message.IsOutgoing, _chatAttachments, username));

            Presence = (await statusTask).Presence;
            Info = await infoTask;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load profile for {Username}", username);
        }
        finally
        {
            IsLoading = false;
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

        MessageInput = string.Empty;
        try
        {
            await _chatService.SendMessageAsync(Username, text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to {Username}", Username);
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
        _presenceWatchHandle?.Dispose();
        _disposables.Dispose();
    }
}
