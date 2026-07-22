# ORBIT

ORBIT is a music library and DJ prep tool. It finds tracks on Soulseek, checks that what it downloads is actually the right file at the quality it claims to be, and analyzes everything it collects (BPM, key, energy, phrase structure, cue points) so you can build sets without needing Mixed In Key or a separate prep step in Rekordbox.

Think of it as: search + downloads + a music library + a lightweight DJ workstation, in one app.

> **Before you use it:** ORBIT connects to the Soulseek P2P network, where your IP address is visible to other peers. Use a VPN if that matters to you, and only download or share what you're legally allowed to.

---

## What it actually does

- **Search & download** — searches Soulseek, filters out fake/mislabeled files (wrong bitrate, wrong duration, upscaled lossy-to-"lossless" fakes) before they ever hit your library, and shows you *why* a result was rejected instead of just hiding it.
- **A real library** — playlists organized into folders, ratings, duplicate detection, drag-and-drop between playlists, sortable columns.
- **Playback** — gapless/crossfade transitions between tracks, pitch control.
- **DJ prep (Workstation)** — waveform view, hot cues, loops, energy/phrase analysis, harmonic (Camelot key) matching for finding compatible tracks, and export to Rekordbox XML.
- **Smart playlists** — build playlists from BPM/energy/mood criteria, or ask it to find tracks similar to one you already have.

It's under active development, so expect some rough edges — but the core loop (search → download → verify → play/prep) works end to end.

---

## Getting started

You'll need [.NET 9 SDK](https://dotnet.microsoft.com/download) and [FFmpeg](https://ffmpeg.org/download.html) on your PATH.

```bash
git clone https://github.com/MeshDigital/Orbit-pure.git
cd Orbit-pure
dotnet restore
dotnet build SLSKDONET.sln -c Debug
dotnet run
```

On first launch, add your Soulseek account details in Settings.

Optional: the Settings page can install an additional AI phrase-detection engine for cue placement. It's off by default and only installs anything (Conda, plus its own environment) after you explicitly confirm.

---

## Built with

- **.NET 9 / C#**, UI in **Avalonia**
- **SQLite** (via EF Core) for the local library
- **Soulseek.NET** for the P2P network connection
- **FFmpeg / NAudio** for audio playback and analysis

---

## Project layout

- `Views/Avalonia` — the UI (pages, controls)
- `ViewModels` — screen logic
- `Services` — search, downloads, library, analysis, and everything else behind the scenes
- `Data`, `Migrations` — the local database
- `Tests` — the test suite
- `DOCS/` — deeper technical write-ups, for anyone poking around the internals

---

## Contributing

Contributions are welcome — especially anything that makes search results more trustworthy, downloads more reliable, or the UI less cluttered. Try to keep changes focused and tested.

License: GPL-3.0
