# ORBIT Import Sources Roadmap (#425)

## Input-Shape Model (inspired by TuneMyMusic)

Instead of chasing fragile per-service private APIs, ORBIT models imports around **four input shapes** so every music platform is supported today (either natively, via URL, or via standard file export):

1. **Connect account (OAuth)** — full playlist & liked-songs sync.
2. **Paste URL** — zero-config public/unlisted playlist import with re-sync support.
3. **Upload / drop file** — `.csv`, `.m3u`, `.m3u8`, `.txt`, `.json` (works with TuneMyMusic, Soundiiz, Exportify, Rekordbox, iTunes/Apple Music exports, Excel).
4. **Paste free text** — one track per line, timestamps, YouTube comments, 1001Tracklists.

## Build-vs-Buy Matrix by Service

| Service | Strategy | Status | Notes / API Reality |
|---|---|---|---|
| **Spotify** | Native OAuth + URL + public scraper | **Shipped** | `SpotifyImportProvider` & `SpotifyLikedSongsImportProvider`. |
| **YouTube / YouTube Music** | Zero-config public URL + Data API v3 pagination + OAuth Liked Videos | **Shipped (#424)** | `YouTubeImportProvider` with title normalization & Recent Imports re-sync. |
| **SoundCloud** | Best-effort public playlist URL (`__sc_hydration`) + file fallback | **Shipped (#425)** | Official public API registration closed since 2021; `SoundCloudImportProvider` reads public `/sets/` URLs without an API key and falls back cleanly to file/text import if blocked. |
| **M3U / M3U8 / CSV / TXT / JSON** | Universal local file import | **Shipped (#425)** | `PlaylistFileImportProvider` + `CsvImportProvider` cover Rekordbox, Traktor, Serato, TuneMyMusic, Soundiiz, Exportify, and Apple Music exports. |
| **Apple Music** | File export / TuneMyMusic → ORBIT | **Supported via file** | MusicKit requires paid Apple Developer token ($99/yr) + JS/Apple-platform auth; exporting M3U/CSV/TXT via TuneMyMusic or Music.app works immediately. |
| **TIDAL** | File export / TuneMyMusic → ORBIT | **Supported via file** | Commercial partner API access required for full playlist reads; TuneMyMusic CSV/JSON export drops straight into ORBIT. |
| **Deezer** | File export / TuneMyMusic → ORBIT | **Supported via file** | OAuth app registration restricted; TuneMyMusic/Soundiiz export drops straight into ORBIT. |
| **Amazon Music** | File export / TuneMyMusic → ORBIT | **Supported via file** | No public playlist API; export via TuneMyMusic/Soundiiz and drop the file into ORBIT. |

## Migration Flow (TuneMyMusic / Soundiiz → ORBIT)

For services without open public APIs (Apple Music, TIDAL, Deezer, Amazon Music), users export any playlist to `.csv`, `.txt`, `.m3u8`, or `.json` using TuneMyMusic or Soundiiz and drop the file directly onto ORBIT's **Import → Playlist or CSV file** drop zone.
