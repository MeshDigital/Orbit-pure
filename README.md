# ORBIT Pure

ORBIT Pure is a reliability-first Soulseek client and music curation workstation built with .NET 9 + Avalonia.
It prioritizes signal quality, forensic filtering, and operator control over raw download volume.

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

---

## Core Capabilities

### 1) Explainable Search & Curation
- Cached filtered-out results can be shown without issuing a new network search.
- Hidden-result reasoning is explicit (bitrate floor, format gate, queue/reliability, safety/forensic gate).
- Search UI exposes quick controls to reveal hidden candidates and relax filters against the cached result set.

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

---

## Contributing

Contributions are welcome, especially around reliability, performance, and explainability.
Please keep changes surgical, test-backed, and aligned with the existing architecture and event-driven patterns.

License: GPL-3.0
