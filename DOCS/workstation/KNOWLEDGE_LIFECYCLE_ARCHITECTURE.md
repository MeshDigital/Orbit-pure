# ORBIT Phase 2 — Knowledge Lifecycle & Agent Swarm (vNext)

Status: **forward-looking architecture blueprint (vNext)**
Status date: 2026-10-30
Builds on: [`DOCS/workstation/knowledge-system-vnext.md`](knowledge-system-vnext.md) (Phase 1 — static knowledge schema)
Audience: engineers implementing the autonomous knowledge-acquisition pipeline.

> **Relationship to Phase 1.** Phase 1 established the *static* entity graph (Track, Playlist,
> EventProfile, SetProject, SetSlot, TransitionKnowledge, TransitionTemplate, feedback tables).
> Phase 2 makes that graph *alive*: it turns `TransitionKnowledge` from a hand-edited row into a
> **computed aggregate** over atomic `TransitionEvidence`, adds automatic candidate discovery,
> confidence/freshness decay, a per-DJ fingerprint, plan-vs-reality reconciliation, and a swarm of
> local specialised agents that keep the graph current without a human driving each step.
>
> Everything here is a **local, in-process C# pipeline**, not a cloud service or an LLM loop. The
> "agents" are specialised .NET services scheduled over the existing background infrastructure
> (`IEventBus`, `AnalysisQueueService`'s dispatch loop, `BackgroundJobQueue`, Avalonia worker
> threads), coordinated through the shared SQLite store (`AppDbContext`).

> **Truth vs. Aggregation (strict separation).** Two kinds of table coexist and must never be blurred:
>
> | Kind | Tables | Rule |
> |---|---|---|
> | **Source of truth** | `TransitionEvidence`, `PerformanceSession`, `PerformanceSessionTrack` | append-only facts; never recomputed from anything else |
> | **Derived / materialized** | `TransitionKnowledge`, `DjFingerprint` | recomputed from source-of-truth rows; never hand-edited |

> **Implementation note.** The authoritative entity shapes live in the code, not this doc:
> `Data/Entities/KnowledgeLifecycleEnums.cs`, `Data/Entities/TransitionEvidenceEntity.cs`,
> `Data/Entities/TransitionKnowledgeEntity.cs`, `Data/Entities/PerformanceSessionEntity.cs`,
> `Data/Entities/DjFingerprintEntity.cs`, and `Models/Musical/CrowdProfileVector.cs`. The C# blocks
> below are illustrative; the code is the contract.

---

## 0. Positioning — a living DJ intelligence platform

A static schema records *what you know*. A knowledge lifecycle decides *what to learn, how much to
trust it, when it expires, and how it should change a specific DJ's recommendations.*

```
                    ┌─────────────────────────────────────────────────────────┐
  analysis events   │  AGENT SWARM (local services, event-driven)             │
  & background scan │                                                        │
        │           │  Analysis Agent ──► Track Knowledge                     │
        ▼           │  Transition Agent ─► Candidates + Evidence              │
   ┌─────────┐      │  Set Builder Agent ─► Story Graphs (fingerprint-filtered)│
   │ IEventBus│◄────►  Performance Analyst ─► Plan-vs-reality diff + Evidence │
   │ (queues)│      │  Export Agent ─► USB / Rekordbox                        │
   └────┬────┘      └─────────────────────────────────────────────────────────┘
        │                               │ writes / reads
        ▼                               ▼
   SQLite (AppDbContext) ── TransitionKnowledge (aggregate)
                            TransitionEvidence (atomic)
                            TransitionCandidate (discovered)
                            DjFingerprint / PerformanceSession
```

The five pillars, in order:

1. **Autonomous acquisition** — transitions are *found*, not only authored.
2. **Confidence & decay** — knowledge is weighted and ages.
3. **DJ Fingerprint** — the graph is tuned to *this* DJ.
4. **Plan vs. reality** — the live set is the ground truth that feeds back.
5. **Agent swarm** — specialised, event-driven workers keep the cycle running.

---

## 1. Pillar 1 — Autonomous Knowledge Acquisition & Evidence Aggregation

### 1.1 `TransitionCandidate` — auto-discovered, not yet trusted

A background scan proposes candidate edges. A candidate is a *hypothesis*; it has no evidence yet,
so it must never be surfaced as knowledge until promoted.

```csharp
namespace SLSKDONET.Data.Entities;

[Table("TransitionCandidates")]
public class TransitionCandidateEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public string SourceTrackId { get; set; } = string.Empty;   // TrackUniqueHash
    public string TargetTrackId { get; set; } = string.Empty;   // TrackUniqueHash

    // Theoretical signals produced by the scanner (all 0-100).
    public double CompatibilityScore { get; set; }
    public double HarmonicScore { get; set; }
    public double BpmScore { get; set; }
    public double EnergyScore { get; set; }
    public double PhraseScore { get; set; }        // outro->intro / phrase-alignment fit

    public string DiscoverySource { get; set; } = string.Empty; // "HarmonicScan"|"PhraseScan"|"EmbeddingScan"
    public DateTime DiscoveredAtUtc { get; set; } = DateTime.UtcNow;

    public bool IsPromoted { get; set; }            // promoted -> TransitionKnowledge
    public Guid? PromotedKnowledgeId { get; set; }
}
```

Discovery sources reuse the existing scoring stack: `TrackMatchScorer` / `SectionVectorService`
(`Services/Similarity/`) for harmonic/beat/drop/outro-intro, and `VocalOverlapReport` for vocal safety.

### 1.2 `TransitionEvidence` — the atomic unit of truth

`TransitionKnowledge` is redesigned to be an **aggregate** over evidence. Every claim about an edge
is stored as one immutable `TransitionEvidence` row with a **source** and a **weight**.

```csharp
public enum TransitionEvidenceSource
{
    HarmonicMatch      = 0,   // algorithmic: key/camelot distance
    PhraseMatch        = 1,   // algorithmic: phrase/energy alignment
    CrowdSuccess       = 2,   // real-world: rated crowd reaction
    ManualRating       = 3,   // explicit DJ rating in Transition Lab
    PerformanceHistory = 4,   // real-world: actually played live
}

[Table("TransitionEvidence")]
public class TransitionEvidenceEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public Guid TransitionKnowledgeId { get; set; }   // FK -> TransitionKnowledge

    public TransitionEvidenceSource Source { get; set; }
    public double Score { get; set; }                 // 0-100, normalised per source
    public double Weight { get; set; }                // source credibility (see §6.1)

    public string? ContextJson { get; set; }          // event profile id, venue, session id, …
    public Guid? PerformanceSessionId { get; set; }   // FK when evidence came from a live set
    public DateTime RecordedAtUtc { get; set; } = DateTime.UtcNow;
}
```

### 1.3 `TransitionKnowledge` — the computed aggregate (v2)

Phase 1 defined `TransitionKnowledge` as a hand-maintained row. Phase 2 keeps the same table shape
conceptually but derives its scores/confidence/freshness **from evidence** rather than storing them
as editable facts.

```csharp
public enum KnowledgeConfidence { Unknown = 0, Experimental = 1, Likely = 2, Confirmed = 3 }

[Table("TransitionKnowledge")]
public class TransitionKnowledgeEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public string SourceTrackId { get; set; } = string.Empty;
    public string TargetTrackId { get; set; } = string.Empty;
    public int TransitionType { get; set; }            // TransitionType / archetype enum

    // ── Aggregated scores (recomputed by the Transition Agent, never hand-edited) ──
    public double AggregateScore { get; set; }         // weighted blend (§6.1)
    public double HarmonicScore { get; set; }
    public double BpmScore { get; set; }
    public double EnergyScore { get; set; }
    public double VocalScore { get; set; }
    public double CrowdFitScore { get; set; }

    public int EvidenceCount { get; set; }
    public KnowledgeConfidence Confidence { get; set; }
    public double Freshness { get; set; }              // 0-1 (§6.3)
    public DateTime LastVerifiedAtUtc { get; set; }
    public bool IsClassic { get; set; }                // "Classic" tag -> exempt from decay

    public bool Verified { get; set; }                 // legacy convenience flag == Confidence >= Likely
    public Guid? DefaultTemplateId { get; set; }
    public string? Notes { get; set; }
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
```

**Promotion rule (candidate → knowledge):** a `TransitionCandidate` is promoted when its
`CompatibilityScore >= θ_promote` (e.g. 70) **or** when it receives its first non-algorithmic
evidence (a manual rating or a live play). Promotion creates the `TransitionKnowledge` row and one
`TransitionEvidence` seeded from the candidate's scores (`HarmonicMatch`/`PhraseMatch` sources).

---

## 2. Pillar 2 — Knowledge Confidence & Decay

### 2.1 Confidence tiers

Confidence is a function of **volume and type** of evidence — not a single stored flag.

```
confidencePoints(K) = Σ over evidence e :  e.Weight × freshness(e.RecordedAtUtc)

tier(K) = Unknown       if confidencePoints < 1.0
        = Experimental  if < 2.5
        = Likely        if < 6.0
        = Confirmed     if >= 6.0
```

Rationale: one algorithmic match (weight 1.0) is only `Experimental`. A manual rating (3.0) or a
live play with good crowd reaction (3.0+) jumps straight to `Likely`. Two independent strong
real-world confirmations reach `Confirmed`. See §6.1 for the source-weight table.

### 2.2 KnowledgeFreshness — exponential decay

Knowledge ages. A transition hammered in 2021 should not dominate a 2028 recommendation unless it
kept being re-used or is tagged `Classic`.

```
λ = ln(2) / 24 months ≈ 0.029 per month     (half-life 24 months; per-DJ and genre-aware later)

freshness(K) = 100 · e^(−λ · yearsSinceLastUse(K)),   clamped to [0, 100]
freshness(K) = 100.0 if K.IsClassic                    # Classic is decay-exempt
```

Worked example (your 2021 → 2028 case): 7 years ≈ 3.5 half-lives → `100 · e^(−0.029·84) ≈ 8.8`, so
the edge retains ~8.8 of 100 freshness unless re-used. A re-use bumps `LastUsedAtUtc` and restores
freshness.

### 2.3 Final recommendation weight

```
confidenceWeight = { Confirmed: 1.00, Likely: 0.85, Experimental: 0.60, Unknown: 0.35 }

recScore(K, DJ) = AggregateScore(K) × confidenceWeight(tier(K)) × freshness(K) × djFit(K, fingerprint)
```

`djFit` (§3) is the fingerprint filter — the last multiplier that makes recommendations *personal*.

---

## 3. Pillar 3 — The DJ Fingerprint

`DjFingerprint` profiles *how this DJ plays*, so the same graph ranks differently for a long-blend
DJ than a drop-swap DJ.

```csharp
[Table("DjFingerprints")]
public class DjFingerprintEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    // Weighted preference over transition types, e.g. { LongBlend: 0.9, DropSwap: 0.2 }.
    public string? PreferredTransitionTypesJson { get; set; }

    // Preferred energy arc, e.g. { Open: 30, Build: 55, Peak: 90, Reset: 40, Close: 70 }.
    public string? EnergyCurveProfileJson { get; set; }

    // Avoidance patterns, e.g. ["vocal->vocal", "bpm-drop>8%", "key-jump>2"].
    public string? AvoidancePatternsJson { get; set; }

    public string? GenreAffinityJson { get; set; }         // learned genre preferences
    public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
}
```

**Filtering:** `djFit(K, DJ)` starts at 1.0 and subtracts a penalty per matching avoidance pattern
and per transition-type affinity mismatch (cosine distance between the edge's type and the
fingerprint's weighted preference vector). It is recomputed by the **Performance Analyst Agent**
from actual play behaviour (§5.4), so the fingerprint converges to observed practice, not stated
preference.

---

## 4. Pillar 4 — Plan vs. Reality: `PerformanceSession`

A `SetProject` is a plan; a DJ set is reality. The delta is where the most valuable knowledge lives:
the **live audibles** (Track X → Track Y on the fly) that the plan never anticipated.

```csharp
[Table("PerformanceSessions")]
public class PerformanceSessionEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public Guid? SetProjectId { get; set; }               // the plan this session was based on
    public Guid? EventProfileId { get; set; }
    public string? Venue { get; set; }
    public DateTime StartedAtUtc { get; set; }
    public DateTime EndedAtUtc { get; set; }
    public string? Notes { get; set; }

    public ICollection<PerformanceSessionTrackEntity> Tracks { get; set; } = new();
}

[Table("PerformanceSessionTracks")]
public class PerformanceSessionTrackEntity
{
    [Key] public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }
    public string TrackId { get; set; } = string.Empty;   // TrackUniqueHash
    public int SequenceIndex { get; set; }

    public int? TransitionType { get; set; }              // actual transition INTO this track
    public double? PlannedDurationSeconds { get; set; }
    public double? ActualDurationSeconds { get; set; }
    public double? CrowdReaction { get; set; }            // 0-100, optional post-gig rating

    public bool WasPlanned { get; set; }                  // present in the SetProject?
    public Guid? PlannedSlotId { get; set; }
    public string? DeviationReason { get; set; }          // "crowd energy dropped", "request", …
}
```

### 4.1 Reconciliation logic (the "live audible" extractor)

The **Performance Analyst Agent** runs this on each recorded session:

```
1. Load the plan's primary path as an ordered slot list P = [p0, p1, …, pN].
2. Load the session's actual sequence A = [a0, a1, …, aM] (SequenceIndex order).
3. Align P and A with a weighted edit-distance (LCS) so insertions/deletions/substitutions
   are tracked, not just string equality.
4. For every adjacent pair in A that is NOT an adjacent pair in P:
       emit TransitionEvidence {
           source = PerformanceHistory (or CrowdSuccess if CrowdReaction present),
           score  = CrowdReaction ?? baseLiveScore (e.g. 65 = "chose it live, so it worked"),
           weight = §6.1 source weight,
           PerformanceSessionId = session.Id
       }
5. For every planned adjacent pair in P that did NOT occur in A (a skipped edge):
       emit TransitionEvidence { source = PerformanceHistory, score = 25 }   # negative signal
6. Aggregate per target TransitionKnowledge: update scores, EvidenceCount, Confidence,
   Freshness (LastVerifiedAtUtc = now), and fold transition-type + energy deltas into
   DjFingerprint (PreferredTransitionTypesJson / EnergyCurveProfileJson / AvoidancePatternsJson).
```

The key product decision: **a live choice is evidence even without a rating** — the DJ trusted it
in the moment, which is a weak-positive signal; a *rated* crowd reaction upgrades it to strong
positive (or strong negative if the crowd hated it).

---

## 5. Pillar 5 — The Multi-Agent Swarm

Each agent is a .NET service with a **trigger** (event-bus subscription or scheduled tick), a
**responsibility**, and **outputs** (events + DB writes). They do not call each other directly;
they communicate through `IEventBus` and the shared store, which keeps them independently
schedulable, testable, and idempotent — matching the existing `AnalysisQueueService` dispatch-loop
pattern.

```csharp
/// <summary>Common contract for lifecycle agents.</summary>
public interface IKnowledgeAgent
{
    string Name { get; }
    Task StartAsync(CancellationToken ct);      // subscribes to triggers / starts ticks
    Task StopAsync();
}
```

| Agent | Trigger | Responsibility | Outputs |
|---|---|---|---|
| **Analysis Agent** | `TrackAnalysisRequestedEvent` (existing) | Run the audio-analysis pipeline (BPM/key/crowd profile/phrases/cues/stems) | `TrackAnalysisCompletedEvent` + track-knowledge rows. *Already implemented as `AnalysisQueueService` + `AudioAnalysisService`.* |
| **Transition Agent** | `TrackAnalysisCompletedEvent` + scheduled background scan | Pairwise harmonic/phrase/energy scan → `TransitionCandidate`; promote + evidence-aggregate into `TransitionKnowledge` | `TransitionCandidateDiscoveredEvent`, `TransitionKnowledgeUpdatedEvent`, new `TransitionEvidence` rows |
| **Set Builder Agent** | `SetBuildRequestedEvent` (user or scheduled) | Draft a Story Graph (slots + edges) from `TransitionKnowledge` filtered by `DjFingerprint`; rank paths | `SetDraftReadyEvent`, `SetProject` draft |
| **Performance Analyst Agent** | `PerformanceSessionRecordedEvent` | Diff plan vs. reality (§4.1); emit evidence; recompute confidence/freshness; update `DjFingerprint` | `KnowledgeUpdatedEvent`, evidence + fingerprint writes |
| **Export Agent** | `SetExportRequestedEvent` | Compile `SetProject` + templates + knowledge metadata to USB/Rekordbox | export artifacts (extends `REKORDBOX_EXPORT_ARCHITECTURE.md`) |

### 5.1 Agent communication workflow (end-to-end)

```
[import/scan] ──► Analysis Agent ──TrackAnalysisCompletedEvent──► Transition Agent
                                                                      │  (scan)
                                                                      ▼
                                                   TransitionCandidate ──promote──► TransitionKnowledge
                                                                                        ▲
[user builds set] ──► Set Builder Agent ──filters by DjFingerprint──► SetProject (Story Graph)
                                                                                        │
[gig played] ──► PerformanceSession ──► Performance Analyst Agent ──diff plan/reality──┘
                                                                      │
                                                                      ├─► TransitionEvidence (+)
                                                                      └─► DjFingerprint update

[user exports] ──► Export Agent ──► USB / Rekordbox (knowledge metadata attached)
```

**Concurrency & idempotency:** heavy work (analysis, scans) runs on the existing queue with a
`SemaphoreSlim` parallelism gate; each agent's message handler is idempotent (keyed by
track-hash-pair or session id), and re-delivery is safe — the same design that makes
`AnalysisQueueService` resilient to duplicate `TrackAnalysisRequestedEvent`s.

---

## 6. Consolidated scoring & decay formulas

### 6.1 Evidence source credibility

Evidence carries its own `Confidence` (0.0-1.0); the source type sets the *default* confidence the
agent assigns when it writes the row. Real-world sources start higher than algorithmic heuristics.

| Source | Default confidence | Rationale |
|---|---|---|
| BpmCompatibility | 0.4 | weak — purely theoretical |
| HarmonyDetection | 0.4 | weak — purely theoretical |
| PhraseMatch | 0.5 | structure-aware, still theoretical |
| LivePerformance | 0.7 | real — it was actually played |
| MashupSuccess | 0.8 | real — a mashup/double-drop that landed |
| CrowdReaction | 0.9 | strongest — real + audience-validated |
| ManualRating | 0.9 | strongest — explicit DJ intent |

### 6.2 Compatibility score (derived)

```
CompatibilityScore(K) = Σ (e.Score × e.Confidence × freshness(e)) / Σ (e.Confidence × freshness(e))
```

Recency-weighted so stale evidence (even strong 2021 ratings) contributes less to the *score* even
before the outer `freshness(K)` multiplier applies.

### 6.3 Freshness (recap)

```
freshness(K) = IsClassic ? 100.0 : 100 * Math.Exp(-λ * yearsSinceLastUse(K)),   λ = ln(2)/24mo ≈ 0.029
```

### 6.4 Recommendation score (recap)

```
recScore(K, DJ) = AggregateScore(K) × confidenceWeight(tier(K)) × freshness(K) × djFit(K, DJ)
```

---

## 7. Schema & migration plan

All changes land via `SchemaMigratorService` raw-SQL patches (never `dotnet ef migrations add`), and
`FreshDatabaseSchemaParityTests` guards new installs. New tables:

```sql
-- Source of truth: append-only evidence
CREATE TABLE TransitionEvidence (
    Id TEXT PRIMARY KEY, TransitionKnowledgeId TEXT NOT NULL,
    Source INTEGER NOT NULL, Score REAL NOT NULL, Confidence REAL NOT NULL DEFAULT 0.5,
    PerformanceSessionId TEXT, MetadataJson TEXT, CreatedAt TEXT NOT NULL
);
CREATE INDEX IX_TransitionEvidence_Knowledge ON TransitionEvidence(TransitionKnowledgeId);

-- Source of truth: played-set reality
CREATE TABLE PerformanceSessions (
    Id TEXT PRIMARY KEY, SetProjectId TEXT, EventProfileId TEXT, Venue TEXT,
    CrowdMomentsJson TEXT, StartedAtUtc TEXT NOT NULL, EndedAtUtc TEXT NOT NULL, Notes TEXT
);
CREATE TABLE PerformanceSessionTracks (
    Id TEXT PRIMARY KEY, SessionId TEXT NOT NULL, TrackId TEXT NOT NULL, SequenceOrder INTEGER NOT NULL,
    Planned INTEGER NOT NULL DEFAULT 0, AudibleChange INTEGER NOT NULL DEFAULT 0,
    TransitionType INTEGER, PlannedDurationSeconds REAL, ActualDurationSeconds REAL,
    CrowdReaction REAL, PlannedSlotId TEXT, DeviationReason TEXT
);
CREATE INDEX IX_PerfSessionTracks_Session ON PerformanceSessionTracks(SessionId, SequenceOrder);

-- Derived / materialized: recomputed from TransitionEvidence
CREATE TABLE TransitionKnowledge (
    Id TEXT PRIMARY KEY, SourceTrackId TEXT NOT NULL, TargetTrackId TEXT NOT NULL,
    SourceCueId TEXT, TargetCueId TEXT, TransitionType INTEGER NOT NULL,
    CompatibilityScore REAL, HarmonicScore REAL, BpmScore REAL, EnergyScore REAL,
    VocalScore REAL, CrowdFitScore REAL, ConfidenceScore REAL,
    ConfidenceLevel INTEGER NOT NULL DEFAULT 0, Freshness REAL NOT NULL DEFAULT 100.0,
    EvidenceCount INTEGER NOT NULL DEFAULT 0, IsClassic INTEGER NOT NULL DEFAULT 0,
    Verified INTEGER NOT NULL DEFAULT 0, LastUsedAtUtc TEXT NOT NULL, LastCalculatedUtc TEXT NOT NULL,
    DefaultTemplateId TEXT, Notes TEXT, CreatedAtUtc TEXT NOT NULL, UpdatedAtUtc TEXT NOT NULL
);
CREATE UNIQUE INDEX IX_TransitionKnowledge_Edge
    ON TransitionKnowledge(SourceTrackId, TargetTrackId, SourceCueId, TargetCueId, TransitionType);

-- Derived / materialized: DJ style profile
CREATE TABLE DjFingerprints (
    Id TEXT PRIMARY KEY, PreferredTransitionTypesJson TEXT, PreferredEnergyCurvesJson TEXT,
    PreferredBpmMovementJson TEXT, PreferredGenresJson TEXT, AvoidancePatternsJson TEXT,
    UpdatedAtUtc TEXT NOT NULL
);

-- Discovered hypotheses
CREATE TABLE TransitionCandidates (
    Id TEXT PRIMARY KEY, SourceTrackId TEXT NOT NULL, TargetTrackId TEXT NOT NULL,
    CompatibilityScore REAL, HarmonicScore REAL, BpmScore REAL, EnergyScore REAL, PhraseScore REAL,
    DiscoverySource TEXT, DiscoveredAtUtc TEXT NOT NULL,
    IsPromoted INTEGER NOT NULL DEFAULT 0, PromotedKnowledgeId TEXT
);
```

---

## 8. Implementation phasing & guardrails

Non-destructive, build-stable slices; each keeps `dotnet build` + `Architecture/*` + parity tests
green and adds isolated-DB tests (`ORBIT_DB_PATH` + `TestDatabaseSetup`).

1. **Slice L1 — schema.** New entities + `DbSet`s in `AppDbContext`; `SchemaMigratorService` raw
   SQL from §7.
2. **Slice L2 — Transition Agent (scan + evidence).** Candidate generation via `TrackMatchScorer`/
   `SectionVectorService`; promotion rule; evidence aggregation + `recScore` helper. Unit tests for
   the formulas in §6.
3. **Slice L3 — Confidence & decay.** `KnowledgeConfidence` + `KnowledgeFreshness` services; tests
   for tier boundaries and the half-life/Classic cases.
4. **Slice L4 — DjFingerprint.** Entity + `djFit` filtering; wire into Set Builder ranking.
5. **Slice L5 — PerformanceSession + reconciliation.** Session import; §4.1 diff algorithm
   (edit-distance alignment); evidence extraction; fingerprint update. Tests for the alignment and
   the skipped-edge negative signal.
6. **Slice L6 — Export Agent.** Attach confidence/freshness metadata to the existing Rekordbox/USB
   export path.
7. **Slice L7 — swarm wiring.** `IKnowledgeAgent` lifecycle; register agents in DI; event-bus
   subscriptions; runtime QA gate.

**Guardrails (unchanged from `CLAUDE.md` / Phase 1):** no placebo UI, no silent `catch {}`,
isolated-DB tests only, roles stay on slots, feedback is append-only, and this document layers on
top of `WORKSTATION_COCKPIT_FOUNDATION.md` / `knowledge-system-vnext.md` rather than superseding them.

## 9. Open decisions for the maintainer

1. **`halfLife` default** — proposed 24 months; expose per-DJ, with genre-aware variants as a later
   tuning surface.
2. **Promotion threshold `θ_promote`** — proposed 70; whether algorithmic-only candidates are ever
   auto-promoted or always await a manual/live signal.
3. **Live-play base score** — the weak-positive default for an unrated live choice (proposed 65).
4. **Fingerprint convergence rate** — how aggressively `djFit` shifts from stated to observed
   behaviour (e.g. EMA with α ≈ 0.2).
5. **Agent scheduling** — the Transition Agent's pairwise scan is `O(n²)`; whether it runs on a
   fixed interval, on analysis-completion only, or on a dedicated low-priority worker budget.
