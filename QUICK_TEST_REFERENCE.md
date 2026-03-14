# Phase 7 Unit Tests - Quick Reference

## 📋 What Was Created

✅ **2 Test Files** (25 total tests)
- UserWorkspaceViewModelTests.cs (14 tests)
- UserWorkspaceNavigationIntegrationTests.cs (11 tests)

✅ **2 Test Runners**
- run-phase7-tests.ps1 (PowerShell)
- run-phase7-tests.bat (Batch)

✅ **2 Documentation Files**
- PHASE_7_TESTS_README.md (Full guide)
- PHASE_7_TESTS_STATUS.md (Status report)

---

## 🚀 Run Tests Immediately

### PowerShell (Recommended)
```powershell
.\run-phase7-tests.ps1
```

### Command Prompt
```cmd
run-phase7-tests.bat
```

### Manual Command
```bash
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~UserWorkspace"
```

---

## 📊 Test Coverage Summary

| Component | Tests | Status |
|-----------|-------|--------|
| **Core ViewModel** | 14 | ✅ Initialization, Properties, Commands, Persistence |
| **Navigation** | 11 | ✅ Commands, PageType, Service Integration |
| **Total** | **25** | **✅ Ready to Execute** |

---

## 🎯 Test Categories

### ViewModel Tests (UserWorkspaceViewModelTests.cs)
1. ✅ Initialization - Child ViewModels created
2. ✅ Pane Widths - Default 320|800|380
3. ✅ Density - Range 0.5-1.5
4. ✅ Properties - Reactive binding
5. ✅ Commands - Increase/Decrease
6. ✅ Persistence - Save/Load config
7. ✅ Disposal - Cleanup subscriptions
8. ✅ Integration - Child ViewModel updates
9. ✅ Events - EventBus wiring

### Navigation Tests (UserWorkspaceNavigationIntegrationTests.cs)
1. ✅ MainViewModel - Has UserWorkspaceViewModel
2. ✅ Command - ExecutesSuccessfully
3. ✅ PageType - Changes on navigate
4. ✅ Enum - Includes UserWorkspace
5. ✅ Service - Registers page
6. ✅ Binding - Command available
7. ✅ Priority - UserWorkspace preferred
8. ✅ Back/Forth - Multiple navigations work
9. ✅ Initialization - ViewModel ready before nav
10. ✅ Startup - Starts at Home
11. ✅ Calls - Service invoked

---

## ✨ Key Test Assertions

**Initialization**
```csharp
Assert.NotNull(vm.DJCompanion);
Assert.NotNull(vm.ForensicInspector);
Assert.NotNull(vm.HealthBar);
```

**Properties**
```csharp
Assert.Equal("320", vm.LeftPaneWidth);
Assert.Equal(1.0, vm.WorkspaceDensity);
```

**Persistence**
```csharp
vm.SaveWorkspaceConfig();
Assert.True(File.Exists(configPath));
```

**Navigation**
```csharp
mainVM.NavigateUserWorkspaceCommand.Execute(null);
Assert.Equal(PageType.UserWorkspace, mainVM.CurrentPageType);
```

---

## 📁 File Locations

```
QMUSICSLSK/
├── Tests/SLSKDONET.Tests/ViewModels/
│   ├── UserWorkspaceViewModelTests.cs (NEW - 14 tests)
│   └── UserWorkspaceNavigationIntegrationTests.cs (NEW - 11 tests)
├── PHASE_7_TESTS_README.md (NEW - Full guide)
├── PHASE_7_TESTS_STATUS.md (NEW - Status report)
├── run-phase7-tests.ps1 (NEW - PowerShell runner)
├── run-phase7-tests.bat (NEW - Batch runner)
└── QUICK_TEST_REFERENCE.md (THIS FILE)
```

---

## 🔍 Test Framework Details

**Framework**: xUnit + Moq + ReactiveUI  
**Namespace**: `SLSKDONET.Tests.ViewModels`  
**Async**: Not required (Scheduler.Immediate)  
**Mocking**: ILibraryService, IEventBus, Services  
**Real**: UserWorkspaceViewModel, child ViewModels  

---

## ⚡ Quick Diagnostic Commands

**Build Tests Only**
```bash
dotnet build Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj
```

**List All Phase 7 Tests**
```bash
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~UserWorkspace" -t
```

**Run Verbose Output**
```bash
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj --filter "FullyQualifiedName~UserWorkspace" -v diag
```

**Generate Coverage Report**
```bash
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj /p:CollectCoverage=true /p:CoverageFormat=opencover
```

---

## 🎓 Learning Resources

- Full guide: [PHASE_7_TESTS_README.md](PHASE_7_TESTS_README.md)
- Status report: [PHASE_7_TESTS_STATUS.md](PHASE_7_TESTS_STATUS.md)
- Test source: `Tests/SLSKDONET.Tests/ViewModels/*.cs`

---

## ✅ Verification Checklist

- [ ] Run `dotnet test` with Phase 7 filter
- [ ] Verify 25/25 tests pass
- [ ] Check test output contains "Test Run Successful"
- [ ] Review coverage for UserWorkspaceViewModel area
- [ ] Confirm all file paths correct in output
- [ ] Document any failures for debugging

---

## 📞 Troubleshooting

**Q: Tests won't run?**  
A: Run `dotnet clean && dotnet build` first to ensure tests are compiled.

**Q: Build errors in test files?**  
A: Check that UserWorkspaceViewModel.cs compiles (not the test build error from earlier).

**Q: Mocking errors?**  
A: Verify all services are properly mocked in CreateViewModel() helper.

**Q: File not found errors?**  
A: Ensure Working Directory is `C:\Users\quint\OneDrive\Documenten\GitHub\QMUSICSLSK`

---

**Status**: ✅ Phase 7 Unit Tests Created & Documented  
**Ready**: Yes - Run tests immediately with `.\run-phase7-tests.ps1`  
**Expected**: All 25 tests pass (after clean build resolves earlier cache issue)
