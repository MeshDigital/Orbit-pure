# Phase 7 Unit Tests - PowerShell Execution Script
# Runs all Phase 7 tests for UserWorkspaceViewModel and Navigation Integration

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Phase 7 Unit Tests - PowerShell Script" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$rootPath = "C:\Users\quint\OneDrive\Documenten\GitHub\QMUSICSLSK"
$testProject = "Tests/SLSKDONET.Tests/SLSKDONET.Tests.csproj"

Set-Location $rootPath

# Test 1: All Phase 7 tests
Write-Host "[1] Running all Phase 7 tests..." -ForegroundColor Yellow
Write-Host ""

$result = & dotnet test $testProject -v normal --filter "FullyQualifiedName~UserWorkspace" --logger "console;verbosity=normal"

if ($LASTEXITCODE -eq 0) {
    Write-Host ""
    Write-Host "[✓ SUCCESS] All Phase 7 tests passed!" -ForegroundColor Green
    Write-Host ""
    
    # Test 2: UserWorkspaceViewModelTests
    Write-Host "[2] Running UserWorkspaceViewModelTests (14 tests)..." -ForegroundColor Yellow
    Write-Host ""
    $result = & dotnet test $testProject -v normal --filter "ClassName~UserWorkspaceViewModelTests" --logger "console;verbosity=normal"
    
    Write-Host ""
    
    # Test 3: Navigation Integration Tests
    Write-Host "[3] Running Navigation Integration Tests (11 tests)..." -ForegroundColor Yellow
    Write-Host ""
    $result = & dotnet test $testProject -v normal --filter "ClassName~UserWorkspaceNavigationIntegrationTests" --logger "console;verbosity=normal"
    
    Write-Host ""
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "✓ Test Execution Complete - All Passed" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
} else {
    Write-Host ""
    Write-Host "[✗ FAILED] Some tests failed. Review output above." -ForegroundColor Red
    Write-Host ""
    exit 1
}

Read-Host "Press Enter to continue"
