# ORBIT Workstation — Knowledge System (vNext) Master Plan

Status: **forward-looking architecture / product master plan (vNext)**
Status date: 2026-10-30
Supersedes: nothing. **Layers on top of** [`WORKSTATION_COCKPIT_FOUNDATION.md`](../WORKSTATION_COCKPIT_FOUNDATION.md) (the canonical functional/UI spec) without deleting it.
Audience: engineers extending the Workstation and the set-prep / transition intelligence surfaces.

> **Relationship to the canonical spec.** The cockpit spec defines *what the Workstation
> surface is* (a timeline-first DJ/DAW hybrid; decks are engine primitives, not UI). This
> document defines *what the Workstation knows*: the reusable knowledge graph underneath the
> timeline. Where the two overlap, this document is the source of truth for the knowledge model
> and explicitly flags any place it proposes extending the canonical spec. Nothing here asks to
> reintroduce deck UI, mixer faders, or a page-based app.

---

## 0. Positioning — Orbit is a knowledge system

Orbit is not a music library. Orbit is not a playlist manager. Orbit is not a Rekordbox clone.

Orbit is a **knowledge graph** describing:

- **Tracks** — the core asset plus its crowd/energy fingerprint and preparation state.
- **Transitions** — reusable, rated edges between tracks (the primary asset of the platform).
- **Sets** — performance plans expressed as *slots* and *story graphs*, not ordered track lists.
- **Events** — the context that shapes how a set is built (festival vs warmup vs radio).
- **Crowd reactions** — post-gig feedback that makes future recommendations smarter.
- **DJ intent** — roles, energy targets, and narrative goals attached to slots, not tracks.

The design favours **reusable relationships over static collections**. Over time Orbit should
learn how *this* DJ performs and reuse that knowledge, so every future set is smarter than the
previous one.

### The knowledge pyramid

```
Cue Lab
   │
   ▼
Track Knowledge      (BPM, key, structure, stems, crowd profile, prep state)
   │
   ▼
Transition Knowledge (A → B edges: scores, ratings, templates, real-world success)
   │
   ▼
Set Knowledge        (slots, roles, energy curve, story graph, set health)
   │
   ▼
Performance Knowledge(events, crowd reactions, feedback-weighted recommendations)
   │
   ▼
AI-Assisted DJ Preparation
```

### The paradigm shift

| Legacy mental model | Orbit model |
|---|---|
| Playlist == Prepared Set | **Playlist** is a container ("what belongs together"); **SetProject** is a plan ("what story am I telling") |
| Transition is a one-off edit | **Transition** is a reusable, rated, first-class graph edge |
| Set is a linear list `A→B→C→D` | Set is a **Story Graph** with branching `MixRecipe` paths |
| Role is a property of the track | Role belongs to the **slot** (Track A = "Peak" in set 1, "Closer" in set 2) |
| Recommendations from theory only | Recommendations are **weighted by real-world performance feedback** |

---

## 1. Domain model — the decoupled entity graph

### 1.1 Track (core asset)

Owns the immutable identity of a piece of music and everything derived from it.

- **Identity:** `TrackUniqueHash` (already the canonical join key across `PlaylistTrackEntity`,
  `SetTrackEntity`, `CuePointEntity`).
- **Metadata:** title, artist, album, genre, format, bitrate, duration (already on `PlaylistTrackEntity`).
- **Analysis:** BPM/key (incl. `ManualBPM`/`TagBPM` dual-truth), `Energy`, `Danceability`,
  `Valence`, `Arousal`, `VocalType`, `VocalIntensity`, `DropTimestamp`, `IsDjTool`, spectral data
  (already present).
- **Structure:** cues (`CuePoints` / `OrbitCue` with `CueRole`), grid, phrases
  (`TrackPhrases`), stems (`StemPreferences`).
- **CrowdProfile** (new, see §1.7) — a 0–100 vector enabling flow analysis beyond BPM/key.
- **PreparationStatus** (new, see §1.8) — an explicit state machine with verification history.

### 1.2 Playlist (collection / crate)

- **Existing:** `PlaylistJobEntity` + `PlaylistTrackEntity` (and `PlaylistFolderEntity` for
  nesting). This already satisfies the requirement: a playlist is a container with
  `trackIds[]` and `tags[]`, and has **no sequencing intelligence**.
- **Constraint honoured:** playlists stay organizational. The optimizer
  (`Services/Playlist/PlaylistOptimizer.cs`) may *suggest* an order, but the playlist entity
  itself never becomes a performance plan.

### 1.3 EventProfile (the context) — **new**

Different events require different set construction. A festival mainstage set is not a warmup,
and neither is a radio show.

```
EventProfile {
    id: Guid
    name: string                 // "Festival Mainstage", "Underground Club", "Warmup", …
    targetEnergyCurve: JSON      // ordered 0–100 energy targets over the timeline
    bpmRange: { min, max }
    genres: string[]             // preferred / acceptable genres
    durationMinutes: int
    audienceType: enum           // Festival | Club | Warmup | Opening | Closing | Radio | Podcast
    crowdProfileBias: JSON       // expected crowd vector (see CrowdProfile) to fit against
}
```

`SetProject` optionally links to one `EventProfile`. This is what turns "generic set health" into
"fitness for *this* gig".

### 1.4 SetProject (the performance plan)

- **Existing:** `SetListEntity` (`Data/Entities/SetListEntity.cs`) is the current realisation. It
  already carries `FlowHealth` and `FlowWeightsJson`.
- **Delta:** promote the concept from "saved set-list" to "performance plan":

```
SetProject {                          // evolves SetListEntity
    id: Guid
    name: string
    eventId: Guid?                    // → EventProfile
    venue: string?
    targetDurationMinutes: int?
    status: enum                      // Draft | Planning | Prepared | Ready | Played | Archived
    notes: string?
    flowWeightsJson: string?          // keep existing per-set FlowWeightSettings
    // NOT a track list. Contains slots and story edges instead.
}
```

> **Constraint: strict separation of concerns.** `SetProject` does **not** contain tracks
> directly. It contains `SetSlot` entities and `SetStoryEdge`/`MixRecipe` entities. This is the
> single most important architectural invariant in this document.

### 1.5 SetSlot (the structural context)

- **Existing:** `SetTrackEntity` (`Data/Entities/SetListEntity.cs`) — currently
  `{ TrackUniqueHash, LibraryId, Position, TransitionType, ManualOffset, TransitionReasoning,
  DjNotes, IsRescueTrack, RescueReason }`.
- **Delta:** add the **role** and **energy target**, and split the transition *edge* out of the
  slot (see §1.6):

```
SetSlot {                            // evolves SetTrackEntity
    id: Guid
    setProjectId: Guid
    trackId: string                  // TrackUniqueHash (or LibraryId)
    role: SlotRole                   // NEW — the heart of "role belongs to the slot"
    energyTarget: double?            // NEW — 0–100, target crowd/energy level at this point
    notes: string?
    transitionInId: Guid?            // → TransitionKnowledge edge into this slot
    transitionOutId: Guid?           // → TransitionKnowledge edge out of this slot
    isRescueTrack: bool              // keep existing stress-test rescue semantics
    rescueReason: string?
    position: int                    // linear fallback ordering within the primary path
}

enum SlotRole {
    Opening, Builder, Atmosphere, Peak, Reset, Closer, Wildcard
}
```

**Why the role is on the slot, not the track:** Track A can be a *Peak* weapon in Set 1 and a
*Closer* in Set 2. The same `TrackUniqueHash` appears in multiple `SetSlot` rows with different
roles. Roles are never persisted on `PlaylistTrackEntity`.

### 1.6 Transition (the knowledge graph edge) + TransitionTemplate (the "how")

This is the centrepiece. Split into **three** layers:

**(a) `TransitionKnowledge` — the reusable edge (NEW, first-class graph asset).**

```
Track A
   │
   ▼
TransitionKnowledge  (A → B)
   │
   ▼
Track B
```

```
TransitionKnowledge {
    id: Guid
    sourceTrackId: string            // TrackUniqueHash
    targetTrackId: string            // TrackUniqueHash
    transitionType: enum             // Blend | Cut | EchoOut | Backspin | DropSwap | DoubleDrop
                                     // | Mashup | AcapellaOverlay | StemMix | GenreJump | EnergyReset
    compatibilityScore: double       // 0–100 combined theoretical score
    harmonicScore: double            // 0–100
    bpmScore: double                 // 0–100
    energyScore: double              // 0–100
    vocalScore: double               // 0–100 (clash-safety)
    crowdFitScore: double            // NEW — from CrowdProfile distance (§1.7)
    userRating: double?              // 0–100 explicit DJ rating
    timesUsed: int                   // NEW — empirical usage count
    eventSuccessScore: double?       // NEW — aggregate of post-gig feedback
    verified: bool                   // true once enough real-world evidence exists
    defaultTemplateId: Guid?         // → TransitionTemplate
    notes: string?
    createdAt / updatedAt: datetime
}
```

**(b) `TransitionTemplate` — the reusable "how" (NEW, promoted from DSP params).**

Describes *how* the transition is executed, independently of any specific pair:

```
TransitionTemplate {
    id: Guid
    name: string                     // "Peak Bass Swap (8-bar)", "1-Bar Drop Swap"
    sourceCue: CueRole?              // e.g. Outro / OutroStart
    targetCue: CueRole?              // e.g. Intro / IntroEnd / Drop
    barsBeforeMix: int               // -16, -8, -4 …
    loopSettings: JSON?              // { bars, repeats } for DoubleDrop-style loops
    effects: JSON?                   // { eqSwapLow/Mid/High, crossoverHz, filterSweep, echoDecay, … }
    notes: string?
}
```

The DSP parameter vocabulary already exists end-to-end in
[`Models/Timeline/TransitionModel.cs`](../../Models/Timeline/TransitionModel.cs)
(`TransitionType` + EQ swap, filter sweep, echo, wave-duck, double-drop loop config) and is
persisted by [`PlaylistTrackTransitionEntity`](../../Data/Entities/PlaylistTrackTransitionEntity.cs).
`TransitionTemplate` is the **named, reusable, exportable** packaging of that vocabulary.

**(c) `PlaylistTrackTransition` / slot binding — the *local application*.**

The existing [`PlaylistTrackTransitionEntity`](../../Data/Entities/PlaylistTrackTransitionEntity.cs)
stays, but its role is clarified: it is a **local application** of a template to a specific
adjacent pair (or, in vNext, a `SetSlot → SetSlot` edge), with per-instance overrides. It is
*not* the global knowledge edge.

> **Separation of concerns:** `TransitionKnowledge` answers *"does A→B work, and how well?"*;
> `TransitionTemplate` answers *"how do I execute it?"*; the binding answers *"apply it here, with
> these overrides."* Over time, verified bindings feed the knowledge edge; the knowledge edge
> feeds AI recommendations; templates make those recommendations executable and exportable.

**Transition type mapping to existing code:**

| vNext `transitionType` | Existing equivalent |
|---|---|
| Blend | `TransitionType.EqSwap` / `TransitionArchetype.LongBlend` |
| Cut | `TransitionType.Cut` / `TransitionArchetype.QuickCut` |
| EchoOut | `TransitionType.EchoOut` |
| DropSwap | `TransitionArchetype.DropSwap` / "Drop Sync" preset |
| DoubleDrop | `TransitionType.DoubleDrop` |
| EnergyReset | `TransitionArchetype.EnergyReset` |
| Backspin, Mashup, AcapellaOverlay, StemMix, GenreJump | **new** (extend `TransitionType` / `TransitionArchetype`) |

### 1.7 CrowdProfile — **new (extend `PlaylistTrackEntity`)**

Set-flow analysis beyond BPM and key. Each track gets a 0–100 vector:

```
CrowdProfile {
    energy: 0–100        // already ~ PlaylistTrackEntity.Energy
    aggression: 0–100    // NEW
    emotion: 0–100       // ~ Valence
    darkness: 0–100      // NEW
    groove: 0–100        // ~ Danceability
    mainstream: 0–100    // ~ Popularity / Arousal blend
}
```

Existing seeds: `PlaylistTrackEntity.Energy`, `.Danceability`, `.Valence`, `.Arousal`,
`.VocalIntensity`, `.ManualEnergy`, `.Popularity`. The delta is `aggression`, `darkness`,
`groove`, `mainstream` — either derived columns or a dedicated `TrackCrowdProfile` JSON/table keyed
by `TrackUniqueHash`. This powers §2.4 (crowd-energy modelling) and the `crowdFitScore` on
`TransitionKnowledge`.

### 1.8 PreparationStatus — the DJ preparation state machine (**new**)

Tracks progress through preparation stages:

```
Imported → Analyzed → Grid Verified → Cue Verified → Set Ready → Performance Ready
```

```
PreparationStatus {
    trackId: string
    stage: enum        // Imported | Analyzed | GridVerified | CueVerified | SetReady | PerformanceReady
    stageChangedAt: datetime
    verifiedBy: string?            // user | analysis-run | cue-forge | export
    history: JSON                  // ordered list of {stage, at, by}
}
```

**Mapping to existing signals:**

| Stage | Existing signal |
|---|---|
| Imported | `LibraryEntryEntity` / `PlaylistTrackEntity` row exists |
| Analyzed | `AudioAnalysisEntity` / `AnalysisRunEntity` complete; `AnalysisStatus` |
| Grid Verified | `TrackPhrases` / beat grid verified (tempo `ManualBPM`/`TagBPM` reconciled) |
| Cue Verified | `CuePointEntity` rows via Cue Forge; `CuePointCount > 0` |
| Set Ready | track is a `SetSlot` in a `SetProject` |
| Performance Ready | ≥1 `PerformanceFeedback` row exists (see §1.9) |

The Workstation must **heavily surface** preparation status (per the existing
`WorkstationEligibilitySummary`/readiness-surface lineage in
[`DOCS/ANALYSIS_COVERAGE_HANDOVER.md`](../ANALYSIS_COVERAGE_HANDOVER.md)).

### 1.9 MixRecipe + Story Graph (adaptive performance) — **new**

A set is not always linear. The DJ may know A works with B and C, B with D, C with E. Model this
as a **Story Graph** (a DAG over `SetSlot`s), not a flat ordered list:

```
       B
     ↗   ↘
A →          D
     ↘   ↗
       C
```

```
SetStoryEdge {                     // also "MixRecipePath"
    id: Guid
    setProjectId: Guid
    fromSlotId: Guid
    toSlotId: Guid
    transitionId: Guid?             // → TransitionKnowledge
    probability: double             // prior likelihood of taking this branch (0–1)
    condition: string?              // "if crowd energy < 60 take the Reset path"
}

MixRecipe {                        // the reusable, named version
    id: Guid
    name: string
    startingTrackId: string
    possiblePaths: JSON             // [{ toTrackId, transitionId, weight, condition }]
}
```

`SetProject` owns slots + story edges; `MixRecipe` is the reusable, named branching pattern that
can be dropped into multiple sets. The existing linear `SetTrackEntity.Position` remains as the
**primary path fallback** (the leftmost spine of the graph).

### 1.10 PerformanceFeedback (the feedback loop) — **new**

After a set is played, the DJ rates, per transition and per track:

```
TransitionFeedback {
    id: Guid
    transitionKnowledgeId: Guid?    // which edge this is about
    setProjectId: Guid?
    eventProfileId: Guid?
    transitionSuccess: double       // 0–100
    crowdReaction: double           // 0–100
    mixDifficulty: double           // 0–100
    notes: string?
    ratedAt: datetime
}

TrackEffectiveness {
    id: Guid
    trackId: string
    setProjectId: Guid?
    eventProfileId: Guid?
    effectiveness: double           // 0–100
    notes: string?
}
```

This data **overrides theoretical compatibility** in future recommendations (§2.2) and is what
matures a `TransitionKnowledge` edge from `verified = false` to `true`.

---

## 2. Phase 1 — Database & graph architecture

### 2.1 Storage substrate decision

**Today:** EF Core + SQLite, schema changes via
[`Services/Data/SchemaMigratorService.cs`](../../Services/Data/SchemaMigratorService.cs) raw SQL
patches (never `dotnet ef migrations add`), with `SchemaReconciler` + `FreshDatabaseSchemaParityTests`
guarding parity (see [`CLAUDE.md`](../../CLAUDE.md) and `ARCHITECTURE.md`).

**Design principle:** the model below is expressed storage-agnostically and is implementable today
in SQLite, and portable later to PostgreSQL (if Orbit gains sync/cloud) and Neo4j (if the graph
grows enough to justify a native graph engine). The graph operations in §3 are all expressible as
**adjacency-list + recursive CTE** queries, which SQLite supports (`WITH RECURSIVE`) and Neo4j
expresses natively (`MATCH (a)-[*1..2]->(b)`).

### 2.2 Relational schema (logical DDL)

New tables (all `Guid` PKs unless noted; SQLite types shown, PostgreSQL equivalents in parentheses):

```sql
-- 1. Event context
CREATE TABLE EventProfiles (
    Id                  TEXT PRIMARY KEY,
    Name                TEXT NOT NULL,
    TargetEnergyCurveJson TEXT,
    BpmMin              REAL,
    BpmMax              REAL,
    GenresJson          TEXT,
    DurationMinutes     INTEGER,
    AudienceType        INTEGER,          -- enum
    CrowdProfileBiasJson TEXT
);

-- 2. Set project (extends SetListEntity; add columns, keep table for continuity)
ALTER TABLE SetLists ADD COLUMN EventProfileId TEXT;
ALTER TABLE SetLists ADD COLUMN Venue TEXT;
ALTER TABLE SetLists ADD COLUMN TargetDurationMinutes INTEGER;
ALTER TABLE SetLists ADD COLUMN Status INTEGER;        -- Draft..Archived
ALTER TABLE SetLists ADD COLUMN Notes TEXT;

-- 3. Set slot (extends SetTrackEntity)
ALTER TABLE SetTracks ADD COLUMN SlotRole INTEGER;      -- SlotRole enum
ALTER TABLE SetTracks ADD COLUMN EnergyTarget REAL;
ALTER TABLE SetTracks ADD COLUMN TransitionInId TEXT;
ALTER TABLE SetTracks ADD COLUMN TransitionOutId TEXT;

-- 4. Story graph edges (branching paths)
CREATE TABLE SetStoryEdges (
    Id                  TEXT PRIMARY KEY,
    SetListId           TEXT NOT NULL,   -- FK SetLists.Id
    FromSetTrackId      TEXT NOT NULL,
    ToSetTrackId        TEXT NOT NULL,
    TransitionId        TEXT,
    Probability         REAL NOT NULL DEFAULT 0.5,
    ConditionText       TEXT
);

-- 5. Reusable mix recipes (named branching patterns)
CREATE TABLE MixRecipes (
    Id                  TEXT PRIMARY KEY,
    Name                TEXT NOT NULL,
    StartingTrackId     TEXT NOT NULL,
    PossiblePathsJson   TEXT NOT NULL
);

-- 6. The knowledge graph edge (first-class asset)
CREATE TABLE TransitionKnowledge (
    Id                  TEXT PRIMARY KEY,
    SourceTrackId       TEXT NOT NULL,   -- TrackUniqueHash
    TargetTrackId       TEXT NOT NULL,
    TransitionType      INTEGER NOT NULL, -- enum
    CompatibilityScore  REAL,
    HarmonicScore       REAL,
    BpmScore            REAL,
    EnergyScore         REAL,
    VocalScore          REAL,
    CrowdFitScore       REAL,
    UserRating          REAL,
    TimesUsed           INTEGER NOT NULL DEFAULT 0,
    EventSuccessScore   REAL,
    Verified            INTEGER NOT NULL DEFAULT 0,  -- bool
    DefaultTemplateId   TEXT,
    Notes               TEXT,
    CreatedAtUtc        TEXT NOT NULL,
    UpdatedAtUtc        TEXT NOT NULL,
    UNIQUE (SourceTrackId, TargetTrackId, TransitionType)
);

-- 7. Reusable transition templates (the "how")
CREATE TABLE TransitionTemplates (
    Id                  TEXT PRIMARY KEY,
    Name                TEXT NOT NULL,
    SourceCueRole       INTEGER,         -- CueRole enum
    TargetCueRole       INTEGER,
    BarsBeforeMix       INTEGER,
    LoopSettingsJson    TEXT,
    EffectsJson         TEXT,
    Notes               TEXT
);

-- 8. Track crowd profile (0-100 vector)
CREATE TABLE TrackCrowdProfiles (
    TrackId             TEXT PRIMARY KEY, -- TrackUniqueHash
    Energy              REAL,
    Aggression          REAL,
    Emotion             REAL,
    Darkness            REAL,
    Groove              REAL,
    Mainstream          REAL,
    UpdatedAtUtc        TEXT NOT NULL
);

-- 9. Preparation state machine + history
CREATE TABLE PreparationStatuses (
    TrackId             TEXT PRIMARY KEY,
    Stage               INTEGER NOT NULL,
    StageChangedAtUtc   TEXT NOT NULL,
    VerifiedBy          TEXT,
    HistoryJson         TEXT
);

-- 10. Performance feedback
CREATE TABLE TransitionFeedbacks (
    Id                  TEXT PRIMARY KEY,
    TransitionKnowledgeId TEXT,
    SetListId           TEXT,
    EventProfileId      TEXT,
    TransitionSuccess   REAL NOT NULL,
    CrowdReaction       REAL NOT NULL,
    MixDifficulty       REAL NOT NULL,
    Notes               TEXT,
    RatedAtUtc          TEXT NOT NULL
);

CREATE TABLE TrackEffectivenesses (
    Id                  TEXT PRIMARY KEY,
    TrackId             TEXT NOT NULL,
    SetListId           TEXT,
    EventProfileId      TEXT,
    Effectiveness       REAL NOT NULL,
    Notes               TEXT,
    RatedAtUtc          TEXT NOT NULL
);
```

**Indexes for hot paths:**

```sql
CREATE INDEX IX_TransitionKnowledge_Source ON TransitionKnowledge(SourceTrackId, Verified);
CREATE INDEX IX_TransitionKnowledge_Target ON TransitionKnowledge(TargetTrackId, Verified);
CREATE INDEX IX_TransitionKnowledge_Pair   ON TransitionKnowledge(SourceTrackId, TargetTrackId);
CREATE INDEX IX_TransitionFeedbacks_Edge   ON TransitionFeedbacks(TransitionKnowledgeId);
CREATE INDEX IX_SetStoryEdges_Set          ON SetStoryEdges(SetListId, FromSetTrackId);
CREATE INDEX IX_PreparationStatuses_Stage  ON PreparationStatuses(Stage);
CREATE INDEX IX_TrackCrowdProfiles_Energy  ON TrackCrowdProfiles(Energy);
```

### 2.3 Graph projection (Neo4j / future engine)

The relational tables above are the system of record. The graph is a **projection** for
traversal-heavy AI queries:

```
(:Track {hash, key, bpm, crowd:{…}})
  -[:TRANSITIONS_TO {type, compatibilityScore, harmonicScore, energyScore, vocalScore,
                     userRating, timesUsed, eventSuccessScore, verified}]-> (:Track)
(:SetProject {name, status})
  -[:HAS_SLOT]-> (:Slot {role, energyTarget})
(:Slot) -[:PLAYS]-> (:Track)
(:Slot) -[:NEXT {probability, condition}]-> (:Slot)
(:SetProject) -[:FOR_EVENT]-> (:EventProfile)
(:EventProfile) -[:RECEIVED_FEEDBACK {crowdReaction}]-> (:TransitionFeedback)
```

The same semantics run in SQLite today via `WITH RECURSIVE` (§3.1). If/when Orbit ships a
backend, the identical model ports to Neo4j with no conceptual change — only the query dialect
changes.

### 2.4 Core API / service surface

The user's prompt asks for "API endpoints." ORBIT-Pure is a desktop app, so the "API" is the
**.NET service layer** (plus `IEventBus` messages). Define it as C# interfaces; a future REST/GraphQL
sync layer would expose the same operations 1:1.

```csharp
// Set project management
interface ISetProjectService {
    Task<SetProject> CreateAsync(SetProjectDraft draft);
    Task<SetProject> AddSlotAsync(Guid setProjectId, SetSlotDraft slot);
    Task<SetProject> SetSlotRoleAsync(Guid slotId, SlotRole role);
    Task<SetProject> AddStoryEdgeAsync(Guid setProjectId, Guid fromSlotId, Guid toSlotId,
                                       Guid? transitionId, double probability, string? condition);
    Task<IReadOnlyList<SetStoryPath>> EnumeratePathsAsync(Guid setProjectId);
    Task<SetHealthReport> GetSetHealthAsync(Guid setProjectId);
}

// Transition knowledge graph
interface ITransitionKnowledgeService {
    Task<TransitionKnowledge> UpsertAsync(TransitionKnowledge knowledge);  // from Transition Lab
    Task<TransitionKnowledge?> GetAsync(string sourceTrackId, string targetTrackId);
    Task<IReadOnlyList<TransitionKnowledge>> QueryAsync(TransitionQuery query); // by type/verified/score
    Task VerifyAsync(Guid transitionId, bool verified);
    Task<TransitionTemplate> SaveTemplateAsync(TransitionTemplate template);
}

// Feedback loop
interface IPerformanceFeedbackService {
    Task IngestTransitionFeedbackAsync(TransitionFeedback feedback);
    Task IngestTrackEffectivenessAsync(TrackEffectiveness feedback);
    Task RecomputeEdgeScoresAsync(string sourceTrackId, string targetTrackId);
}

// AI copilot (§3)
interface IOrbitCopilot {
    Task<SetBuildResult> BuildSetAsync(SetBuildRequest request);
    Task<IReadOnlyList<BridgePath>> FindBridgeAsync(string sourceTrackId, string targetTrackId, int maxHops);
    Task<IReadOnlyList<SetHealthIssue>> DetectClashesAsync(Guid setProjectId);
    Task<IReadOnlyList<Route>> GenerateAlternativeRoutesAsync(Guid slotId, int count);
    Task<IReadOnlyList<MashupOpportunity>> ScoutMashupsAsync(Guid setProjectId);
}
```

REST mapping (future): `POST /sets`, `POST /sets/{id}/slots`, `PATCH /sets/{id}/slots/{slotId}/role`,
`POST /sets/{id}/edges`, `GET /sets/{id}/paths`, `GET /sets/{id}/health`,
`GET /copilot/bridge?from=&to=`, `GET /copilot/routes?slot=`, `POST /feedback/transitions`, etc.

---

## 3. Phase 2 — AI copilot & algorithmic logic

The copilot is a **preparation assistant**, not a recommendation gimmick. Every suggestion must be
traceable to a scored edge and carry an explanation string (the codebase already has the
`TransitionReasoning` / `TransitionSuggestion.Reasoning` / `Reasoning` forensic-log pattern — keep it).

### 3.1 Bridge Finder — automated bridge-track discovery

**Goal:** connect Track A (slot 1) to Track Z (slot 2) with 1–2 bridge tracks, harmonically and
energetically coherent.

```
Inputs:  source S, target T, maxHops ∈ {1,2}, constraints {
           bpmBandPct,           // ±8% default per hop
           maxHarmonicDistance,  // ≤2 per hop
           energyMonotonic,      // prefer non-decreasing toward T
           crowdFitWeight
         }

1. Build candidate edge set E:
   E_verified  = { e ∈ TransitionKnowledge : e.Verified = true }
   E_theory    = synthesize edges from TrackMatchScorer / SectionVectorService
                 where harmonicScore ≥ threshold and bpmScore ≥ threshold

2. Edge weight w(e) =
     α · compatibilityScore
   + β · userRating        (0 if null)
   + γ · eventSuccessScore (0 if null)
   + δ · ln(1 + timesUsed)         // trust mature edges
   + ε · crowdFitScore
   where β,γ,δ scale up as evidence accumulates (cold start ⇒ theoretical dominance).

3. Path search:
   - depth 1: direct edge S→T (verified preferred).
   - depth 2: WITH RECURSIVE traversal S→B→T, pruned by constraints.
     (Neo4j: MATCH (s)-[:TRANSITIONS_TO*1..2]->(t).)
   - rank by product/sum of edge weights; break ties by harmonic continuity.

4. Fallback: if no path satisfies constraints, relax bpmBandPct to ±12% and harmonic distance to 3,
   and mark the result "suggested (unverified)" instead of "verified".

Output: ranked BridgePath[] {
    hops: [S, B?, T],
    perHop: [{ transitionId?, transitionType, harmonicScore, energyScore, confidence, reasoning }],
    overallConfidence
}
```

**Neo4j query shape** (for the future engine):

```cypher
MATCH (s:Track {hash:$from}), (t:Track {hash:$to})
MATCH path = (s)-[:TRANSITIONS_TO*1..2]->(t)
WHERE all(r IN relationships(path)
          WHERE r.harmonicScore >= 70 AND r.energyScore >= 60)
RETURN path
ORDER BY reduce(acc = 1.0, r IN relationships(path) | acc * (0.5*r.compatibilityScore + 0.3*coalesce(r.userRating,0) + 0.2*log(1+r.timesUsed)))
LIMIT 10
```

### 3.2 Performance Feedback Loop — weighting the graph with reality

**Ingestion** (§1.10) feeds three aggregations back into `TransitionKnowledge`:

1. `timesUsed++` on every played edge.
2. `eventSuccessScore` = decayed mean of `crowdReaction` (and `transitionSuccess`) across events,
   with more recent events weighted higher and **segmented by `EventProfile`** (a transition that
   kills on mainstage may flop in a warmup — this is the "event-aware" part).
3. `verified` flips `true` when `timesUsed ≥ N` (e.g. 3) **and** `eventSuccessScore ≥ θ` (e.g. 70).

**Recommendation weighting:** future rankings use

```
finalScore(e) = (1 - λ(e)) · theoretical(e) + λ(e) · empirical(e)
λ(e) = min(0.7, 0.15 · ln(1 + e.timesUsed))
```

Cold edges (never played) are driven by theory; as evidence accrues, **real-world success overrides
theoretical compatibility**, exactly as the product requires. A theoretically "perfect" harmonic
match that crowds repeatedly reject gets down-weighted; a "theoretically risky" energy reset that
consistently works gets promoted.

### 3.3 Advanced Set Health — scoring the narrative, not just transitions

Extend the existing `SetHealthIssues` (key/energy/vocal clash) into a full narrative score.

**Metrics (0–100):**

| Metric | Definition |
|---|---|
| Transition Quality | mean of `finalScore(e)` over all primary-path edges |
| Harmonic Continuity | 1 − (mean harmonic distance / 12), weighted by slot importance |
| Energy Flow | fit of slot `EnergyTarget`/track energy to `EventProfile.TargetEnergyCurve` |
| Genre Consistency | entropy of genre sequence vs `EventProfile.genres` |
| Novelty | 1 − (fraction of edges already used by this DJ in prior sets) |
| Crowd Recovery Potential | presence + quality of `Reset`/`Atmosphere` slots after each `Peak` |
| Peak Placement | how well peaks align with the event's expected peak window (from `EventProfile`) |
| Preparation Completeness | mean `PreparationStatus` progress across all slotted tracks |

**Outputs:**

```
SetHealthReport {
    overallScore           // weighted blend of the 8 metrics
    narrativeScore         // energy flow + peak placement + novelty + recovery
    technicalScore         // transition quality + harmonic continuity + genre consistency
    performanceRiskScore   // 1 − preparation completeness, penalised by unverified edges
    issues: SetHealthIssue[]  // reuses KeyClashIssue / EnergyGapIssue / VocalClashIssue
    weakestSlotId
}
```

### 3.4 Remaining copilot abilities

- **Set Builder** — constrained path construction: "60-min festival set, start X, end Y, max +8%
  BPM." Enumerate primary paths through the story graph meeting duration/BPM/energy constraints,
  then rank by `SetHealthReport.overallScore`.
- **Clash Detector** — runs the existing `VocalOverlapReport` + `KeyClashIssue` + `EnergyGapIssue`
  logic across the whole timeline, plus new vocal-conflict sweep over slotted tracks.
- **Alternative Route Generator** — for a given `SetSlot`, run §3.1 over the slot's outgoing
  `SetStoryEdge`s to produce `N` divergent peak-time pathways.
- **Mashup Scout** — cross-reference `SavedDouble` candidates, `VocalOverlapReport` (acapella
  opportunities), and stem availability to surface acapella-overlay / double-drop pairings.

---

## 4. Phase 3 — Workstation UI/UX blueprint

### 4.1 Reconciliation with the canonical cockpit

The cockpit stays: header (transport + tool toggles), main canvas (timeline), contextual inspector,
bottom drawer, left nav. Decks remain engine primitives. Two knowledge surfaces are added **on top**:

1. **Set Projects** — the *story-graph* surface. Lives as a **new left-nav module** (proposed
   extension of the canonical `Dashboard | Library | Analysis | Workstation | Settings` list) and,
   when editing, reuses the Workstation timeline with a **Story overlay**.
2. **Transition Lab** — the *knowledge-graph editor*. Lives as a **tool/lane within the Workstation**
   (or a companion surface), never a separate deck UI.

> **Spec change flag.** The canonical spec locks the left-nav list. Adding `Set Projects` and
> `Transition Lab` is an intentional, ratifiable extension — recorded here so the maintainer can
> fold it into `WORKSTATION_COCKPIT_FOUNDATION.md` deliberately rather than have it creep in.

### 4.2 Set Projects — planning branching paths without it feeling like a playlist

**Layout:** a **Story Graph canvas** (primary), with a slot inspector (right) and a track pool
(bottom drawer, reusing the existing track-pool/drawer).

```
┌──────────────────────────────────────────────────────────────────┐
│ HEADER: transport · Set name · EventProfile chip · Set Health     │
├──────────────────────────────────────────────────────────────────┤
│          │   STORY GRAPH CANVAS                                   │
│          │                                                       │
│          │     [Opening]──▶[Builder]──▶[Peak]──▶[Reset]          │
│          │                    │           │                       │
│          │                    └──▶[Peak2]─┘──▶[Closer]           │
│          │                                                       │
│          │   (nodes = slots w/ role badges + energy target)      │
│          │   (edges = transitions w/ score + type + verified)    │
│          │                                                       │
│          │                       INSPECTOR (slot/edge detail)     │
├──────────────────────────────────────────────────────────────────┤
│ BOTTOM DRAWER: track pool · bridge suggestions · prep status      │
└──────────────────────────────────────────────────────────────────┘
```

**How it stays distinct from a playlist:**

- Nodes are **slots**, not tracks. The primary label is the **role** ("Peak", "Closer"), with the
  track as secondary; a track is only ever *slotted into* a role.
- Each node shows an **energy target** (0–100) and the track's actual `CrowdProfile` beneath it,
  making the energy *arc* visible — a playlist has no arc.
- Branching edges are first-class: a slot can fan out to 2–3 next-slots with a small probability /
  condition label ("if crowd < 60 → Reset"). This is a **story graph**, not an ordered list.
- The linear `Position` spine renders as the primary path; alternate branches render as dimmer
  parallel edges.
- A persistent **Set Health** strip (narrative / technical / risk) updates live as slots/edges change.

**Role assignment UX:** drag a track from the bottom drawer onto the canvas → it becomes a slot;
right-click / inspector assigns `SlotRole` from a segmented control (`Opening … Wildcard`) and sets
`energyTarget` with a slider. Reusing a track in a second set automatically gets a fresh slot and
role — never mutating the track.

### 4.3 Transition Lab — audition, score, verify, save to the graph

**Layout:** a focused two-pane surface built on the **existing transition engine**, not a deck UI.

```
┌──────────────────────────────────────────────────────────────────┐
│ HEADER: Track A (outgoing)  →  Track B (incoming)                │
├──────────────────────────────────────────────────────────────────┤
│  WAVEFORM OVERLAP (timeline-first)                               │
│  - aligned phrase/beat grid, cue markers, mix-out/in handles     │
│  - transition window (bars-before-mix) highlighted               │
│  - real-time TransitionModel preview (EQ swap / filter / echo)   │
├──────────────────────────────────────────────────────────────────┤
│  INSPECTOR (right):                                              │
│  - TransitionType (segmented) + TransitionTemplate selector     │
│  - template editor: sourceCue / targetCue / barsBeforeMix /      │
│    loopSettings / effects                                        │
│  - score panel: harmonic · bpm · energy · vocal · crowdFit       │
│  - RATE row: userRating slider · "Verified" toggle              │
│  - [Audition] [Save as template] [Save to knowledge graph]      │
└──────────────────────────────────────────────────────────────────┘
```

**Workflow (the four verbs):**

1. **Audition** — loads A/B internally (decks as engine primitives), applies the `TransitionModel`,
   plays the overlap window. No faders/jog wheels; transport only.
2. **Score** — the system pre-fills `harmonicScore/bpmScore/energyScore/vocalScore/crowdFitScore`
   from `TrackMatchScorer` + `VocalOverlapReport` + `CrowdProfile` distance; the DJ adjusts
   `userRating`.
3. **Verify** — toggling `verified` (or a "This worked live" shortcut) flips the edge to
   evidence-backed; ≥1 `TransitionFeedback` row auto-suggests verification.
4. **Save** — writes/updates the `TransitionKnowledge` edge **and** optionally persists the
   parameter set as a named `TransitionTemplate`, making the recipe reusable and exportable
   (Rekordbox/export path in [`DOCS/REKORDBOX_EXPORT_ARCHITECTURE.md`](../REKORDBOX_EXPORT_ARCHITECTURE.md)).

**UI constraints (CLAUDE.md-compliant):** no raw hex/magic margins (use `DesignTokens.axaml`, 4px
grid); scores shown as bars/percentages, not coloured pills; icon-only buttons get tooltips; no
dead/placebo controls — every control binds to a real command or is hidden.

---

## 5. Migration & phasing into the codebase

Non-destructive, build-stable slices. Each lands via `SchemaMigratorService` raw SQL and must keep
`dotnet build` + `Architecture/*` + `FreshDatabaseSchemaParityTests` green, with new VM logic backed
by isolated-DB tests (`ORBIT_DB_PATH` + `TestDatabaseSetup`).

1. **Slice K1 — schema + entities.** Add the new tables/columns from §2.2 as raw-SQL patches;
   add matching EF entities + `DbSet`s in `Data/AppDbContext.cs`; extend `SetTrackEntity` with
   `SlotRole`/`EnergyTarget`; add `TransitionKnowledge`, `TransitionTemplate`, `EventProfile`,
   `SetStoryEdge`, `MixRecipe`, `TrackCrowdProfile`, `PreparationStatus`, `TransitionFeedback`,
   `TrackEffectiveness`. No UI yet.
2. **Slice K2 — knowledge service.** Implement `ITransitionKnowledgeService`,
   `IPerformanceFeedbackService` + §3.2 weighting; unit tests for the `λ(e)` decay and
   `verified` thresholds.
3. **Slice K3 — bridge finder.** Implement §3.1 with `WITH RECURSIVE` over `TransitionKnowledge`,
   falling back to `TrackMatchScorer`/`SectionVectorService`; tests for 1- and 2-hop, constraint
   pruning, and fallback marking.
4. **Slice K4 — set health.** Implement §3.3 over the story-graph primary path; reuse/extend
   `SetHealthIssues`; expose `SetHealthReport`.
5. **Slice K5 — UI (Set Projects story canvas).** Story graph overlay on the Workstation timeline;
   slot nodes + role inspector + energy targets; branching edges; prep-status surfacing.
6. **Slice K6 — UI (Transition Lab).** Waveform-overlap audition + template editor + score/rate/
   verify/save, binding the existing `TransitionModel`/`TransitionEngine`.
7. **Slice K7 — copilot wiring + export.** Set Builder / Alternative Routes / Mashup Scout as
   commands; template → Rekordbox/export hand-off; runtime QA gate
   ([`runtime-qa-cockpit-gate.md`](runtime-qa-cockpit-gate.md)).

---

## 6. Non-goals & guardrails

- **Do not merge Playlist and SetProject.** Any PR that reintroduces track lists as the set model
  is out of scope.
- **Do not reintroduce deck UI / mixer faders / jog wheels / tabs-as-primary.** Decks stay engine
  primitives (canonical spec §2, §6).
- **Do not vibecode** (per `CLAUDE.md`): no placebo controls, no silent `catch {}`, tokens + SVG
  icons, no `dotnet ef migrations add`, isolated-DB tests only.
- **Roles live on slots only.** Never add `SlotRole` to `PlaylistTrackEntity`.
- **Feedback data is append-only.** Ratings are immutable facts; recomputation derives aggregates,
  never rewrites history.
- **This document does not supersede** `WORKSTATION_COCKPIT_FOUNDATION.md`. It is the knowledge
  model layered beneath it.

## 7. Open decisions for the maintainer

1. Ratify the left-nav extension (`Set Projects`, `Transition Lab`) into the canonical spec.
2. CrowdProfile dimensions: derive `aggression/darkness/groove/mainstream` from existing DSP
   features vs. ship a `TrackCrowdProfiles` table and backfill lazily.
3. Graph substrate: keep SQLite `WITH RECURSIVE` (recommended for now) vs. introduce an embedded
   graph index when edge count passes a threshold (e.g. >100k verified edges).
4. `verified` thresholds (`N`, `θ`) — proposed 3 plays / 70 success, tunable per DJ.
