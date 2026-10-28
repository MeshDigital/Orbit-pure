# Cue Lab and Waveform Parity Plan

## Product goal

Cue Lab is ORBIT's professional track-preparation workspace. Its job is to make beat grids, hot cues,
memory cues, loops, structural markers, and mix-in/mix-out points fast to inspect and safe to export.
It is not Flow Builder and it is not Edit Lab.

The waveform is the primary source of truth. Transport and cue actions support it; analysis controls,
metadata, and secondary inspectors must not compete with it.

"Rekordbox parity" here means equivalent practical readability and preparation speed. It does not claim
to reproduce Pioneer's proprietary analysis algorithms byte-for-byte.

## Confirmed problems in the current implementation

### Waveform correctness

1. The extractor advanced by FFT size instead of the intended time-bucket size. Later audio could be
   absent while the resulting samples were still stretched over the full track duration.
2. A 2,000-sample minimum made short-track buckets too small for useful low-frequency FFT resolution;
   the low band could collapse to zero bins.
3. Each band was normalized against its own peak. Quiet high-frequency content could therefore look as
   important as the loudest bass drop.
4. Unwindowed FFT frames allowed spectral leakage, and a single frame did not represent a whole bucket.
5. The Cue Lab renderer averaged samples when multiple samples shared a pixel, erasing transients and
   bass-return edges.
6. Existing database blobs had no explicit migration/refresh path after an extractor correction.

### Cue Lab UI and workflow

1. The detailed waveform does not receive enough vertical priority. Three lower panels consume a fixed
   272 DIPs even when their contents are not relevant to the current action.
2. Transport, beat jump, placement, generation, snapping, zoom, follow, and overflow actions all share
   one long toolbar. On constrained widths the primary workflow becomes a horizontal scroll exercise.
3. The header repeats navigation and editing actions while also showing track identity, four metadata
   groups, save state, and several unrelated popovers.
4. Energy, onset, vocal, section, phrase, grid, cue, loop, and playhead layers are always drawn together.
   The display is information-rich but visually poorly prioritized.
5. Auto-generate, grid editing, templates, and shortcuts are distributed between the toolbar, inspector,
   and More menu without a consistent mental model.
6. The fixed three-column lower area does not adapt its information architecture to available width.

## P0 — Trustworthy waveform foundation

### Implementation

- Map every output bucket to exact integer PCM frame boundaries with no gaps or overlaps.
- Use Hann-windowed, overlapping FFT frames across each complete bucket.
- Retain roughly eight waveform buckets per second for normal-length tracks, with a smaller short-track
  minimum that preserves bass resolution.
- Combine sustained band power with a transient contribution so kicks and bass returns remain visible.
- Normalize low/mid/high against one robust track-wide reference; never normalize bands independently.
- Use peak-preserving reduction for collapsed display columns and interpolation only at sub-sample zoom.
- Version completed analysis output. Identify old waveforms and offer an explicit high-priority refresh.

### Acceptance tests

- A loud section in the final quarter appears in the final quarter, not earlier and not as a flat tail.
- A quiet high band remains quieter than a loud low band on the shared display scale.
- An 80 Hz synthetic bass tone produces non-zero low-band data on short and normal-length fixtures.
- A one-bucket drop spike survives whole-track pixel reduction.
- Silent input produces silent output without normalization noise.
- Existing Library, Workstation, Flow Builder, and Cue Lab readers continue to consume the same blob layout.

## P1 — Waveform-first responsive workspace

### Desktop hierarchy

1. **Track bar:** previous/next, browser toggle, title/artist, BPM/key/duration, save state and Save.
2. **Action bar:** transport and beat jump on the left; Cue/Drop and loop creation in the center; Snap,
   zoom and follow on the right. Move generation, grid repair, templates and shortcuts into a clearly
   named Preparation menu instead of an anonymous More menu.
3. **Overview:** keep compact, with whole-track navigation and cue badges.
4. **Detailed waveform:** flexible star-sized height and the dominant visual surface.
5. **Preparation dock:** resizable and collapsible. At wide sizes show Pads/Loop, Cues and Selected Cue;
   at constrained widths use tabs so each remains usable instead of squeezing three columns.

### Responsive behavior

- Respond to available control size, never Windows render scaling.
- At 2560×1440, allocate extra space primarily to waveform width/height and readable cue rows, not larger
  margins or oversized buttons.
- At 1920×1080 and smaller heights, collapse the preparation dock before starving the waveform.
- Keep primary transport, Cue/Drop, Snap and zoom visible without horizontal scrolling.
- Preserve all current features; changes are to hierarchy, placement and disclosure, not capability.

## P2 — Readable analysis layers and faster editing

- Default to a clean three-band waveform, beat grid, cue/loop markers and playhead.
- Add one Layers control for energy, onset, vocal and phrase overlays. Remember the user's choice.
- Add non-destructive vertical gain with Fit / 150% / 200% options for quiet masters.
- Prevent cue-label collisions and keep the selected cue label visible at high marker density.
- Keep selected cue, pad and list row synchronized and scroll the selected cue into view.
- Consolidate generation into one Preparation surface with template, confidence explanation and a clear
  statement that user cues are preserved.
- Promote beat-grid repair from an overflow afterthought to a focused grid mode with Bar 1, fine slip,
  reset and audible beat-jump verification.

## P3 — Validation and polish

### Signal fixtures

Use deterministic synthetic fixtures plus real tracks covering house, techno, drum & bass, hip-hop,
live/variable-tempo recordings, quiet intros, breakdowns, double drops, short edits and long mixes.
Check timing, bass-band separation, drop contrast, silence, short files and sample rates.

### UI matrix

Verify 2560×1440 and 1920×1080 at 100%, 125% and 150% Windows scaling, with browser open/closed,
preparation dock expanded/collapsed, no cues, maximum cues, long titles and active loop editing.

### Performance and safety

- Cache static waveform rendering; playback must not rebuild analysis layers.
- No layout invalidation loop during zoom, resize, panel collapse or playback.
- Re-analysis never clears user cues or silently changes Rekordbox data.
- Save/export behavior and existing cue session synchronization remain covered by regression tests.

## Delivery sequence

1. P0 waveform correction, regression fixtures and cache refresh path.
2. P1 responsive workspace hierarchy and preparation dock.
3. P2 layer controls, vertical gain, label management and preparation workflow consolidation.
4. P3 real-track comparison, UI matrix, performance pass and final polish.
