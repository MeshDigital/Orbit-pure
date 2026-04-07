# Orbit-Pure — Architectural Deep-Dive

> **Date:** April 7, 2026  
> **Branch:** `master`  
> **Build:** 661 / 661 tests green  
> **Stack:** .NET 9 · Avalonia 11 · ReactiveUI · SkiaSharp · NAudio 2.2 · EF Core / SQLite · ONNX Runtime DirectML

This document is the authoritative architectural reference for every major subsystem in
Orbit-Pure. Each section covers responsibility, data flow, key types, concurrency model,
extension points, and known trade-offs.

---

## Table of Contents

1. [High-Level Architecture](#1-high-level-architecture)
2. [Data Layer](#2-data-layer)
3. [Audio Analysis Pipeline](#3-audio-analysis-pipeline)
4. [DJ Deck Engine](#4-dj-deck-engine)
5. [Stem Separation System](#5-stem-separation-system)
6. [Similarity & Playlist Intelligence](#6-similarity--playlist-intelligence)
7. [Timeline DAW Editor](#7-timeline-daw-editor)
8. [Video Export Pipeline](#8-video-export-pipeline)
9. [DJ Platform Integrations](#9-dj-platform-integrations)
10. [Library & Smart Playlists](#10-library--smart-playlists)
11. [External Services (Spotify · MusicBrainz)](#11-external-services-spotify--musicbrainz)
12. [Search Engine](#12-search-engine)
13. [Download System](#13-download-system)
14. [Background Jobs & Performance](#14-background-jobs--performance)
15. [UI Architecture (MVVM + ReactiveUI)](#15-ui-architecture-mvvm--reactiveui)
16. [Cross-Cutting Concerns](#16-cross-cutting-concerns)

---

## 1. High-Level Architecture

```
┌──────────────────────────────────────────────────────────────────────┐
│                         Avalonia UI (XAML)                           │
│  LibraryPage · DeckView · TimelineView · VideoExportView · Settings  │
└────────────────────────────┬─────────────────────────────────────────┘
                             │  ReactiveUI bindings / MediatR events
┌────────────────────────────▼─────────────────────────────────────────┐
│                        ViewModel Layer                                │
│  LibraryViewModel · DeckViewModel · TimelineViewModel · StemMixerVM  │
└──────┬──────────────┬──────────────┬──────────────┬──────────────────┘
       │              │              │              │
┌──────▼──────┐ ┌─────▼──────┐ ┌────▼──────┐ ┌────▼────────────────┐
│  Audio Deck │ │  Analysis  │ │ Timeline  │ │  Library / Search   │
│  Engine     │ │  Pipeline  │ │  DAW      │ │  Playlist / Export  │
└──────┬──────┘ └─────┬──────┘ └────┬──────┘ └────┬────────────────┘
       │              │              │              │
┌──────▼──────────────▼──────────────▼──────────────▼────────────────┐
│                    EF Core / SQLite  (AppDbContext)                  │
│  Tracks · AudioAnalysis · CuePoints · Playlists · StyleDefinitions  │
└─────────────────────────────────────────────────────────────────────┘
```

**Dependency injection** is used throughout. `App.axaml.cs` bootstraps the DI container
(Microsoft.Extensions.DI). All services are registered as singletons or scoped factory
(`IDbContextFactory<AppDbContext>`). ViewModels are resolved via the navigation service.

**Event bus** (`EventBusService`) is a thin pub/sub layer used for cross-ViewModel
communication (e.g., `TrackAnalysisCompletedEvent`, `LibraryFoldersChangedEvent`). It is
not a replacement for direct service calls — use it only for fire-and-forget notifications
where no response is needed.

---

## 2. Data Layer

### Files
| File | Purpose |
|---|---|
| `Data/AppDbContext.cs` | EF Core DbContext — all 20+ DbSets |
| `Data/Entities/` | Entity classes (one per DB table) |
| `Data/Enums.cs` | Shared enum types used in entities |
| `Migrations/` | EF Core schema migrations |

### Key Entities

```
TrackEntity             — core file metadata (path, hash, format, bitrate)
LibraryEntryEntity      — enriched library row (BPM, key, energy, genre, subgenre)
AudioAnalysisEntity     — raw analysis results + 512-dim Essentia embedding BLOB
AudioFeaturesEntity     — Spotify/computed features (danceability, valence, energy)
CuePointEntity          — hot cues and memory cues per track (TrackUniqueHash FK)
PlaylistJobEntity       — project / playlist container
PlaylistTrackEntity     — playlist membership + position + track metadata snapshot
StyleDefinitionEntity   — user-defined ML style labels (ML.NET Style Lab)
SmartCrateDefinitionEntity — saved Smart Crate filter rules
TrackPhraseEntity       — Rekordbox-parsed chorus/bridge phrase segments
TrackTechnicalEntity    — heavy analysis data (waveform preview, ANLZ data)
```

### Identity Strategy

Every track is identified by a **SHA-256 content hash** (`TrackUniqueHash`).  
Path-based lookups are secondary — the hash survives file moves and renames.  
All cross-table foreign keys reference `TrackUniqueHash`, not a surrogate integer key.

### Concurrency

The app uses `IDbContextFactory<AppDbContext>` everywhere — each service creates a
short-lived `await using var db = await _dbFactory.CreateDbContextAsync()` scope.
This prevents context-sharing across async boundaries and avoids the EF Core
"second operation started" exception.

---

## 3. Audio Analysis Pipeline

### Responsibility
Decode any audio format → extract BPM, key, energy, waveform, cue suggestions →
persist to `AudioAnalysisEntity` + `AudioFeaturesEntity`.

### Files
```
Services/AudioAnalysis/
  AudioIngestionPipeline.cs     FFmpeg decode → normalised 44.1 kHz stereo WAV
  BpmDetectionService.cs        Essentia BeatTrackerMultiFeature wrapper
  KeyDetectionService.cs        Essentia KeyExtractor (HPCP) → Camelot wheel mapping
  EnergyScoringService.cs       RMS + dynamic complexity → 1–10 energy score
  WaveformExtractionService.cs  FFT-based per-band waveform data for visualisation
  BeatgridDetectionService.cs   Beat-grid anchor + phase calculation
  CuePointDetectionService.cs   Auto-cue suggestion from energy/onset events
  TrackAudioSource.cs           Input descriptor  (path + optional stem override)
Services/AnalysisQueueService.cs  Priority work queue + Mission Control integration
```

### Data Flow

```
[Track file]
    │
    ▼
AudioIngestionPipeline.DecodeToTempWavAsync()
    │  FFmpeg: -ar 44100 -ac 2 -f wav → /tmp/orbit_XXXXXXXX.wav
    ▼
┌───────────────────────────────────────────┐
│  Parallel analysis tasks (Task.WhenAll)   │
│  ├─ BpmDetectionService                   │ → float bpm, float confidence
│  ├─ KeyDetectionService                   │ → "Am" → Camelot "8A"
│  ├─ EnergyScoringService                  │ → int 1–10
│  ├─ WaveformExtractionService             │ → float[] per-band magnitudes
│  └─ BeatgridDetectionService              │ → beat anchor + phase
└───────────────────────────────────────────┘
    │
    ▼
AudioAnalysisEntity  →  AppDbContext.SaveChangesAsync()
    │
    ▼
EventBusService.Publish(TrackAnalysisCompletedEvent)
    │
    ▼
DiscogsEffnetEmbeddingExtractor  →  AudioAnalysisEntity.EmbeddingBlob (512 floats)
    │
    ▼
SimilarityIndex.InvalidateAsync()  (TTL-based lazy rebuild on next query)
```

### Queue Architecture

`AnalysisQueueService` implements a **priority channel** (`Channel<AnalysisRequest>`).
Playlist tracks have priority 0 (front of queue); library background scan uses priority 1.
Parallelism is bounded by `SemaphoreSlim` with a count derived from `SystemInfoHelper.GetOptimalThreadCount()`:
- 4-core → 2 workers
- 8-core → 4 workers
- 16-core → 8 workers

The `MissionControlService` subscribes to per-thread progress events and drives the
Mission Control dashboard (`ActiveThreadInfo` grid + throughput display).

### Cancellation

Every analysis call accepts a `CancellationToken`. A linked token is created per-request
combining a 120-second timeout (configurable, up from 45 s) with the queue-level cancellation.
FFmpeg processes are killed via `Process.Kill()` on cancellation.

---

## 4. DJ Deck Engine

### Responsibility
CDJ-style playback state per deck: play/pause, cue, hot cues (8 slots), loops (standard +
loop-roll), beat jump, pitch (key-lock), tempo, sync.

### Files
```
Services/Audio/
  DeckEngine.cs            Core playback engine (NAudio sample provider chain)
  BpmSyncService.cs        Beat-match + phase alignment between two decks
  RateSampleProvider.cs    (inner class in DeckEngine) Linear-interp pitch/rate shift
  SmbPitchShiftingSampleProvider  Key-lock: tempo ≠ pitch (SMB phase vocoder)
  MasterBus.cs             Mixer: gain, EQ, crossfader, final output
  SnappingEngine.cs        Quantize-snap beat jumps to nearest grid position
  TransitionEngine.cs      Scheduled crossfade + effect automation
  TransitionPreviewPlayer.cs  Non-destructive A/B transition preview

ViewModels/DeckViewModel.cs     Binds DeckEngine to Avalonia deck UI
ViewModels/DeckSlotViewModel.cs Hot-cue slot state (label, color, position)
```

### NAudio Provider Chain

```
AudioFileReader (file decode, WaveFormat native)
    │
    ▼
[StereoToMonoSampleProvider]  (if mono source)
    │
    ▼
RateSampleProvider             Tempo control (linear interpolation resampler)
    │  PlaybackRate = 1 + TempoPercent/100
    ▼
[SmbPitchShiftingSampleProvider]  Key-lock only — compensates pitch back
    │  PitchFactor = 1 / PlaybackRate
    ▼
VolumeSampleProvider           Per-deck gain / slip-mode muting
    │
    ▼
MasterBus (WaveMixerStream32)  Sum all active decks + EQ + crossfader
    │
    ▼
WasapiOut (low-latency output)
```

### Pitch Range Modes
| Mode | Range | Use case |
|---|---|---|
| `PitchRange.Narrow` | ±8% | EDM precision mixing |
| `PitchRange.Medium` | ±16% | General purpose |
| `PitchRange.Wide` | ±50% | Large BPM bridging |

### Loop System

`LoopRegion` holds `InSeconds`, `OutSeconds`, `IsActive`, `IsRoll`, `RollEntrySeconds`.

| Method | Behaviour |
|---|---|
| `SetLoop(barLength)` | Sets In at current position, Out at In + barLength beats |
| `ExitLoop()` | Clears loop; loop-roll restores playhead to roll-entry position |
| `HalfLoop()` | Moves Out to midpoint of current loop |
| `DoubleLoop()` | Doubles loop length by extending Out |
| `MoveLoop(beats)` | Shifts both In and Out simultaneously |
| `ActivateLoopRoll(bars)` | Sets a loop and marks it as roll |

### BPM Sync

`BpmSyncService` is stateless. Beat-match:

```
masterEffectiveBpm = masterTrackBpm × (1 + master.TempoPercent / 100)
slave.TempoPercent = (masterEffectiveBpm / slaveTrackBpm − 1) × 100
```

Phase-align: rounds the slave playhead to the nearest beat boundary then offsets
by the master's current intra-beat phase. Produces sub-beat accuracy with a single
`DeckEngine.Seek()` call.

### Thread Safety
`DeckEngine` is manipulated only from the UI thread (ReactiveUI `WhenAnyValue` bindings).
The NAudio `Read()` method is called on the audio thread via `WasapiOut`. Thread safety
is achieved by making audio-thread-visible state (`PlaybackRate`, `PositionSeconds`)
`volatile` and using `Interlocked` for loop-boundary checks.

---

## 5. Stem Separation System

### Responsibility
Decompose a full mix into up to 5 stems (vocals, drums, bass, other, piano) using
ONNX Runtime with Spleeter / Demucs models. Cache stems on disk. Mix stems in real-time.

### Files
```
Services/Audio/Separation/
  IStemSeparator.cs          Interface: SeparateAsync → Dictionary<StemType, string>
  OnnxStemSeparator.cs       Spleeter-5stem ONNX via DirectML
  DemucsOnnxSeparator.cs     Demucs-4stem ONNX (vocals/drums/bass/other)
  CachedStemSeparator.cs     Decorator: SHA-256 cache key → skip if stems exist
  SpleeterCliSeparator.cs    Fallback: Spleeter Python CLI subprocess
  NeuralMixEqSampleProvider  NAudio sample provider for real-time stem EQ
  DSP/                       IIR filters used by NeuralMixEqSampleProvider

Services/Audio/
  StemCacheService.cs        Cache path resolution + model tag stamping
  StemMixerService.cs        Mix multiple stems with per-stem gain/mute
  StemPreferenceService.cs   User preference persistence (last used stems)
  BatchStemExportService.cs  Export all stems for a track list to a folder
  RealTimeStemEngine.cs      Live stem-aware playback (demux + remix on-the-fly)

ViewModels/StemMixerViewModel.cs   Binds StemMixerService to UI
ViewModels/StemWaveformViewModel.cs Per-stem waveform display
ViewModels/NeuralMixEqViewModel.cs  Per-stem EQ controls
Views/StemMixerView.axaml
Views/StemWaveformView.axaml
Views/NeuralMixEqView.axaml
```

### Stem Cache Strategy

```
Cache key = SHA256(filePath + "|" + modelVersion)
Cache path = {CacheDir}/{hash[0..1]}/{hash}.{stemName}.wav

On SeparateAsync():
  if all stem files exist → return cached paths immediately
  else → run ONNX inference → write stems → return new paths
```

Model version is embedded in `StemCacheService.GetModelTag(modelPath)` (SHA256 first 8
chars of the ONNX file itself), so a model update automatically invalidates old cached stems.

### ONNX Inference (OnnxStemSeparator)

1. **Decode** → `AudioIngestionPipeline` → 44100 Hz stereo PCM float[]
2. **STFT** → Short-time Fourier transform → complex magnitude/phase spectrogram
3. **Inference** → `InferenceSession.Run(namedOnnxValues)` via DirectML GPU acceleration
4. **iSTFT** → Reconstruct waveform per stem from masked spectrogram
5. **Write** → WAV files via `NAudio.Wave.WaveFileWriter`

`CachedStemSeparator` wraps any `IStemSeparator` — enables transparent swapping between
Spleeter ONNX, Demucs ONNX, and the CLI fallback without changing call sites.

### NeuralMix EQ

`NeuralMixEqSampleProvider` inserts a 3-band IIR EQ (low/mid/high) per stem into the
NAudio provider chain. The VM exposes `GainDb` (−∞ to +6 dB) and `Muted` per stem.
`StemMixerService` sums all stems via `MixingSampleProvider`.

---

## 6. Similarity & Playlist Intelligence

### Responsibility
Find similar tracks using 512-dim audio embeddings + HNSW index. Optimise playlist
order for harmonic flow and energy continuity via greedy graph search.

### Files
```
Services/Similarity/
  SimilarityIndex.cs              HNSW + brute-force cosine similarity index
  DiscogsEffnetEmbeddingExtractor  Extract 512-dim Essentia Discogs-Effnet embeddings

Services/Playlist/
  PlaylistOptimizer.cs            Greedy nearest-neighbour on complete directed graph
  PlaylistOptimizerOptions.cs     Weights + energy curve shape

ViewModels/SimilarTracksViewModel.cs  Drives "Similar Tracks" sidebar panel
Services/SmartCrateService.cs         Dynamic crate evaluation against criteria
```

### SimilarityIndex Architecture

```
Library size ≤ 5 000 tracks:
  Brute-force cosine similarity  (O(n) per query, < 50 ms for n=5000)

Library size > 5 000 tracks:
  HNSW (Hierarchical Navigable Small Worlds) approximate nearest-neighbour
  built via HnswLite + HnswLite.RamStorage
  Build: O(n log n)
  Query: O(log n) — returns approximate top-N in < 5 ms

Cache TTL: 1 hour (configurable via SimilarityIndex.IndexTtl)
Rebuild triggered by: new analysis, manual invalidation
```

Cosine similarity:
$$\text{sim}(a, b) = \frac{a \cdot b}{\|a\| \cdot \|b\|}$$

### PlaylistOptimizer

Greedy nearest-neighbour on a complete directed graph.

**Edge cost:**
$$\text{cost}(a, b) = w_H \cdot d_{\text{Camelot}}(a, b) + w_T \cdot |bpm_a - bpm_b| + w_E \cdot |energy_a - energy_b| + \text{jumpPenalty}$$

Default weights (from `PlaylistOptimizerOptions`):
| Weight | Default | Meaning |
|---|---|---|
| `wH` | 2.0 | Harmonic distance (Camelot wheel) |
| `wT` | 0.05 | BPM difference per unit |
| `wE` | 0.3 | Energy score difference |

**Camelot distance** is the minimum clockwise/counterclockwise steps on the 12-position
Camelot wheel, treating inner (minor) and outer (major) rings as distinct. Compatible
transitions (same slot, adjacent slot, or parallel mode) score 0–1. Incompatible = 4–6.

**Energy-curve post-pass:** after the greedy reorder an optional second pass re-sequences
to match `Rising`, `Wave` (low→high→low), or `Peak` shapes. It preserves harmonic windows
of ≤3 tracks while reordering between windows.

Complexity: O(n²) — suitable for playlists up to ~500 tracks without perceivable latency.

---

## 7. Timeline DAW Editor

### Responsibility
DAW-style arrangement: place, trim, and transition between clips on multiple lanes.
Full beat-snapped editing, gain envelopes, transitions DSP, undo/redo command stack.

### Files
```
Models/Timeline/
  TimelineSession.cs    Root container (tempo, bars, tracks list, JSON serialisation)
  TimelineTrack.cs      A lane (name, mute, solo, clips list)
  TimelineClip.cs       A clip placed at a beat position (StartBeat, LengthBeats, hash)
  TransitionModel.cs    Transition type + duration between adjacent clips
  GainPoint.cs          Envelope point (beat position, gain dB)

Services/Timeline/
  BeatGridService.cs    Pure-function beat↔seconds conversions + grid snapping
  TransitionDsp.cs      Applies echo-out / filter-fade / crossfade DSP
  WaveformRenderer.cs   Renders clip waveform thumbnail into SKBitmap

ViewModels/TimelineViewModel.cs
  TimelineCommandStack  Undo/redo: 100-command history (ITimelineCommand interface)
  AddClipCommand
  RemoveClipCommand
  MoveClipCommand
  SplitClipCommand
  AddTransitionCommand
```

### Coordinate System

All positions are stored in **beats** relative to `TimelineSession.ProjectBpm`.

```
Seconds = beats × (60 / ProjectBpm)
Beats   = seconds × (ProjectBpm / 60)
```

The UI renders at pixels/beat calculated as `canvasWidth / TotalBeats`.
`BeatGridService.SnapToGrid(position, GridResolution)` enforces alignment to
Quarter (1 beat), Eighth (0.5 beat), or Sixteenth (0.25 beat) grid lines.

Snap threshold: if the distance to the nearest grid line is ≤ `1 / (32 × bpm / 60)` seconds,
the clip snaps automatically.

### Undo/Redo

```csharp
interface ITimelineCommand {
    string Description { get; }
    void Execute(TimelineSession session);
    void Undo(TimelineSession session);
}

class TimelineCommandStack {
    Stack<ITimelineCommand> _undoStack;   // max 100
    Stack<ITimelineCommand> _redoStack;
    void Push(cmd) → Execute + clear redo stack
    bool Undo() → pop undoStack, push redoStack
    bool Redo() → pop redoStack, push undoStack
}
```

### JSON Serialisation

`TimelineSession.ToJson()` / `TimelineSession.FromJson(json)` use `System.Text.Json` with
`WriteIndented = true`, `DefaultIgnoreCondition = WhenWritingNull`, and
`JsonStringEnumConverter`. `[JsonIgnore]` on computed properties (`TotalBeats`,
`TotalDurationSeconds`) prevents redundant data and round-trip drift.

---

## 8. Video Export Pipeline

### Responsibility
Render audio-reactive video: SkiaSharp → PNG frame stream → FFmpeg stdin pipe → H.264/AAC MP4.
Supports built-in presets (Bars, Waveform, Circle, Spectrum) and Shadertoy-compatible GLSL shaders.

### Files
```
Services/Video/
  VisualEngine.cs           Renders one VisualFrame into an SKBitmap (or custom GLSL)
  VisualFrame.cs            Per-frame audio snapshot (FFT bands, energy, beat pulse, BPM)
  VideoRenderer.cs          Orchestrates frame loop → FFmpeg stdin pipe
  VisualPreset.cs (enum)    Bars | Waveform | Circle | Spectrum | CustomGlsl
  YouTubeChapterExportService  Generates YouTube chapter timestamps from clip positions

ViewModels/VideoExportViewModel.cs
Views/VideoExportView.axaml
```

### Frame Pipeline

```
AudioMix (NAudio PCM)
    │  FFT (512-point, Hann window) per frame
    ▼
VisualFrame { Energy, BeatPulse, Bpm, Band0..Band5 }
    │
    ▼
VisualEngine.Render(frame) → SKBitmap (1920×1080 @ 30 fps)
    │
    ▼
SKBitmap → PNG bytes (SKBitmap.Encode)
    │
    ▼
FFmpeg stdin pipe (rawvideo or piped PNG stream)
    │
    ▼
libx264 video + AAC audio → output.mp4
```

FFmpeg command:
```
ffmpeg -framerate 30 -f rawvideo -pix_fmt bgra -s 1920x1080 -i pipe:0
       -i mix.wav
       -c:v libx264 -preset fast -crf 20
       -c:a aac -b:a 192k
       -map 0:v -map 1:a
       output.mp4
```

Orbit writes raw BGRA frames directly to FFmpeg's stdin (`Process.StandardInput.BaseStream`),
avoiding the PNG encode overhead at 30 fps for full HD output.

### GLSL/SkSL Shader System

`VisualEngine.LoadGlslShader(filePath)` accepts any Shadertoy-compatible GLSL fragment shader.

Translation applied by `TranslateToSkSl()`:
| Shadertoy GLSL | SkSL (SkiaSharp runtime effect) |
|---|---|
| `void mainImage(out vec4 fragColor, in vec2 fragCoord)` | `half4 main(float2 fragCoord)` |
| `vec2`, `vec3`, `vec4` | `float2`, `float3`, `float4` |
| `mat2`, `mat3`, `mat4` | `float2x2`, `float3x3`, `float4x4` |

Injected uniform block (available in every shader):
```glsl
uniform float2 iResolution;  // render dimensions
uniform float  iTime;        // session progress × total duration (seconds)
uniform float  iEnergy;      // 0–1 overall energy
uniform float  iBeatPulse;   // 0–1 beat strength (peaks on downbeat)
uniform float  iBpm;         // current BPM
uniform float  iBand0;       // sub-bass  (20–80 Hz)
uniform float  iBand1;       // bass      (80–250 Hz)
uniform float  iBand2;       // low-mid   (250–500 Hz)
uniform float  iBand3;       // mid       (500–2000 Hz)
uniform float  iBand4;       // high-mid  (2000–4000 Hz)
uniform float  iBand5;       // presence  (4000–8000 Hz)
```

### YouTube Chapter Export

`YouTubeChapterExportService` converts `TimelineClip` start positions to YouTube timestamp
format (`0:00 Track Title`), written to a plain-text file suitable for pasting into a video description.

---

## 9. DJ Platform Integrations

### Files
```
Services/Library/
  PlaylistExportService.cs       Rekordbox XML (COLLECTION + PLAYLISTS + cues + grids)
  RekordboxExportExtensions.cs   USB path translation (Windows → Rekordbox USB format)

Services/Integrations/
  AbletonLiveProjectWriter.cs    Writes .als XML (MIDI + audio clips mapping)
  SeratoMetadataImporter.cs      Reads Serato _Serato_ GEOB ID3 tags
  TraktorMetadataImporter.cs     Reads Traktor NML collection XML
```

### Rekordbox XML (PlaylistExportService)

The exported XML conforms to the Rekordbox 6.x collection schema:

```xml
<DJ_PLAYLISTS Version="1.0.0">
  <COLLECTION Entries="N">
    <TRACK TrackID="…" Name="…" Artist="…" BPM="128.00" …>
      <TEMPO Inizio="0.000" Bpm="128.00" Metro="4/4" Battito="1"/>
      <!-- Hot cues (Num 0–7) -->
      <POSITION_MARK Name="Cue 1" Type="0" Start="4.218" Num="0" Red="255" …/>
      <!-- Memory cues (Num -1) -->
      <POSITION_MARK Name="Mem" Type="0" Start="32.000" Num="-1" …/>
    </TRACK>
  </COLLECTION>
  <PLAYLISTS>
    <NODE Type="0" Name="ROOT">
      <NODE Type="1" Name="My Playlist" KeyType="0" Entries="N">
        <TRACK Key="1"/>
        …
      </NODE>
    </NODE>
  </PLAYLISTS>
</DJ_PLAYLISTS>
```

Key implementation details:
- `TEMPO` node is always anchored at beat 0 (`Inizio="0"`) — CDJs require this to lock BPM grid
- Cues are deduplicated within a 50 ms window (`BuildCueList` in `RekordboxExportExtensions`)
- `TranslateToUsbPath` converts `C:\Music\track.mp3` → `/music/track.mp3` (USB mount relative)
- RGB colour is split into separate `Red`/`Green`/`Blue` XML attributes from the stored hex string

### Ableton Live (.als)

`AbletonLiveProjectWriter` generates a minimal `.als` XML structure mapping `TimelineClip`
positions to Ableton `AudioClip` nodes within `AudioTrack` lanes. Uses beat/time coordinates
from `TimelineSession.BeatsToSeconds()`.

### Serato Tag Import

`SeratoMetadataImporter` parses the proprietary `_Serato_ Markers2` GEOB frame from MP3
ID3v2 tags. The binary blob uses a documented-but-unofficial format:
- Header: `Serato Markers2\0`
- Entries: type byte + length-prefixed payload (cue, loop, flip, bpm lock)

### Traktor NML Import

`TraktorMetadataImporter` uses `XDocument.Load()` on the Traktor `collection.nml` file.
Maps `<ENTRY>` → `LibraryEntryEntity` and `<CUE_V2>` → `CuePointEntity`.

---

## 10. Library & Smart Playlists

### Files
```
Services/
  LibraryService.cs               Core CRUD + fast hash-indexed lookups
  LibraryFolderScannerService.cs  Recursive folder watch + delta/incremental sync
  SmartPlaylistService.cs         Evaluate SmartPlaylistCriteria against library
  SmartCrateService.cs            Named saved crates (persistent smart filters)
  LibraryOrganizationService.cs   Bulk move/rename/clean-up operations
  LibraryUpgradeScout.cs          Self-healing: detect low-quality files for upgrade

ViewModels/LibraryViewModel.cs (+ .Commands, .Events, .Workspace partials)
ViewModels/Library/
  VirtualizedTrackCollection.cs   IList + ISupportIncrementalLoading (page-by-page load)
  TrackListViewModel.cs           Delegates to VirtualizedTrackCollection
```

### VirtualizedTrackCollection

Critical for large libraries (10 000+ tracks). Implements:
- `IList<PlaylistTrackViewModel>` — for Avalonia `ItemsControl` binding
- `ISupportIncrementalLoading` — triggers `LoadMoreItemsAsync()` as the user scrolls
- `INotifyCollectionChanged` — fires batch `Reset` instead of per-item `Add`
- `IDisposable` — cleans up all subscriptions

Page size: 50 tracks. Pre-fetches the next page when the scroll position reaches 70% of
the current loaded count.

### LibraryViewModel Memory Management

`LibraryViewModel` implements `IDisposable` with `CompositeDisposable _disposables`.
All `IEventBus` subscriptions are registered into `_disposables`.
Avalonia's navigation service calls `Dispose()` on navigation away from the Library page.

### SmartPlaylistCriteria

```csharp
class SmartPlaylistCriteria {
    double? MinBpm, MaxBpm;
    int?    MinEnergy, MaxEnergy;
    string? Genre;
    string? Key;
    string? KeyMode;   // "major" | "minor"
    int?    MinBitrate;
    bool?   IsVerified;
}
```

Evaluation is done in-memory after retrieving candidate tracks from the database.
The `SmartPlaylistService.EvaluateAsync()` call returns a filtered `IEnumerable<LibraryEntryEntity>`.

---

## 11. External Services (Spotify · MusicBrainz)

### SpotifyBatchClient

All Spotify API calls go through one path: `SpotifyBatchClient`.

**Concurrency model:**
```
SemaphoreSlim _lock(1,1)  — serialises all outbound HTTP requests

Per request:
  1. Acquire _lock
  2. Check _circuitOpen → if open and within backoff window → throw immediately
  3. Await HTTP response
  4. If 403 → set _circuitOpen = true, record reopenAt = now + 5 min → throw
  5. If 429 → parse Retry-After header → Task.Delay(retryAfter) → retry (up to 3×)
  6. If 5xx → exponential backoff (1s, 2s, 4s) → retry
  7. Release _lock
```

The circuit breaker is **global** — one 403 locks all Spotify requests for 5 minutes.
This is intentional: Spotify bans are account-level, not per-endpoint.

### Enrichment Pipeline

```
SpotifyEnrichmentService.EnrichBatchAsync(trackHashes)
    │
    ▼
SpotifyBatchClient.GetAudioFeaturesAsync(spotifyIds[]) — batches of 100
    │
    ▼
AudioFeaturesEntity.{Danceability, Energy, Valence, Speechiness, Acousticness}
    │
    ▼
AppDbContext.SaveChangesAsync()
    │
    ▼
EventBusService.Publish(TrackEnrichedEvent)
```

`SpotifyBulkFetcher` handles the chunking (50-track windows for `/tracks`, 100-track for `/audio-features`).
`SpotifyMetadataService` manages OAuth token refresh via `SpotifyAuthService`.

### MusicBrainzService

Implements `IMusicBrainzService`. Uses the MusicBrainz JSON API with ISRC lookup for
cross-platform track matching. Rate-limited to 1 req/sec (MusicBrainz ToS). Results are
cached in `SpotifyMetadataCacheEntity` (reusing the same cache table with a `Source` discriminator).

---

## 12. Search Engine

### Files
```
Services/
  SearchOrchestrationService.cs     Main entry point — manages multi-source parallel search
  SearchCandidateFitScorer.cs       Scores a candidate against a SearchQuery
  SearchCandidateRankingPolicy.cs   Sorts + filters the scored candidate list
  SearchNormalizationService.cs     Normalises artist/title strings for matching
  SearchResultMatcher.cs            Fuzzy string matching + bitrate/format verification
  SearchFilterPolicy.cs             Hard-reject filters (safety, format, quality gates)
  SearchLoadSheddingPolicy.cs       Drops excess candidates under high load
  AdaptiveLaneTuner.cs              Adjusts lane weights at runtime based on result quality
  Ranking/TieredTrackComparer.cs    Two-stage gatekeeper (exact match → weighted rank)
  ResultFingerprinter.cs            Deduplication fingerprint for candidates
  ResultSorter.cs                   Final sort by composite score
```

### Search Architecture (Three-Zone Model)

```
[Initiator]              [Brain]                    [Gatekeeper]
SearchQuery           CandidateRankingPolicy      SearchFilterPolicy
    │                      │                           │
    ▼                      ▼                           ▼
SoulseekAdapter    ScoreCandidate(query, result)  Hard-reject rules:
MusicBrainz           wBitrate × bitrate             – format block list
Spotify               + wReliability × peerScore     – minimum bitrate
                      + wMatch × titleScore           – safety filter
    │                      │
    ▼                      ▼
ResultFingerprinter → dedup → TieredTrackComparer → ranked output
```

### TieredTrackComparer (Two-Stage Gatekeeper)

Stage 1 — **Exact gate**: if one candidate is an exact artist+title match and the other is
not, the exact match always wins regardless of score.

Stage 2 — **Weighted rank**: if both pass or both fail the exact gate, compare by:
$$\text{score} = w_1 \cdot \text{bitrateScore} + w_2 \cdot \text{reliabilityScore} + w_3 \cdot \text{matchScore}$$

Weights are configurable via `ScoringWeights` in `Configuration/ScoringWeights.cs`.

### AdaptiveLaneTuner

Monitors per-lane result quality over a sliding 60-second window. If a lane's average
match score drops below `ScoringConstants.LaneTuningThreshold`, it reduces that lane's
query frequency (down to 0.25×) to avoid wasting connections on low-yield sources.
Recovery: score above threshold for 30 seconds restores full weight.

---

## 13. Download System

### Files
```
Services/
  DownloadManager.cs              Central download orchestration + state machine
  DownloadOrchestrationService.cs Coordinates queue → Soulseek → local file write
  DownloadHealthMonitor.cs        Detects stalled downloads + auto-retry
  DownloadDiscoveryService.cs     Resolves alternative sources on failure
  CrashRecoveryService.cs         Startup recovery for interrupted downloads
  CrashRecoveryJournal.cs         JSON journal of active download state
  IO/SafeWriteService.cs          Atomic file write (write-to-temp + rename)
```

### Download State Machine

```
Queued → Searching → Connecting → Downloading → Verifying → Complete
                        │                           │
                        ▼                           ▼
                      Failed ←───────────────── VerifyFailed
                        │
                        ▼ (retry < 3)
                      Queued
```

### Crash Recovery Journal

On every 5-second checkpoint, `CrashRecoveryService` writes:
```json
{
  "session_id": "abc123",
  "active_downloads": [
    {
      "track_id": "…",
      "state": "downloading",
      "bytes_written": 2457600,
      "part_file": "C:\\temp\\track.mp3.part",
      "last_checkpoint": "2026-04-07T20:00:00Z"
    }
  ]
}
```

On startup, `RecoverAsync()` scans the journal and:
1. Resumes downloads still within the Soulseek peer connection window
2. Moves orphaned `.part` files to a recovery folder
3. Rolls back incomplete metadata writes

### Safe Write

`SafeWriteService.WriteAtomicAsync(finalPath, writeAction)`:
1. Write to `finalPath + ".part"`
2. Call optional `verifyAction` (e.g., check file size, MP3 header)
3. `File.Move(tempPath, finalPath, overwrite: true)` — atomic on NTFS

---

## 14. Background Jobs & Performance

### Files
```
Services/Jobs/BackgroundJobQueue.cs     Channel<T>-based generic background job queue
Services/IO/MemoryMappedAudioReader.cs  Zero-copy waveform access via MemoryMappedFile
Services/AnalysisQueueService.cs        Priority analysis queue (wraps BackgroundJobQueue)
Services/SystemInfoHelper.cs            CPU/RAM detection for optimal thread count
Services/AnalysisResultDiskCache.cs     Per-track analysis result file cache
```

### BackgroundJobQueue

```csharp
Channel<Func<CancellationToken, Task>> _channel =
    Channel.CreateBounded<>(new BoundedChannelOptions(capacity: 5000) {
        FullMode = BoundedChannelFullMode.Wait
    });
```

Producers `await EnqueueAsync(job)`. The queue blocks producers when full (back-pressure).
Worker count is determined at startup: `Math.Min(Environment.ProcessorCount / 2, 8)`.

### MemoryMappedAudioReader

Used for waveform rendering and seeking on large files without repeated I/O:

```csharp
var mmf = MemoryMappedFile.CreateFromFile(path, FileMode.Open);
var accessor = mmf.CreateViewAccessor(offset: byteOffset, size: byteCount, MemoryMappedFileAccess.Read);
// Read directly into float[] via unsafe pointer — zero allocation waveform access
```

Disposed via `IDisposable` when the track is unloaded from the waveform view.

### Analysis Result Disk Cache

`AnalysisResultDiskCache` stores serialised `AudioAnalysisEntity` snapshots as JSON files
keyed by `{TrackHash}.analysis.json` in a configurable cache directory.
Cache hit skips all Essentia processing — critical for library re-scans.

---

## 15. UI Architecture (MVVM + ReactiveUI)

### Pattern
- **MVVM** — Views bind to ViewModels via Avalonia data bindings
- **ReactiveUI** — `WhenAnyValue`, `WhenAnyObservable`, `ReactiveCommand`, `ObservableAsPropertyHelper`
- **MediatR-style events** — `EventBusService` for cross-ViewModel communication
- **Navigation** — `AvaloniaNavigationService` manages a `ContentControl`-hosted page stack

### ViewModel Lifetime
```
NavigationService.Navigate<TViewModel>()
    → resolves TViewModel from DI
    → sets MainViewModel.CurrentPage = tvm
    → previous TViewModel.Dispose() called if IDisposable
```

### ReactiveCommand Pattern

All user actions are `ReactiveCommand<TInput, TOutput>`. Long-running operations
use `CreateFromObservable` or `CreateFromTask` — never `Task.Result` or `.Wait()` on the
UI thread.

```csharp
AnalyzeCommand = ReactiveCommand.CreateFromTask(
    async ct => await _analysisService.AnalyzeAsync(SelectedTrack, ct),
    canExecute: this.WhenAnyValue(x => x.SelectedTrack).Select(t => t != null));
```

### DesignTokens

`Themes/DesignTokens.axaml` defines the global design token set:
- Colour palette (primary, secondary, surface, on-surface, error variants)
- Typography scale (body, label, headline)
- Spacing scale (xs=4, sm=8, md=16, lg=24, xl=40)
- Corner radius + elevation shadow values

All Avalonia styles reference tokens via `{DynamicResource TokenName}` — enabling
future theme switching without touching individual control templates.

### SidebarViewModel

Manages the left-rail navigation state:
- `ActivePage` (enum `PageType`) drives `ContentControl` page swap
- Collapsed/expanded state persisted to `AppConfig`
- Section groups (Library, DJ Studio, Settings) with animated expand/collapse

---

## 16. Cross-Cutting Concerns

### Logging

`Microsoft.Extensions.Logging.ILogger<T>` is injected everywhere.
Log levels:
- `Debug` — per-action traces (analysis step timing, keyboard event routing)
- `Information` — significant state changes (analysis complete, export started)
- `Warning` — recoverable issues (missing embedding, fallback to brute-force)
- `Error` — exceptions that are caught and handled
- `Critical` — unrecoverable errors that trigger the crash report dialog

Logs are written to `logs/orbit-{date}.log` via `Serilog.Sinks.File` with 7-day rolling.

### Native Dependency Health

`NativeDependencyHealthService` checks at startup:
- `ffmpeg` — `ffmpeg -version` subprocess
- Essentia binary — `essentia_streaming_extractor_music --help`
- ONNX model files — `File.Exists` for each configured model path

Missing dependencies set `DependencyStatus = DependencyStatus.Missing` on the relevant
service, which flows to the status bar (`⚠️ Repair Required`) and disables related commands.

### Security

- Spotify OAuth tokens are stored in `ISecureTokenStorage` (Windows Data Protection API / DPAPI via `ProtectedDataService`)
- No credentials are written to appsettings.json or cleartext files
- File paths accepted as user input are validated with `Path.GetFullPath` + containment check before use
- Search queries are parameterised EF Core LINQ — no raw SQL string concatenation

### Configuration

`Configuration/AppConfig.cs` — singleton, loaded from `appsettings.json` + `config.ini`.
`ScoringWeights.cs` — runtime-adjustable search ranking weights.
`ScoringConstants.cs` — fixed thresholds used in quality gates and lane tuning.

`ConfigManager.cs` watches `config.ini` via `FileSystemWatcher` and raises
`ConfigurationChangedEvent` for live reload (same pattern as keyboard profile reload in #119).

---

## Appendix — Service Registration Summary

| Service | Lifetime | Notes |
|---|---|---|
| `AppDbContext` | Scoped (factory) | `IDbContextFactory<AppDbContext>` |
| `LibraryService` | Singleton | In-memory hash index |
| `SimilarityIndex` | Singleton | HNSW rebuilt on TTL |
| `DeckEngine` (×4) | Singleton | One per deck slot |
| `BpmSyncService` | Singleton | Stateless |
| `OnnxStemSeparator` | Singleton | DirectML session held open |
| `StemCacheService` | Singleton | |
| `PlaylistOptimizer` | Transient | Stateless |
| `AnalysisQueueService` | Singleton | Owns background worker threads |
| `BackgroundJobQueue` | Singleton | Channel, bounded 5 000 |
| `SpotifyBatchClient` | Singleton | Holds HttpClient |
| `VisualEngine` | Transient | Per video export job |
| `VideoRenderer` | Transient | Per video export job |
| `PlaylistExportService` | Transient | |
| `KeyboardMappingService` | Singleton | (planned, #123) |
| `KeyboardEventRouter` | Singleton | (planned, #124) |
