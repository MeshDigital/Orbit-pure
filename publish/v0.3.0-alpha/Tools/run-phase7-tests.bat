@echo off
REM Phase 7 Unit Tests Execution Script
REM Runs all Phase 7 tests for UserWorkspaceViewModel and Navigation Integration

cd /d "C:\Users\quint\OneDrive\Documenten\GitHub\QMUSICSLSK"

echo ========================================
echo Phase 7 Unit Tests - Execution Script
echo ========================================
echo.

echo [1] Running all Phase 7 tests...
echo.
dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -v normal --filter "FullyQualifiedName~UserWorkspace"

if %ERRORLEVEL% EQU 0 (
    echo.
    echo [SUCCESS] All Phase 7 tests passed!
    echo.
    echo [2] Running UserWorkspaceViewModelTests only...
    dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -v normal --filter "ClassName~UserWorkspaceViewModelTests"
    
    echo.
    echo [3] Running Navigation Integration Tests only...
    dotnet test Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj -v normal --filter "ClassName~UserWorkspaceNavigationIntegrationTests"
) else (
    echo.
    echo [FAILED] Some tests failed. Review output above.
    exit /b 1
)

echo.
echo ========================================
echo Test Execution Complete
echo ========================================
pause
