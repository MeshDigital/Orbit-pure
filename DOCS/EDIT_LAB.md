# Edit Lab

The Edit Lab helps a DJ prepare tracks; it doesn't mix for them. It cuts tracks and their stems into
clips on a bar grid, plays them live, and renders the result as a new library track. Typical jobs
are a custom intro (B's vocal over A's drums), an acapella or instrumental, a bassline swap, or an
extended intro. It replaced the old Stems page, which nothing opened and whose mixer never reached
the audio output.

## User flow

1. **Open.** Use the sidebar (EXPERIMENTAL → Edit Lab) and pick *New edit*, or pick
   **Open in Edit Lab** from a Library track's context menu or its inspector `…` menu. That creates
   a project with the track as its base.
2. **Add tracks.** In the bin, `+` opens a library search (downloaded tracks only). The first track
   is the **base**: the project's BPM and key come from it. You can type your own BPM or key in the
   header, or choose *Use as base* on another track.
3. **Stems.** Each track shows whether its stems are in the stem cache. *Separate stems* runs Demucs
   (with progress and cancel). It's cached, so it only runs once per file. When the model is
   missing, the bin says so and the button is hidden.
4. **Clips.** A track's `…` menu adds a full mix, vocals, drums, bass, other or instrumental clip at
   the playhead. In the arranger:
   - Drag a clip to move it, also to another lane. It snaps to beat or bar; hold Shift to place it
     freely.
   - Drag a clip's edge to trim it.
   - Click the ruler to move the playhead. Drag across the ruler to set the loop.
   - Ctrl+wheel zooms and the wheel scrolls.
   - Keys: Del, Ctrl+Z/Y, Ctrl+D (duplicate), S (split at the playhead), L (loop the clip),
     Space (play), Esc (cancel a drag).
5. **Inspector.** For the selected clip you can set the stem, start bar, length, source in-point
   (as a bar of the source), gain, fades, mute/solo, tempo mode (Project / Match / Native) and key
   mode (Auto / Manual semitones). It shows the stretch %, the shift and the resulting key, and
   warns when the stretch is more than 8 %.
6. **Templates** (toolbar) create ordinary clips that you can edit afterwards, as one undo step:
   Custom intro, Acapella, Instrumental, Bassline swap, Extended intro.
7. **Preview** plays the edit live. It pauses the main player if that is playing, and stops if you
   start the main player again. Leaving the page stops the preview and saves the project.
8. **Render** (header, primary):
   - Writes WAV 24-bit or FLAC at the base track's sample rate, for the whole edit or only the loop
     region, with progress and cancel.
   - Adds the file to the library as `"<base title> (ORBIT Edit)"`, numbered if that name is taken.
   - Optionally adds a memory cue at every clip start, and queues analysis.
   - Failures show an error toast and leave no file behind.

## Matching

- **Auto-match tempo** (default on): a clip that follows the tempo plays at speed =
  project BPM / source BPM. The source BPM is first folded by ×½/×2 so that a half-time read doesn't
  stretch 100 %. Key-lock keeps the pitch where it was.
- **Auto-match key** (default off): a clip is shifted by the smallest number of semitones (−6…+6)
  that makes its Camelot key compatible with the project key (same key, relative major/minor, or one
  step on the wheel). On a tie the exact key wins, then shifting down. With the toggle off, the
  inspector still shows the suggestion and *Use suggestion* applies it.
- **Per-clip overrides:**
  - Tempo: *Project* follows the toggle, *Match* always stretches, *Native* never stretches.
  - Key: *Manual* uses the clip's own semitones regardless of the toggle.
- In-points and template maths use each source's precise analysed BPM and first downbeat, the same
  numbers Cue Forge's grid uses. Bar 1 of a source is its downbeat.

## Architecture

```
Models/EditLab/EditProject.cs        EditProject → EditLane → EditClip; EditSource (analysis + stem paths)
Models/EditLab/EditMatching.cs       EditMatching (speed, folding, stretch warning), EditKeyMath (Camelot)
Services/EditLab/
  EditProjectStore.cs                JSON per project in <dir of library.db>/EditLab/projects
  EditProjectEditor.cs               every mutation; snapshot undo/redo (200), drag gestures, coalescing
  EditTemplates.cs                   presets → ordinary clips
  EditRenderPlan.cs                  project → frame-exact ClipPlans (+ problems: missing stems/files)
  EditTimelineRenderer.cs            pull renderer: ClipVoice per clip (seek, resample/stretch, SMB, envelope)
  EditPreviewEngine.cs               background producer → chunk queue → preview device (live)
  EditOfflineRenderer.cs             24-bit WAV / FLAC (ffmpeg) file render
  EditLabSourceService.cs            library search/load, stem cache + Demucs, playable files
  EditLabRenderService.cs            render → file → tags → LibraryEntry + cues + analysis
ViewModels/EditLab/                  EditLabViewModel (+ .Templates, .Render), bin/lane/inspector VMs
Views/Avalonia/EditLabPage.axaml     page; Controls/EditArrangerControl.cs draws the timeline
```

- **One renderer for both paths.** The live preview and the offline render both play the same
  `EditRenderPlan` through `EditTimelineRenderer`, so what you hear is what gets rendered. Live
  preview runs that renderer on a background producer about 0.5 s ahead of the playhead. The audio
  thread only copies chunks. Seek, loop and edits bump a generation number, so stale audio is
  dropped. The preview has its own shared-mode output (the device from Settings), and its volume is
  a `VolumeSampleProvider`, never `IWavePlayer.Volume`.
- **ClipVoice chain:**
  1. `AudioFileReader`, seeked to the exact frame: by byte position for WAV/MP3, through
     `ExactSeek` for Media Foundation formats.
  2. Converted to stereo.
  3. A 4-point Hermite variable-rate resampler does sample-rate conversion and tempo in one step,
     with zero latency.
  4. NAudio's `SmbPitchShiftingSampleProvider` does key-lock and the semitone shift. Its
     3072-frame latency is discarded, so shifted clips stay on the grid.
  5. Gain and linear fades.
- **Playable files.** WAV/AIFF/MP3 are read directly. FLAC, M4A, Opus and similar formats are
  decoded once by ffmpeg into the existing `PlayableAudio` cache, so every clip seek is instant and
  exact.
- **Provenance.**
  - The library entry's `SourceProvenance` is
    `orbit-edit-lab;project=<id>;sources=<hash,…>`.
  - The project lists its renders in `Renders`.
  - BPM and `TagBPM` are both set to the project tempo, so analysis won't re-read the tempo as half
    or double time.
- **Persistence.** Projects are JSON files, so there's no schema change. They autosave 600 ms after
  each edit. Tests point `ORBIT_DB_PATH` at a temp database and so also get a temp project folder.

## Known limits (v1)

- **Time-stretch and pitch quality.** The SMB phase vocoder (FFT 4096) smears transients and
  softens drums beyond about ±8 %, which is where the UI warns. A ±1–2 semitone shift on vocals
  sounds fine; larger shifts sound phasey. Clips at native tempo with no shift bypass it and are
  bit-exact.
- **Resampling.** The Hermite resampler has no anti-alias filter, so speeding up or downsampling
  (for example 48 → 44.1 kHz) can alias faintly above about 18 kHz.
- **Edits during preview** restart the producer at the playhead, so a short gap is possible.
- **Adding tracks.** Tracks are added from the in-page library search. You can't drag them from the
  Library page, because the two pages are never visible at the same time.
- **Lanes and envelopes.** Lanes have no gain or pan, and fades are linear. There is no automation.
- **Stem alignment.** Demucs output is assumed to be sample-aligned with its source.

## Tests

`Tests/SLSKDONET.Tests/Services/EditLab/*` and `ViewModels/EditLabViewModelTests.cs` cover:

- **Model:** matching and key maths, the project store.
- **Renderer** (synthetic click, sine and burst sources):
  - Timing: bar alignment, in-points, stretch timing, SR conversion, key-locked alignment, mid-clip
    seek parity.
  - Levels: gain, fades, octave shift, mute/solo, clipping.
  - Plan and file: loop range, missing stems, cancel.
- **Preview:** parity with the offline render, loop wrap, seek, end.
- **Editing:** editor operations and undo, templates.
- **Render to library:** the isolated DB, provenance, cues, numbered titles.
- **View model flows.**
