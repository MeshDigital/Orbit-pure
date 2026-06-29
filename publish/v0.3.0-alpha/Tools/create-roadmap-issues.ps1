#!/usr/bin/env pwsh
# Creates all OrbitMusicIntelligence roadmap issues on MeshDigital/Orbit-pure
param(
    [string]$Repo = "MeshDigital/Orbit-pure"
)

function New-Issue {
    param([string]$Title, [string]$Labels, [string]$Body)
    $url = gh issue create --repo $Repo --title $Title --label $Labels --body $Body 2>&1
    Write-Host "  Created: $url"
}

Write-Host "=== Creating roadmap issues on $Repo ===" -ForegroundColor Cyan

# ── 1. Audio Analysis ──────────────────────────────────────────────────────────

New-Issue `
  -Title "1.1 Standardize Audio Ingestion Pipeline (FFmpeg + Essentia)" `
  -Labels "audio-analysis,enhancement,roadmap" `
  -Body @"
## Goal
Create a robust, reusable audio ingestion pipeline shared by all analysis services.

## Steps
- [ ] Create ``AudioAnalysis/AudioIngestionPipeline`` (Application/Infrastructure layer)
- [ ] Use FFmpeg (FFmpeg.AutoGen or process) for decoding to 44.1kHz mono/stereo WAV
- [ ] Define a ``TrackAudioSource`` abstraction (file path, stream, temp file)
- [ ] Add logging for decode errors and unsupported formats
- [ ] Add unit tests with small sample files (mp3, flac, wav)
- [ ] Integrate with existing ``EssentiaAnalyzer`` so all analysis uses this pipeline

## References
- Existing: ``EssentiaAnalyzer``, ``AudioAnalysis`` namespace
- Binding: FFmpeg.AutoGen or ``System.Diagnostics.Process``
"@

New-Issue `
  -Title "1.2 Implement BPM Detection Pipeline (Essentia BeatTrackerMultiFeature)" `
  -Labels "audio-analysis,enhancement,roadmap" `
  -Body @"
## Goal
Reliable BPM detection for all analyzed tracks.

## Steps
- [ ] Add ``BpmDetectionService`` in ``AudioAnalysis``
- [ ] Use Essentia ``BeatTrackerMultiFeature`` via ``EssentiaAnalyzer``
- [ ] Implement smoothing for unstable BPM (median/mean over segments)
- [ ] Store BPM + confidence on ``TrackModel``
- [ ] Add unit tests with known-BPM tracks
- [ ] Expose BPM in UI (TrackDetailsView)

## References
- Existing: ``EssentiaAnalyzer.ExtractBpm()``
- Algorithm: BeatTrackerMultiFeature + median smoothing
"@

New-Issue `
  -Title "1.3 Implement Key Detection Pipeline (Essentia KeyExtractor + Camelot)" `
  -Labels "audio-analysis,enhancement,roadmap" `
  -Body @"
## Goal
Accurate musical key detection with DJ-friendly Camelot wheel notation.

## Steps
- [ ] Add ``KeyDetectionService`` in ``AudioAnalysis``
- [ ] Use Essentia ``KeyExtractor`` (HPCP + TonalCentroid)
- [ ] Implement Krumhansl-Schmuckler fallback for edge cases
- [ ] Map results to Open Key notation and Camelot wheel notation
- [ ] Store key + confidence on ``TrackModel``
- [ ] Add unit tests with known-key tracks
- [ ] Show key (Camelot + Open Key) in TrackDetailsView

## References
- Existing: ``EssentiaAnalyzer.ExtractKey()``
- Reference: HPCP profiles, Camelot mapping table
"@

New-Issue `
  -Title "1.4 Implement Energy Level Scoring (1-10 Scale)" `
  -Labels "audio-analysis,enhancement,roadmap" `
  -Body @"
## Goal
Provide an MIK-style energy score for each track on a 1–10 scale.

## Steps
- [ ] Add ``EnergyScoringService`` in ``AudioAnalysis``
- [ ] Use Essentia features: ``Danceability``, ``DynamicComplexity``, loudness
- [ ] Design heuristic or ML.NET model to map features → 1–10 scale
- [ ] Store energy score + raw features on ``TrackModel``
- [ ] Add unit tests with synthetic low/high energy examples
- [ ] Display energy in UI (bar or numeric indicator)

## References
- Existing: ``EssentiaAnalyzer.ExtractEnergyLevel()``
- ML path: ML.NET regression on labelled tracks
"@

New-Issue `
  -Title "1.5 Implement Cue Point Detection (Onsets + Phrase Detection)" `
  -Labels "audio-analysis,enhancement,roadmap" `
  -Body @"
## Goal
Automatically suggest cue points (Intro, Drop, Chorus, Breakdown, Outro).

## Steps
- [ ] Add ``CuePointDetectionService`` in ``AudioAnalysis``
- [ ] Use Essentia ``Onsets`` + spectral flux peaks
- [ ] Implement simple phrase detection (sections based on energy + onset density)
- [ ] Define cue types: Intro, Drop, Chorus, Breakdown, Outro
- [ ] Store cue points on ``TrackModel`` (time, label, confidence)
- [ ] Add unit tests with hand-labelled tracks
- [ ] Visualize cue markers on waveform in UI
"@

# ── 2. Similarity & Playlist Intelligence ──────────────────────────────────────

New-Issue `
  -Title "2.1 Implement Track Embedding Extraction (Essentia DiscogsEffnet)" `
  -Labels "similarity,enhancement,roadmap" `
  -Body @"
## Goal
Represent each track as a 2048-dim embedding for similarity and recommendation.

## Steps
- [ ] Add ``EmbeddingExtractionService`` in ``Similarity``
- [ ] Use Essentia ``DiscogsEffnet`` (or MSD-MusicNN) via ``EssentiaAnalyzer``
- [ ] Define ``TrackEmbedding`` model (trackId, vector[2048], modelVersion)
- [ ] Store embeddings in LiteDB or existing DB
- [ ] Add migration/upgrade path for embedding version changes
- [ ] Add unit tests for embedding extraction on sample tracks

## References
- Existing: ``SimilarityService``
- Storage: LiteDB with HNSW vector extension
"@

New-Issue `
  -Title "2.2 Implement Track Similarity Search (Cosine Distance + ANN)" `
  -Labels "similarity,enhancement,roadmap" `
  -Body @"
## Goal
Find similar tracks for recommendations and automix seeding.

## Steps
- [ ] Add ``SimilarityIndex`` service extending ``SimilarityService``
- [ ] Implement cosine similarity between 2048-dim embeddings
- [ ] Provide API: ``GetSimilarTracks(trackId, topN)``
- [ ] Optimize with in-memory cache or HNSW / k-d tree ANN structure
- [ ] Add unit tests with synthetic embeddings
- [ ] Expose "Similar Tracks" panel in UI

## References
- Library: ``MathNet.Numerics`` for vector ops
- ANN: HNSW via LiteDB extension or custom
"@

New-Issue `
  -Title "2.3 Implement Playlist Optimization Graph (Key / BPM / Energy)" `
  -Labels "similarity,enhancement,roadmap" `
  -Body @"
## Goal
Order tracks for harmonic, tempo, and energy-consistent playlists using graph search.

## Steps
- [ ] Add ``PlaylistOptimizer`` in ``Playlist`` domain
- [ ] Model tracks as graph nodes with edge weight = f(Camelot distance, ΔBPM, ΔEnergy, embedding distance)
- [ ] Use QuickGraph shortest-path (negated weights = longest/best path)
- [ ] Support constraints: start track, end track, max BPM jump, energy curve
- [ ] Add unit tests with small synthetic playlists
- [ ] Integrate into ``PlaylistGenerator`` as "AI Automix" mode

## References
- Library: QuickGraph ``ShortestPathAlgorithm`` with negated weights
- Existing: ``PlaylistGenerator``
"@

New-Issue `
  -Title "2.4 Implement Energy-Aware Sequencing Patterns (Builds & Drops)" `
  -Labels "similarity,enhancement,roadmap" `
  -Body @"
## Goal
Shape playlists with intentional energy curves for DJ sets.

## Steps
- [ ] Extend ``PlaylistOptimizer`` with energy pattern post-pass:
  - [ ] Rising (low → high)
  - [ ] Wave (low → high → low)
  - [ ] Peak (steady → spike → steady)
- [ ] Add ``EnergyCurveConfig`` model
- [ ] Add UI controls to choose energy pattern in playlist creation wizard
- [ ] Add tests verifying energy curve adherence after ordering
"@

# ── 3. Stem Separation ─────────────────────────────────────────────────────────

New-Issue `
  -Title "3.1 Integrate Demucs ONNX Model for Stem Separation" `
  -Labels "stem-separation,enhancement,roadmap" `
  -Body @"
## Goal
Offline 4-stem separation (vocals, drums, bass, other) using Demucs ONNX.

## Steps
- [ ] Add ``DemucsModelManager`` in ``StemSeparator``
- [ ] Support configurable ONNX model path (Demucs-4s)
- [ ] Use ``Microsoft.ML.OnnxRuntime`` for inference
- [ ] Implement ``SeparateStems(track)`` → 4 WAV float[] arrays
- [ ] Handle GPU (DirectML) / CPU selection with fallback
- [ ] Add tests with short audio clips

## References
- Existing: ``StemSeparator``, ONNX runtime already wired
- Model: Demucs v4 ONNX export (open source, MIT)
"@

New-Issue `
  -Title "3.2 Implement Stem Caching Strategy" `
  -Labels "stem-separation,enhancement,roadmap" `
  -Body @"
## Goal
Avoid recomputing stems for the same track by caching to disk.

## Steps
- [ ] Define stem cache directory: ``/stems/{sha256(path)}/{stem}.wav``
- [ ] Add ``StemCacheService`` to check/create stems
- [ ] Store metadata sidecar (model version, date, source checksum)
- [ ] Add cache cleanup strategy (LRU eviction or manual purge)
- [ ] Add tests for cache hit/miss behavior and version invalidation
"@

New-Issue `
  -Title "3.3 Implement Stem Mixer for Timeline Editor" `
  -Labels "stem-separation,timeline-daw,enhancement,roadmap" `
  -Body @"
## Goal
Allow per-stem volume / mute / solo control in timeline mixes.

## Steps
- [ ] Add ``StemMixer`` using NAudio ``ISampleProvider`` chain
- [ ] Support per-stem: gain, mute, solo
- [ ] Integrate with timeline engine (each clip references active stems)
- [ ] Add Avalonia UI controls for stem levels in timeline view
- [ ] Add tests for basic mixing correctness (gain math)
"@

# ── 4. Timeline DAW Editor ─────────────────────────────────────────────────────

New-Issue `
  -Title "4.1 Implement Timeline Model (Clips, Tracks, Sessions)" `
  -Labels "timeline-daw,enhancement,roadmap" `
  -Body @"
## Goal
Define the data model for arranging tracks and stems on a multi-track timeline.

## Steps
- [ ] Define ``TimelineSession``, ``TimelineTrack``, ``TimelineClip`` models
- [ ] Support audio clips referencing full tracks or individual stems
- [ ] Store: start beat, length beats, fade in/out, gain envelope
- [ ] Add JSON serialization for saving/loading sessions
- [ ] Add unit tests for model operations (add / move / remove / split clips)
"@

New-Issue `
  -Title "4.2 Implement Waveform Rendering for Timeline (SkiaSharp)" `
  -Labels "timeline-daw,enhancement,roadmap" `
  -Body @"
## Goal
Visual waveforms for clips in the timeline using SkiaSharp.

## Steps
- [ ] Add ``WaveformRenderer`` using SkiaSharp
- [ ] Precompute downsampled RMS waveform data per track (stored in cache)
- [ ] Create Avalonia control that draws waveforms in timeline lanes
- [ ] Support zoom/scroll with LOD (level-of-detail) sampling
- [ ] Add tests for waveform generation correctness (RMS values)

## References
- Existing: ``WaveformControl`` (Rekordbox PWAV path) — reuse pattern
- Library: SkiaSharp ``SKCanvas.DrawRect`` per sample bin
"@

New-Issue `
  -Title "4.3 Implement Beat Grid & Snapping in Timeline" `
  -Labels "timeline-daw,enhancement,roadmap" `
  -Body @"
## Goal
Align clips and transitions to musical beats using BPM-derived grid.

## Steps
- [ ] Use BPM + first downbeat offset to compute beat grid per track
- [ ] Render beat / bar markers as vertical lines in timeline lanes
- [ ] Implement clip-edge snapping to nearest beat
- [ ] Support grid resolutions: 1/4, 1/8, 1/16 notes
- [ ] Add tests for beat position calculations: ``(beat / bpm) * 60`` seconds
"@

New-Issue `
  -Title "4.4 Implement Transitions & Basic Effects in Timeline" `
  -Labels "timeline-daw,enhancement,roadmap" `
  -Body @"
## Goal
Provide DJ-style transitions between clips with configurable DSP.

## Steps
- [ ] Define transition types: crossfade, echo-out, filter sweep, cut
- [ ] Add ``TransitionModel`` attached to clip boundaries (type, duration beats)
- [ ] Implement DSP for transitions using NAudio ``ISampleProvider``
- [ ] Add UI to select and configure transitions in timeline
- [ ] Add tests for transition parameter mapping (duration → samples)
"@

# ── 5. Video Export ────────────────────────────────────────────────────────────

New-Issue `
  -Title "5.1 Implement Audio-Reactive Visual Engine (SkiaSharp)" `
  -Labels "video-export,enhancement,roadmap" `
  -Body @"
## Goal
Generate audio-reactive visuals driven by spectral/energy features.

## Steps
- [ ] Add ``VisualEngine`` consuming per-frame FFT / energy data
- [ ] Define visual primitives: bars, circles, waveforms
- [ ] Map features (energy, frequency bands) → visual parameters (size, color, motion)
- [ ] Add Avalonia preview control using SkiaSharp
- [ ] Add tests for feature → visual state mapping logic
"@

New-Issue `
  -Title "5.2 Implement Offline Video Rendering Pipeline (SkiaSharp → FFmpeg)" `
  -Labels "video-export,enhancement,roadmap" `
  -Body @"
## Goal
Export timeline mixes as MP4 video files with audio-reactive visuals.

## Steps
- [ ] Add ``VideoRenderer`` service
- [ ] Render frames offscreen using SkiaSharp (1920×1080 @ 30fps default)
- [ ] Pipe raw frames (or numbered PNGs) to FFmpeg stdin
- [ ] Combine frames + final audio mix: ``ffmpeg -framerate 30 -i pipe:0 -i mix.wav -c:v libx264 -c:a aac output.mp4``
- [ ] Make resolution, framerate, codec configurable
- [ ] Add tests for command-line generation and basic frame output

## References
- Library: ``System.Diagnostics.Process`` piped to FFmpeg
"@

New-Issue `
  -Title "5.3 Add Video Export UI for Timeline Sessions" `
  -Labels "video-export,enhancement,roadmap" `
  -Body @"
## Goal
Let users configure and trigger video export from the timeline.

## Steps
- [ ] Add ``VideoExportView`` in Avalonia (resolution, fps, visual preset, output path)
- [ ] Integrate with ``VideoRenderer`` and timeline mixdown
- [ ] Show export progress bar and estimated time
- [ ] Surface error messages clearly
- [ ] Add tests for configuration validation (invalid path, codec, resolution)
"@

# ── 6. Integrations ────────────────────────────────────────────────────────────

New-Issue `
  -Title "6.1 Extend Rekordbox XML Export (Cues, Beat Grids, Playlists)" `
  -Labels "integrations,enhancement,roadmap" `
  -Body @"
## Goal
Export full DJ metadata (cues, BPM grids, playlists) to Rekordbox XML.

## Steps
- [ ] Extend ``RekordboxXmlExporter`` with:
  - [ ] ``POSITION_MARK`` nodes for cue points from ``CuePointDetectionService``
  - [ ] ``TEMPO`` nodes for BPM grid
  - [ ] Key and energy in ``COMMENTS`` or custom fields
- [ ] Add support for exporting ``PlaylistGenerator`` output as ``PLAYLIST`` nodes
- [ ] Add tests comparing output XML to known-good Rekordbox schema

## References
- Existing: ``RekordboxXmlExporter``
"@

New-Issue `
  -Title "6.2 Implement Ableton Live Project Export (.als)" `
  -Labels "integrations,enhancement,roadmap" `
  -Body @"
## Goal
Export mixes or playlists as Ableton Live projects.

## Steps
- [ ] Research .als format (XML-based, gzip compressed)
- [ ] Add ``AbletonLiveProjectWriter`` service
- [ ] Export tracks as AudioClip items on Ableton timeline
- [ ] Include BPM and warp markers where analysis data is available
- [ ] Add tests for basic project XML structure
"@

New-Issue `
  -Title "6.3 Parse Serato and Traktor Metadata" `
  -Labels "integrations,enhancement,roadmap" `
  -Body @"
## Goal
Import existing DJ metadata (cues, BPM, key) from Serato/Traktor libraries.

## Steps
- [ ] Add ``SeratoMetadataImporter`` (parse ``_Serato_`` tags via TagLib#)
- [ ] Add ``TraktorMetadataImporter`` (parse .nml XML)
- [ ] Map imported cues, BPM, key to Orbit-pure ``TrackModel``
- [ ] Add tests with sample library files

## References
- Library: TagLib# for ID3/MP4 tag access
"@

# ── 7. Infrastructure & UX ────────────────────────────────────────────────────

New-Issue `
  -Title "7.1 Add Background Job System for Analysis & Stems" `
  -Labels "infrastructure,enhancement,roadmap" `
  -Body @"
## Goal
Run heavy tasks (analysis, stem separation, video render) off the UI thread with progress.

## Steps
- [ ] Add ``IBackgroundJobQueue`` abstraction
- [ ] Implement using ``System.Threading.Channels`` or TPL Dataflow
- [ ] Queue jobs for: audio analysis, stem separation, video rendering
- [ ] Add per-job progress reporting (IProgress<T>)
- [ ] Add tests for job scheduling, cancellation, and error propagation

## References
- Pattern: ``Channel<T>`` producer/consumer with CancellationToken propagation
"@

New-Issue `
  -Title "7.2 Implement Caching & Performance Tuning for Large Libraries (100k+)" `
  -Labels "infrastructure,enhancement,roadmap" `
  -Body @"
## Goal
Make Orbit-Pure scale to 100k+ tracks without UI stalls or repeated computation.

## Steps
- [ ] Add caching for: embeddings, waveforms, analysis results (disk + memory)
- [ ] Use lazy loading and pagination in library UI (already has ISupportIncrementalLoading)
- [ ] Profile analysis pipeline on a 10k-track test set
- [ ] Add configuration for analysis parallelism (max concurrent workers via SemaphoreSlim)
- [ ] Document performance recommendations

## References
- Existing: ``VirtualizedTrackCollection`` (ISupportIncrementalLoading)
"@

New-Issue `
  -Title "7.3 Improve UX for Analysis & Automix Flows" `
  -Labels "infrastructure,enhancement,roadmap" `
  -Body @"
## Goal
Make the core AI workflows feel deliberate, smooth, and feedback-rich.

## Steps
- [ ] Design "Analyze Library" flow: select folder → queue → per-track progress
- [ ] Design "Create Automix Playlist" flow: select tracks → set constraints → generate
- [ ] Add clear status indicators per track: analyzed, stems ready, in playlist
- [ ] Add error surfaces for failed analysis and stem separation jobs
- [ ] Add hover/tooltip showing last-analyzed timestamp and model version
"@

Write-Host ""
Write-Host "=== Done! All roadmap issues created. ===" -ForegroundColor Green
Write-Host "View at: https://github.com/$Repo/issues?q=label%3Aroadmap" -ForegroundColor Cyan
