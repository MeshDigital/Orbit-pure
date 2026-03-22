# ORBIT Pure

Reliability-first Soulseek client and music curation workstation with explainable search, forensic filtering, and operator-grade download controls.

ORBIT Pure is built with .NET 9 + Avalonia and prioritizes signal quality over raw download volume.

> Legal & privacy notice
> ORBIT connects to the Soulseek P2P network, where your IP may be visible to peers. Use a VPN and only download/share content you are legally allowed to use.

---

## Project State (March 2026)

- Current branch baseline is `master`, with the latest documented release milestone at `0.1.0-alpha.33`.
- Build and tests are green for the latest integration pass:
  - `dotnet build SLSKDONET.sln -c Debug`
  - `dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -c Debug --no-build`
- Product focus is now on three operational pillars:
  - explainable search decisions
  - resilient high-fidelity download orchestration
  - dense, real-time operator UX in Download Center and Library

### Newly documented March 2026 runtime systems

- metadata-aware search lane planning (`Strict` → `Standard` → `Desperate`)
- shared fit/ranking blend scoring for search and discovery
- reactive buffered search-stream ingestion with explicit session ownership
- adapter-side hard-cap circuit breaker for pathological result streams
- compact preferred-reason propagation across search, discovery, and downloads

Start with:

- `DOCS/REACTIVE_SEARCH_RUNTIME_TECHNICAL_2026-03-22.md`
- `DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md`
- `SOULSEEK_LOGIN_AND_SERVICE_SIGNALS_TECHNICAL.md`

---

## Core Capabilities

### 1) Explainable Search & Curation
- Cached filtered-out results can be shown without issuing a new network search.
- Hidden-result reasoning is explicit (bitrate floor, format gate, queue/reliability, safety/forensic gate).
- Search UI exposes quick controls to reveal hidden candidates and relax filters against the cached result set.
- Search winners now use shared blend reasoning that combines identity match, metadata fit, peer reliability, and queue pressure.

### 2) Download Orchestration & Manual Override
- Multi-lane discovery with fast-lane/golden-winner short-circuiting.
- Structured per-track live peer result feed in row details (time, user, state, detail, speed, file).
- Manual per-candidate force download from row details when the operator wants a specific peer/file.

### 3) Forensic Quality Controls
- Safety/bouncer gating for suspect/upscaled results.
- Forensic quality surfacing in track/list views.
- Spectral and integrity-aware decision signals are surfaced across search and download UX.

### 4) Network & Runtime Resilience
- Hardened disconnect/reconnect behavior and distributed-parent recovery paths.
- Crash-aware download state handling and recovery-friendly orchestration.
- Global exception reporting and cleaner non-fatal runtime diagnostics.
- Search streams now use explicit stop-listening semantics, buffered UI delivery, idle telemetry, and adapter-side hard caps.

### 5) Library & Workstation UX
- Album-first flows and dense playlist grid/cards.
- Hover/flyout forensic diagnostics and quality signaling.
- Performance-friendly population and virtualization-oriented UI updates for larger libraries.

---

## Recent Delivery Timeline (alpha.25 → alpha.33)

- `alpha.33`: Search hidden-result transparency, row-level live peer details, and force-candidate download action.
- `alpha.32`: Discovery reason surfacing, fast-lane UX improvements, disconnect handling hardening, and UI cleanup.
- `alpha.31`: Library slim-rail behavior and responsive playlist card/grid refinements.
- `alpha.30`: Discovery lane semaphore budget, distributed-parent trigger refinement, dedup/fingerprinter upgrade.
- `alpha.29`: Strict purist lossless policy and stronger fake-lossless rejection criteria.
- `alpha.28`: Parent-health monitoring, streaming tier cancellation improvements, peer-lane dashboard, health banner.
- `alpha.27`: Visual dashboard upgrades (slim rail defaults, circular forensic ring, quality HUD).
- `alpha.26`: Search and queue-aware protocol tuning (response caps, file caps, queue-depth filtering).
- `alpha.25`: Golden-search gate and library/workflow activation refinements.

For full details, see RECENT_CHANGES.md.

---

## Tech Stack

- UI: Avalonia + ReactiveUI
- Reactive collections: DynamicData + Rx buffering/session control
- Runtime: .NET 9 (C#)
- Network: Soulseek.NET integration
- Data: SQLite (EF Core)
- Audio/forensics: FFmpeg, NAudio, Essentia, NWaves
- Analysis/ML: ML.NET-based feature workflows where applicable

---

## Quick Start

1. Clone:

  ```bash
  git clone https://github.com/MeshDigital/Orbit-pure.git
  cd Orbit-pure
  ```

2. Restore and build:

  ```bash
  dotnet restore
  dotnet build SLSKDONET.sln -c Debug
  ```

3. Run:

  ```bash
  dotnet run
  ```

4. First launch:
- Configure Soulseek credentials in Settings.
- Ensure `ffmpeg` is installed and available on PATH for audio forensic services.

---

## Repository Map

- `Views/Avalonia`: XAML pages and controls
- `ViewModels`: reactive presentation state
- `Services`: orchestration, discovery, network, forensic, and IO services
- `Data`, `Migrations`: persistence layer and schema evolution
- `Tests`: unit/integration test coverage
- `RECENT_CHANGES.md`: chronological release and implementation notes
- `DOCS`: deep technical writeups for major runtime systems

---

## Search Runtime Architecture Highlights

### Query planning and lane execution
- `SearchNormalizationService` now builds structured `SearchPlan` instances instead of relying only on loose text variations.
- `SearchOrchestrationService` executes ordered lanes with explicit `Strict`, `Standard`, and `Desperate` semantics.
- Broad-search escalation is bounded and lane-aware, reducing stale accumulation and low-signal fan-out.

### Shared ranking and explainability
- `SearchCandidateFitScorer` scores candidate fit using artist/title/album/duration plus accessibility hints.
- `SearchCandidateRankingPolicy` blends match score, fit score, peer reliability, and queue penalty into final rank.
- `SearchBlendReasonFormatter` converts telemetry into compact operator-facing reasons.

### Firehose-safe search streaming
- `SearchViewModel` owns a real search session with `SerialDisposable` subscription control and explicit `IsListening` state.
- Search results are projected off the UI thread and buffered into chunked updates (`250ms` or `50` items).
- `SoulseekAdapter` enforces hard result/file caps and surfaces `SearchLimitExceededException` when a search must be cut off safely.

### Validation
- Focused tests cover query planning, ranking, reason propagation, orchestration lane behavior, adapter cap propagation, and search UI batch/cancel behavior.
- Latest focused validation included:
  - `SearchOrchestrationServiceTests`
  - `SearchViewModelTests`

---

## Documentation

- `DOCUMENTATION_INDEX.md`: master doc map
- `DOCS/REACTIVE_SEARCH_RUNTIME_TECHNICAL_2026-03-22.md`: end-to-end search runtime deep dive
- `DOCS/SEARCH_STREAM_FIREHOSE_HARDENING_PLAN_2026-03-22.md`: implementation plan and acceptance criteria
- `RECENT_CHANGES.md`: release-oriented implementation log

---

## Contributing

Contributions are welcome, especially around reliability, performance, and explainability.
Please keep changes surgical, test-backed, and aligned with the existing architecture and event-driven patterns.

License: GPL-3.0
