# The Gravity Well: Metadata Persistence & DJ Compatibility

**ORBIT** doesn't just download files; it enriches them with "Musical Intelligence" and seals that data into the file itself. This ensures that your library is portable, professional, and "Self-Healing."

## Feature: DJ-Ready Tagging (Phase 0.5)

### The Problem: "Dumb Files"
Standard downloads usually only contain basic tags: Artist, Title, Album.
Professional DJs need more: **Key**, **BPM**, and **IDs**. Without these, you have to re-analyze every track in Rekordbox or Serato, and you lose the "Sonic Identity" if you move the file.

### The Solution: The Gravity Well
ORBIT fetches rich metadata from Spotify (Phase 0.2) and now **persists** it permanently in two places:

1.  **The Database (Local Library)**
    *   Stores `MusicalKey` (e.g., "8A"), `BPM` (e.g., 128.0), and `AnalysisOffset`.
    *   Enables advanced sorting and "Smart Playlists" within the app.

2.  **The Physical File (ID3/Vorbis Tags)**
    *   Writes industry-standard tags that DJ software can read *instantly*.

### Supported Tags

| Data Point | ID3 Frame (MP3) | Vorbis Comment (FLAC) | Purpose |
| :--- | :--- | :--- | :--- |
| **Initial Key** | `TKEY` | `INITIALKEY` | Harmonic Mixing (Camelot Notation supported) |
| **BPM** | `TBPM` | `BPM` | Beatmatching / Tempo Sync |
| **Spotify ID** | `TXXX:SPOTIFY_TRACK_ID` | `SPOTIFY_TRACK_ID` | **Self-Healing Anchor** (Allows auto-upgrades) |

### Technical Implementation

The `MetadataTaggerService` handles the standardization:
```csharp
// Example: Writing DJ Tags
file.Tag.InitialKey = "8A";       // TKEY
file.Tag.BeatsPerMinute = 128;    // TBPM
```

The `MetadataEnrichmentOrchestrator` ensures this happens automatically after every download, "sealing" the intelligence into the file before it even hits your library.

### Verification
To verify, open a downloaded file in **Mp3tag** or import it into **Rekordbox**. You should see the Key and BPM pre-populated without any analysis required.
