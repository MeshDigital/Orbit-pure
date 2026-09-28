# Automatic Downloads Phase 2 Integration Plan

**Status**: Ready to Execute  
**Target Duration**: 3-4 hours  
**Effort Level**: Mechanical / Well-Scoped  
**Risk**: Low (feature-gated, non-breaking)  
**Date Created**: May 14, 2026

---

## Overview

The Automatic Downloads system completed Phase 1 (investigation + PoC) on May 13, 2026. Four service skeletons were created, 11 config flags were added, and 25 test skeletons were compiled.

**Phase 2 transforms these skeletons into production-ready integration:**
- Wire services into Dependency Injection
- Hook DownloadManager to call AutoSearchService
- Convert test skeletons into working tests
- Add Settings UI bindings for strict-mode toggle
- Validate end-to-end with real download

This plan is **isolated** from A10 FlowBuilder, Frequent Sources, and Workstation Cockpit work streams.

---

## Scope

### What's INCLUDED:
- ✅ DI registration for 4 AutoDownload services
- ✅ DownloadManager integration hook
- ✅ Settings ViewModel + UI bindings
- ✅ Test skeleton → working test implementation (25 tests)
- ✅ End-to-end validation
- ✅ Feature flag safety (disabled by default)
- ✅ Build + test suite validation

### What's OUT OF SCOPE:
- ❌ AutoDownload algorithm improvements (deferred to Phase 3)
- ❌ Advanced search heuristics (deferred to Phase 3)
- ❌ Workstation UI integration (not needed for Phase 2)
- ❌ Telemetry or analytics (never planned)
- ❌ PII collection (local-only guarantee maintained)

---

## Tasks

### Task 1: DI Registration & Service Wiring
**File**: `App.axaml.cs` → `ConfigureSharedServices()` method  
**Effort**: 15-20 minutes  
**Checklist**:

```
- [ ] Register AutoSearchService as singleton
- [ ] Register MatchScorer as singleton
- [ ] Register SoulseekSearchHelper as singleton
- [ ] Register PrefetchVerifier as singleton
- [ ] Verify TrackFingerprintBuilderService is already registered
- [ ] Build to confirm no DI errors
```

**Code Pattern**:
```csharp
services.AddSingleton<AutoSearchService>();
services.AddSingleton<MatchScorer>();
services.AddSingleton<SoulseekSearchHelper>();
services.AddSingleton<PrefetchVerifier>();
```

**Dependencies**: 
- `IAppConfig` (already available)
- `ISoulseekAdapter` (already available)
- `IDatabaseService` (already available)
- `TrackFingerprintBuilderService` (verify present in DI)

---

### Task 2: DownloadManager Integration Hook
**File**: `Services/DownloadManager.cs` → `ProcessTrackAsync()` method  
**Effort**: 20-30 minutes  
**Checklist**:

```
- [ ] Inject AutoSearchService into DownloadManager constructor
- [ ] Add optional strict-mode code path before existing discovery call
- [ ] Condition: if (config.EnableAutoDownloadStrictMode && track is not null)
- [ ] Call: var result = await _autoSearchService.FindBestMatchAsync(track, cancellationToken)
- [ ] Fallback: if result is null, proceed to existing DownloadDiscoveryService path
- [ ] Log: emit diagnostic logs with "StrictMode: {result.FilePath}" when feature enabled
- [ ] Test: ensure existing path still works when feature disabled
```

**Code Pattern**:
```csharp
private readonly AutoSearchService _autoSearchService;

public DownloadManager(
    // ... existing params ...
    AutoSearchService autoSearchService)
{
    // ... existing assignments ...
    _autoSearchService = autoSearchService;
}

private async Task ProcessTrackAsync(...)
{
    // ... existing setup code ...
    
    if (_appConfig.EnableAutoDownloadStrictMode && track is not null)
    {
        var strictResult = await _autoSearchService.FindBestMatchAsync(track, ct);
        if (strictResult is not null)
        {
            _logger.Information("StrictMode: Selected {FilePath}", strictResult.FilePath);
            // Use strictResult instead of discoveryResult
            discoveryResult = strictResult;
            goto DownloadPhase; // Or refactor to variable
        }
    }
    
    // Fallback to existing DownloadDiscoveryService
    discoveryResult = await _discoveryService.FindBestMatchAsync(...);
    
    DownloadPhase:
    // ... proceed with download ...
}
```

**Dependencies**:
- `AutoSearchService` (injected)
- `AppConfig.EnableAutoDownloadStrictMode` property

---

### Task 3: Settings ViewModel & UI Bindings
**File**: `ViewModels/SettingsViewModel.cs`  
**Effort**: 20-25 minutes  
**Checklist**:

```
- [ ] Add 11 reactive properties for AutoDownload config flags
- [ ] Bind each to AppConfig via ConfigManager.UpdateConfig()
- [ ] Implement property change listeners (OnPropertyChanged pattern)
- [ ] Add validation for numeric ranges (timeouts, file size thresholds, bitrates)
- [ ] Add validation for string lists (allowed extensions, excluded phrases)
- [ ] Update SettingsWindow.axaml to render toggles/inputs
- [ ] Test: toggle strict mode, verify Settings reflects change
```

**Config Properties to Bind**:
```
1. EnableAutoDownloadStrictMode (bool, toggle)
2. AutoDownloadInitialWaitMs (int, spinner, min=1000 max=10000)
3. AutoDownloadExtendedWaitMs (int, spinner, min=5000 max=60000)
4. AutoDownloadAllowedExtensions (string, text input, comma-delimited)
5. AutoDownloadMinFileSizeBytes (int, spinner, min=256KB max=5MB)
6. AutoDownloadMinBitrateKbps (int, spinner, min=128 max=320)
7. AutoDownloadExactFirstOnly (bool, toggle)
8. AutoDownloadMaxCandidatesToScore (int, spinner, min=10 max=200)
9. AutoDownloadExcludedPhrases (string, text area, comma-delimited)
10. AutoDownloadDiagnosticsEnabled (bool, toggle)
11. MinSearchDurationSeconds (int, spinner, already exists but may need range update)
```

**Code Pattern**:
```csharp
public bool EnableAutoDownloadStrictMode
{
    get => _appConfig.EnableAutoDownloadStrictMode;
    set
    {
        if (_appConfig.EnableAutoDownloadStrictMode != value)
        {
            _appConfig.EnableAutoDownloadStrictMode = value;
            _configManager.UpdateConfig(_appConfig);
            this.RaisePropertyChanged();
        }
    }
}
```

**Dependencies**:
- `IAppConfig` (injected)
- `ConfigManager.UpdateConfig()` method

---

### Task 4: Test Skeleton Implementation
**File**: `Tests/SLSKDONET.Tests/Services/AutoDownload/*.cs`  
**Effort**: 45-60 minutes  
**Checklist**:

```
- [ ] AutoSearchServiceTests.cs (8 tests)
  - [ ] FindBestMatchAsync_WithExactTitle_ReturnsMatch
  - [ ] FindBestMatchAsync_WithNoMatch_ReturnsNull
  - [ ] FindBestMatchAsync_WithTimeout_ReturnsNull
  - [ ] FindBestMatchAsync_SkipsExcludedPhrases
  - [ ] FindBestMatchAsync_EnforcesMinBitrate
  - [ ] FindBestMatchAsync_EnforcesMinFileSize
  - [ ] FindBestMatchAsync_RespectsManyToOneLimits
  - [ ] FindBestMatchAsync_FallsBackToTemplate_AfterExactWait

- [ ] MatchScorerTests.cs (8 tests)
  - [ ] ScoreCandidate_WithExactMatch_Returns100
  - [ ] ScoreCandidate_WithPartialMatch_Returns70
  - [ ] ScoreCandidate_WithFormatMismatch_Returns0
  - [ ] ScoreCandidate_WithLowBitrate_Returns0
  - [ ] ScoreCandidate_Deterministic_SameInputSameScore
  - [ ] ScoreCandidate_ClampsTo0_100Range
  - [ ] ScoreCandidate_WeightComponents_Correctly
  - [ ] ScoreCandidate_HandlesNullTracks_Gracefully

- [ ] SoulseekSearchHelperTests.cs (9 tests)
  - [ ] BuildFilteredQuery_AppendsMinBitrate
  - [ ] BuildFilteredQuery_AppendsExtensions
  - [ ] BuildFilteredQuery_AppendsMinFileSize
  - [ ] FilterCandidates_RemovesExcludedPhrases
  - [ ] FilterCandidates_EnforcesMinBitrate
  - [ ] FilterCandidates_EnforcesMinFileSize
  - [ ] RegisterServerExcludedPhrases_CachesBans
  - [ ] BuildFilteredQuery_Deterministic
  - [ ] BuildFilteredQuery_HandlesEmptyLists

- [ ] Additional integration tests (optional, Phase 2.5)
  - [ ] End-to-end: Queue track → StrictMode enabled → AutoSearchService called → Download starts
```

**Code Pattern**:
```csharp
[Fact]
public async Task FindBestMatchAsync_WithExactTitle_ReturnsMatch()
{
    // Arrange
    var mockConfig = new Mock<IAppConfig>();
    var mockAdapter = new Mock<ISoulseekAdapter>();
    var service = new AutoSearchService(mockConfig.Object, mockAdapter.Object);
    var track = new TrackEntity { Artist = "TestArtist", Title = "TestTrack" };

    // Act
    var result = await service.FindBestMatchAsync(track, CancellationToken.None);

    // Assert
    Assert.NotNull(result);
    Assert.Contains("TestArtist", result.FilePath, StringComparison.OrdinalIgnoreCase);
}
```

**Dependencies**:
- Moq (mocking framework, already available)
- XUnit (already available)
- TrackEntity, AutoSearchService, etc.

---

### Task 5: End-to-End Validation
**Effort**: 30-45 minutes  
**Checklist**:

```
- [ ] Build: dotnet build SLSKDONET.sln (must pass, 0 errors)
- [ ] Unit tests: dotnet test --filter AutoDownload (all 25+ tests pass)
- [ ] Integration: Manually queue a known track
  - [ ] Set EnableAutoDownloadStrictMode = true in appsettings.json
  - [ ] Start app, verify Settings UI has new toggles
  - [ ] Queue download, observe logs show "StrictMode: ..." message
  - [ ] Verify download completes or fails predictably (no 2-4 min hangs)
- [ ] Regression: Run full test suite (all existing tests still pass)
- [ ] Feature gate: Disable strict mode, verify fallback to discovery service works
- [ ] Logs: Check for any ERROR or FATAL messages
```

---

## Risks & Mitigations

| Risk | Severity | Mitigation |
|------|----------|-----------|
| DI registration fails | High | Register each service in isolation; build frequently |
| DownloadManager hook breaks existing flow | High | Feature gate (default=false); keep fallback path intact |
| Test skeletons don't compile after body implementation | Medium | Use Moq.Mock pattern consistently; copy-paste from passing tests |
| Settings UI doesn't bind correctly | Medium | Use existing SettingsViewModel pattern; test binding with reactive property changed |
| End-to-end validation blocks due to network | Medium | Use mock Soulseek responses in tests; manual QA in isolated environment |

---

## Validation Checklist

**Pre-integration**:
- [ ] All 4 service skeleton files compile
- [ ] No circular dependencies in DI graph
- [ ] AppConfig has all 11 new properties (verified in app initialization)

**During integration**:
- [ ] Each task completes without build errors
- [ ] Unit tests for that task pass before moving to next task
- [ ] No regression in existing DownloadManager or SearchResultMatcher tests

**Post-integration**:
- [ ] Full test suite passes: `dotnet test SLSKDONET.sln`
- [ ] Build clean: `dotnet build SLSKDONET.sln` (0 errors, baseline warnings only)
- [ ] Manual QA: Queue 3 known tracks, toggle strict mode, verify behavior
- [ ] Logs: No ERROR/FATAL messages during download with strict mode enabled

---

## Next Steps

1. **Execute Task 1** (DI Registration) — 15-20 min
   - Modify App.axaml.cs
   - Build, confirm no errors
   - Commit: `wip: add autodownload services to di`

2. **Execute Task 2** (DownloadManager Hook) — 20-30 min
   - Modify DownloadManager.cs
   - Run existing tests, confirm pass
   - Commit: `wip: wire autodownload to downloadmanager`

3. **Execute Task 3** (Settings UI) — 20-25 min
   - Modify SettingsViewModel.cs + SettingsWindow.axaml
   - Manual QA: toggle strict mode in UI
   - Commit: `wip: add autodownload settings bindings`

4. **Execute Task 4** (Test Implementation) — 45-60 min
   - Implement 25 test bodies
   - Run: `dotnet test --filter AutoDownload`
   - All pass
   - Commit: `feat: implement autodownload test suite`

5. **Execute Task 5** (End-to-End Validation) — 30-45 min
   - Full build + test
   - Manual QA
   - Commit: `feat: autodownload phase 2 integration complete`

6. **Final Steps**:
   - Verify all commits are clean
   - Create summary of Phase 2 deliverables
   - Update `automatic_downloads_phase2_memory.md` with completion state
   - Closure: "Automatic Downloads feature ready for beta testing"

---

## Success Criteria

✅ **All 4 services wired into DI**  
✅ **DownloadManager calls AutoSearchService when feature enabled**  
✅ **25+ tests implemented and passing**  
✅ **Settings UI renders strict-mode toggle and 10 config inputs**  
✅ **End-to-end: Queue track → strict mode enabled → download uses AutoSearchService**  
✅ **Build clean, 0 errors, baseline warnings only**  
✅ **Full test suite passes (no regressions)**  
✅ **Feature disabled by default (safe for release)**

---

## Phase 2 Complete ✅

When all tasks pass and end-to-end validation succeeds:
- Automatic Downloads system transitions from **Investigation** to **Shipping**
- Feature becomes available to users (opt-in via Settings)
- Phase 3 can begin (advanced heuristics, performance hardening)
