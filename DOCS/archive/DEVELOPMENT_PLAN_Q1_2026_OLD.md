# ORBIT Development Plan: Next Steps (Dec 28, 2025 - Q1 2026)

**Status**: 71% Complete - Awaiting Feedback  
**Date Created**: December 28, 2025  
**Document Purpose**: Strategic planning for Phase 7 and beyond

---

## Executive Summary

ORBIT has reached a critical inflection point:
- ✅ **Core Features**: 95% complete (search, download, enrichment, player)
- ✅ **Stability**: 85% complete (crash recovery, health monitoring, forensic logging)
- ⚠️ **Testing & Quality**: 0% complete (no automated tests)
- ⚠️ **User Experience**: 70% complete (UX polish, edge cases)
- ⚠️ **Performance**: 60% complete (database, UI virtualization, binary analysis)

**Key Decision Point**: Continue feature development OR focus on stabilization/testing?

---

## Current Backlog Assessment

### Completed (This Week - Dec 21-28)

1. **Documentation Audit** ✅
   - 6 new comprehensive docs (2,377 lines)
   - Coverage: 52% → 65%
   - All technical deepdives complete

2. **Audio Analysis Pipeline** ✅
   - FFmpeg + Essentia sidecar integration
   - Drop detection & DJ cue generation
   - Forensic logging with correlation IDs
   - Track Inspector auto-refresh

3. **Library Display Enhancements** ✅
   - Duration badges (⏱)
   - File size display (💾)
   - Smart KB/MB formatting

4. **Queue Visibility** ✅
   - AnalysisQueueService with events
   - Glass Box status bar
   - Pause/Resume support
   - Smart ETA calculation

### In Progress / Blocked

1. **UI Virtualization** ⏸️
   - Large track lists (1000+) cause lag
   - Requires ItemsRepeater refactoring
   - Est. 16 hours of work

2. **Unit Testing** ❌
   - 0% coverage (no tests written)
   - Critical services untested
   - Est. 40+ hours to achieve 60% coverage

3. **Essentia Binary Distribution** ⏸️
   - Currently requires manual installation
   - Should be bundled or auto-downloaded
   - Est. 8 hours

4. **Event Bus Memory Leak** ⚠️
   - Subscribers not always unsubscribed
   - WeakReference implementation needed
   - Est. 6 hours

5. **Correlation ID Propagation** ⚠️
   - UI ViewModels don't participate
   - Some service calls missing IDs
   - Est. 12 hours to audit + fix

---

## Proposed Development Plan

### Phase 7: Testing & Quality Assurance (4 weeks)

**Goal**: Establish automated test coverage and professional testing practices.

#### 7.1: Unit Test Foundation (16 hours)
- [ ] Create `SLSKDONET.Tests` project (xUnit)
- [ ] Add test fixtures and mocks
- [ ] Implement 3 critical service test suites:
  - `DropDetectionEngineTests` (10 cases)
  - `SearchNormalizationServiceTests` (15 cases)
  - `DownloadHealthMonitorTests` (12 cases)
- [ ] Achieve 35% code coverage

**Deliverable**: ~200 passing tests, 35% coverage baseline

#### 7.2: Integration Test Suite (12 hours)
- [ ] Database integration tests (EF Core)
- [ ] Download orchestration tests (multi-lane logic)
- [ ] Enrichment pipeline tests (Spotify API mocking)
- [ ] Recovery journal tests (crash scenarios)

**Deliverable**: ~100 integration tests

#### 7.3: UI/E2E Testing (8 hours)
- [ ] Create test harness for Avalonia UI
- [ ] Smoke tests for critical user flows:
  - Search → Download → Player
  - Spotify Import → Enrichment
  - Settings persistence

**Deliverable**: 5+ E2E test scenarios

#### 7.4: Performance Benchmarks (4 hours)
- [ ] Create `Benchmarks` project (BenchmarkDotNet)
- [ ] Profile hot paths:
  - Search ranking (N=1000)
  - Download selection (N=500)
  - Library queries (N=10,000 tracks)
  - Forensic logging (throughput)

**Deliverable**: Baseline metrics document

---

### Phase 8: Performance & Scalability (4 weeks)

**Goal**: Optimize database, UI rendering, and external integrations for large libraries.

#### 8.1: Database Query Optimization (12 hours)
- [ ] Profile slow queries (SQL Server Profiler / EF Core logs)
- [ ] Add covering indexes for top 10 queries
- [ ] Implement query result caching (Redis alternative: in-memory with TTL)
- [ ] Batch query refactoring (N+1 elimination)

**Expected Gain**: 2-5x faster library loads

#### 8.2: UI Virtualization (20 hours)
- [ ] Refactor Library list to ItemsRepeater (Avalonia)
- [ ] Implement scroll-based lazy loading
- [ ] Optimize rendering for 10,000+ tracks
- [ ] Add "Load more" button for pagination

**Expected Gain**: 60fps rendering, <200ms response time

#### 8.3: Download Manager Optimization (8 hours)
- [ ] Profile multi-lane orchestration (flame graphs)
- [ ] Reduce lock contention in DownloadManager
- [ ] Optimize stall detection polling (currently 15s → 30s+)
- [ ] Memory profile for 500+ queued items

**Expected Gain**: Lower CPU, reduced latency

#### 8.4: Essentia Sidecar Hardening (6 hours)
- [ ] Implement 60s timeout watchdog (currently 45s)
- [ ] Add process pool (spawn 2-4 workers instead of 1)
- [ ] Graceful fallback if binary missing
- [ ] Performance telemetry (throughput, success rate)

**Expected Gain**: 2x throughput, reduced failures

---

### Phase 9: User Experience Polish (2 weeks)

**Goal**: Professional UI/UX refinements and edge case handling.

#### 9.1: Settings UI Cleanup (4 hours)
- [ ] Group related settings (Network, Audio, Analysis, Playback)
- [ ] Add explanatory tooltips for each setting
- [ ] Implement reset-to-defaults button
- [ ] Add import/export settings as JSON

#### 9.2: Error Handling & User Feedback (6 hours)
- [ ] Consistent error message format
- [ ] User-friendly tooltips (instead of stack traces)
- [ ] Automatic error reporting (optional telemetry)
- [ ] Recovery suggestions for common failures

#### 9.3: Keyboard Shortcuts & Accessibility (4 hours)
- [ ] Implement keyboard navigation (Tab, Enter, Escape)
- [ ] Add keyboard shortcuts reference (Help menu)
- [ ] Screen reader support audit
- [ ] High contrast theme option

#### 9.4: Onboarding & Help (3 hours)
- [ ] First-run wizard (auth setup, preferences)
- [ ] In-app help context (F1)
- [ ] Quick start tutorial slides
- [ ] FAQ & troubleshooting links

---

### Phase 10: Release Preparation (2 weeks)

**Goal**: Prepare for v1.0 production release.

#### 10.1: Release Build Setup (4 hours)
- [ ] Configure release profiles (Debug vs Release)
- [ ] Code obfuscation for .NET assemblies
- [ ] Installer creation (Windows MSI + portable)
- [ ] Auto-update mechanism (Squirrel.Windows)

#### 10.2: Documentation Finalization (6 hours)
- [ ] User manual (PDF + HTML)
- [ ] Installation guide (OS-specific)
- [ ] Troubleshooting guide (FAQ format)
- [ ] API documentation (for plugins, future)

#### 10.3: Security Audit (6 hours)
- [ ] Token storage verification (DPAPI)
- [ ] Network request validation (SSL/TLS)
- [ ] Input sanitization audit
- [ ] Dependency vulnerability scan (Snyk)

#### 10.4: Beta Testing Program (4 hours)
- [ ] Create beta test group (10-20 users)
- [ ] Set up feedback channel (Discord/GitHub Issues)
- [ ] Create bug report template
- [ ] Establish issue triage process

---

## Alternative: Lightweight Plan (Minimal Scope)

If resources are limited, consider this reduced-scope plan:

### Phase 7 Lite: Critical Tests Only (2 weeks)
- Drop Detection tests (ensure DJ cues work)
- Download orchestration tests (verify multi-lane)
- Recovery journal tests (crash safety)
- **Target**: 30% coverage, 50 critical tests

### Phase 8 Lite: Database Optimization Only (2 weeks)
- Profile top 10 slow queries
- Add 3-5 critical indexes
- Implement result caching
- **Expected**: 2x faster library loads

### Phase 9 Lite: Skip detailed UX, focus on:
- Error message consistency
- Keyboard shortcuts (Ctrl+S save, etc.)
- **Timeline**: 1 week

**Total Timeline**: 5 weeks (vs 10 weeks full plan)

---

## Resource Estimation

### Full Plan (Weeks 1-10)

| Phase | Task | Hours | Duration | Dev Days |
|-------|------|-------|----------|----------|
| 7 | Unit Tests | 16 | 2w | 4 |
| 7 | Integration Tests | 12 | 1.5w | 3 |
| 7 | E2E Tests | 8 | 1w | 2 |
| 7 | Benchmarks | 4 | 0.5w | 1 |
| 8 | DB Optimization | 12 | 1.5w | 3 |
| 8 | UI Virtualization | 20 | 2.5w | 5 |
| 8 | Download Optim. | 8 | 1w | 2 |
| 8 | Essentia Harden | 6 | 0.75w | 1.5 |
| 9 | Settings UI | 4 | 0.5w | 1 |
| 9 | Error Handling | 6 | 0.75w | 1.5 |
| 9 | A11y & Shortcuts | 4 | 0.5w | 1 |
| 9 | Onboarding | 3 | 0.5w | 0.75 |
| 10 | Release Build | 4 | 0.5w | 1 |
| 10 | Docs | 6 | 0.75w | 1.5 |
| 10 | Security | 6 | 0.75w | 1.5 |
| 10 | Beta Program | 4 | 0.5w | 1 |
| **TOTAL** | | **121 hours** | **10 weeks** | **31 dev-days** |

---

## Risk Assessment

### High Risk Items

1. **UI Virtualization** - Complex Avalonia refactoring
   - Mitigation: Start with library list only, test heavily
   - Fallback: Keep current implementation if too risky

2. **Database Scaling** - Unknown with 100k+ tracks
   - Mitigation: Create test database with 50k tracks
   - Fallback: Add manual pagination UI

3. **Essentia Reliability** - External process management
   - Mitigation: Implement watchdog + retry logic
   - Fallback: Make analysis optional

### Medium Risk Items

4. **Memory Leaks** - Long-running app stability
   - Testing required: 24-hour stress test

5. **Spotify API Changes** - External dependency
   - Mitigation: Maintain offline-first design

---

## Success Criteria

### Phase 7 (Testing)
- [ ] ≥30% unit test coverage
- [ ] ≥150 passing tests
- [ ] All critical service tests green
- [ ] Performance baseline established

### Phase 8 (Performance)
- [ ] Library load: <500ms for 10,000 tracks
- [ ] UI: 60fps with 1,000+ visible items
- [ ] Database: <50ms for typical queries
- [ ] Essentia: <60s per album (20 tracks)

### Phase 9 (UX)
- [ ] Zero fatal exceptions in 8-hour session
- [ ] First-time users can complete setup in <5 min
- [ ] All error messages have recovery suggestions

### Phase 10 (Release)
- [ ] MSI installer works on Windows 7+
- [ ] Portable ZIP doesn't require installation
- [ ] Auto-update checks work silently
- [ ] Beta testers report <5 blocker issues

---

## Decision Matrix

### Option A: Full Stabilization Plan (10 weeks)
**Pros**:
- Production-ready quality
- 60%+ test coverage
- Optimized for scale
- Professional release

**Cons**:
- Longer timeline
- No new features visible to users
- High effort investment

**Recommendation**: ✅ **Best for professional product**

---

### Option B: Lightweight Plan (5 weeks)
**Pros**:
- Faster time-to-release
- Essential testing + optimization
- Reasonable risk mitigation

**Cons**:
- Lower test coverage (30%)
- Potential scalability issues
- May need post-release fixes

**Recommendation**: ⚠️ **Viable if timeline critical**

---

### Option C: Feature-First Plan (Skip to Phase 11)
**Pros**:
- Visible feature progress
- Community requests satisfied
- Momentum maintained

**Cons**:
- Release quality suffers
- Tech debt accumulates
- Higher post-release bug rate

**Recommendation**: ❌ **Not recommended**

---

## Next Steps (Pending Feedback)

**Please advise on:**

1. **Plan Preference**: Full (10w) vs Lightweight (5w) vs Other?
2. **Timeline**: When is v1.0 release target?
3. **Resources**: 1 developer full-time vs part-time vs weekend?
4. **Priorities**: Testing > Performance > UX, or different order?
5. **Risk Appetite**: Enterprise-grade stable vs "good enough" beta?

**Once you provide feedback, I will:**
- Create detailed sprint breakdown
- Assign specific issue numbers/GitHub milestones
- Begin implementation on prioritized phase
- Establish weekly progress tracking

---

**Document Status**: ⏳ **AWAITING FEEDBACK**  
**Last Updated**: December 28, 2025, 2:45 PM

