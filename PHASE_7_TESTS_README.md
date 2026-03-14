# Phase 7 Unit Tests Documentation

## Overview
Comprehensive unit and integration tests for Phase 7: Unified User Workspace implementation. Tests verify:
- ✅ UserWorkspaceViewModel initialization and property binding
- ✅ Child ViewModel orchestration (DJCompanion, ForensicInspector, HealthBar)
- ✅ Navigation command integration
- ✅ PageType enum update
- ✅ Workspace persistence (save/load configuration)
- ✅ Pane width and density control
- ✅ Event propagation through child ViewModels

---

## Test Files

### 1. **UserWorkspaceViewModelTests.cs**
**Location**: `Tests/SLSKDONET.Tests/ViewModels/UserWorkspaceViewModelTests.cs`

Tests core ViewModel functionality with 14 test cases:

| Test | Purpose |
|------|---------|
| `Constructor_InitializesChildViewModels` | Verifies DJCompanion, ForensicInspector, HealthBar initialized |
| `Constructor_InitializesPaneWidths` | Validates default layout: Left 320px, Center 800px, Right 380px |
| `Constructor_InitializesDensitySetting` | Ensures density defaults to 1.0 |
| `CurrentSetlist_PropertyChanged_NotifiesSubscribers` | Tests reactive property binding |
| `IsLoadingTrack_PropertyChanged_NotifiesSubscribers` | Validates load state notification |
| `WorkspaceDensity_CanBeChanged` | Tests density range (0.5-1.5) modification |
| `IncreaseDensityCommand_IncreasesDensity` | Tests density increment |
| `DecreaseDensityCommand_DecreasesDensity` | Tests density decrement |
| `SaveWorkspaceConfig_CreatesConfigFile` | Verifies persistence to `%APPDATA%/ORBIT/workspace_config.json` |
| `SaveAndLoadWorkspaceConfig_PreservesPaneWidths` | Validates round-trip pane width persistence |
| `SaveAndLoadWorkspaceConfig_PreservesDensity` | Validates round-trip density persistence |
| `TrackSelected_UpdatesDJCompanionCurrentTrack` | Tests track selection propagation |
| `Dispose_ClearsSubscriptions` | Verifies IDisposable implementation |
| `MultipleTracksSelected_ChildViewModelsUpdated` | Tests sequential track updates |
| `EventBusIntegration_ChildViewModelsHaveEventBusReference` | Validates EventBus wiring |
| `PaneWidth_CanBeModifiedIndependently` | Tests pane independence |
| `HelpText_CanBeSetAndRead` | Tests help text property |

**Key Assertions**:
```csharp
- Child ViewModels not null
- Pane widths match defaults (320, 800, 380)
- Workspace density range valid (0.5 to 1.5)
- Configuration persists to disk
- Reactive properties notify subscribers
- Disposal cleanup works correctly
```

---

### 2. **UserWorkspaceNavigationIntegrationTests.cs**
**Location**: `Tests/SLSKDONET.Tests/ViewModels/UserWorkspaceNavigationIntegrationTests.cs`

Tests integration with MainViewModel and navigation system (11 test cases):

| Test | Purpose |
|------|---------|
| `MainViewModel_HasUserWorkspaceViewModel` | Verifies ViewModel property exists |
| `NavigateUserWorkspaceCommand_ExecutesSuccessfully` | Tests command execution |
| `NavigateUserWorkspaceCommand_ChangesCurrentPageType` | Validates PageType change |
| `PageType_IncludesUserWorkspace` | Confirms enum includes UserWorkspace |
| `NavigationService_CanRegisterUserWorkspacePage` | Tests page registration |
| `UserWorkspaceCommand_AvailableForBinding` | Verifies ICommand interface |
| `UserWorkspace_Preferred_Over_DJCompanion_InNavigation` | Tests navigation priority |
| `MultipleNavigations_BetweenUserWorkspaceAndOtherPages` | Tests back-and-forth navigation |
| `UserWorkspaceViewModel_IsInitialized_BeforeNavigation` | Validates initialization order |
| `CurrentPageType_StartsAtHome` | Confirms startup page |
| `NavigationService_Called_OnUserWorkspaceNavigation` | Mocks and verifies service calls |

**Key Assertions**:
```csharp
- MainViewModel.UserWorkspaceViewModel not null
- NavigateUserWorkspaceCommand exists and can execute
- PageType.UserWorkspace in enum
- Navigation service called with "UserWorkspace" route
- CurrentPageType changes to UserWorkspace after navigation
- Command implements ICommand interface
```

---

## Running the Tests

### Prerequisites
- .NET 9+ SDK
- Xunit test framework (already in project)
- Moq mocking library (already in project)

### Run All Tests
```powershell
cd C:\Users\quint\OneDrive\Documenten\GitHub\QMUSICSLSK
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -v normal
```

### Run Phase 7 Tests Only
```powershell
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -v normal --filter "FullyQualifiedName~UserWorkspace"
```

### Run Specific Test File
```powershell
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -v normal --filter "FullyQualifiedName~UserWorkspaceViewModelTests"
```

### Run Specific Test
```powershell
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -v normal --filter "Name~SaveAndLoadWorkspaceConfig_PreservesPaneWidths"
```

### Run with Coverage Report
```powershell
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj /p:CollectCoverage=true /p:CoverageFormat=opencover
```

---

## Test Coverage

### UserWorkspaceViewModel
| Category | Coverage |
|----------|----------|
| Initialization | ✅ Constructor, defaults |
| Properties | ✅ CurrentSetlist, IsLoadingTrack, WorkspaceDensity, HelpText |
| Commands | ✅ IncreaseDensity, DecreaseDensity |
| Persistence | ✅ Save/Load WorkspaceConfig |
| Child Integration | ✅ DJCompanion, ForensicInspector, HealthBar |
| Reactive Binding | ✅ WhenAnyValue subscriptions |
| Disposal | ✅ IDisposable cleanup |

### Navigation Integration
| Category | Coverage |
|----------|----------|
| MainViewModel | ✅ UserWorkspaceViewModel property |
| Commands | ✅ NavigateUserWorkspaceCommand |
| Enum | ✅ PageType.UserWorkspace |
| Service | ✅ INavigationService registration |
| Routing | ✅ Page-to-view mapping |

---

## Expected Test Results

When all tests pass, you should see output similar to:
```
Test Run Successful.
Total tests: 25
     Passed: 25
     Failed: 0
     Skipped: 0
Time: 2.345s
```

---

## Common Issues & Troubleshooting

### Issue: "DJCompanionViewModel" constructor not found
**Cause**: DJCompanionViewModel requires specific service injections.
**Solution**: Verify constructor parameters match app.axaml.cs registrations.

### Issue: "UserWorkspaceViewModel not found"
**Cause**: File not in correct namespace or not compiled.
**Solution**: Ensure `ViewModels/UserWorkspaceViewModel.cs` was created and builds successfully.

### Issue: Navigation test fails with "NavigateTo not called"
**Cause**: Main view model not properly wired.
**Solution**: Verify MainViewModel injects UserWorkspaceViewModel and initializes NavigateUserWorkspaceCommand.

### Issue: Persistence tests fail
**Cause**: `%APPDATA%/ORBIT/` directory permissions.
**Solution**: Run tests with admin privileges or ensure directory is writable.

---

## Phase 7 Test Checklist

- ✅ UserWorkspaceViewModelTests created (14 tests)
- ✅ UserWorkspaceNavigationIntegrationTests created (11 tests)
- ✅ Tests compile without errors
- ✅ Tests can be executed via `dotnet test`
- ✅ Mocking setup complete (ILibraryService, IEventBus, etc.)
- ✅ Reactive scheduler configured for unit testing
- ✅ Documentation written

---

## Next Steps (Phase 7.2)

After these tests pass, implement:
1. **Page Integration Tests** - Verify XAML binding to ViewModels
2. **Event Flow Tests** - Test TrackSelectionChangedEvent propagation
3. **Stress Test Integration** - Verify <300ms track switching latency
4. **UI Layout Tests** - Test GridSplitter responsiveness (if Avalonia testing framework available)

---

## References

- **xUnit Documentation**: https://xunit.net/docs/getting-started/netcore
- **Moq Tutorial**: https://github.com/moq/moq4/wiki/Quickstart
- **ReactiveUI Testing**: https://www.reactiveui.net/docs/handbook/testing/
- **Project**: ORBIT v0.1.0-alpha.9.7 (Phase 7)
