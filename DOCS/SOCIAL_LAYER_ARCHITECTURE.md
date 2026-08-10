# Social & Serving Layer — Architecture Reference

Status: Authoritative technical reference
Date: 2026-08-10
Scope: Soulseek serving (upload) resolvers, share indexing, presence, 1:1/room chat, contacts,
notifications

## Why this exists

Until 2026-07-30, ORBIT was a pure Soulseek *download* client that faked its own share health:
it called `client.SetSharedCountsAsync(...)` (a cosmetic count announcement) but never
implemented any of the five serving-side delegates the Soulseek.NET library requires, so it
could not actually serve a byte to any peer. A peer messaging the maintainer ("you're not
sharing anything") triggered the audit that found this. The fix, and everything built on top
of it since (contacts, presence, chat, notifications), is what this document covers.

## Canonical components

Serving (upload) side:

1. [Services/ShareIndexService.cs](../Services/ShareIndexService.cs) — the share index
2. [Services/SoulseekAdapter.cs](../Services/SoulseekAdapter.cs) — resolver wiring (`CreateClientOptions`), upload dispatch (`HandleEnqueueDownloadAsync`)
3. [Services/ISoulseekAdapter.cs](../Services/ISoulseekAdapter.cs) — the app-facing contract

Presence / chat / social:

1. [Services/UserPresenceWatchService.cs](../Services/UserPresenceWatchService.cs)
2. [Services/ChatService.cs](../Services/ChatService.cs) (1:1)
3. [Services/RoomChatService.cs](../Services/RoomChatService.cs) (public rooms)
4. [Services/PeerReliabilityService.cs](../Services/PeerReliabilityService.cs) — always-on peer reliability tracking, the Users-page row source
5. [Services/NotificationCenterService.cs](../Services/NotificationCenterService.cs)
6. [Services/DatabaseService.cs](../Services/DatabaseService.cs) — `GetDownloadHistoryForUserAsync`, `GetDownloadedUsersSummaryAsync`, `GetLastSuccessfulPeerForTrackAsync`, chat read/write

ViewModels / UI:

1. [ViewModels/UsersViewModel.cs](../ViewModels/UsersViewModel.cs)
2. [ViewModels/UserProfileViewModel.cs](../ViewModels/UserProfileViewModel.cs) (transient, per opened profile)
3. [ViewModels/RoomsViewModel.cs](../ViewModels/RoomsViewModel.cs) / `RoomViewModel`
4. [Views/Avalonia/UsersPage.axaml](../Views/Avalonia/UsersPage.axaml)
5. [Views/Avalonia/Controls/MessageThreadView.axaml](../Views/Avalonia/Controls/MessageThreadView.axaml) — shared chat-bubble list for both 1:1 and room threads

Data:

1. `PrivateMessages` table (`Data/Entities/PrivateMessageEntity.cs`) — unique index on nullable `SoulseekMessageId` for replay dedup
2. `RoomMessages` table (`Data/Entities/RoomMessageEntity.cs`)
3. Both added via the standard raw-SQL `Services/Data/SchemaMigratorService.cs` patch convention. Room rosters are **not** persisted — refetched live each session, matching how the app treats other live server state.

## Serving side: how a peer downloads from you

Soulseek.NET requires five delegates on `SoulseekClientOptions` to act as a real peer. Before
this work, none were set, so the library used its defaults (do-not-respond / empty response /
do-nothing):

| Delegate | Purpose | Default if unset |
|---|---|---|
| `SearchResponseResolver` | answer peer search queries against your shares | do not respond |
| `BrowseResponseResolver` | answer "browse this user's shares" requests | empty response |
| `DirectoryContentsResolver` | answer directory-listing requests | empty directory |
| `EnqueueDownload` | accept/reject an incoming download request | do nothing |
| `UserInfoResolver` | answer "get user info" requests | library default |

`SoulseekAdapter.CreateClientOptions()` now wires all five. Each is backed by
`ShareIndexService`, a `ConcurrentDictionary`-backed index of `AppConfig.SharedFolderPath`/
`DownloadDirectory`, keyed by **virtual path** — the absolute local path with `/` normalized to
`\`, matching the real Soulseek client convention with zero remapping needed on Windows. The
index refreshes on a 60-second TTL plus a fingerprint check on folder change (no
`FileSystemWatcher`).

Peer-supplied filenames are **only ever** checked against this pre-built index, never touch
disk directly — this is a deliberate path-traversal guard, not an oversight.

Accepting an enqueue request (not throwing) only completes the queue handshake — the library
does **not** call `UploadAsync` for you. `HandleEnqueueDownloadAsync` validates the requested
filename against the index, then fires `UploadSharedFileAsync` and tracks `_activeUploadCount`
via `Interlocked` for the free-upload-slot heuristics exposed in Search/UserInfo responses.
Rejecting must throw `Soulseek.DownloadEnqueueException` — any other exception is also treated
as a rejection but with a generic message.

Two filename conventions matter here and are easy to get backwards: in a `BrowseResponse`/
`Directory`, `File.Filename` is **basename only** (`Directory.Name` carries the folder path); in
a top-level `SearchResponse`, `File.Filename` is the **full virtual path**.

Search matching inside `ShareIndexService.Search` is a flat linear AND/NOT substring scan — not
word-boundary aware, fine at the tens-of-thousands-of-files scale this app has been tested at,
would need an inverted index at much larger scale. There are no real audio attributes
(bitrate/length) in shared `File` entries — reading tags for every enumerated file during index
build was deliberately skipped in favor of correctness-first serving.

## Presence

`UserPresenceWatchService` wraps `WatchUserAsync`/`UnwatchUserAsync`/`GetUserStatusAsync` with
**reference counting** — it deliberately does not auto-watch every historical peer (could be
hundreds), only watches while a profile is actively open. `WatchAsync` returns
`(IDisposable Handle, UserWatchSnapshot? Snapshot)`; the snapshot (speed/share counts/country —
data the Soulseek server includes for free with every watch ack) is cached per username so a
second caller watching an already-watched user gets data without a redundant network call.
Status changes republish on the app event bus as `UserPresenceChangedEvent`. The app can also
set its own status via `SetStatusAsync` (Online/Away picker in the Users page header).

## Chat

- **1:1** (`ChatService`) and **room** (`RoomChatService`) messages both persist to their
  respective table and republish on the event bus for live UI append.
- **Dedup**: the DB unique index on `SoulseekMessageId` isn't sufficient alone, because the DB
  write is fire-and-forget relative to the live UI append — both services also keep an
  in-memory `ConcurrentDictionary<int, byte>` of seen message IDs.
- `MessageThreadView` (shared control for both 1:1 and room chat) provides real virtualization,
  avatars with deterministic initials/colors, date-separated message grouping, auto-scroll with
  a "new messages" indicator, "load earlier messages" pagination, and Enter-to-send.
- Image attachments ride on the file-serving pipeline described above, since the Soulseek
  protocol itself has no native attachment support.

## Contacts (Users page)

`UsersViewModel` merges two data sources for its row list:

1. `PeerReliabilityService` (always-on, not opt-in — every peer you've interacted with)
2. `DatabaseService.GetDownloadedUsersSummaryAsync` (grouped from `DownloadHistoryEntity.PeerUsername`)

Opening a profile creates a transient `UserProfileViewModel` with four tabs: Overview, Browse
Shares, Download History, Chat. Browse Shares reuses `UserCollectionViewModel`, switched from a
singleton to a **transient** registration so each opened profile gets its own instance — the
existing Search-page browse overlay is unaffected since it only ever resolves one instance at
its own one-time construction.

## Known-good-peer ranking

`SearchCandidateRankingPolicy.CalculateFinalScore` includes an `isKnownGoodPeerForTrack` bonus
(weighted higher than the free-slot bonus), looked up once per
`DownloadDiscoveryService.FindBestMatchAsync` call — a peer you've successfully downloaded a
given track from before ranks higher next time.

## Notifications

`NotificationCenterService` (singleton, `ReactiveObject`) is a **persistent** notification
history, distinct from the pre-existing **ephemeral** toast system (`INotificationService`/
`ToastRequestedEvent` — auto-dismissing, no history). It subscribes to:

- `TrackStateChangedEvent` (State==Completed → resolves Artist/Title from in-memory
  `DownloadManager.GetAllDownloads()`, deliberately avoiding a DB race against
  `DownloadHistoryEntity`'s independently-timed write)
- `PrivateMessageReceivedEvent` (incoming only)
- `RoomMessageReceivedEvent` (incoming only)

It builds a capped (200-item) `ObservableCollection<NotificationItem>` with `UnreadCount`, and
also fires the existing toast (`INotificationService.Show`) for the same event — persistent
history and the transient toast come from one unified call site rather than two competing
systems. Also mirrored as real OS-level Windows Action Center toasts
(`Services/WindowsToastService.cs`) for peer chat specifically.

## Known limitations (explicit, not oversights)

- No documented server-side rate limit was found for `WatchUserAsync`; the ref-counted design
  is conservative but not stress-tested at scale.
- Share search has no relevance ranking beyond substring match.
- No `FileSystemWatcher` on shared folders — index refresh is TTL/fingerprint based, not live.

## Related

- [RECENT_CHANGES.md](../RECENT_CHANGES.md) — 2026-07-30/07-31 entries have the original
  implementation narrative and live-verification notes
- [ARCHITECTURE.md](../ARCHITECTURE.md) → "Social & Serving Layer" — condensed summary version of this document
