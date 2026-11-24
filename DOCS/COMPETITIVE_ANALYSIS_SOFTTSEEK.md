# Competitive analysis — SoFTTSeek vs. ORBIT Pure

**Date:** 2026-11-24
**Source:** https://www.softtseek.com/ — a single-page site with `/`, `/privacy`, `/terms`
(no `/changelog`, `/download`, `/blog`, or `sitemap.xml`; the FAQ is inline on the homepage).

SoFTTSeek is a commercial-ish, invite-gated Soulseek client (macOS + Windows) that positions
itself as *"The Soulseek client you've been waiting for."* ORBIT Pure is an open-source,
Windows-first Soulseek client + DJ-prep workstation. The two products only partially overlap:
**SoFTTSeek wins on breadth, polish and distribution; ORBIT wins on depth (audio integrity +
DJ prep).** This note lists what ORBIT can learn, what it already does better, and concrete
next steps.

---

## 1. The headline

| Axis | SoFTTSeek | ORBIT Pure |
|---|---|---|
| Platform | macOS 12+ (AS + Intel) **and** Windows 10+ | Windows x64 only (Linux/macOS experimental, playback broken — `DOCS/platform/MACOS_FEASIBILITY.md`) |
| Media | Audio **and video** (subtitles, video library) | Audio only (video export intentionally removed) |
| Streaming sync | Spotify **and YouTube** (OAuth connect, auto-search Soulseek) | Spotify import + metadata (PKCE); **no YouTube import** (only chapter/timestamp export) |
| Visualizer | 246+ **MilkDrop** presets | 12 custom SkiaSharp presets |
| Rekordbox | Bidirectional "keep in sync" loop | Deep one-way export (merge-mode) + read-only `RekordboxBridge` cue reader |
| Audio forensics | Not advertised | FFT fake-lossless detection, spectral forensics, quarantine (killer feature) |
| DJ prep (BPM/key/energy/cues/stems/mixing) | Not advertised | Core strength (Flow, Flow Builder, Cue Forge, Edit Lab, ONNX genre/mood) |
| Distribution model | Invite / referral-code gated | Open source, GPL-3.0, free |
| Web presence | Polished landing page, screenshots, FAQ, privacy/terms | GitHub README only |

---

## 2. What ORBIT can learn (the real gaps)

### 2.1 Distribution: macOS is the single biggest market lever
SoFTTSeek ships macOS as a first-class platform; DJs skew heavily toward macOS. ORBIT's own
`DOCS/platform/MACOS_FEASIBILITY.md` (2026-11-14) already proves the code **compiles** for
`osx-x64`/`linux-x64` and **starts** under smoke tests. The blockers are runtime, not
architecture:
- `IAudioOutputBackend` (NAudio WASAPI/ASIO → miniaudio/CoreAudio) — audio playback can't work off-Windows today.
- `IAudioDecoder` (Media Foundation → FFmpeg) for FLAC/M4A/AIFF decode + `ExactSeek`.
- `Microsoft.ML` 3.0.1 blocks `osx-arm64`; needs an upgrade for Apple Silicon.
- `.app`/`.dmg` + notarisation (US$99/yr Apple Developer).

Estimated 6–9 weeks. **This is the highest-leverage item on the list** — before any new feature.

### 2.2 YouTube playlist/liked-videos import
SoFTTSeek's "Auto-sync Spotify & YouTube playlists" is its #1 hero feature. ORBIT already has the
exact architecture to slot this in: the `IStreamingImportProvider` pattern used by
`SpotifyImportProvider` (paginate → preview → commit → background enrich). YouTube needs:
- OAuth for playlists + liked videos (YT Data API v3).
- A `YouTubeImportProvider` implementing the same streaming interface.
- Reuse the existing "match against library by hash → Spotify id → fuzzy artist|title" resolution.

Moderate effort, high user value, and it rounds out the "connect your streaming account" story.

### 2.3 MilkDrop-compatible visualizers
ORBIT's visualizer is a real, well-engineered FFT pipeline (log-band spectrum, auto-gain, reference-counted
gating) but only 12 custom presets. SoFTTSeek's "246+ MilkDrop presets" taps the massive Winamp MilkDrop
ecosystem (thousands of free `.milk` presets). The clean path is **libprojectM / projectM**, an open-source
MilkDrop reimplementation — feed ORBIT's existing mono-mix spectrum bands into it and get hundreds of
community presets for near-zero art cost. This is a large "wow" upgrade for little effort, and it pairs
naturally with the existing fullscreen theater mode.

### 2.4 Album-mode search results
SoFTTSeek advertises "album mode" plus format filters and bitrate controls. ORBIT's multi-lane search and
quality gates are deeper, but the manual shows no album grouping. People search by album constantly;
grouping matching results under their album (and collapsing duplicates across peers) is a natural fit for
ORBIT's existing duplicate-aware search results.

### 2.5 Marketing / web presence — the cheapest big win
SoFTTSeek's site is a template ORBIT could (and should) copy outright:

1. **Hero + one crisp line.** SoFTTSeek: *"The Soulseek client you've been waiting for."* ORBIT's
   README opens with three dense technical paragraphs. ORBIT's actual differentiator is *stronger*
   ("downloads that are provably the real file, and a full DJ-prep workstation") — lead with it.
2. **Screenshot gallery.** SoFTTSeek shows 7 screenshots (search, library, videos, visualizer,
   spotify-sync, playlist, downloads). ORBIT ships real UI but has no gallery — only a banner PNG in
   `orbit-work/assets/`. A static page (GitHub Pages is free) with 6–8 screenshots would close most of the gap.
3. **FAQ.** SoFTTSeek answers the obvious questions (what is Soulseek, is it free, platforms, Rekordbox).
   ORBIT has a whole `USER_MANUAL.md` to draw from; surface the top 6–8 Qs.
4. **Privacy + Terms pages.** SoFTTSeek has both, emphasizing local storage, keychain, "no servers."
   ORBIT already *behaves* this way ("no telemetry, local operation") but has no page that says so.
   Table stakes for trust, and trivial to add.
5. **Prominent dual download buttons + platform badges.** SoFTTSeek puts a direct `.dmg`/`.exe` above
   the fold; ORBIT buries a GitHub-release link in the README.

### 2.6 Invite/referral model — note, don't copy
SoFTTSeek is referral-code gated ("Access requires a valid referral code"), which manufactures scarcity
and a curated community. ORBIT is open-source and free — that's a *differentiator*, not a weakness, and
gating would conflict with the GPL ethos. Worth understanding (it also functions as a mild
anti-abuse/anti-piracy control), but the recommendation is to **keep ORBIT open** and market that
contrast explicitly.

### 2.7 Overlay network — observe, don't rebuild
SoFTTSeek runs its own P2P overlay (cryptographic peer IDs = public keys, bootstrap server, publish files)
on top of / alongside Soulseek. That's a large architectural bet and likely its community/anti-abuse
moat. ORBIT relies purely on Soulseek. Not a near-term ORBIT project; just flag it as the thing to watch
in their direction.

---

## 3. Where ORBIT already wins (don't chase parity here)

- **Audio integrity** — real FFT fake-lossless/transcode detection, spectral forensics, quarantine,
  per-result rejection reasons. SoFTTSeek advertises none of this.
- **DJ prep depth** — BPM/key/energy/phrase/cue analysis, ONNX genre+mood, stem separation, harmonic
  matching, Flow Mode, Flow Builder, Cue Forge, Edit Lab. None of it appears on the SoFTTSeek site.
- **Download orchestration** — multi-lane discovery, fast-lane short-circuit, crash-aware recovery,
  per-peer live feed, manual "force this file" override.
- **Rekordbox fidelity** — merge-mode re-export that preserves edits made inside Rekordbox, plus the
  read-only `RekordboxBridge` cue reader. SoFTTSeek's "sync" is broader but shallower.
- **Honest UX** — every rejection, failure and half-analysed state is explained rather than hidden.
- **Open, no telemetry, no gate.**

**Positioning takeaway:** ORBIT should not try to be "a Soulseek client with video." It should own the
line *"the Soulseek client for people who care about what they're actually downloading — and for DJs who
want it prepped before it hits Rekordbox."* SoFTTSeek is the easy, broad consumer client; ORBIT is the
serious, forensic pro tool. The two can coexist; ORBIT's job is to make the depth legible at a glance.

---

## 4. Prioritized next steps

| # | Item | Effort | Impact |
|---|---|---|---|
| 1 | Landing page (hero, screenshots, FAQ, privacy/terms, clear download) | Low | High (visibility/trust) |
| 2 | macOS/Linux port via the seams already specced in `MACOS_FEASIBILITY.md` | 6–9 wks | Highest (market) |
| 3 | YouTube playlist/liked-videos import (reuse `IStreamingImportProvider`) | Medium | High |
| 4 | MilkDrop presets via projectM | Medium | Medium-High (appeal) |
| 5 | Album-mode search grouping | Low-Medium | Medium |
| 6 | Surface the existing "vs other clients" table on the website | Trivial | Medium |
