# Automatic Downloads Phase 2 Memory

**Created**: May 14, 2026  
**Phase**: Integration (Phase 2)  
**Status**: Ready to Execute  
**Related Plans**: A10 FlowBuilder, Frequent Sources, Workstation Cockpit (isolated, non-blocking)

---

## Summary

**Automatic Downloads** is a deterministic, opt-in track selection system that replaces fuzzy guessing with exact-first, filtered-fallback search. Phase 1 (investigation) completed May 13, 2026 with 5 orchestrator fixes applied (timeouts reduced, fuzzy matching tightened, scoring determinism hardened). Phase 2 (integration) wires the 4 service skeletons and 25 test stubs into the production application.

**Not telemetry. Not PII. Local-only feature. Disabled by default.**

---

## Current Implementation State

### Phase 1 Completed ✅
- ✅ Root cause analysis: 2-4 min downloads traced to 120s discovery timeout + hedged search + relaxation strategy
- ✅ Orchestrator fixes applied:
  - Discovery timeout: 120s → 45s
  - Fuzzy matching removed (strict boundary-only enforcement)
  - Threshold raised: 70/100 → 85/100
  - Hedged search disabled
  - Relaxation strategy disabled
- ✅ Build validates (0 errors)
- ✅ Existing tests pass (no regressions)

### Phase 2 In Progress ⏳
- 🔲 Task 1: DI registration (AutoSearchService, MatchScorer, SoulseekSearchHelper, PrefetchVerifier)
- 🔲 Task 2: DownloadManager hook (call AutoSearchService when EnableAutoDownloadStrictMode=true)
- 🔲 Task 3: Settings UI bindings (11 config properties + strict-mode toggle)
- 🔲 Task 4: Test skeleton implementation (25 tests → working bodies)
- 🔲 Task 5: End-to-end validation (build, tests, manual QA)

---

## Config Flags Added (Phase 1)

| Property | Type | Default | Purpose |
|----------|------|---------|---------|
| `EnableAutoDownloadStrictMode` | bool | false | Enable/disable feature |
| `AutoDownloadInitialWaitMs` | int | 4000 | Wait for exact match (ms) |
| `AutoDownloadExtendedWaitMs` | int | 20000 | Wait for template/filtered (ms) |
| `AutoDownloadAllowedExtensions` | string | "flac,wav,aiff,aif,ape,alac" | Quality-first file types |
| `AutoDownloadMinFileSizeBytes` | int | 524288 (512KB) | Reject tiny/corrupt files |
| `AutoDownloadMinBitrateKbps` | int | 320 | Reject low-bitrate MP3s |
| `AutoDownloadExactFirstOnly` | bool | false | Skip template if exact fails |
| `AutoDownloadMaxCandidatesToScore` | int | 50 | Limit search result set |
| `AutoDownloadExcludedPhrases` | string | "remix,cover,live,acoustic" | Skip variants |
| `AutoDownloadDiagnosticsEnabled` | bool | false | Emit diagnostic logs |
| `MinSearchDurationSeconds` | int | 5 (was 16) | Min search time per tier |

All properties injected via `IAppConfig` interface. Persisted to `appsettings.json` via `ConfigManager`.

---

## Services Created (Phase 1 - Skeletons)

### 1. AutoSearchService (`Services/AutoDownload/AutoSearchService.cs`)
**Purpose**: Orchestrate exact-first, filtered-fallback search with time windows.  
**Methods**:
- `FindBestMatchAsync(track, ct)` → returns `CandidateResult` or null
- `SearchExactFilenameAsync(...)` → searches for artist/title/extension exact match
- `SearchFilteredTemplateAsync(...)` → searches with Soulseek server filters (minbitrate, ext, mfs)

**State**: Skeleton (method bodies TODO)  
**Tests**: 8 skeleton test methods in AutoSearchServiceTests.cs (TODO bodies)

### 2. MatchScorer (`Services/AutoDownload/MatchScorer.cs`)
**Purpose**: Deterministic scoring independent of current SearchResultMatcher.  
**Methods**:
- `ScoreCandidate(target, candidate, options)` → returns 0-100
- `CalcExactnessScore(...)` → 50% weight
- `CalcFormatScore(...)` → 20% weight
- `CalcBitrateScore(...)` → 15% weight
- `CalcReliabilityScore(...)` → 10% weight
- `CalcResponseTimeScore(...)` → 5% weight

**State**: Skeleton (method bodies TODO)  
**Tests**: 8 skeleton test methods in MatchScorerTests.cs (TODO bodies)

### 3. SoulseekSearchHelper (`Services/AutoDownload/SoulseekSearchHelper.cs`)
**Purpose**: Wrap Soulseek protocol filter tokens and phrase exclusion.  
**Methods**:
- `BuildFilteredQuery(query, config)` → appends `minbitrate:X ext:Y mfs:Z` tokens
- `FilterCandidates(results, config)` → post-search validation
- `RegisterServerExcludedPhrases(adapter, config)` → cache banned terms

**State**: Skeleton (method bodies TODO)  
**Tests**: 9 skeleton test methods in SoulseekSearchHelperTests.cs (TODO bodies)

### 4. PrefetchVerifier (`Services/AutoDownload/PrefetchVerifier.cs`)
**Purpose**: Post-download verification (file size, format, fingerprint extraction).  
**Methods**:
- `VerifyDownloadAsync(track, candidate, filePath, ct)` → returns VerificationResult enum
- `ExtractFingerprint(track, filePath)` → delegate to TrackFingerprintBuilderService

**State**: Skeleton (method bodies TODO)  
**Tests**: Integrated into MatchScorerTests or separate (to be decided Phase 2)

---

## Test Status (Phase 1)

**Total Skeleton Tests**: 25  
**Compiling**: ✅ Yes (all 25 methods exist, bodies are TODO comments)  
**Passing**: 🔲 0/25 (will implement in Phase 2 Task 4)  
**Coverage Target**: 80%+ (existing code path coverage)

Test files:
- `Tests/SLSKDONET.Tests/Services/AutoDownload/AutoSearchServiceTests.cs` (8 tests)
- `Tests/SLSKDONET.Tests/Services/AutoDownload/MatchScorerTests.cs` (8 tests)
- `Tests/SLSKDONET.Tests/Services/AutoDownload/SoulseekSearchHelperTests.cs` (9 tests)

---

## Dependencies

### Direct Dependencies
- **ISoulseekAdapter**: Protocol layer, search execution
- **IAppConfig**: Feature flags and tuning parameters
- **IDatabaseService**: Track metadata lookup, logging
- **TrackFingerprintBuilderService**: Audio fingerprint extraction (post-download verification)
- **SearchOrchestrationService**: Already exists; not modified by AutoDownload
- **SearchResultMatcher**: Already exists; AutoDownload uses independent MatchScorer

### Transitive Dependencies
- **Soulseek.NET**: GPL-3.0 P2P protocol library (wrapped by ISoulseekAdapter)
- **FFmpeg + Essentia**: Audio analysis (used by TrackFingerprintBuilderService only)
- **Entity Framework Core**: ORM for database access
- **Serilog**: Structured logging

### Peer Systems (NOT dependencies, parallel work streams)
- **A10 FlowBuilder**: Musical intelligence, UI integration (separate)
- **Frequent Sources**: Social feature, peer tracking (separate)
- **Workstation Cockpit**: DAW-style UI, timeline editing (separate)

---

## Key Decisions (Phase 1)

### Decision 1: Exact-First Strategy ✅
**Choice**: Search for exact artist/title match FIRST (3-5s wait), then fall back to template query (20-30s wait).  
**Rationale**: Users prefer exact matches to templates; avoids remix/cover/live variants by default.  
**Trade-off**: Longer discovery time but deterministic results; users can opt-in only.

### Decision 2: Deterministic Scoring ✅
**Choice**: Create independent MatchScorer (not using existing SearchResultMatcher).  
**Rationale**: Allows Phase 3 experimentation without breaking existing search UI; cleaner test isolation.  
**Trade-off**: Duplicate scoring logic (SearchResultMatcher still exists for general search); addressed in Phase 3 refactor.

### Decision 3: Server-Side Filtering ✅
**Choice**: Use Soulseek server `minbitrate`, `ext`, `mfs` tokens instead of client-side post-processing.  
**Rationale**: Reduces network transfer, faster filtering, aligns with Soulseek philosophy.  
**Trade-off**: Requires protocol knowledge; tested in SoulseekSearchHelperTests.

### Decision 4: Local-Only, No Telemetry ✅
**Choice**: All feature flags/diagnostics stay in appsettings.json; no telemetry uploads.  
**Rationale**: Privacy-first; matches ORBIT-Pure values; no user tracking.  
**Trade-off**: Diagnostic logs are file-only; no analytics dashboard.

### Decision 5: Feature-Gated & Disabled by Default ✅
**Choice**: All new functionality gated behind `EnableAutoDownloadStrictMode`; default=false.  
**Rationale**: Safe for release; non-breaking; users opt-in explicitly.  
**Trade-off**: Feature unavailable to users who don't enable it (intentional).

---

## Open Questions & Future Work (Phase 3+)

### Phase 2.5 (Post-Integration Polish)
- Should PrefetchVerifier integrate into DownloadManager atomic rename, or run async post-completion?
- Should Settings UI have a "Test AutoDownload" button that simulates a search?
- Should diagnostic logs go to Serilog or separate file?

### Phase 3 (Advanced Heuristics)
- Mashup search: Support multi-artist queries (e.g., "Artist1 vs Artist2")?
- Rematch fallback: If AutoSearchService returns no match, should we auto-retry with relaxed filters?
- Performance: Cache server excluded phrases; precompute filter token lists?
- Analytics: Track strict-mode hit/miss rate in local log (no telemetry, diagnostics only)?

### Phase 4 (Integration with A10)
- Should AutoDownload inform Flow Intelligence about download source reliability?
- Should A10 suggest playlists that AutoDownload can fill deterministically?

### Phase 5 (Workstation UI)
- Should Workstation Cockpit show AutoDownload status in timeline (e.g., "strict match", "fallback match")?
- Should export include AutoDownload metadata for rekordbox/Serato metadata preservation?

---

## Interaction with Other Plans

### A10 FlowBuilder (Parallel, Non-Blocking)
- ❌ No direct dependency: AutoDownload doesn't call A10 methods
- ⚠️ Potential future: A10 could suggest playlists that AutoDownload fills
- 📝 Note: A10 runs independently; AutoDownload is transparent to A10 logic

### Frequent Sources (Parallel, Non-Blocking)
- ❌ No direct dependency: AutoDownload doesn't call Frequent Sources methods
- ⚠️ Potential future: Frequent Sources could track "reliable peers" for AutoDownload retry logic
- 📝 Note: Frequent Sources could launch anytime; doesn't wait for AutoDownload Phase 2

### Workstation Cockpit (Parallel, Non-Blocking)
- ❌ No direct dependency: Cockpit doesn't call AutoDownload methods
- ⚠️ Potential future: Cockpit UI could display AutoDownload status in timeline
- 📝 Note: Cockpit continues development independently; AutoDownload invisible to Cockpit until Phase 4

---

## Verification Checklist (Phase 2 Completion)

**Build & Tests**:
- [x] `dotnet build SLSKDONET.sln` → 0 errors (2026-05-21)
- [x] `dotnet test --filter AutoDownload` → 28/28 passing (2026-05-21)
- [x] `dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj` → 837 passed, 0 failed, 2 skipped (2026-05-21)

**Integration**:
- [ ] DI graph resolves all 4 services without circular dependencies
- [ ] DownloadManager injects AutoSearchService without errors
- [ ] SettingsViewModel binds 11 config properties with change notifications
- [ ] SettingsWindow renders strict-mode toggle + 10 config inputs

**Manual QA**:
- [ ] Queue known track with strict mode ENABLED
  - [ ] Logs show "StrictMode: ..." message
  - [ ] Download completes in < 60s (not 2-4 min)
  - [ ] Downloaded file matches artist/title/bitrate criteria
- [ ] Queue known track with strict mode DISABLED (fallback to discovery service)
  - [ ] Logs show discovery service path
  - [ ] Download behavior unchanged from baseline
- [ ] GUI projection gate passes for strict ON/OFF (see [DOCS/strict_mode_gui_validation_checklist.md](DOCS/strict_mode_gui_validation_checklist.md))
- [x] Verify feature disabled by default (safety check) (`EnableAutoDownloadStrictMode` default = `false` in `AppConfig`, verified 2026-05-21)

**Documentation**:
- [ ] Phase 2 plan updated with completion date
- [ ] Memory file updated with final status
- [ ] PR summary written (if PR created)

---

## Progress Log

### 2026-05-13 — Phase 1 Complete ✅
- Root cause analysis completed (5 bottlenecks identified)
- Orchestrator fixes applied (discovery timeout 120→45s, fuzzy matching removed, threshold 70→85)
- 4 service skeletons created (all compile)
- 25 test skeletons created (all compile, bodies TODO)
- 11 config flags added to AppConfig
- Build validation: 0 errors

### 2026-05-14 — Phase 2 Planning ✅
- Focus analysis completed (AutoDownload Phase 2 is highest priority)
- Phase 2 plan drafted (5 tasks, 3-4 hour target)
- Memory file created (this document)
- Ready to execute Phase 2

### [PENDING] 2026-05-14 — Phase 2 Execution ⏳

### 2026-05-21 — Strict-Mode Closure Checkpoint ✅
- Task 1 (DI wiring): Completed.
- Task 2 (DownloadManager strict gate path): Completed.
- Task 3 (Settings bindings and UI): Completed.
- Task 4 (AutoDownload test bodies): Completed.
- Sidequest (bounded event contract split): Completed with compatibility-preserving type/namespace/signature behavior.

Automated validation executed:
- `dotnet build SLSKDONET.sln -nologo` -> success.
- `dotnet test --filter "FullyQualifiedName~SLSKDONET.Tests.Services.AutoDownload.AutoSearchServiceTests|FullyQualifiedName~SLSKDONET.Tests.Services.DownloadManagerStrictModeGateTests"` -> 13/13 passed.
- `dotnet test --filter "FullyQualifiedName~SLSKDONET.Tests.Services.AutoDownload.MatchScorerTests|FullyQualifiedName~SLSKDONET.Tests.Services.AutoDownload.SoulseekSearchHelperTests"` -> 18/18 passed.

Task 5 status:
- Automated portion: complete and green.
- Manual real-download proof: pending (final gate before production-ready strict-mode sign-off).

#### Task 5 Final Manual Runtime QA (single-pass gate)
1. Enable strict-mode and diagnostics:
  - In Settings: `EnableAutoDownloadStrictMode = true`.
  - In Settings: `AutoDownloadDiagnosticsEnabled = true`.
2. Queue one known track and let discovery run end-to-end.
3. Confirm strict-path evidence in logs:
  - `Strict-mode AutoDownload selected ... via exact|filtered_template ...`
  - `autodownload_search_started`
  - `autodownload_selected` (or `autodownload_no_match` for negative case)
4. Verify resulting file respects configured gates:
  - extension in `AutoDownloadAllowedExtensions`
  - bitrate >= `AutoDownloadMinBitrateKbps`
  - size >= `AutoDownloadMinFileSizeBytes`
5. Disable strict-mode and repeat same track class:
  - `EnableAutoDownloadStrictMode = false`
  - confirm legacy discovery path behavior is unchanged.
6. Record outcome and stamp Task 5 complete only if both ON and OFF runs pass.

Evidence capture helpers (local SQLite):
- DB file path: `%APPDATA%/ORBIT/library.db`
- Activity log table: `ActivityLogs`

PowerShell + sqlite3 evidence queries (if sqlite3 is available):
1. Strict diagnostics actions (latest 30):
  - `sqlite3 "$env:APPDATA/ORBIT/library.db" "SELECT Timestamp, Action, substr(Details,1,220) FROM ActivityLogs WHERE Action LIKE 'autodownload_%' ORDER BY Timestamp DESC LIMIT 30;"`
2. Strict selection confirmation:
  - `sqlite3 "$env:APPDATA/ORBIT/library.db" "SELECT Timestamp, Action, Details FROM ActivityLogs WHERE Action='autodownload_selected' ORDER BY Timestamp DESC LIMIT 5;"`
3. Strict fallback/no-match confirmation:
  - `sqlite3 "$env:APPDATA/ORBIT/library.db" "SELECT Timestamp, Action, Details FROM ActivityLogs WHERE Action IN ('autodownload_candidate_found','autodownload_no_match') ORDER BY Timestamp DESC LIMIT 10;"`

If `sqlite3` is not installed:
- Use app runtime logs plus the strict gate info log emitted by DownloadManager:
  - `Strict-mode AutoDownload selected ... via exact|filtered_template ...`

Exit criteria for final stamp:
- Strict ON run has explicit strict-selection evidence and valid file outcome.
- Strict OFF run follows legacy path with baseline-equivalent behavior.
- No runtime regressions in queue processing, progress updates, or completion events.

Runtime gate attempt status (2026-05-21):
- `sqlite3` confirmed available locally.
- `%APPDATA%/ORBIT/library.db` queried for `Action LIKE 'autodownload_%'` returned no rows.
- `logs/*.log` searched for `Strict-mode AutoDownload selected`, `Strict-mode`, and `AutoDownload` returned no strict-mode evidence.
- Result: Task 5 cannot be stamped yet from automation alone; one interactive ON/OFF real-download session is still required to generate proof artifacts.

#### Task 5 Evidence Capture Template (fill during interactive run)

Run date/time:
- Strict ON run start:
- Strict ON run end:
- Strict OFF run start:
- Strict OFF run end:

Track under test:
- ON track artist/title:
- OFF track artist/title:

Strict ON configuration:
- EnableAutoDownloadStrictMode: true
- AutoDownloadDiagnosticsEnabled: true

Strict ON expected evidence:
- Log line contains: Strict-mode AutoDownload selected ... via exact|filtered_template
- ActivityLogs contains: autodownload_search_started
- ActivityLogs contains one of: autodownload_selected | autodownload_no_match
- Optional ActivityLogs: autodownload_candidate_found

Strict ON evidence snippets:
- Runtime log excerpt:
- ActivityLogs rows excerpt:
- Resulting file extension/bitrate/size:

Strict OFF configuration:
- EnableAutoDownloadStrictMode: false

Strict OFF expected evidence:
- No strict selection log line for this run window
- No new autodownload_* ActivityLogs entries for this run window
- Legacy discovery path behavior observed

Strict OFF evidence snippets:
- Runtime log excerpt:
- ActivityLogs rows excerpt:
- Legacy-path observation:

Pass/fail verdict:
- Strict ON: PASS | FAIL
- Strict OFF: PASS | FAIL
- Task 5 final verdict: PASS | FAIL

Closure rule:
- Mark Task 5 complete only when Strict ON and Strict OFF are both PASS with evidence.
- Task 1: DI Registration (15-20 min)
- Task 2: DownloadManager Hook (20-30 min)
- Task 3: Settings UI Bindings (20-25 min)
- Task 4: Test Implementation (45-60 min)
- Task 5: End-to-End Validation (30-45 min)
- Estimated completion: Today (May 14) + 3-4 hours of focused work

### 2026-05-21 — Phase 2 Validation Sweep (Automated) ✅
- Strict-mode service tests and orchestrator gate tests passing:
  - `AutoSearchServiceTests` + `DownloadManagerStrictModeGateTests` = 13/13 passing
- AutoDownload-focused filter suite passing:
  - `dotnet test ... --filter "FullyQualifiedName~AutoDownload"` = 28/28 passing
- Full test project regression passing:
  - `dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj` = 837 passed, 0 failed, 2 skipped
- Build gate passing:
  - `dotnet build SLSKDONET.sln` = 0 errors
- Remaining blocker:
  - Manual/live-network QA with real Soulseek traffic is still required to close Task 5 runtime validation.

---

## Notes for Next Developer

1. **Always default features to DISABLED**: Check `EnableAutoDownloadStrictMode` before calling AutoSearchService.
2. **Fallback is safety net**: If AutoSearchService returns null, existing DownloadDiscoveryService handles the download.
3. **Tests are your friend**: All 25 test skeletons have TODO bodies; copy patterns from passing SearchResultMatcher tests.
4. **Soulseek protocol filters are powerful**: `minbitrate:320 ext:flac mfs:512000` significantly reduces junk results.
5. **Excluded phrases work server-side**: Register them once in `SoulseekSearchHelper.RegisterServerExcludedPhrases()`.
6. **Performance is NOT Phase 2**: Phase 2 is integration only; optimization is Phase 3.
7. **Privacy-first**: No telemetry uploads, no PII storage, all local-only.
8. **Build early and often**: After each task (Task 1-5), run `dotnet build` to catch errors immediately.

---

## Related Files

**Code**:
- `Services/DownloadManager.cs` (orchestrator)
- `Services/DownloadDiscoveryService.cs` (fallback path)
- `Services/SearchResultMatcher.cs` (existing scoring, now hardened)
- `Services/AutoDownload/AutoSearchService.cs` (Phase 2 integration focus)
- `Services/AutoDownload/MatchScorer.cs` (Phase 2 integration focus)
- `Services/AutoDownload/SoulseekSearchHelper.cs` (Phase 2 integration focus)
- `Services/AutoDownload/PrefetchVerifier.cs` (Phase 2 integration focus)
- `Configuration/AppConfig.cs` (11 new properties)
- `App.axaml.cs` (DI registration, Task 1)
- `ViewModels/SettingsViewModel.cs` (Settings bindings, Task 3)

**Tests**:
- `Tests/SLSKDONET.Tests/Services/AutoDownload/AutoSearchServiceTests.cs`
- `Tests/SLSKDONET.Tests/Services/AutoDownload/MatchScorerTests.cs`
- `Tests/SLSKDONET.Tests/Services/AutoDownload/SoulseekSearchHelperTests.cs`

**Plans**:
- `DOCS/automatic_downloads_phase2_plan.md` (this execution roadmap)
- `DOCS/automatic_downloads_phase1_investigation.md` (Phase 1 findings)

**Related Memory**:
- `/memories/session/focus_analysis_may_14.md` (prioritization decision)
- `/memories/repo/track-match-section-architecture.md` (SearchResultMatcher design)
- Not related: workstation_cockpit.md, workstation_flow_intelligence_A10.md, frequent_sources_profile.md

---

## Status: Ready to Execute ✅

Phase 2 is mechanically well-scoped (5 tasks, 3-4 hours, low risk). All prerequisite context is available. Build is clean. DI pattern is established. Test patterns are available for copy-paste. Proceed with Task 1 (DI Registration) when ready.
