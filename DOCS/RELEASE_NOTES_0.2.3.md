# ORBIT 0.2.3 — release notes

**Released:** 2026-10-29 · **Previous public build:** 0.2.2 (2026-10-29) · **Runtime:** win-x64,
self-contained single file.

0.2.3 is an incremental patch on the 0.2 early-stable line. It fixes Cue Forge auditioning the wrong
track after an auto-mix cross, makes track analysis show real per-stage progress (and says so when the
Essentia extractor is missing), keeps a forced re-analysis alive across track switches, and flags a
broken deck in the Mix sidepanel with a one-click "fix and analyse" path.

Install over 0.2.2 (same per-user AppId, no uninstall needed). `%APPDATA%\ORBIT` is untouched. No schema
change.

---

## Cue Forge: the track on screen is the track that plays

After an auto-mix cross the queue/current-track identity can still name the outgoing track while the
shared player holds the incoming file. Cue Forge trusted `CurrentTrackHash` first, so the outgoing track
looked attached; clicking its cue skipped the attachment check, seeked, and played the **incoming** track
— the outgoing cue could not be auditioned or moved.

- The **actually loaded file** is now the authority; queue identity is only a fallback when no path is
  reported yet.
- Navigating to another track **hands a borrowed player back** first, so the page and the audio can't
  diverge.
- A cue audition **re-checks attachment** immediately before it seeks, so a racing track change can never
  turn it into a seek of someone else's audio.

## Analysis: real staged progress instead of a stuck 20%

`AnalysisQueueService` called `AnalyzeFileAsync` without the progress callback the pipeline already
supports, so the UI only ever saw the hard-coded `Analyzing audio · 20%`.

- The pipeline's per-stage callback is now wired, and progress is **monotonic per track**.
- The pipeline reports **eight ordered stages** (checking integrity, decoding, waveform, loudness &
  energy, AI genre/mood, BPM/key/grid, structure & cues, saving & fingerprint), and the Cue Forge banner
  and Analysis page row show **"Stage 3/8 · Extracting waveform · 41%"**.
- Loudness and the AI mood pass report their own stages, so the bar keeps moving during the long steps.

## Analysis: an alert when Essentia is missing

The Essentia extractor is the only source of BPM, key and the beat grid, but its absence was only logged
at debug level and the run later failed with a dependency-shaped error.

- When a run needs Essentia and the extractor isn't installed, ORBIT shows a **toast** ("Essentia analysis
  is not installed …") once per session and the step text reads
  **"Analyzing audio (Essentia missing — BPM/key skipped)"**.
- The pipeline logs a warning and the missing-output exception now names Essentia when BPM/key can't be
  produced.

## Cue Forge: a forced re-analysis survives track switches

`LoadTrackAsync` cleared the pending/status state on every switch and the progress handlers only matched
the current track, so leaving a track silently lost its run status (the queue itself never cancels).

- Pending runs are tracked **per track hash**; switching away no longer cancels or forgets them.
- Returning to a track that is still analysing **restores the live stage**; a run that finishes while the
  track is closed simply drops its pending state and the fresh features load from the database on return.

## Mix sidepanel: warn on a broken deck, fix it in one click

The Mix sidepanel now shows a **needs-attention** warning when either deck has incomplete analysis
(missing analysis data or BPM) or **no cues**, naming the affected side(s).

- Each affected deck gets a **Fix & analyse** button that opens the track in Cue Forge **and immediately
  queues a full analysis** for it, so its own pending banner tracks the run and reloads on completion.

## Setup: the version-gated "What's new" re-onboarding

This is the first release that ships the upgrade re-onboarding. An install that completed an older
wizard version — or predates the wizard — sees a short **What's new** page on the next launch instead of
a blank start, then is marked at the current setup version so it doesn't reappear until the next release
bumps it. A genuinely fresh install still gets the full first-run wizard, and **Settings → Advanced →
Setup** still reopens the full wizard on demand.

---

## Upgrade notes

- No schema change and no data migration. The same per-user AppId upgrades in place over 0.2.2.
- Cue Forge's forced re-analysis is now tracked per track; if a run is interrupted (app closed), it is
  simply not pending on the next launch and the normal analysis queue still owns it.

## Verification

- `dotnet build` (Release): **0 errors**.
- Full test suite: **2765 passed, 11 skipped, 0 failed**.
- New coverage: the wrong-file cue click and borrowed-player hand-back, staged progress, the
  Essentia-missing path, the per-track pending state, and the Mix panel attention warning.

## Known issues / limitations

- **Existing auto cues are not migrated** (carried from 0.2.1): tracks cued by an older build keep their
  positions until **Auto-generate** runs again.
- **Downbeat phase can be off by a few beats** on some tracks; use **Bar 1 here** (and the ×2/÷2/manual
  BPM override) to correct a track, then re-run Auto-generate.
- **When Essentia is not installed**, a full-pipeline run still reports a partial failure because the
  requested BPM/key outputs cannot be produced (the features that did run are preserved). The new toast
  explains the cause; install the extractor under `Tools/Essentia` to enable BPM/key.
- **The update check contacts GitHub** (one unauthenticated GET, at most once a day); opt out with the
  existing `EnableUpdateCheck` setting.

## Artifacts

| File | What it is |
|---|---|
| `artifacts/ORBIT-Setup-0.2.3-win-x64.exe` | Per-user installer (no admin prompt), shortcuts, uninstaller |
| `artifacts/ORBIT-0.2.3-win-x64.zip` | Portable package (same publish output) |
| `artifacts/ORBIT-0.2.3-win-x64-manifest.txt` | Version, runtime, configuration, publish timestamp |

Install and uninstall both leave `%APPDATA%\ORBIT` (library database, config, fingerprints, stems)
untouched, so reinstalling never costs you your library.
