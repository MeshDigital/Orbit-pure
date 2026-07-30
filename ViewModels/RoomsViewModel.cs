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

/// <summary>Chat-rooms panel: available room list, join/leave, and the set of currently-joined rooms.</summary>
public class RoomsViewModel : ReactiveObject, IDisposable
{
    private readonly RoomChatService _roomChat;
    private readonly ChatAttachmentService _chatAttachments;
    private readonly IFileInteractionService _fileInteraction;
    private readonly IEventBus _eventBus;
    private readonly ILogger<RoomsViewModel> _logger;
    private readonly List<RoomSummary> _allAvailableRooms = new();
    private readonly CompositeDisposable _disposables = new();

    public ObservableCollection<RoomSummary> AvailableRooms { get; } = new();
    public ObservableCollection<RoomViewModel> JoinedRooms { get; } = new();

    /// <summary>Filters <see cref="AvailableRooms"/> (the server's full room list can run into the hundreds).</summary>
    private string _roomFilterText = string.Empty;
    public string RoomFilterText
    {
        get => _roomFilterText;
        set
        {
            this.RaiseAndSetIfChanged(ref _roomFilterText, value);
            ApplyRoomFilter();
        }
    }

    private RoomViewModel? _selectedRoom;
    public RoomViewModel? SelectedRoom
    {
        get => _selectedRoom;
        set => this.RaiseAndSetIfChanged(ref _selectedRoom, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private bool _isJoining;
    public bool IsJoining
    {
        get => _isJoining;
        private set => this.RaiseAndSetIfChanged(ref _isJoining, value);
    }

    /// <summary>Non-null after a failed join/create attempt — surfaced in the UI instead of only being logged.</summary>
    private string? _joinStatusMessage;
    public string? JoinStatusMessage
    {
        get => _joinStatusMessage;
        private set
        {
            this.RaiseAndSetIfChanged(ref _joinStatusMessage, value);
            this.RaisePropertyChanged(nameof(HasJoinStatusMessage));
        }
    }

    public bool HasJoinStatusMessage => !string.IsNullOrWhiteSpace(JoinStatusMessage);

    /// <summary>
    /// Free-text room name for joining or creating a room not in <see cref="AvailableRooms"/> —
    /// per the Soulseek protocol, joining a room name that doesn't exist yet creates it, so this
    /// single field/command covers both "join an existing room by name" and "start a new one".
    /// </summary>
    private string _newRoomName = string.Empty;
    public string NewRoomName
    {
        get => _newRoomName;
        set => this.RaiseAndSetIfChanged(ref _newRoomName, value);
    }

    private bool _createNewRoomAsPrivate;
    public bool CreateNewRoomAsPrivate
    {
        get => _createNewRoomAsPrivate;
        set => this.RaiseAndSetIfChanged(ref _createNewRoomAsPrivate, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshRoomListCommand { get; }
    public ReactiveCommand<string, Unit> JoinRoomCommand { get; }
    public ReactiveCommand<RoomViewModel, Unit> LeaveRoomCommand { get; }
    public ReactiveCommand<Unit, Unit> CreateOrJoinRoomCommand { get; }

    public RoomsViewModel(RoomChatService roomChat, ChatAttachmentService chatAttachments, IFileInteractionService fileInteraction, IEventBus eventBus, ILogger<RoomsViewModel> logger)
    {
        _roomChat = roomChat;
        _chatAttachments = chatAttachments;
        _fileInteraction = fileInteraction;
        _eventBus = eventBus;
        _logger = logger;

        RefreshRoomListCommand = ReactiveCommand.CreateFromTask(RefreshRoomListAsync);
        JoinRoomCommand = ReactiveCommand.CreateFromTask<string>(JoinRoomAsync);
        LeaveRoomCommand = ReactiveCommand.CreateFromTask<RoomViewModel>(LeaveRoomAsync);

        var canCreateOrJoin = this.WhenAnyValue(x => x.NewRoomName, text => !string.IsNullOrWhiteSpace(text));
        CreateOrJoinRoomCommand = ReactiveCommand.CreateFromTask(CreateOrJoinRoomAsync, canCreateOrJoin);

        _eventBus.GetEvent<RoomMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => !e.IsOutgoing)
            .Subscribe(OnIncomingRoomMessage)
            .DisposeWith(_disposables);

        _ = RefreshRoomListAsync();
    }

    /// <summary>Marks a joined room unread on an incoming message, unless it's the room currently being viewed.</summary>
    private void OnIncomingRoomMessage(RoomMessageReceivedEvent e)
    {
        var isActive = SelectedRoom != null && string.Equals(SelectedRoom.RoomName, e.RoomName, StringComparison.OrdinalIgnoreCase);
        if (isActive)
            return;

        var room = JoinedRooms.FirstOrDefault(r => string.Equals(r.RoomName, e.RoomName, StringComparison.OrdinalIgnoreCase));
        if (room != null)
            room.HasUnread = true;
    }

    private async Task CreateOrJoinRoomAsync()
    {
        var roomName = NewRoomName.Trim();
        if (string.IsNullOrWhiteSpace(roomName))
            return;

        NewRoomName = string.Empty;
        await JoinOrCreateRoomCoreAsync(roomName, CreateNewRoomAsPrivate).ConfigureAwait(true);
    }

    public async Task RefreshRoomListAsync()
    {
        IsLoading = true;
        try
        {
            var rooms = await _roomChat.GetRoomListAsync().ConfigureAwait(true);
            _allAvailableRooms.Clear();
            _allAvailableRooms.AddRange(rooms.OrderByDescending(r => r.UserCount));
            ApplyRoomFilter();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to refresh Soulseek room list");
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyRoomFilter()
    {
        AvailableRooms.Clear();
        var query = string.IsNullOrWhiteSpace(_roomFilterText)
            ? _allAvailableRooms
            : _allAvailableRooms.Where(r => r.Name.Contains(_roomFilterText, StringComparison.OrdinalIgnoreCase));

        foreach (var room in query)
            AvailableRooms.Add(room);
    }

    private Task JoinRoomAsync(string roomName) => JoinOrCreateRoomCoreAsync(roomName, isPrivate: false);

    /// <summary>Selects (joining if necessary) a room by name — used to jump straight to a room from a notification click.</summary>
    public Task OpenRoomAsync(string roomName) => JoinOrCreateRoomCoreAsync(roomName, isPrivate: false);

    /// <summary>
    /// Joins a room by name — or, per the Soulseek protocol, creates it if no room by that name
    /// exists yet. Shared by clicking an existing <see cref="AvailableRooms"/>/<see cref="JoinedRooms"/>
    /// entry and by <see cref="CreateOrJoinRoomAsync"/>'s free-text entry.
    /// </summary>
    private async Task JoinOrCreateRoomCoreAsync(string roomName, bool isPrivate)
    {
        if (string.IsNullOrWhiteSpace(roomName))
            return;

        if (JoinedRooms.Any(r => string.Equals(r.RoomName, roomName, StringComparison.OrdinalIgnoreCase)))
        {
            SelectedRoom = JoinedRooms.First(r => string.Equals(r.RoomName, roomName, StringComparison.OrdinalIgnoreCase));
            SelectedRoom.HasUnread = false;
            return;
        }

        JoinStatusMessage = null;
        IsJoining = true;
        try
        {
            var snapshot = await _roomChat.JoinRoomAsync(roomName, isPrivate).ConfigureAwait(true);
            var room = new RoomViewModel(snapshot.Name, snapshot.IsPrivate, _roomChat, _chatAttachments, _fileInteraction, _eventBus, _logger);
            await room.LoadHistoryAsync(snapshot.Members).ConfigureAwait(true);
            JoinedRooms.Add(room);
            SelectedRoom = room;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to join/create room {RoomName}", roomName);
            // ex.Message is the clean, user-presentable text SoulseekAdapter.JoinRoomAsync
            // already composed for forbidden/timeout cases; fall back to a generic message
            // for anything else rather than leaking a raw exception type name.
            JoinStatusMessage = ex is InvalidOperationException
                ? ex.Message
                : $"Couldn't join \"{roomName}\" — {ex.Message}";
        }
        finally
        {
            IsJoining = false;
        }
    }

    private async Task LeaveRoomAsync(RoomViewModel room)
    {
        try
        {
            await _roomChat.LeaveRoomAsync(room.RoomName).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to leave room {RoomName}", room.RoomName);
        }
        finally
        {
            JoinedRooms.Remove(room);
            room.Dispose();
            if (ReferenceEquals(SelectedRoom, room))
                SelectedRoom = JoinedRooms.FirstOrDefault();
        }
    }

    public void Dispose()
    {
        _disposables.Dispose();

        foreach (var room in JoinedRooms)
            room.Dispose();

        JoinedRooms.Clear();
    }
}
