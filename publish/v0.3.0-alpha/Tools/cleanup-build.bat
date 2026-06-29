@echo off
REM ORBIT Build Cleanup Script
REM Kills lingering MSBuild worker nodes and .NET Host processes that lock files

echo ================================================
echo ORBIT Build Environment Cleanup
echo ================================================
echo.

echo [1/3] Shutting down MSBuild worker nodes...
dotnet build-server shutdown
if %ERRORLEVEL% EQU 0 (
    echo ✓ MSBuild workers stopped
) else (
    echo ! MSBuild shutdown returned exit code %ERRORLEVEL%
)
echo.

echo [2/3] Killing orphaned .NET Host processes...
taskkill /F /IM dotnet.exe /FI "WINDOWTITLE eq *" 2>nul
if %ERRORLEVEL% EQU 0 (
    echo ✓ Orphaned processes terminated
) else (
    echo ! No orphaned processes found (this is good)
)
echo.

echo [3/3] Cleaning build artifacts...
if exist "bin" (
    rmdir /s /q bin
    echo ✓ Removed bin folder
)
if exist "obj" (
    rmdir /s /q obj
    echo ✓ Removed obj folder
)
echo.

echo ================================================
echo Cleanup Complete!
echo You can now build ORBIT with a fresh slate.
echo ================================================
pause
