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
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>A single joined chat room: live roster + message thread (shares the same thread UI as 1:1 chat).</summary>
public class RoomViewModel : ReactiveObject, IDisposable
{
    private readonly RoomChatService _roomChat;
    private readonly ChatAttachmentService _chatAttachments;
    private readonly IFileInteractionService _fileInteraction;
    private readonly ILogger _logger;
    private readonly CompositeDisposable _disposables = new();

    private const int MessagePageSize = 50;

    // Bounds how large Messages can grow from live room traffic during a single long-lived
    // session — rooms are typically far chattier than 1:1 chat, so this matters even more here.
    // Explicit "Load earlier" pagination is exempt — that growth is user-initiated and bounded by clicks.
    private const int MaxLiveMessages = 500;

    public string RoomName { get; }
    public bool IsPrivate { get; }
    public ObservableCollection<RoomMemberSnapshot> Members { get; } = new();
    public ObservableCollection<ChatMessageViewModel> Messages { get; } = new();

    /// <summary>Set by <see cref="RoomsViewModel"/> when a message arrives while this room isn't selected; cleared on selection.</summary>
    private bool _hasUnread;
    public bool HasUnread
    {
        get => _hasUnread;
        set => this.RaiseAndSetIfChanged(ref _hasUnread, value);
    }

    private string _messageInput = string.Empty;
    public string MessageInput
    {
        get => _messageInput;
        set => this.RaiseAndSetIfChanged(ref _messageInput, value);
    }

    public ReactiveCommand<Unit, Unit> SendMessageCommand { get; }
    public ReactiveCommand<Unit, Unit> SendImageCommand { get; }
    public ReactiveCommand<Unit, Unit> LoadOlderMessagesCommand { get; }
    public ReactiveCommand<ChatMessageViewModel, Unit> DeleteMessageCommand { get; }

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

    public RoomViewModel(string roomName, bool isPrivate, RoomChatService roomChat, ChatAttachmentService chatAttachments, IFileInteractionService fileInteraction, IEventBus eventBus, ILogger logger)
    {
        RoomName = roomName;
        IsPrivate = isPrivate;
        _roomChat = roomChat;
        _chatAttachments = chatAttachments;
        _fileInteraction = fileInteraction;
        _logger = logger;

        var canSend = this.WhenAnyValue(x => x.MessageInput, text => !string.IsNullOrWhiteSpace(text));
        SendMessageCommand = ReactiveCommand.CreateFromTask(SendMessageAsync, canSend);
        SendImageCommand = ReactiveCommand.CreateFromTask(SendImageAsync);

        var canLoadOlder = this.WhenAnyValue(x => x.IsLoadingOlderMessages, x => x.HasMoreHistory, (loading, hasMore) => !loading && hasMore);
        LoadOlderMessagesCommand = ReactiveCommand.CreateFromTask(LoadOlderMessagesAsync, canLoadOlder);

        DeleteMessageCommand = ReactiveCommand.CreateFromTask<ChatMessageViewModel>(DeleteMessageAsync);

        eventBus.GetEvent<RoomMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => string.Equals(e.RoomName, RoomName, StringComparison.OrdinalIgnoreCase))
            .Subscribe(e =>
            {
                Messages.Add(new ChatMessageViewModel(e.Id, e.Username, e.Message, e.TimestampUtc, e.IsOutgoing, _chatAttachments, e.Username));
                ChatGroupingHelper.Apply(Messages);
                TrimLiveMessagesIfNeeded();
            })
            .DisposeWith(_disposables);

        eventBus.GetEvent<RoomMembershipChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => string.Equals(e.RoomName, RoomName, StringComparison.OrdinalIgnoreCase))
            .Subscribe(e =>
            {
                var existing = Members.FirstOrDefault(m => string.Equals(m.Username, e.Username, StringComparison.OrdinalIgnoreCase));
                if (e.Joined && existing.Username is null)
                    Members.Add(new RoomMemberSnapshot(e.Username, UserPresenceState.Online, 0, 0, 0, null));
                else if (!e.Joined && existing.Username is not null)
                    Members.Remove(existing);
            })
            .DisposeWith(_disposables);
    }

    public async Task LoadHistoryAsync(IReadOnlyList<RoomMemberSnapshot>? initialMembers = null)
    {
        if (initialMembers != null)
        {
            Members.Clear();
            foreach (var member in initialMembers)
                Members.Add(member);
        }

        HasMoreHistory = false; // avoid a flash of "Load earlier" before the first page has actually loaded
        try
        {
            var history = await _roomChat.GetRoomHistoryAsync(RoomName, MessagePageSize).ConfigureAwait(true);
            Messages.Clear();
            foreach (var message in history)
                Messages.Add(new ChatMessageViewModel(message.Id, message.Username, message.Message, message.TimestampUtc, message.IsOutgoing, _chatAttachments, message.Username));
            ChatGroupingHelper.Apply(Messages);
            HasMoreHistory = history.Count >= MessagePageSize;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load room history for {RoomName}", RoomName);
        }
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
        if (Messages.Count == 0)
            return;

        IsLoadingOlderMessages = true;
        try
        {
            var oldestLoaded = Messages[0].TimestampUtc;
            var older = await _roomChat.GetRoomHistoryAsync(RoomName, MessagePageSize, oldestLoaded).ConfigureAwait(true);
            HasMoreHistory = older.Count >= MessagePageSize;

            for (var i = older.Count - 1; i >= 0; i--)
            {
                var message = older[i];
                Messages.Insert(0, new ChatMessageViewModel(message.Id, message.Username, message.Message, message.TimestampUtc, message.IsOutgoing, _chatAttachments, message.Username));
            }
            ChatGroupingHelper.Apply(Messages);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load older messages for room {RoomName}", RoomName);
        }
        finally
        {
            IsLoadingOlderMessages = false;
        }
    }

    /// <summary>Removes a single message from local history only — the Soulseek protocol has no message recall, so it's still visible to other room members.</summary>
    private async Task DeleteMessageAsync(ChatMessageViewModel message)
    {
        try
        {
            await _roomChat.DeleteMessageAsync(message.Id).ConfigureAwait(true);
            Messages.Remove(message);
            ChatGroupingHelper.Apply(Messages);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to delete message {Id} in room {RoomName}", message.Id, RoomName);
        }
    }

    private async Task SendMessageAsync()
    {
        var text = MessageInput.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        MessageSendError = null;
        MessageInput = string.Empty;
        try
        {
            await _roomChat.SendMessageAsync(RoomName, text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to room {RoomName}", RoomName);
            MessageInput = text; // restore the typed text so it isn't silently lost
            MessageSendError = $"Couldn't send: {ex.Message}";
        }
    }

    private async Task SendImageAsync()
    {
        ImageSendError = null;

        var filters = new[] { new FileDialogFilter("Images", new List<string> { "png", "jpg", "jpeg", "gif", "bmp", "webp" }) };
        var picked = await _fileInteraction.OpenFileDialogAsync("Send an image", filters).ConfigureAwait(true);
        if (string.IsNullOrWhiteSpace(picked))
            return;

        try
        {
            var offer = _chatAttachments.PrepareOutgoingImage(picked, ChatAttachmentService.AnyoneRecipient);
            await _roomChat.SendMessageAsync(RoomName, offer).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send image to room {RoomName}", RoomName);
            ImageSendError = ex.Message;
        }
    }

    public void Dispose() => _disposables.Dispose();
}
