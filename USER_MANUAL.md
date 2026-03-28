# ORBIT Pure — User Manual

> **Legal & Privacy Notice**  
> ORBIT connects to the Soulseek P2P network. Your IP address may be visible to other peers. Always use a VPN. Only download and share content you have the legal right to use.

---

## Table of Contents

1. [What is ORBIT Pure?](#what-is-orbit-pure)
2. [First-Run Setup](#first-run-setup)
3. [Connecting to Soulseek](#connecting-to-soulseek)
4. [Searching for Music](#searching-for-music)
   - [Search Lanes Explained](#search-lanes-explained)
   - [Why Is My Result Hidden?](#why-is-my-result-hidden)
   - [Relaxing Filters Without Re-Searching](#relaxing-filters-without-re-searching)
5. [Browse a User's Collection](#browse-a-users-collection)
6. [Download Center](#download-center)
   - [Track State Machine](#track-state-machine)
   - [Manual Override — Force a Specific File](#manual-override--force-a-specific-file)
7. [Library & Quality Rings](#library--quality-rings)
8. [Forensic Quality Controls](#forensic-quality-controls)
9. [Settings Reference](#settings-reference)
10. [Troubleshooting](#troubleshooting)

---

## What is ORBIT Pure?

ORBIT Pure is a Soulseek client built for people who care deeply about audio quality. Unlike general-purpose clients, every search result comes with an **explicit explanation** of why it ranked where it did and why lower-quality alternatives were hidden.

**Key differences from other Soulseek clients:**

| Feature | Other clients | ORBIT Pure |
|:--------|:-------------|:-----------|
| Result filtering | Silent rejection | Explicit reason shown per result |
| Quality gating | Manual inspection | Automated forensic gate + spectral analysis |
| Download orchestration | First-result-wins | Multi-lane discovery, fast-lane short-circuit |
| Download recovery | None | Crash-aware state recovery on restart |
| Peer browsing | Flat file list | Folder-tree browser with suspicious-lossless warnings |

---

## First-Run Setup

### Prerequisites

| Dependency | Required? | Notes |
|:-----------|:---------|:------|
| **FFmpeg** | Required for audio analysis | Install via `winget install ffmpeg` (Windows), `brew install ffmpeg` (macOS), or your Linux package manager |
| **Soulseek account** | Required | Create one free at [slsknet.org](https://www.slsknet.org) |
| **.NET 9 Runtime** | Required | Bundled with ORBIT installer; or download from [dotnet.microsoft.com](https://dotnet.microsoft.com) |

### FFmpeg Installation

ORBIT checks for FFmpeg at startup and shows a warning banner if it cannot find it. Without FFmpeg, audio analysis, spectral forensics, and format transcoding are disabled; search and download still work normally.

**Windows (recommended):**
```
winget install --id Gyan.FFmpeg
```
Then restart ORBIT.

**macOS:**
```
brew install ffmpeg
```

**Ubuntu/Debian:**
```
sudo apt install ffmpeg
```

After installing FFmpeg, go to **Settings → Dependencies** and click **Re-check** to verify detection.

---

## Connecting to Soulseek

1. Open **Settings → Connection**.
2. Enter your Soulseek **Username** and **Password**.
3. Optionally enable **Auto-connect on startup**.
4. Click **Connect**.

The status bar shows:
- 🟢 **Connected** — logged in and ready.
- 🟡 **Connecting** — handshake in progress.
- 🔴 **Disconnected** — check your credentials and network.

ORBIT automatically reconnects after brief network interruptions. For persistent failures, check the Soulseek service status or try a different port under **Settings → Connection → Advanced**.

---

## Searching for Music

Type an artist and title in the search bar and press **Enter** (or click **Search**).

ORBIT runs searches across three quality tiers in parallel:

### Search Lanes Explained

| Lane | Criteria | When it wins |
|:-----|:---------|:------------|
| **Strict** | Lossless only (FLAC/WAV/ALAC), ≥ 700 kbps, trusted peers with short queues | Always preferred if a qualifying result is found |
| **Standard** | High-quality lossy (MP3 320 kbps, AAC, Ogg Vorbis ≥ 256 kbps) | Wins when no Strict result is found |
| **Desperate** | Any format at ≥ 128 kbps that passes the safety gate | Last resort; labeled clearly in UI |

When any lane finds a result that passes all quality gates, it is promoted to the **Download Queue** automatically. Other lanes are stopped (fast-lane short-circuit).

### Why Is My Result Hidden?

Click the **Show Hidden** toggle below the search results to see every peer that responded, including filtered-out files. Each hidden result shows one of the following reasons:

| Reason | Meaning |
|:-------|:--------|
| **Bitrate floor** | File bitrate is below the minimum for this search lane |
| **Format gate** | File format is not accepted in this lane (e.g., MP3 in Strict lane) |
| **Safety / bouncer** | Forensic gate suspects the file is upscaled or fake-lossless |
| **Queue depth** | Peer queue is too long relative to their upload speed |
| **Peer reliability** | Peer has a history of failed/cancelled transfers |
| **Duplicate** | Another peer already has an identical file queued |

### Relaxing Filters Without Re-Searching

To include a hidden result:
1. Click **Show Hidden**.
2. Find the result you want.
3. Click **Relax filter** or **Force download from this peer** on the row.

This uses the already-retrieved result set — no new network search is issued.

---

## Browse a User's Collection

From any search result row, right-click a username and choose **Explore User Collection**. This opens a folder-tree browser showing all files that user shares publicly.

### Browser Features

- **Folder tree** — navigate folders just like a local file browser.
- **Music Only** toggle — hide non-audio files instantly.
- **Filter box** — type any text to narrow results by filename, artist, title, album, or username.
- **Sort by** — sort the visible tree by **Name**, **Format**, or **Bitrate** (highest first).
- **Suspicious lossless warning** — files flagged with a ⚠️ are FLAC/WAV files whose bitrate suggests they may be transcoded from a lossy source.

### Queuing Files

- Select a **folder** and click **Queue Selected** to queue all music files in that folder.
- Click **Queue All** to queue every music file visible in the current filtered view.

---

## Download Center

The Download Center is the central view for all active and pending downloads. Each track row shows:

- **State indicator** — current phase (Searching, Queuing, Downloading, Verifying, Enriching, Completed, Failed).
- **Peer live feed** — real-time list of peers responding to the discovery search for that track.
- **Progress bar** — download bytes transferred vs. file size.
- **Speed** — current transfer rate in KB/s.

### Track State Machine

```
Queued → Searching → (Strict / Standard / Desperate discovery)
       → Downloading → Verifying → Enriching → Completed
                    ↘ Failed (with retry or manual override)
```

ORBIT automatically retries on transient failures (network drop, queue full). Permanent failures (banned peer, format mismatch) are marked Failed with an actionable suggestion.

### Manual Override — Force a Specific File

If ORBIT selected a peer you don't want:

1. Expand the track row to see the **Peer Live Feed**.
2. Find the peer/file you prefer.
3. Click **Force this file** on that row.

ORBIT will cancel the in-progress transfer (if any) and start downloading from the peer you chose.

---

## Library & Quality Rings

The Library view shows all tracks imported into ORBIT, organized by artist and album.

Each track has a **Quality Ring** indicator:

| Ring colour | Meaning |
|:-----------|:--------|
| 🟢 Green | High-fidelity lossless, verified clean |
| 🟡 Yellow | High-quality lossy (320 kbps+) |
| 🟠 Orange | Acceptable quality (128–256 kbps) |
| 🔴 Red | Below minimum quality or flagged suspect |
| ⚫ Grey | Analysis not yet run |

Hover any ring to see the full diagnostic breakdown (bitrate, sample rate, bit depth, forensic score).

---

## Forensic Quality Controls

ORBIT applies an automatic **safety gate** to every file before it enters your library:

1. **Format check** — ensures the declared format matches the file's actual content.
2. **Bitrate plausibility** — FLAC files with suspiciously low bitrates (< 400 kbps) are flagged as possible MP3-to-FLAC re-encodes.
3. **Spectral analysis** (requires FFmpeg) — detects high-frequency cutoffs characteristic of lossy encoding hidden inside lossless containers.
4. **Essentia audio features** (optional) — extracts BPM, key, energy, and other DJ-relevant metadata automatically after download.

Files that fail the safety gate are held in **Quarantine** and not added to the library until you review and approve them manually.

---

## Settings Reference

| Section | Key Setting | Notes |
|:--------|:-----------|:------|
| **Connection** | Username / Password | Soulseek credentials |
| **Connection** | Port | Default: 2234. Change if your ISP blocks it. |
| **Connection** | Auto-connect | Reconnects on startup |
| **Download** | Download directory | Where files are saved |
| **Download** | Name format | Template for filenames, e.g. `{artist} - {title}` |
| **Quality** | Strict minimum bitrate | Default: 700 kbps for lossless lane |
| **Quality** | Standard minimum bitrate | Default: 256 kbps |
| **Quality** | Desperate minimum bitrate | Default: 128 kbps |
| **Quality** | Fake-lossless rejection | Enable/disable the spectral safety gate |
| **Dependencies** | FFmpeg / Essentia | Shows detected versions; **Re-check** button |
| **Library** | Upgrade Scout | Background search for better-quality copies of existing tracks |

---

## Troubleshooting

### ORBIT won't connect

- Verify your Soulseek username and password at [slsknet.org](https://www.slsknet.org).
- Check that port 2234 (or your custom port) is not blocked by your firewall or ISP.
- Try enabling a VPN — some ISPs throttle or block P2P connections.

### FFmpeg not detected

- Install FFmpeg using the instructions in the [First-Run Setup](#first-run-setup) section.
- Ensure FFmpeg is on your system `PATH` after installation.
- Go to **Settings → Dependencies → Re-check** after installing.

### Download stuck in "Searching"

- The track may have no results in the current quality tier. Try switching to a lower lane in **Settings → Quality**.
- If the issue persists, open **Download Center**, expand the track row, and check the **Peer Live Feed** for status messages.

### Fake-lossless file rejected

- ORBIT's spectral gate detected that the FLAC/WAV file is likely a re-encode of a lossy file.
- Go to **Library → Quarantine**, inspect the file's spectral graph, and decide whether to approve it manually.
- If you trust the source, you can whitelist the peer in **Settings → Safety**.

### Search returns no results

- Broaden your search query (fewer words, no special characters).
- Check that you are connected (green status indicator).
- Try the Desperate lane by temporarily lowering the bitrate minimum in **Settings → Quality**.

---

*ORBIT Pure is open-source software distributed under its project license. Use responsibly and respect the Soulseek community guidelines.*
