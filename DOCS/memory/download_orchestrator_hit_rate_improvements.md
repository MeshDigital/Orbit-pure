---
status: Completed
date: 2026-06-16
scope: Download orchestrator hit rate and stability improvements
---

# Download Orchestrator Hit Rate Improvements

## Context

The download orchestrator was finding Soulseek matches "once in a blue moon." A Belgian folk track ("De Strangers - Antwarpe ♥") showed 0 matched / 0 queued / 0 filtered after a 45s discovery timeout. Investigation identified four independent reasons hits were being missed and retry cycles were too slow.

## Changes Delivered

### 1. SafetyFilterService.cs — FLAC bitrate floor lowered (700 → 400 kbps)

**Problem:** The lossless gate at line 122 rejected any FLAC with reported bitrate ≤ 700 kbps. Real 16-bit/44.1kHz FLACs of acoustic, folk, and quieter music routinely compress to 400–650 kbps. These were being silently dropped with "Bitrate Too Low" before scoring, so the discovery result showed 0 candidates even when peers had the file.

**Change:** Floor lowered to 400 kbps. This still catches MP3-to-FLAC transcodes (they report ~320 kbps) and other low-bitrate fakes, while accepting the full range of real lossless files.

### 2. DownloadManager.cs — In-session retry delay 20 min → 3 min

**Problem:** After a no-match result, the track was placed back in the queue with `NextRetryTime = now + 20 minutes ± 5 minutes jitter`. With `MaxSearchAttempts = 3`, a track spent up to 60 minutes cycling through in-session retries before escalating. This made it nearly impossible to observe improvement in a reasonable window.

**Change:**
- Base delay: 20 min → 3 min
- Jitter: ±300 s → ±60 s (prevents negative delays at the new shorter base)
- Log message updated to match new delay

### 3. AutoCleanerService.cs — Diacritic stripping in Aggressive tier

**Problem:** The Aggressive tier used `StripMusicalIdentity(Smart)` but left accented characters intact (é, ö, ü, ñ etc.). `ProtocolHardeningService.DangerousQueryChars` (`[^\w\s\-\.\']`) keeps these because .NET's `\w` matches Unicode word chars. However, many Soulseek peers store files with ASCII filenames — "Jose" instead of "José", "Blur" instead of "Blür" — so the accented query fails to match the ASCII filename.

**Change:** `StripDiacritics()` helper added using Unicode NFD decomposition + NonSpacingMark category filter. Applied to the Aggressive tier output: `StripDiacritics(StripMusicalIdentity(Smart))`. Dirty and Smart tiers are left unchanged so identity-precise searches still fire first.

### 4. DownloadDiscoveryService.cs — MP3 fallback uses Aggressive tier

**Problem:** The integrated MP3 fallback (lines 213–238, fires after all lossless tiers fail) was using `tiers.Smart` as its query. This duplicates the MP3 hedge that already fires alongside the Dirty FLAC search at i==0 — the same Smart query had already been tried against the MP3 index. The fallback added no new search signal.

**Change:** `fallbackRawQuery = tiers.Aggressive` instead of `tiers.Smart`. After all lossless tiers (Dirty FLAC, Smart FLAC, Aggressive FLAC) fail and the Smart MP3 hedge has already run, the fallback now tries the diacritic-stripped Aggressive query against the MP3 index — a fresh vector not yet attempted.

## Search Flow After These Changes

```
i=0  Dirty FLAC  +  Smart MP3 hedge (parallel race)
i=1  Smart FLAC
i=2  Aggressive FLAC
     Aggressive MP3 fallback  ← now uses Aggressive instead of Smart (was duplicate)
```

## Stability Fixes (same session, earlier in conversation)

### App.axaml.cs — Soulseek noise suppression

`IsTransientSoulseekRootCause` now filters three previously-missed patterns:
- `ObjectDisposedException` from `System.Net.Sockets.Socket` where stack trace contains `Soulseek.Network.Tcp.Listener` or `ListenContinuouslyAsync` — the socket is disposed during reconnect but the listener loop fires one more read
- `ObjectDisposedException` where `ObjectName == "Connection"` (SoulseekDotNet internal connection objects)
- Any exception whose type namespace starts with `"Soulseek"` (catch-all for library internals)
- Messages containing `"reported as failed by remote client"` (`TransferReportedFailedException`)

### DownloadManager.cs — Fire-and-forget exception observation

`_ = StartAsync()` and `_ = PauseAllAsync()` were fire-and-forget with no exception observation. Both now attach `.ContinueWith(t => logger.LogError/LogWarning(...), TaskContinuationOptions.OnlyOnFaulted)` so failures surface in the log instead of becoming unobserved task exceptions.

### Downloads Hub UI

- "QUEUE BY PLAYLIST" section added to Downloads Hub tab, showing `ActiveGroups` via `DownloadGroupTemplate`
- "ON DECK" expander in Current Session tab starts expanded (`IsExpanded="True"`)
- `DownloadGroupViewModel._isExpanded` defaults to `false` so group track lists start collapsed
