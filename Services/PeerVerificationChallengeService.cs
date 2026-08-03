using System;
using System.Collections.Generic;
using System.Reactive.Disposables;
using System.Reactive.Linq;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using ReactiveUI;
using SLSKDONET.Configuration;
using SLSKDONET.Models;
using SLSKDONET.Views;

namespace SLSKDONET.Services;

/// <summary>
/// Some Soulseek peers run "gate bots" that block downloads behind a private-chat challenge —
/// e.g. "Reply only with this word / Antworte nur mit diesem Wort: 3005. This unlocks downloads
/// for 7 days." When enabled (<see cref="AppConfig.AutoSolveDownloadVerificationChallenges"/>),
/// this detects that specific shape of message and replies with the extracted word automatically.
///
/// Deliberately narrow and gated for safety, since this auto-sends chat messages to strangers on
/// the user's behalf based on parsing untrusted text:
///  - Only reacts to messages that look like this exact "reply with word: TOKEN" shape — never a
///    general-purpose auto-responder.
///  - The extracted token is capped to a short bare alphanumeric string, so a peer can't smuggle
///    arbitrary text (commands, spam, other people's usernames, etc.) into the reply.
///  - Only fires for peers we've actually attempted to download from recently (an active transfer,
///    or one within <see cref="RecentDownloadWindow"/>) — never for unsolicited incoming chat from
///    someone we have no download relationship with.
///  - Auto-replies at most once per peer per app session.
/// It never chases follow-up asks from the same bot (e.g. "send !friend for priority access") —
/// the pattern match only ever fires on the original challenge shape.
/// </summary>
public sealed class PeerVerificationChallengeService : IDisposable
{
    private static readonly Regex ChallengePattern = new(
        @"(reply|antworte).{0,60}?(word|wort).{0,20}?:\s*([A-Za-z0-9]{1,24})\b",
        RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.Compiled);

    private static readonly TimeSpan RecentDownloadWindow = TimeSpan.FromMinutes(30);

    private readonly ChatService _chatService;
    private readonly DownloadManager _downloadManager;
    private readonly DatabaseService _databaseService;
    private readonly AppConfig _config;
    private readonly INotificationService _notificationService;
    private readonly ILogger<PeerVerificationChallengeService> _logger;
    private readonly CompositeDisposable _disposables = new();
    private readonly HashSet<string> _repliedPeers = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _repliedLock = new();

    public PeerVerificationChallengeService(
        IEventBus eventBus,
        ChatService chatService,
        DownloadManager downloadManager,
        DatabaseService databaseService,
        AppConfig config,
        INotificationService notificationService,
        ILogger<PeerVerificationChallengeService> logger)
    {
        _chatService = chatService;
        _downloadManager = downloadManager;
        _databaseService = databaseService;
        _config = config;
        _notificationService = notificationService;
        _logger = logger;

        eventBus.GetEvent<PrivateMessageReceivedEvent>()
            .ObserveOn(RxApp.MainThreadScheduler)
            .Where(e => !e.IsOutgoing)
            .Subscribe(e => _ = HandleIncomingMessageAsync(e))
            .DisposeWith(_disposables);
    }

    private async Task HandleIncomingMessageAsync(PrivateMessageReceivedEvent e)
    {
        if (!_config.AutoSolveDownloadVerificationChallenges)
            return;

        var match = ChallengePattern.Match(e.Message);
        if (!match.Success)
            return;

        var word = match.Groups[3].Value;

        lock (_repliedLock)
        {
            if (!_repliedPeers.Add(e.PeerUsername))
                return; // Already auto-replied to this peer this session.
        }

        try
        {
            if (!await HasRecentDownloadAttemptAsync(e.PeerUsername).ConfigureAwait(false))
            {
                lock (_repliedLock) { _repliedPeers.Remove(e.PeerUsername); }
                return;
            }

            await _chatService.SendMessageAsync(e.PeerUsername, word).ConfigureAwait(false);
            _logger.LogInformation("[VerificationChallenge] Auto-replied '{Word}' to {Username}'s download-unlock challenge", word, e.PeerUsername);
            _notificationService.Show(
                "Auto-solved a download challenge",
                $"{e.PeerUsername} asked for a reply to unlock downloads — sent \"{word}\" automatically.",
                NotificationType.Information);
        }
        catch (Exception ex)
        {
            lock (_repliedLock) { _repliedPeers.Remove(e.PeerUsername); }
            _logger.LogWarning(ex, "[VerificationChallenge] Failed to auto-reply to {Username}", e.PeerUsername);
        }
    }

    private async Task<bool> HasRecentDownloadAttemptAsync(string username)
    {
        if (_downloadManager.HasActiveTransferWithPeer(username))
            return true;

        var recent = await _databaseService.GetDownloadHistoryForUserAsync(username, limit: 1).ConfigureAwait(false);
        return recent.Count > 0 && (DateTime.UtcNow - recent[0].RecordedAt) <= RecentDownloadWindow;
    }

    public void Dispose() => _disposables.Dispose();
}
