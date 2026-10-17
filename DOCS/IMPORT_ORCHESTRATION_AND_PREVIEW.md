# Import Orchestration and Preview

**Updated:** October 17, 2026
**Focus:** how a playlist gets from Spotify, a pasted tracklist or a CSV file into a library playlist and the download queue, and what the preview shows along the way

---

## The flow at a glance

1. **Import page** (`Views/Avalonia/ImportPage.axaml`, `SpotifyImportViewModel`): pick a source.
   - Spotify: a playlist/album link (works without a connection for public playlists via the
     web-player fallback), one of "Your playlists", or Liked Songs (needs a connection).
   - Tracklist: paste text (Ctrl+Shift+V pastes and previews).
   - CSV: drop or choose a file.
   - Recent imports: the last five imported playlists. Open one in the Library, or re-fetch its
     source ("check for new tracks"), which opens the preview set to update that playlist.
2. **`ImportOrchestrator.StartImportWithPreviewAsync`** works out a deterministic job id
   (hash of the canonical source), looks for an earlier import of the same source (id, then
   URL, then Spotify playlist id), opens the preview and streams the provider's batches into it.
3. **Preview** (`ImportPreviewPage.axaml`, `ImportPreviewViewModel`): the user picks a
   destination and rows. One primary button commits.
4. **Commit**: `DownloadManager.QueueProject` persists the playlist and queues downloads, and
   returns a `ProjectQueueResult` with what actually happened. The toast reports those counts
   and the app opens the playlist in the Library.

---

## Primary files

| File | Role |
|---|---|
| `Services/ImportOrchestrator.cs` | Preview session lifecycle, duplicate lookup, streaming, error translation, Retry / Reconnect, background sync (`SilentImportAsync`) |
| `Services/ImportProviders/*` | Spotify (API, scraper fallback), Spotify Liked Songs, pasted tracklist, CSV. All are `IStreamingImportProvider` |
| `Services/ImportProviders/SpotifyImportErrors.cs` | Spotify/network exceptions → short actionable `ImportSourceException` |
| `Services/Import/ImportMatching.cs` | `LibraryMatchIndex`, `PlaylistMembershipIndex`, `ImportTrackMapper`, `ProjectQueueResult` |
| `Services/Import/ImportQueueGateway.cs` | `IImportQueueGateway`: the preview's seam to DownloadManager (fakeable in tests) |
| `ViewModels/ImportPreviewViewModel.cs` | Row status, destination, suggestion, summary, commit |
| `ViewModels/SelectableTrack.cs` | One preview row: selection, editable artist/title, status, duration |
| `ViewModels/SpotifyImportViewModel.cs`, `RecentImportItemViewModel.cs` | Import page |
| `Services/DownloadManager.cs` (`QueueProject`) | Gap analysis + persistence + queueing |

---

## Provider contract

- Stream batches (`ImportBatchResult`): tracks, source title/type/URL/owner, estimated total.
- **On failure, throw `ImportSourceException`** (set `RequiresSignIn` when connecting the
  account would fix it). Providers used to log and yield nothing, so the preview finished as
  "Loaded 0 tracks — ready to import" with the reason only in the log.
- Spotify: `client.Paginate` pages past 100 (playlists) / 50 (albums, Liked Songs); the client's
  `SimpleRetryHandler` honours 429 Retry-After. Removed/episode items are skipped; local files
  are kept (they can still be searched by artist/title). Missing ISRC doesn't matter.
- A pasted tracklist has no source URL of its own. Only a detected backlink (1001Tracklists) is
  stored as the playlist's `SourceUrl`, never the pasted text.

---

## The preview

### Header
Source icon, title and "Spotify · by Owner · 37 tracks".

### Destination step
Segmented choice **New playlist | Add to existing**.

- **New playlist**: editable name, defaulting to (and following) the source title until the user
  edits it. If a playlist with that name exists, a warning offers "Add to that playlist instead".
- **Add to existing**: searchable list of every playlist (smart playlists excluded) with track
  counts. Picking one loads its rows and re-labels the preview.
- **Suggestion card** (`callout`), shown when a likely duplicate is found:
  - *Same source imported before* (id / URL / Spotify playlist id): destination defaults to
    updating it. "You imported this before as 'X'. N tracks there, M of these are new."
  - *Same name*, or a recent playlist that already holds ≥60% of the tracks: destination stays
    New; the card offers to update instead.
  - Buttons: **Update 'X'** (recommended) / **Import as new**. "Import as new" mints a fresh id so
    the two playlists stay separate.

### Row status
| Status | Meaning | Default |
|---|---|---|
| New | Not in the library: will download | checked |
| Similar | Not owned, but a 75–88% fuzzy match exists (tooltip names it) | checked |
| Owned | In the library: the playlist links the file, nothing downloads | checked |
| Queued | Already waiting/searching/downloading | checked |
| In playlist | Already in the destination: skipped | unchecked |
| Retry | In the destination but failed before: retried | checked |
| Repeat | Same track earlier in this import | unchecked |
| No match | No title: can't be searched | unchecked |

"Owned" and "In playlist" use the **same rules as `QueueProject`** (`ImportMatching.cs`): library
by hash → Spotify id → loose artist|title → fuzzy ≥0.88; playlist membership by hash → linked
library file's hash → Spotify id → loose key. Editing a row's artist/title re-matches it.

### Summary strip
Mono chips that double as filters: All · New · Owned · In playlist · Queued · No match (zero
counts hidden). Selection actions sit in the `…` menu: select all/none shown, select only tracks
to download, reset to the suggested selection.

### Footer
One sentence: "Add 37 tracks to new playlist 'Night Drive' · 12 already owned, 25 will download",
Cancel, and one primary button labelled for the action ("Import 37 tracks", "Add 12 to 'X'",
"Update 'X' (+3)"). The button is driven only by `AddToLibraryCommand.CanExecute`; every input
(loading, selection, destination, target, name, commit state) funnels through
`RaiseCommitStateChanged()`. When disabled, a caption says why ("Choose a playlist to add to",
"Name the new playlist", "Select at least one track", "Everything is already in 'X'",
"Loading tracks… 150 of 1,800").

### States
Loading with progress (n of total) · error with Retry / Connect Spotify · partial-load warning
banner (rows kept) · empty source · "you already own everything" notice · empty filter.

---

## Commit

- Sends only checked rows that change the playlist (not repeats, unsearchable or already-there
  rows). Owned rows are linked to the existing file.
- **Existing destination keeps its own title, type and link.** (It used to be renamed to the
  incoming source.) An update of the same source refreshes the stored source URL.
- Idempotent: after success the button reads "Already imported"; the command also blocks
  re-entry while running. A failure stays on the page with the error under the button and can
  be retried; nothing is reported as success.
- Toast text comes from `ProjectQueueResult` (added / linked / queued for download / retried /
  already there), or "Nothing new to add…" when nothing changed.
- Paste/CSV rows without Spotify metadata are enriched in the background after the commit.

## Background sync (`SilentImportAsync`)

Library "Sync" and "Sync all" stream the source and send it through the same `QueueProject`
gap analysis (priority 10, so the auto-acquire setting still applies). It writes to the earlier
playlist's id and keeps its name.

---

## Tests

- `Tests/SLSKDONET.Tests/ViewModels/ImportPreviewCommitStateTests.cs`: commit button
  CanExecute after load / merge without target / target picked.
- `Tests/SLSKDONET.Tests/ViewModels/ImportPreviewFlowTests.cs`: statuses, counts, footer text,
  existing destination, re-import suggestion, name/overlap suggestions, commit payload,
  idempotency, failure, states, cancel.
- `Tests/SLSKDONET.Tests/Services/Import/ImportMatchingAndProviderErrorTests.cs`: matching rules
  and the provider failure contract.

## Known limits

- Spotify-generated mixes (Discover Weekly etc.) often 404 through the Web API for third-party
  apps; the preview says so.
- The public web-player fallback (not connected) may return only part of a large playlist.
- Overlap detection only checks the 8 most recent playlists plus a same-named one.
