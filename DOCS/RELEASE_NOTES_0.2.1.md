# ORBIT 0.2.1 — release notes

**Released:** 2026-10-29 · **Previous public build:** 0.2.0 (2026-10-28) · **Runtime:** win-x64,
self-contained single file.

0.2.1 is a patch on the 0.2 early-stable line: six commits since 0.2.0, no database migration and no
new first-run work. It fixes the two things DJs hit most in 0.2.0 — auto cues landing in the wrong
place, and Cue Forge keeping the shared player after an edit — plus exact cue playback, a one-click
clear, and update notices that can finally see early builds.

Install over 0.2.0 (same per-user AppId, no uninstall needed). `%APPDATA%\ORBIT` is untouched.

---

## Cue placement: the drop is now the actual drop

**Reported:** "the phrasing is clearly visible but the drop is placed way before the actual drop."

Three things combined on the test track (*TR Tactics — Against The Grain (Audio Remix)*, 174 BPM):

1. **The good phrase analysis was never read.** Cue generation only trusted
   `audio_features.PhraseSegmentsJson` when its `PhraseSegmentsSource` was set. That column was only
   ever *filled when empty*, so tracks analysed before the half-time BPM correction kept boundaries at
   double their real durations (`Intro 0–88 s`, `Drop 1 @ 132 s`, outro past the end of a 256 s file)
   and an empty source. The fresh `TrackPhrases` rows (`Intro 0–44`, `Build 44–66`, `Drop 66–132`)
   existed but nothing read them — not Auto-generate, and not Cue Forge's phrase map or its
   "intro ends…" suggestion.
2. **Without phrases, every sub-bass return tied.** The cached fast path never persists the
   spectral-flux curve, so all sub-bass returns scored identically and the *earliest* one in the
   selection window won (49.4 s). An 8-bar snap then pulled it to 45.2 s — 21 s early, in the intro.
3. **The downbeat was three beats late**, so bar/8-bar snapping could move a correct 66.21 s drop to
   67.24 s on its own.

What changed:

- **One phrase source** (`Engine/Analysis/PhraseSourceResolver.cs`): fresh `TrackPhrases` when present,
  Rekordbox PSSI when it wrote the legacy column, stale rows sanitized (segments starting past the end
  are dropped, overlong durations clamped). Cue Forge's phrase map, its section suggestion and
  Auto-generate all read that one list, so what you see and what gets cued cannot come from two
  different analyses.
- **Drop choice is a vote** (`CueGenerationService.MergeDropCandidates`): phrase drops, the stored drop
  hint (`TimeTillDropSeconds`/`DropTimeSeconds`), sub-bass returns, spectral-flux novelty, broadband
  energy jumps and the genre family's own signal all vote. Votes within two bars merge and every
  agreeing *other* source adds a corroboration bonus — three weak-but-agreeing detections now beat one
  loud false positive, independent of candidate order.
- **Drops are cleaned up at beat resolution**, not on the bar/8-bar grid whose phase depends on the
  downbeat guess; DSP drops are only phrase-snapped while that stays within ¾ bar. `[IN -16]`/`[IN -8]`
  remain exact bar arithmetic back from the chosen drop.
- **The structural job rewrites `PhraseSegmentsJson` on every run** (it used to write only when empty),
  so a re-analysis repairs old rows instead of preserving them. Rekordbox's PSSI segments are never
  overwritten by heuristic ones.

On that track, after Auto-generate:

| Cue | 0.2.0 | 0.2.1 |
|---|---|---|
| `[IN -16]` | 0:23.11 | **0:44.15** (intro end) |
| `[IN -8]` | 0:34.15 | **0:55.18** |
| `[DROP 1]` | 0:45.18 | **1:06.22** |
| `[DROP 2]` | 2:57.60 | 2:56.57 |
| `[OUT]` | 3:41.74 | 3:40.36 |

Existing auto cues are **not** rewritten on update — press **Auto-generate** on a track (or re-run its
structure analysis) to replace them. Your own cues and Intro end / Outro markers are always kept.

## Cue Forge: one click clears the slate

The AUTO-GENERATE panel and the Preparation flyout have **Clear cues & reset view**: every cue is
removed as a single undo step and the waveform returns to the whole-track view (the playhead returns to
0 when stopped). Ctrl+Z restores the set.

## The playlist gets its track back

Prelistening a cue borrows the shared player and pauses the playlist track. Until now that was where it
stayed: saving an edit left the mix silent on the inspected track until you left the page.

- `PlayerBorrow` now remembers the playlist's file, title/artist, position **and whether it was
  playing**.
- **Saving** (Save / Ctrl+S, and the auto-save on track switch) or leaving the page hands the player
  back to that track at its old position and **resumes it** when it was playing.
- The queue is untouched: only the borrowed file was ever replaced, `CurrentTrack` still points at the
  playlist track, so auto-advance and next/previous keep working.

## Cue playback starts exactly on the cue

For FLAC/M4A the exact seek is a decode-forward on a background thread. Starting the output before it
landed let the reader deliver samples from wherever it still was, heard as "playback starts before the
cue". Play now waits for that seek (bounded at 3 s) before opening the output, so a cue click starts on
the cue — including the plain Play button at the playhead.

## Cue Forge opens on the track you meant

- Opening Cue Forge follows the track that is playing instead of whatever was last edited (the view
  model is a singleton created at startup, so the old constructor-time restore had nothing to follow).
- Explicit "Open in Cue Forge" / "Fix in Cue Forge" links keep their own track for that visit, so an
  incoming deck's cues can be edited while something else plays.
- The last-edited track is written by `ConfigManager` (`[CueForge] LastTrackHash/Title/Artist`) and
  restored on next launch — it used to be written by the view model only, so it never survived a
  restart.
- Rebuilding the docked browser no longer reports a transient selection (first row) back as a click.

## Player: a stopped deck can no longer keep playing

WASAPI raises `PlaybackStopped` synchronously from `Stop()`. Because a deck is stopped by disposing it,
that event fired while the outgoing deck was still current; the handler read a deliberate stop as "the
track ended", promoted the preloaded next deck at full level, and `Stop()` then cleared `_current` —
leaving audio running with nothing referencing it ("it just kept playing, never stops").

- `Stop()` detaches `_current`/`_next` (and the crossfade state) before disposing, so its own teardown
  event is ignored; `Deck.IsDisposed` plus a `_stopping` flag make that explicit.
- The stop handler, crossfade timer and `PromoteNextDeck` re-check under a lock that the deck they act on
  is still current; every created deck is tracked and anything that lost its reference is disposed.
- Regression tests reproduce both WASAPI (synchronous) and WaveOutEvent (asynchronous) behaviour — four
  of the six new cases fail on 0.2.0.

## Update notices now see early builds

The update check only queried `/releases/latest` (GitHub defines that as the newest **non-prerelease**)
and compared bare numeric cores, so alpha/beta/RC builds were invisible and — because the version line
restarted at 0.2.0 after 0.10.0-alpha — old alpha users were never told 0.2.0 existed.

- Fetches the full releases list, classifies each tag's channel from its suffix (falling back to
  GitHub's prerelease flag), and orders by publication date so the version-line reset is handled.
- Separate notices for a newer stable build, a newer early build, running a pre-release, and the
  existence of the early channel; a stable user is never offered a pre-release older than their build.
- Update toasts are clickable and open the matching release page; the URL stays in the text as fallback.

---

## Verification

- `dotnet build` (Debug and Release): **0 errors** (38 pre-existing warnings).
- Full test suite: **2727 passed, 11 skipped, 0 failed** (2738 total), including new coverage for the
  phrase-source precedence, the multi-signal drop merge, the cue-start seek wait, the clear command and
  the playlist hand-back/resume behaviour.
- Verified on the real library track above: generated cues `0:44.15 / 0:55.18 / 1:06.22 / 2:34.50 /
  2:45.53 / 2:56.57 / 3:40.36`, saved as 7 hot + 3 memory cues.

## Known issues / limitations

- **Cue Forge's navigation can still open the first track of the last playlist** instead of the track
  you left it on; the Library's **Cue Forge** button (and the Mix panel's "Fix in Cue Forge") always
  open the requested track. The follow-the-player behaviour is still being tuned.
- **Downbeat phase can be three beats late** on some tracks (the strongest-beat guess). Drops no longer
  depend on it, but bar numbers and manual bar-snapping do — use **Bar 1 here** in the Preparation menu
  to correct a track, then re-run Auto-generate.
- **Existing auto cues are not migrated.** Tracks cued by 0.2.0 keep their old positions until
  Auto-generate runs again.
- **The update check contacts GitHub** (one unauthenticated GET, at most once a day); opt out with the
  existing `EnableUpdateCheck` setting.

## Artifacts

| File | What it is |
|---|---|
| `artifacts/ORBIT-Setup-0.2.1-win-x64.exe` | Per-user installer (no admin prompt), shortcuts, uninstaller |
| `artifacts/ORBIT-0.2.1-win-x64.zip` | Portable package (same publish output) |
| `artifacts/ORBIT-0.2.1-win-x64-manifest.txt` | Version, runtime, configuration, publish timestamp |

Install and uninstall both leave `%APPDATA%\ORBIT` (library database, config, fingerprints, stems)
untouched, so reinstalling never costs you your library.
