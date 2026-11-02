# Sets & Transitions Rebuild Plan

Status: **ratified 2026-11-02** (see §0). Phase 0 done on `fix/phase-0-hardening` (see §6, Phase 0 status).
Date: 2026-11-02 · Branch: `plan/sets-rebuild-2026-11-02` · Baseline: `master` @ `90a003f` (0.3.2)
Scope: the "knowledge system" work merged between `e82f06c` (0.2.3) and `90a003f`: Set Projects
(Story Graph), Transition Lab, Set Health, the Copilot, and the Knowledge Swarm agents.

How to give feedback: every decision is numbered **D1…D9** and every work item **P0.1…P6.x**. Reply
with "D3: option B", "P2.4: drop", etc. Items marked **[verify]** come from reading the code and
still need a test or a run to confirm.

---

## 0. Decisions record (maintainer, 2026-11-02)

| ID | Outcome |
|---|---|
| D1 | **A**: Sets becomes its own top-level page; the Workstation overlay goes |
| D2 | **A**: Flow Builder converges into Sets over time, ending as auto-mix playback only |
| D3 | **A**: linear spine + stacked Plan B alternates |
| D4 | **A**: one host-agnostic transition editor; Transition Lab is retired |
| D5 | **A**: one intent tag; `TransitionTemplates` become user presets |
| D6 | **A**: Copilot runs only on DJ initiation. Keep the weights (BPM 0.45, confidence 0.45, harmonic 0.30, energy 0.05) and the artist\|title dedupe |
| D7 | **A**: swarm agents off by default |
| D8 | Approved: evidence from manual rating + Rekordbox history; `MixRecipes`, `DjFingerprints`, `TrackCrowdProfiles` stay in the schema with no UI |
| D9 | Approved: update the foundation docs |
| P0.1–P0.9 | Approved; run on `fix/phase-0-hardening` |
| P2.10, P2.12 | Approved |
| P3.2 | Mirror table `SetTrackTransitions` via `SchemaMigratorService` raw SQL |
| P4.1 | Approved: benchmark the Copilot `BridgeFinder` against Flow Builder's A10 engine with `Tools/CopilotBattleTest`; keep the better one, delete the other |
| P4.2–P4.5 | Approved; explicit user actions only |
| Housekeeping | `.kimi-*` copies deleted, stale worktree pruned, baseline taken (§8) |

---

## 1. Summary

The 0.3.x merge added about 10.7k lines over 66 commits. It sorts into three layers of very
different quality:

| Layer | What it is | State | Verdict |
|---|---|---|---|
| **Data model** | `SetLists` gained role/energy/status; new tables for knowledge edges, evidence, candidates, templates, event profiles, story edges, fingerprints, performance sessions | Reasonable, tested, created via `SchemaReconciler` | **Keep** (trim unused tables later, see D8) |
| **Services / math** | `SetHealthService`, `KnowledgeScoringService`, `StoryGraphLayout`, `Services/Copilot/*` (set builder, bridge finder, clash detector) | Pure, unit-tested, mostly unused by the app | **Keep**, wire into UI behind DJ-initiated actions |
| **UI surfaces** | Story Graph + Transition Lab drawn as overlays on the Workstation page; 4 picker windows; Set Health side panel | Scaffold quality: dead controls, data loss, no labels, rule violations | **Rebuild** |

Workstation was never upgraded. Its Prep surface is unchanged. Set Projects and Transition Lab were
bolted on as extra `WorkstationMode` values whose panels float over the cockpit (`ZIndex=30/31`,
hard-coded `Margin="184,16,24,16"`).

The bigger structural problem: ORBIT now has **two set editors and two transition editors that don't
know about each other**:

- **Flow Builder** (mature, 1.9k-line VM): orders a *playlist*, scores adjacent pairs, edits
  transitions (mix-out/in, length, preset, preview, save), inserts bridges, proposes orders (A10).
  Its saved transitions (`PlaylistTrackTransitions`) drive auto-mix playback.
- **Set Projects / Story Graph** (new): a *SetProject* with slot roles, energy targets, branches and
  Set Health. It can't order, preview or export anything.
- **Transition Lab** (new): a second transition editor for any track pair. It writes
  `TransitionKnowledge` / `TransitionTemplates`, which Flow Builder never reads.

The plan below fixes the defects first (Phase 0), then gives sets their own page, then merges the
two transition editors into one, and only after that connects the Copilot.

---

## 2. What exists today (inventory)

### 2.1 Navigation & entry points
- Left nav, **EXPERIMENTAL** group: Cue Forge · Edit Lab · Flow Builder · **Set Projects** ·
  **Transition Lab** · Workstation ([MainWindow.axaml](../../Views/Avalonia/MainWindow.axaml)).
- *Set Projects* opens `SetProjectPickerWindow` (non-modal) → publishes `OpenSetProjectsRequestedEvent`
  → `WorkstationViewModel` switches to `WorkstationMode.StoryGraph`.
- *Transition Lab* opens `TrackPairPickerWindow` → `OpenTransitionLabRequestedEvent` → `WorkstationMode.TransitionLab`.
- Library context menu **Add to Set** → `AddToSetWindow` ([TrackOperationsViewModel.cs](../../ViewModels/Library/TrackOperationsViewModel.cs)).

### 2.2 UI files (to be rebuilt)
| File | Notes |
|---|---|
| [StoryGraphPanel.axaml](../../Views/Avalonia/StoryGraph/StoryGraphPanel.axaml) | Bottom row: Add Track · Undo · Redo · Role combo · Energy slider; Set Health docked right |
| [StoryGraphView.cs](../../Views/Avalonia/StoryGraph/StoryGraphView.cs) | Custom-drawn: coloured rectangles + energy bar. **No text, no hit-testing** |
| [TransitionLabPanel.axaml](../../Views/Avalonia/TransitionLab/TransitionLabPanel.axaml) | 2 rows / 17 controls, free-text cue-role boxes, unlabelled loop boxes |
| [TransitionWaveformCanvas.cs](../../Views/Avalonia/TransitionLab/TransitionWaveformCanvas.cs) | RMS bars + beat ticks + mix box; allocates pens in `Render` |
| [SetHealthDashboardView.axaml](../../Views/Avalonia/SetHealth/SetHealthDashboardView.axaml) | 8 stacked ProgressBars, no empty state |
| 4 picker windows (`SetProjectPicker`, `AddToSet`, `TrackPicker`, `TrackPairPicker`) | Plain ListBoxes, first 500 tracks by title, no search |

### 2.3 Services
| Area | Files | Reachable from UI? |
|---|---|---|
| Set Health | `Services/KnowledgeLifecycle/SetHealthService.cs` | Yes (side panel) |
| Knowledge scoring / repo | `KnowledgeScoringService`, `KnowledgeLifecycleRepository` | Yes (Transition Lab "Save Knowledge") |
| Copilot | `Services/Copilot/*` (`OrbitCopilot`, `SetBuilder`, `BridgeFinder`, `ClashDetector`, `AlternativeRouteFinder`) | **No.** Only DI and `Tools/CopilotBattleTest` |
| Swarm | `KnowledgeSwarmHost` + `TransitionAgent`, `PerformanceAnalystAgent`, `ExportAgent` | Host **runs on every launch**. Only `TransitionAgent` has a trigger; the other two listen for events nothing publishes |
| Old set service | `Services/SetListService.cs` | **Unused** by any production code |

### 2.4 Overlapping vocabularies
Four enums describe "kind of transition":
`TransitionArchetype` (on `SetTrackEntity`), `KnowledgeTransitionType` (knowledge edges),
`Models.Timeline.TransitionType` (DSP: Cut/Crossfade/EchoOut/FilterSweep), and Flow Builder's
`PresetName` string (Auto/Fade/Rise/Blend/Wave/Melt/Custom). Section D5 decides which survive.

### 2.5 Brief vs. reality (the DeepSeek hand-off text)
| Brief claim | Reality |
|---|---|
| "Phase 2 completed & CI-green" | Services and tests exist; nothing in the UI uses the Copilot |
| "Edges strictly evidence-derived, never auto-scanned" | `TransitionAgent` auto-scans after every analysis, but it writes *candidates*, not edges, so this mostly holds |
| Pro DJ Link scaffolded | Only on branch `feat/pro-dj-link`, not in `master` |
| Velopack updater | Not built. 0.3.1 shipped a simpler GitHub-release check + installer download |
| "Treat Phase 2 as locked" | **Not adopted.** Keep the math and its tests; the product decisions stay open (see D6) |

---

## 3. Defects found (evidence)

Ordered by impact. Each one maps to a Phase 0 fix.

| # | Defect | Evidence | Impact |
|---|---|---|---|
| F1 | **Role/energy edits are never saved.** `SetSlotRole` / `SetSlotEnergyTarget` mutate in-memory entities from a disposed `DbContext`; there's no `SaveChanges`. `AddTrackAsync` then reloads from the DB and **wipes unsaved edits** | [StoryGraphViewModel.cs](../../ViewModels/StoryGraph/StoryGraphViewModel.cs) `SetSlotRole`, `AddTrackAsync` | Silent data loss |
| F2 | **No way to select a slot.** `StoryGraphView` has no pointer handling, so `SelectedSlotId` is never set; the Role combo and Energy slider do nothing | `StoryGraphView.cs` (no `OnPointerPressed`) | Placebo UI (CLAUDE.md rule 1) |
| F3 | **Nodes have no labels.** Rectangles only: no title, artist, BPM or key | `StoryGraphView.Render` | Feature unusable |
| F4 | **Shared global undo stack.** `IUndoService` is a singleton shared with Workstation's Flow transition edits. Story Graph `Load()` calls `_undoService.Clear()` (wipes the Workstation history), and Ctrl+Z crosses surfaces | `App.axaml.cs:904`, `StoryGraphViewModel.Load` | Lost undo history, undo applied to the wrong surface |
| F5 | **[verify] UI-thread violations.** All new VMs `await …ConfigureAwait(false)` and then set bound properties / mutate bound `ObservableCollection`s (`Items.Clear()` in pickers, `Layout`, `WaveformData`, `Report`). In Avalonia that is "call from invalid thread" or silently missed updates | 41 `ConfigureAwait(false)` across `ViewModels/StoryGraph`, `TransitionLab`, `SetHealth` | Possible crashes or a blank canvas |
| F6 | **Silent failures.** Add-Track failure → `Trace.WriteLine`; Transition Lab built with a `null` logger so audition failures are swallowed; Save Template / Save Knowledge give no feedback and unhandled `ReactiveCommand` exceptions | `StoryGraphPanel.axaml.cs`, `WorkstationViewModel` ctor, `TransitionLabViewModel` | CLAUDE.md rule 2 |
| F7 | **Made-up defaults shown as data.** Unanalysed tracks render as 120 BPM / 300 s; a missing knowledge edge scores 50 in Set Health; missing energy targets count as 50 | `TransitionLabViewModel.BuildTrackWaveformData`, `SetHealthDashboardViewModel.LoadAsync` | Misleading health scores |
| F8 | **Pickers unusable on real libraries.** `LibraryEntries.OrderBy(Title).Take(500)`, no search | `TrackPickerViewModel`, `TrackPairPickerViewModel` | Can't find most tracks |
| F9 | **Canvas goes stale.** Changing Bars in Transition Lab doesn't redraw (snapshot `WaveformData`) | `TransitionLabViewModel` | Wrong visual |
| F10 | **Design-system violations.** `Brushes.OrangeRed` etc. instead of tokens, pens allocated in `Render`, `Margin="184,…"`, 17 controls in one row with no overflow | Both canvases, panels | CLAUDE.md rules 3 + UI rules |
| F11 | **Child VMs bypass DI and aren't disposed.** `new StoryGraphViewModel(…)` / `new TransitionLabViewModel(…, null)` inside the Workstation ctor; neither implements `IDisposable` | `WorkstationViewModel` ctor | Leaks, untestable wiring |
| F12 | **Background work with nothing to show.** `TransitionAgent` scans after every analysis (loads up to the SQL cap of full `AudioFeatures` rows incl. embedding blobs, unordered `Take` before the Camelot filter; `_seenKeys` grows unbounded). No UI reads `TransitionCandidates` | `TransitionAgent`, `KnowledgeLifecycleRepository.GetCandidateFeaturesAsync` | Wasted work during batch analysis |
| F13 | **Dead agents.** `PerformanceAnalystAgent` and `ExportAgent` listen for `PerformanceSessionRecordedEvent` / `SetExportRequestedEvent`; nothing publishes either | `Events/KnowledgeLifecycleEvents.cs` | Built-but-never-wired |
| F14 | **Pickers are loose, non-modal windows** (`window.Show()`), so several can stack | `MainViewModel.NavigateToSetProjects`, `StoryGraphPanel` | UX |

---

## 4. Decisions for you to validate

### D1 — Where do Sets live?
- **A. Own top-level page "Sets"** (recommended). Remove the Workstation overlay. Workstation stays
  track-level prep (grid, cues, loops, stems); Sets is set-level planning.
- B. Fold Sets into Flow Builder (Flow Builder gains roles/energy/health; "playlist" becomes the set).
- C. Keep it as a Workstation mode, but render it properly inside the cockpit grid.

*Why A:* the Workstation cockpit is a per-track waveform surface and the set plan is a different
zoom level. The current overlay already proves the cockpit has no room for it. B is attractive
long-term (one set editor), but it collides with "playlists stay containers, SetProject is a plan".
D2 handles the convergence instead.

### D2 — What happens to Flow Builder?
- **A. Converge over time** (recommended). Sets reuses Flow Builder's pieces (track row, pair score,
  transition editor). Flow Builder gets a "Start a set from this playlist" action. Once Sets can
  order, preview and export, Flow Builder becomes "auto-mix playback for a playlist" and nothing more.
- B. Keep both permanently as separate tools.
- C. Retire Flow Builder now and make Sets the only editor (high risk: Flow Builder drives auto-mix playback).

### D3 — Story Graph shape for v1
- **A. Linear set lane + per-slot alternates** (recommended). One horizontal spine of slot cards; a
  slot can hold 0–3 "Plan B" alternates shown stacked under it. Covers "if the crowd is flat, take the
  Reset path" without a free-form graph editor. Uses `SetStoryEdges` underneath.
- B. Full branching node graph (what the vNext doc proposes). Much more UI work; a DJ can't follow
  it at a glance.
- C. Linear only, no branching.

### D4 — Transition Lab
- **A. No nav entry. "The transition editor" becomes one shared component** (recommended):
  Flow Builder's `MixTransitionPanel`/`MixTransitionViewModel`, made host-agnostic. It opens from a
  Sets transition chip, a Flow Builder pair, or Library → right-click two tracks → "Edit transition".
  Delete `TransitionLabPanel` + `TransitionWaveformCanvas`.
- B. Keep Transition Lab as its own page but rebuild it.
- C. Keep both editors.

### D5 — One transition vocabulary
- **A.** Keep `TransitionType` (DSP) + Flow Builder presets as the executable "how". Map
  `KnowledgeTransitionType` to a small *intent* tag shown on edges (Blend, Cut, Drop swap, Echo out,
  Energy reset). Migrate `SetTrackEntity.TransitionArchetype` onto the same intent tag. Turn
  `TransitionTemplates` into **user presets** in the preset picker (recommended).
- B. Keep all four. (Not recommended.)

### D6 — How much should the Copilot do? (memory rule: *ORBIT assists DJs, never replaces them*)
- **A. Only when the DJ asks** (recommended): "Suggest next", "Find a bridge", "Check clashes",
  "Draft a set" (the draft opens as a new set the DJ edits). Nothing auto-applies. Every suggestion
  shows why ("you rated A→B 85; key 8A→9A; ±1.5 BPM") and how much data it rests on.
- B. Ambient suggestions while editing (side panel that refreshes as you edit).
- C. Park the Copilot entirely until Sets is stable.

Also decide: **keep the brief's tuned weights** (BPM 0.45 / confidence 0.45 / harmonic 0.30 /
energy 0.05, stepped Camelot penalty, artist|title dedupe). Recommendation: keep them as-is and treat
them as tunable constants, not locked.

### D7 — Background knowledge agents
- **A. Off by default** (recommended). `TransitionAgent` sits behind an Advanced setting until
  "Suggest next" consumes candidates (P4). Don't start `PerformanceAnalystAgent` or `ExportAgent`
  until each has a real trigger.
- B. Leave running.
- C. Delete the swarm and call the services directly from the actions that need them.

### D8 — Where "played live" evidence comes from
The knowledge graph starts empty for every user: the bridge finder over *verified* edges returns
nothing until the DJ has rated transitions. Sources, in recommended order:
1. **Manual rating** in the shared transition editor (P3), the cheapest and most direct.
2. **Rekordbox history import** via the existing read-only master.db bridge (Companions/RekordboxBridge):
   played-in-order pairs become `LivePerformance` evidence. This gives `PerformanceAnalystAgent`
   its first real trigger.
3. Pro DJ Link live capture (branch `feat/pro-dj-link`; blocked on real packet captures). Later.

Until then the Copilot must **fall back to algorithmic scoring** (`TrackMatchScorer`), labelled as such.

Also: trim tables nobody will use in the next 2 phases (`MixRecipes`, `DjFingerprints`,
`TrackCrowdProfiles`)? Recommendation: **leave them in the schema** (dropping needs migrations) but
add no UI and don't populate them.

### D9 — Canonical spec
`DOCS/WORKSTATION_COCKPIT_FOUNDATION.md` (2026-05-09) is stale: it describes Flow/Stems/Export
tools *inside* the Workstation and a 5-item nav. The app has since moved to pages (Flow Builder,
Cue Forge, Edit Lab). Recommendation: update the spec to "Workstation = track prep cockpit; Sets =
set planning page; transition editor = shared component", and mark both DeepSeek docs as
*proposal, partially implemented* with a status table.

---

## 5. Target design (assuming the recommended options)

### 5.1 Sets page layout
Follows `DOCS/UI_DESIGN_SYSTEM.md`: one header row, at most one toolbar, tokens only, mono for numbers.

```
┌────────────────────────────────────────────────────────────────────────────────┐
│ Sets ▸ Friday @ Club X   [Draft ▾]  [Club · 172–176 · 90 min ▾]     [Export]  │ header (1 primary)
├──────────────┬─────────────────────────────────────────────────┬───────────────┤
│ SETS         │ ENERGY ARC  ▁▂▃▅▆▇▆▃▅▇█▆▂   target ── actual ▬   │ INSPECTOR     │
│ + New set    │                                                 │ (slot | edge) │
│ ● Friday…    │ ┌───────┐ ▸82 ┌───────┐ ▸64! ┌───────┐ ▸91 ┌──── │               │
│ ○ Warmup…    │ │OPENING│ ──▶ │BUILDER│ ───▶ │ PEAK  │ ──▶ │     │ Role [seg]    │
│ ○ Radio…     │ │Title  │     │Title  │      │Title  │     │     │ Energy ──●──  │
│              │ │Artist │     │Artist │      │Artist │     │     │ Notes         │
│              │ │174 8A │     │174 9A │      │175 9A │     │     │ Alternates    │
│              │ │▬▬▬▬░░ │     │▬▬▬▬▬░ │      │▬▬▬▬▬▬ │     │     │  + Add Plan B │
│              │ └───────┘     └───────┘      └───────┘     └──── │               │
│              │                  ╰ Plan B: Title (174 9A)        │               │
├──────────────┴─────────────────────────────────────────────────┴───────────────┤
│ SET HEALTH  Overall 78 · Narrative 81 · Technical 74 · Risk 22   ⚠ 2 issues ▸  │ strip
├────────────────────────────────────────────────────────────────────────────────┤
│ TRACK POOL  [search…] [chips: Analysed · In key · BPM ±4] (VirtualGrid)  drag ▲ │ bottom drawer
└────────────────────────────────────────────────────────────────────────────────┘
```

- **Slot card** (160×~112, radius 6): role label (`section` class), title/artist (2 lines, trimmed),
  `fact` badges for BPM + Camelot, an energy meter showing *target* vs the track's *actual* energy.
  Selected = accent border. Role colour comes from new tokens (`BrushRoleOpening`…), not raw brushes.
- **Transition chip** between cards: compatibility score in `mono`, a status dot (verified / rated /
  algorithmic only), a warning glyph for clashes. Click → inspector shows the edge; double-click →
  shared transition editor (P3).
- **Interaction:** drag from the pool into the lane (insert at the drop point); drag cards to reorder;
  Del removes; Ctrl+Z/Y on a **Sets-local undo stack**; right-click card → Replace, Add Plan B,
  Open in Workstation (prep that track), Remove.
- **States:** no sets → empty state with "New set" / "Start from playlist"; set with 0 slots →
  "Drag tracks from the pool, or Start from playlist"; loading → skeleton cards; DB error → inline
  error card + toast.
- **Set Health strip**: bars, not pills. Shows "—" plus "needs analysis" when inputs are missing,
  never a made-up 50. Clicking ⚠ lists issues with jump-to-slot.

### 5.2 Rendering approach
Use normal Avalonia controls (`ItemsControl` + horizontal `VirtualizingStackPanel` of slot cards,
with chips as items between cards) rather than a custom-drawn canvas. That gives text,
hit-testing, accessibility, theming and drag-drop for free. Draw only the energy arc as a small
custom control (cached pens, no StyledProperty reads in `Render`). Keep `StoryGraphLayout` only if
D3 = B.

### 5.3 State & persistence
- `SetsViewModel` (page), `SetLaneViewModel`, `SlotViewModel`, `TransitionChipViewModel`,
  `SetHealthViewModel`. All registered in DI, `IDisposable`, no `new` of services inside VMs.
- One **`ISetProjectService`** (replacing the unused `SetListService`) owns every write:
  create / rename / delete / duplicate set, add / insert / move / remove slot, set role / energy /
  notes, add / remove alternate. Every call persists immediately.
- **Undo:** a Sets-local `UndoService` instance (not the global singleton). Each op persists on both
  Execute and Undo.
- **Threading rule:** services may use `ConfigureAwait(false)`; VMs never do before touching bound
  state. Add an architecture guard (P0.9).

### 5.4 Shared transition editor
- Extract a host-agnostic `TransitionEditorViewModel` from `MixTransitionViewModel`, with input
  `(outgoingHash, incomingHash, context)` where context = `PlaylistPair(playlistTrackIds)` |
  `SetPair(setTrackIds)` | `AdHoc`.
- Save writes the executable transition to the context's store: `PlaylistTrackTransitions` for
  Flow Builder, transition fields on `SetTracks` (or a new `SetTrackTransitions` table mirroring
  `PlaylistTrackTransitions`, see the open question in P3.2) for Sets. For AdHoc, only a preset/template.
- A **Rate** row (0–100 slider + "Worked live" toggle) appends `ManualRating` / `LivePerformance`
  evidence and recomputes the `TransitionKnowledge` edge. Ratings are append-only.
- Opening a pair that has history pre-fills from the best-rated saved transition for that track pair
  and shows "Rated 85 · used in 2 sets".
- Reuses the existing waveform renderer and `TransitionPreviewPlayer`.

---

## 6. Work plan

Each phase is a separate branch and PR. Each commit builds with 0 errors and keeps
`Tests/SLSKDONET.Tests/Architecture/*` green. Tests use the isolated DB.

### Phase 0: Stop the damage (small, ship first, about 1 PR)
| ID | Work | Fixes |
|---|---|---|
| P0.1 | Persist role/energy edits immediately through a small set-service method. Make `AddTrackAsync` not wipe pending state | F1 |
| P0.2 | Give Story Graph its own undo-service instance; remove `_undoService.Clear()` on the global stack | F4 |
| P0.3 | Marshal all bound-state updates to the UI thread in StoryGraph / TransitionLab / SetHealth / picker VMs. First add a failing test that reproduces the cross-thread update | F5 |
| P0.4 | Replace `Trace.WriteLine` / null logger with `ILogger` + `NotificationCenterService` toasts; add `ThrownExceptions` handlers on the new commands; toast on Save Template / Save Knowledge success | F6 |
| P0.5 | Make pickers modal (`ShowDialog`) and add a search box that queries the DB (no `Take(500)` on the unfiltered list) | F8, F14 |
| P0.6 | Gate `KnowledgeSwarmHost` behind an Advanced setting, default **off** (if D7 = A) | F12, F13 |
| P0.7 | **Hide** the Transition Lab nav entry (it becomes the shared editor in P3). Keep Set Projects visible only if P0.1–P0.3 land. Otherwise hide it as well (CLAUDE.md: unfinished = hidden) | F2 |
| P0.8 | Release hygiene: bump `CurrentSetupVersion`, update `WhatsNewStepViewModel.Items` if anything visible changes | — |
| P0.9 | New architecture guard: ViewModels under `ViewModels/` may not call `ConfigureAwait(false)` (allow-list for existing violators, ratchet down) | F5 regression |

*Not fixed in Phase 0, because Phase 2 replaces them:* F3, F7, F9, F10, F11.

**Phase 0 status (2026-11-02, `fix/phase-0-hardening`): done, not yet visually verified in the running app.**
- P0.1: new `Services/Sets/SetProjectService` is the single write path; every role/energy edit,
  undo and redo is written at once (serialized, latest value wins); AddTrack waits for pending writes.
- P0.2: Story Graph owns its undo stack; slider drags collapse into one undo step.
- P0.3: no `ConfigureAwait(false)` left in the Story Graph / Set Health / set picker VMs. F5 was
  **latent**: SQLite's async calls usually finish synchronously, so it rarely fired. A test with an
  off-thread fake proves the fix (and fails if the bug is reintroduced).
- P0.4: failures log + toast; Add Track no longer uses `Trace.WriteLine`.
- P0.5: pickers open modally; Add Track searches the whole library (title/artist, wildcards escaped).
  The Transition Lab pair picker was left as-is (unreachable after P0.7, deleted in P3.7).
- P0.6: `EnableKnowledgeAgents` (default off) in Settings → Advanced → Knowledge agents. The two
  trigger-less agents are no longer registered.
- P0.7: Transition Lab nav entry, overlay and handler removed; the mode folds back to Prep. Its
  files stay until P3.7. **Set Projects stays visible**, which needed two small extras so its
  controls aren't dead: click-to-select nodes (F2) and track/role labels on nodes (part of F3).
  The inspector is disabled until a slot is selected; empty states added.
- P0.8: `CurrentSetupVersion` 6 → 7; What's New rewritten. The 0.3.2 copy advertised the Copilot,
  Transition Lab and knowledge export, none of which users can reach.
- P0.9: `ViewModelConfigureAwaitGuardTests`, a ratchet over 19 pre-existing files.
- Also fixed: knowledge comment annotations were culture-formatted (`F:0,95` on nl/de/fr).
- Tests: 2953 passed / 11 skipped / 0 failed (baseline 2930 / 11 / 2).

### Phase 1: Ratify (docs only)
| ID | Work |
|---|---|
| P1.1 | Record D1–D9 outcomes in this file |
| P1.2 | Update `WORKSTATION_COCKPIT_FOUNDATION.md` (D9); add a status header to `knowledge-system-vnext.md` and `KNOWLEDGE_LIFECYCLE_ARCHITECTURE.md` marking each slice Done / Partial / Not wired |
| P1.3 | Add the backlog items to `DOCS/OPEN_WORK_PLAN_2026-09-27.md` |

### Phase 2: Sets page (replaces the Story Graph overlay)
| ID | Work |
|---|---|
| P2.1 | `ISetProjectService` + isolated-DB tests (CRUD, insert/move/remove, role/energy/notes, alternates, duplicate set) |
| P2.2 | `SetsPage` + VMs per §5.3; nav entry "Sets" (replaces "Set Projects"); route `OpenSetProjectsRequestedEvent` to the page |
| P2.3 | Set list rail (create / rename / duplicate / delete with confirm), replacing `SetProjectPickerWindow` |
| P2.4 | Set lane: slot cards, transition chips (scores from the existing `TrackMatchScorer`, the same source Flow Builder uses), selection, keyboard, drag reorder |
| P2.5 | Track pool drawer: `VirtualGrid` + search + chips; drag into the lane; "Start from playlist" imports a playlist's order as slots |
| P2.6 | Inspector: slot (role segmented control, energy slider, notes, alternates) and edge (scores as bars, intent tag, "Edit transition" which is a stub until P3, so hidden until then) |
| P2.7 | Set Health strip: remove the made-up 50s; show "needs analysis"; issue list with jump-to-slot |
| P2.8 | Energy arc control (target vs actual), cached pens, tokens |
| P2.9 | Library "Add to Set" → flyout of recent sets + "New set…" with toast confirmation, replacing `AddToSetWindow` |
| P2.10 | Remove the overlay: delete the two panels from `WorkstationPage.axaml`, the child VMs and event handlers from `WorkstationViewModel`; keep `WorkstationMode.StoryGraph/TransitionLab` enum names (session compat) folded to `Waveform` |
| P2.11 | Delete `StoryGraphPanel`, `StoryGraphView`, `TrackPickerWindow`, `SetProjectPickerWindow`, `AddToSetWindow` + their VMs (keep `StoryGraphLayout` only if D3 = B) |
| P2.12 | Design tokens for role colours; screenshots of every empty / loading / error state checked before merge |

**Phase 2 status (2026-11-02, `feat/phase-2-sets-page`):**
- **P2.1 done**, except alternates. `ISetProjectService` covers listing, slot details (library +
  analysis joined; missing BPM/key stay null), rename, duplicate (slots + story edges remapped,
  new Draft), delete (children removed explicitly), insert/move/remove with contiguous positions,
  and notes. **Alternates moved to P2.6:** `SetStoryEdges` can't express "Plan B for this slot"
  without the alternate polluting the spine order, so they need a small schema addition
  (`SetTracks.AlternateForSlotId`, raw SQL). That's decided together with the inspector UI.
- **P2.2 done** (shell): `SetsPage` + `SetsViewModel`, nav entry **Sets**. The old **Set Projects**
  entry, its overlay and the `OpenSetProjectsRequestedEvent` routing deliberately stay until the
  P2.10 checkpoint.
- **P2.3 done:** set rail with New (prompt), Rename (prompt), Duplicate, Delete (confirm), via the
  rail's context menu and the header "…" menu; empty / loading / error states. The selected set
  shows an interim slot list (role, title, artist, BPM, key, "Not in library") until the lane (P2.4).
- Verified in the compiled app on an isolated `--profile`: rail, selection, slot list, duplicate,
  delete-with-confirm and rename all work.

**Phase 2 complete (2026-11-02).** Everything below was also checked in the compiled app on an
isolated profile with a copy of the real library.
- **P2.4 lane:** slot cards (role stripe from `BrushRole*` tokens, mono BPM/key, energy target vs
  analysed energy) joined by transition chips (`TrackPairCompatibilityScorer`, evidence dot,
  warnings). Click/arrows select; Ctrl+arrows, drag, Delete and a context menu edit; a Sets-local
  async undo stack covers every edit (remove snapshots the slot and its alternates).
- **P2.5 pool:** library search with Analysed / Fits key (Camelot neighbours) / BPM ±4% chips
  relative to the selected slot; drag, double-click or + to add; "Add playlist…" appends a
  playlist's library tracks in order as one undoable step and reports what it skipped.
- **P2.6 inspector:** role chips, energy target (saved on release, clearable), notes (saved on
  focus loss), transition breakdown bars, and **Plan B** alternates in a new `SetSlotAlternates`
  table (raw-SQL patch): add from the pool, swap in (role/energy/notes stay with the slot), remove.
- **P2.7/P2.8 health:** `SetHealthService` formulas on real inputs only (no neutral 50s), "—" plus
  a hint when a metric has no data, an issues list that jumps to the slot, and the energy arc as a
  sparkline inside the strip (a separate row above the lane couldn't stay aligned with scrolling).
- **P2.9:** Library "Add to Set" is a submenu of recent sets + "New set…", for a whole selection.
- **P2.10/P2.11:** overlay, Story Graph, Set Projects nav and picker windows deleted.
- **P2.12:** role colours are tokens; empty / loading / error states exist for rail, lane, pool,
  inspector and health; screenshots checked.
- Found live and fixed: sets with differently formatted timestamps sorted wrongly (text ordering in
  SQLite); pool rows were 52px; the health strip clipped its issues button.
- `CurrentSetupVersion` 7 → 8 with new What's New copy.
- Not done here: "Edit transition" from a chip (needs the shared editor, Phase 3); intent tags (P3.5).

### Phase 3: One transition editor
| ID | Work |
|---|---|
| P3.1 | Extract `TransitionEditorViewModel` from `MixTransitionViewModel` (host-agnostic, §5.4). Flow Builder keeps working (regression tests on save/preview/auto-mix playback) |
| P3.2 | Sets transition storage. **Open question:** extend `SetTrackEntity` with trigger/preset fields, or add a `SetTrackTransitions` table mirroring `PlaylistTrackTransitions` (recommended: the mirror table, via `SchemaMigratorService` raw SQL) |
| P3.3 | Rate row → evidence + `KnowledgeLifecycleRepository.RecomputeAsync` (append-only) |
| P3.4 | Pre-fill from history; "Rated · used in N sets" note |
| P3.5 | D5 vocabulary consolidation: intent tag; `TransitionTemplates` surface as user presets |
| P3.6 | Library context "Edit transition…" for two selected tracks (AdHoc context) |
| P3.7 | Delete `TransitionLabPanel`, `TransitionWaveformCanvas`, `TrackPairPicker*`, `TransitionLabViewModel` + nav entry |

**Phase 3 status (2026-11-02, `feat/phase-3-transition-editor`, stacked on Phase 2):**
- **P3.1 done.** `MixTransitionViewModel` saves through a switchable `ITransitionRepository`;
  `LoadSetPairAsync` builds track models from library entries (slot ids as keys, waveform bands
  included). The Sets page opens it as a dialog (inspector "Edit transition…" or double-click a
  chip); chips show a check when the set has a saved transition. "Open in Flow Builder" is hidden
  for set pairs. Playlist behaviour is unchanged.
- **P3.2 done.** `SetTrackTransitions` mirror table (raw SQL, FK cascade from set and both slots,
  unique slot pair) + `SetTransitionRepository`. Slot removal snapshots transitions for undo;
  duplicate copies them; delete clears them.
- **P3.3 done.** "Rate this transition" in every editor host: 0–100 + "played it live" →
  append-only `ManualRating` / `LivePerformance` evidence (`TransitionRatingService`), edge recomputed.
- **P3.4 done.** A pair without a saved transition here starts from the newest one saved for the
  same tracks in any playlist or set, with a note saying where from; stored only on Save.
- **P3.5 deferred.** With Transition Lab gone nothing writes `TransitionTemplates`, and Flow
  Builder's presets are a fixed list. User presets and intent tags each need a UI decision first
  (where a preset is named/managed; whether the intent tag is picked or inferred).
- **P3.6 already covered.** The Library's Mix mode (toggle above a playlist, click two tracks)
  opens this same editor, now with rating.
- **P3.7 done.** Transition Lab view models, panel, canvas, picker, template VM and its event are
  deleted; `TransitionWaveformLayout` stays (the Copilot's clash detector uses it).
- Verified in the compiled app: the editor opens from a set with waveforms and an Auto plan from
  the cues, saves into the set, saves a rating, and the chip/inspector reflect both on close.
- `CurrentSetupVersion` 8 → 9 with combined Phase 2 + 3 What's New copy.

### Phase 4: Copilot in Sets (DJ-initiated only)
| ID | Work |
|---|---|
| P4.1 | **Pick one engine.** Flow Builder has its own bridge finder and A10 flow suggestion; the Copilot has `BridgeFinder` / `SetBuilder`. Benchmark both on the same library with `Tools/CopilotBattleTest` + the existing Flow Builder tests, keep the better one, delete the other |
| P4.2 | "Suggest next" on a slot: ranked list (verified knowledge first, then `TransitionCandidates`, then algorithmic), each with its reason and data basis |
| P4.3 | "Find bridge" between two selected slots → insert as slot or Plan B |
| P4.4 | "Check clashes" → issues in the Set Health strip (vocal overlap, harmonic) with jump-to-slot |
| P4.5 | "Draft a set" (target BPM, minutes, optional seed) → opens as a **new** Draft set; never edits an existing one |
| P4.6 | Re-enable `TransitionAgent` by default only now, if P4.2 actually uses its candidates; fix F12 (order before cap, project the needed columns, bound `_seenKeys`) |

### Phase 5: Export
| ID | Work |
|---|---|
| P5.1 | "Export" in the Sets header → Rekordbox XML playlist + optional USB copy through the existing export path (`ExportAgent` logic called directly or via `SetExportRequestedEvent`) |
| P5.2 | **Decision:** knowledge annotations in the CDJ comment field (`[N:85 T:90 R:12] C:High F:0.95`). Recommendation: opt-in, *appended* to the existing comment, and kept compatible with the Rekordbox merge-mode export that keeps comments edited in Rekordbox |

### Phase 6: Evidence sources (after the Sets page is in use)
| ID | Work |
|---|---|
| P6.1 | Rekordbox history import → `PerformanceSession` → `PerformanceAnalystAgent` (its first real trigger) |
| P6.2 | Event profiles: simple preset dropdown (Club / Warmup / Peak / Radio) feeding BPM range + energy curve; no full editor yet |
| P6.3 | Pro DJ Link: stays on its branch until real packet captures exist |

### Explicitly out of scope
- Workstation Prep refresh (separate plan if wanted).
- Velopack / branch-channel updater (separate decision; the current GitHub updater is shipped).
- `MixRecipes`, `DjFingerprint`, crowd-profile vectors: no UI, no population until there's a consumer.
- Free-form branching graph (unless D3 = B).

---

## 7. Risks
| Risk | Mitigation |
|---|---|
| Flow Builder regressions while extracting the transition editor (it drives auto-mix playback) | P3.1 lands behind tests on save / preview / playback before any Sets wiring |
| Users with sets created in 0.3.x | All existing `SetLists` / `SetTracks` / `SetStoryEdges` rows load in the new page; edges map to Plan B alternates when D3 = A |
| Empty knowledge graph makes the Copilot look broken | Algorithmic fallback, labelled; "based on N rated transitions" provenance |
| Scope creep from the vNext docs | Phase gates; anything not in §6 needs an explicit D-decision |

## 8. Housekeeping (done 2026-11-02)
- Deleted the untracked `.kimi-analysis-fixes/`, `.kimi-merge-ready/`, `.kimi-review/` repo copies.
- Removed the stale `.git/worktrees/ORBIT-Pure-mergecheck` entry (read-only OneDrive folders had
  blocked `git worktree prune`).
- **Baseline at `90a003f`:** build 0 errors / 39 warnings; tests 2930 passed, 11 skipped, **2 failed**.
  Both failures are `AnnotatedCommentFormatterTests`: the formatter used the current culture, so a
  Dutch-locale machine wrote `F:0,95` into Rekordbox comments. CI (en-US) never saw it. Fixed in Phase 0.

## 9. Suggested order & size
| Phase | Size | Depends on |
|---|---|---|
| 0 Stop the damage | S (1 PR) | — |
| 1 Ratify | XS (docs) | your feedback |
| 2 Sets page | L (3–4 PRs: service → page shell → lane/pool → inspector/health/cleanup) | 0, 1 |
| 3 Transition editor | M–L (2–3 PRs) | 2 |
| 4 Copilot | M (2 PRs) | 2, 3 |
| 5 Export | S–M | 2 |
| 6 Evidence sources | M | 3, 5 |
