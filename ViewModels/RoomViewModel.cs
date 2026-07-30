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

    private string? _imageSendError;
    public string? ImageSendError
    {
        get => _imageSendError;
        private set => this.RaiseAndSetIfChanged(ref _imageSendError, value);
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

        eventBus.GetEvent<RoomMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => string.Equals(e.RoomName, RoomName, StringComparison.OrdinalIgnoreCase))
            .Subscribe(e => Messages.Add(new ChatMessageViewModel(e.Username, e.Message, e.TimestampUtc, e.IsOutgoing, _chatAttachments, e.Username)))
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

        try
        {
            var history = await _roomChat.GetRoomHistoryAsync(RoomName).ConfigureAwait(true);
            Messages.Clear();
            foreach (var message in history)
                Messages.Add(new ChatMessageViewModel(message.Username, message.Message, message.TimestampUtc, message.IsOutgoing, _chatAttachments, message.Username));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load room history for {RoomName}", RoomName);
        }
    }

    private async Task SendMessageAsync()
    {
        var text = MessageInput.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return;

        MessageInput = string.Empty;
        try
        {
            await _roomChat.SendMessageAsync(RoomName, text).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to send message to room {RoomName}", RoomName);
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
