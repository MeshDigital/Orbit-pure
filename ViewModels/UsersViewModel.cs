using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Reactive;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Services;

namespace SLSKDONET.ViewModels;

/// <summary>
/// Row list for the Users/Contacts page: every Soulseek peer ever tracked, sourced from
/// <see cref="PeerReliabilityService"/> (always-on) merged with <see cref="DatabaseService"/>'s
/// per-track download history, optionally decorated with FrequentSources friend/pin badges
/// when that (opt-in) feature is enabled.
/// </summary>
public class UsersViewModel : ReactiveObject, IDisposable
{
    private readonly PeerReliabilityService _peerReliability;
    private readonly DatabaseService _databaseService;
    private readonly FrequentSourceService? _frequentSourceService;
    private readonly AppConfig _config;
    private readonly IServiceProvider _serviceProvider;
    private readonly ISoulseekAdapter _adapter;
    private readonly ChatService _chatService;
    private readonly UserPresenceWatchService _presenceWatch;
    private readonly IEventBus _eventBus;
    private readonly ILogger<UsersViewModel> _logger;
    private readonly CompositeDisposable _disposables = new();

    // Presence dots: bounds how many contacts get a live server watch to what's actually visible
    // (post-filter), not the full "everyone you've ever downloaded from" list, which could be
    // large. Conversations is always watched in full — that list is inherently small.
    private const int MaxPresenceWatchedContacts = 150;
    private readonly Dictionary<string, IDisposable> _presenceHandles = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    /// <summary>
    /// Recent 1:1 conversations (peers you've actually exchanged messages with), most recently
    /// active first — distinct from <see cref="Rows"/>, which lists everyone ever downloaded from.
    /// Answers "what's ongoing" and "where are there new messages" directly.
    /// </summary>
    public ObservableCollection<ConversationRowViewModel> Conversations { get; } = new();

    public IReadOnlyList<UserPresenceState> MyStatusOptions { get; } = new[] { UserPresenceState.Online, UserPresenceState.Away };

    private UserPresenceState _myStatus = UserPresenceState.Online;
    public UserPresenceState MyStatus
    {
        get => _myStatus;
        set
        {
            if (_myStatus == value) return;
            this.RaiseAndSetIfChanged(ref _myStatus, value);
            _ = SetMyStatusAsync(value);
        }
    }

    private async Task SetMyStatusAsync(UserPresenceState status)
    {
        try
        {
            await _adapter.SetStatusAsync(status).ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to set own status to {Status}", status);
        }
    }

    public ObservableCollection<UserRowViewModel> Rows { get; } = new();

    private readonly List<UserRowViewModel> _allRows = new();

    private string _filterText = string.Empty;
    public string FilterText
    {
        get => _filterText;
        set => this.RaiseAndSetIfChanged(ref _filterText, value);
    }

    private bool _isLoading;
    public bool IsLoading
    {
        get => _isLoading;
        private set => this.RaiseAndSetIfChanged(ref _isLoading, value);
    }

    private UserProfileViewModel? _selectedProfile;
    public UserProfileViewModel? SelectedProfile
    {
        get => _selectedProfile;
        private set => this.RaiseAndSetIfChanged(ref _selectedProfile, value);
    }

    /// <summary>Free-text username entry for starting a chat with a peer not yet in your history/reliability stats.</summary>
    private string _newChatUsername = string.Empty;
    public string NewChatUsername
    {
        get => _newChatUsername;
        set => this.RaiseAndSetIfChanged(ref _newChatUsername, value);
    }

    public ReactiveCommand<Unit, Unit> RefreshCommand { get; }
    public ReactiveCommand<string, Unit> OpenProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> CloseProfileCommand { get; }
    public ReactiveCommand<Unit, Unit> StartNewChatCommand { get; }

    /// <summary>Chat-rooms panel, exposed as a nested VM so the Users page can render both Contacts and Rooms from one DataContext.</summary>
    public RoomsViewModel Rooms { get; }

    private bool _isRoomsPaneActive;
    public bool IsRoomsPaneActive
    {
        get => _isRoomsPaneActive;
        set => this.RaiseAndSetIfChanged(ref _isRoomsPaneActive, value);
    }

    public UsersViewModel(
        PeerReliabilityService peerReliability,
        DatabaseService databaseService,
        AppConfig config,
        IServiceProvider serviceProvider,
        ISoulseekAdapter adapter,
        RoomsViewModel rooms,
        ChatService chatService,
        UserPresenceWatchService presenceWatch,
        IEventBus eventBus,
        ILogger<UsersViewModel> logger,
        FrequentSourceService? frequentSourceService = null)
    {
        _peerReliability = peerReliability;
        _databaseService = databaseService;
        _config = config;
        _serviceProvider = serviceProvider;
        _adapter = adapter;
        _chatService = chatService;
        _presenceWatch = presenceWatch;
        _eventBus = eventBus;
        _logger = logger;
        _frequentSourceService = frequentSourceService;
        Rooms = rooms;

        RefreshCommand = ReactiveCommand.CreateFromTask(LoadAsync);
        OpenProfileCommand = ReactiveCommand.CreateFromTask<string>(OpenProfileAsync);
        CloseProfileCommand = ReactiveCommand.Create(() => { SelectedProfile?.Dispose(); SelectedProfile = null; });

        var canStartChat = this.WhenAnyValue(x => x.NewChatUsername, text => !string.IsNullOrWhiteSpace(text));
        StartNewChatCommand = ReactiveCommand.CreateFromTask(StartNewChatAsync, canStartChat);

        // Debounced: ApplyFilter() rebuilds Rows via Clear+Add, which used to run on every keystroke.
        this.WhenAnyValue(x => x.FilterText)
            .Throttle(TimeSpan.FromMilliseconds(200), RxApp.MainThreadScheduler)
            .Subscribe(_ => ApplyFilter())
            .DisposeWith(_disposables);

        _eventBus.GetEvent<PrivateMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => !e.IsOutgoing)
            .Subscribe(OnIncomingMessage)
            .DisposeWith(_disposables);

        _eventBus.GetEvent<ConversationClearedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                var row = Conversations.FirstOrDefault(c => string.Equals(c.Username, e.PeerUsername, StringComparison.OrdinalIgnoreCase));
                if (row != null)
                    Conversations.Remove(row);
            })
            .DisposeWith(_disposables);

        _eventBus.GetEvent<UserPresenceChangedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(e =>
            {
                var row = Rows.FirstOrDefault(r => string.Equals(r.Username, e.Username, StringComparison.OrdinalIgnoreCase));
                if (row != null)
                    row.Presence = e.Presence;

                var conversation = Conversations.FirstOrDefault(c => string.Equals(c.Username, e.Username, StringComparison.OrdinalIgnoreCase));
                if (conversation != null)
                    conversation.Presence = e.Presence;
            })
            .DisposeWith(_disposables);

        _ = LoadAsync();
    }

    /// <summary>
    /// Adds/moves a peer to the top of <see cref="Conversations"/> on an incoming message and marks
    /// it unread — unless that peer's profile (and therefore their Chat tab) is the one currently open.
    /// </summary>
    private void OnIncomingMessage(PrivateMessageReceivedEvent e)
    {
        var isActive = SelectedProfile != null && string.Equals(SelectedProfile.Username, e.PeerUsername, StringComparison.OrdinalIgnoreCase);
        var existing = Conversations.FirstOrDefault(c => string.Equals(c.Username, e.PeerUsername, StringComparison.OrdinalIgnoreCase));

        if (existing != null)
        {
            existing.LastMessage = e.Message;
            existing.LastMessageUtc = e.TimestampUtc;
            if (!isActive)
                existing.IsUnread = true;

            var index = Conversations.IndexOf(existing);
            if (index > 0)
                Conversations.Move(index, 0);
        }
        else
        {
            Conversations.Insert(0, new ConversationRowViewModel(e.PeerUsername, e.Message, e.TimestampUtc, isUnread: !isActive));
            _ = WatchOnePresenceAsync(e.PeerUsername);
        }
    }

    private async Task LoadConversationsAsync()
    {
        var recent = await _chatService.GetRecentConversationsAsync().ConfigureAwait(true);
        Conversations.Clear();
        foreach (var c in recent)
            Conversations.Add(new ConversationRowViewModel(c.Username, c.LastMessage, c.LastMessageUtc, isUnread: c.HasUnread));

        _ = RefreshPresenceWatchesAsync();
    }

    /// <summary>
    /// Keeps presence dots live for contacts currently visible in <see cref="Rows"/> (capped —
    /// see <see cref="MaxPresenceWatchedContacts"/>) and every row in <see cref="Conversations"/>
    /// (always small). Diffs against the currently-held watch set rather than unwatching and
    /// rewatching everything on every call, so filtering by a keystroke doesn't cause needless
    /// watch/unwatch server chatter for usernames that stay in view.
    /// </summary>
    private async Task RefreshPresenceWatchesAsync()
    {
        var desired = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var row in Rows.Take(MaxPresenceWatchedContacts))
            desired.Add(row.Username);
        foreach (var conversation in Conversations)
            desired.Add(conversation.Username);

        foreach (var username in _presenceHandles.Keys.Where(u => !desired.Contains(u)).ToList())
        {
            _presenceHandles[username].Dispose();
            _presenceHandles.Remove(username);
        }

        foreach (var username in desired)
        {
            if (_presenceHandles.ContainsKey(username))
                continue;

            await WatchOnePresenceAsync(username).ConfigureAwait(true);
        }
    }

    /// <summary>Watches a single username's presence and seeds the matching row(s) from the
    /// initial snapshot; later changes arrive via the UserPresenceChangedEvent subscription.</summary>
    private async Task WatchOnePresenceAsync(string username)
    {
        if (_presenceHandles.ContainsKey(username))
            return;

        try
        {
            var (handle, snapshot) = await _presenceWatch.WatchAsync(username).ConfigureAwait(true);
            if (_disposed)
            {
                // Closed while the watch request was in flight — release immediately rather than
                // leaking the ref-count for this username forever (see UserPresenceWatchService).
                handle.Dispose();
                return;
            }

            _presenceHandles[username] = handle;
            if (snapshot != null)
            {
                var row = Rows.FirstOrDefault(r => string.Equals(r.Username, username, StringComparison.OrdinalIgnoreCase));
                if (row != null)
                    row.Presence = snapshot.Presence;

                var conversation = Conversations.FirstOrDefault(c => string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase));
                if (conversation != null)
                    conversation.Presence = snapshot.Presence;
            }
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to watch presence for {Username}", username);
        }
    }

    /// <summary>Jumps straight to a conversation or room — used when a chat/room notification is clicked.</summary>
    public async Task OpenConversationFromNotificationAsync(string? username, string? roomName)
    {
        if (!string.IsNullOrWhiteSpace(username))
        {
            IsRoomsPaneActive = false;
            await OpenProfileAsync(username).ConfigureAwait(true);
        }
        else if (!string.IsNullOrWhiteSpace(roomName))
        {
            IsRoomsPaneActive = true;
            await Rooms.OpenRoomAsync(roomName).ConfigureAwait(true);
        }
    }

    /// <summary>
    /// Starts a chat with an arbitrary Soulseek username — doesn't require any prior download
    /// history or reliability stats, since a conversation is a valid reason to look someone up
    /// on its own.
    /// </summary>
    private async Task StartNewChatAsync()
    {
        var username = NewChatUsername.Trim();
        if (string.IsNullOrWhiteSpace(username))
            return;

        NewChatUsername = string.Empty;
        await OpenProfileAsync(username).ConfigureAwait(true);
    }

    public async Task LoadAsync()
    {
        IsLoading = true;
        try
        {
            var summaries = await _databaseService.GetDownloadedUsersSummaryAsync().ConfigureAwait(true);
            var summaryByUser = summaries.ToDictionary(s => s.Username, StringComparer.OrdinalIgnoreCase);

            var knownUsernames = new HashSet<string>(_peerReliability.GetKnownUsernames(), StringComparer.OrdinalIgnoreCase);
            foreach (var username in summaryByUser.Keys)
                knownUsernames.Add(username);

            IReadOnlyDictionary<string, (bool IsFriend, bool IsPinned)> friendFlags = new Dictionary<string, (bool, bool)>(StringComparer.OrdinalIgnoreCase);
            if (_config.EnableFrequentSources && _frequentSourceService != null)
            {
                try
                {
                    var ranked = await _frequentSourceService.GetRankedAsync().ConfigureAwait(true);
                    friendFlags = ranked
                        .GroupBy(r => r.SourceUsername, StringComparer.OrdinalIgnoreCase)
                        .ToDictionary(
                            g => g.Key,
                            g => (IsFriend: g.Any(x => x.IsFriend), IsPinned: g.Any(x => x.IsPinned)),
                            StringComparer.OrdinalIgnoreCase);
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "Failed to load FrequentSources enrichment for Users page");
                }
            }

            _allRows.Clear();
            foreach (var username in knownUsernames)
            {
                summaryByUser.TryGetValue(username, out var summary);
                var peerSnapshot = _peerReliability.GetSnapshot(username);
                friendFlags.TryGetValue(username, out var flags);

                _allRows.Add(new UserRowViewModel(
                    username,
                    totalDownloads: summary.TotalDownloads,
                    completedDownloads: summary.CompletedDownloads,
                    lastDownloadedAtUtc: summary.TotalDownloads > 0 ? summary.LastDownloadedAtUtc : null,
                    reliabilityScore: _peerReliability.GetReliabilityScore(username),
                    lastSeenUtc: peerSnapshot?.LastSeenUtc,
                    isFriend: flags.IsFriend,
                    isPinned: flags.IsPinned));
            }

            _allRows.Sort((a, b) => b.TotalDownloads.CompareTo(a.TotalDownloads));
            ApplyFilter();

            await LoadConversationsAsync().ConfigureAwait(true);
        }
        finally
        {
            IsLoading = false;
        }
    }

    private void ApplyFilter()
    {
        Rows.Clear();
        var query = string.IsNullOrWhiteSpace(_filterText)
            ? _allRows
            : _allRows.Where(r => r.Username.Contains(_filterText, StringComparison.OrdinalIgnoreCase));

        foreach (var row in query)
            Rows.Add(row);

        _ = RefreshPresenceWatchesAsync();
    }

    private async Task OpenProfileAsync(string username)
    {
        if (string.IsNullOrWhiteSpace(username))
            return;

        SelectedProfile?.Dispose();
        var profile = _serviceProvider.GetRequiredService<UserProfileViewModel>();
        SelectedProfile = profile;

        var conversation = Conversations.FirstOrDefault(c => string.Equals(c.Username, username, StringComparison.OrdinalIgnoreCase));
        if (conversation != null)
            conversation.IsUnread = false;

        _ = _databaseService.MarkConversationReadAsync(username);
        await profile.LoadUserAsync(username).ConfigureAwait(true);
    }

    public void Dispose()
    {
        _disposed = true;
        foreach (var handle in _presenceHandles.Values)
            handle.Dispose();
        _presenceHandles.Clear();

        _disposables.Dispose();
        SelectedProfile?.Dispose();
        Rooms.Dispose();
    }
}

/// <summary>A recent 1:1 conversation row — Username + last-message preview + unread state.</summary>
public class ConversationRowViewModel : ReactiveObject
{
    public string Username { get; }

    private string _lastMessage;
    public string LastMessage
    {
        get => _lastMessage;
        set => this.RaiseAndSetIfChanged(ref _lastMessage, value);
    }

    private DateTime _lastMessageUtc;
    public DateTime LastMessageUtc
    {
        get => _lastMessageUtc;
        set => this.RaiseAndSetIfChanged(ref _lastMessageUtc, value);
    }

    private bool _isUnread;
    public bool IsUnread
    {
        get => _isUnread;
        set => this.RaiseAndSetIfChanged(ref _isUnread, value);
    }

    private UserPresenceState _presence = UserPresenceState.Unknown;
    public UserPresenceState Presence
    {
        get => _presence;
        set => this.RaiseAndSetIfChanged(ref _presence, value);
    }

    public ConversationRowViewModel(string username, string lastMessage, DateTime lastMessageUtc, bool isUnread)
    {
        Username = username;
        _lastMessage = lastMessage;
        _lastMessageUtc = lastMessageUtc;
        _isUnread = isUnread;
    }
}

/// <summary>Single row in the Users/Contacts list.</summary>
public class UserRowViewModel : ReactiveObject
{
    public string Username { get; }
    public int TotalDownloads { get; }
    public int CompletedDownloads { get; }
    public DateTime? LastDownloadedAtUtc { get; }
    public double ReliabilityScore { get; }
    public DateTime? LastSeenUtc { get; }
    public bool IsFriend { get; }
    public bool IsPinned { get; }

    private UserPresenceState _presence = UserPresenceState.Unknown;
    public UserPresenceState Presence
    {
        get => _presence;
        set => this.RaiseAndSetIfChanged(ref _presence, value);
    }

    public UserRowViewModel(
        string username,
        int totalDownloads,
        int completedDownloads,
        DateTime? lastDownloadedAtUtc,
        double reliabilityScore,
        DateTime? lastSeenUtc,
        bool isFriend,
        bool isPinned)
    {
        Username = username;
        TotalDownloads = totalDownloads;
        CompletedDownloads = completedDownloads;
        LastDownloadedAtUtc = lastDownloadedAtUtc;
        ReliabilityScore = reliabilityScore;
        LastSeenUtc = lastSeenUtc;
        IsFriend = isFriend;
        IsPinned = isPinned;
    }
}
