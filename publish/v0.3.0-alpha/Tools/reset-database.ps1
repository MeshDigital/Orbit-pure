# Database Schema Update Script
# This script helps update the database schema after code changes

Write-Host "=== QMUSICSLSK Database Schema Update ===" -ForegroundColor Cyan
Write-Host ""

$dbPath = "$env:APPDATA\ORBIT\library.db"

if (Test-Path $dbPath) {
    Write-Host "Found existing database at: $dbPath" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "The database needs to be updated to include new columns:" -ForegroundColor Yellow
    Write-Host "  - InstrumentalProbability (PlaylistTracks, LibraryEntries)" -ForegroundColor White
    Write-Host "  - Arousal (AudioFeatures)" -ForegroundColor White
    Write-Host ""
    
    # Show database stats
    Write-Host "Current database contains:" -ForegroundColor Cyan
    Write-Host "  - Projects and tracks from previous sessions" -ForegroundColor White
    Write-Host ""
    
    $response = Read-Host "Do you want to DELETE and recreate the database? (yes/no)"
    
    if ($response -eq "yes") {
        Write-Host "Backing up current database..." -ForegroundColor Cyan
        $backupPath = "$dbPath.backup_$(Get-Date -Format 'yyyyMMdd_HHmmss')"
        Copy-Item $dbPath $backupPath
        Write-Host "Backup created: $backupPath" -ForegroundColor Green
        
        Write-Host "Deleting database files..." -ForegroundColor Cyan
        Remove-Item "$dbPath" -Force
        Remove-Item "$dbPath-shm" -Force -ErrorAction SilentlyContinue
        Remove-Item "$dbPath-wal" -Force -ErrorAction SilentlyContinue
        
        Write-Host "" -ForegroundColor Green
        Write-Host "✅ Database deleted. The app will recreate it on next launch." -ForegroundColor Green
        Write-Host ""
        Write-Host "NOTE: All tracks, projects, and metadata will be lost!" -ForegroundColor Red
        Write-Host "Backup is available at: $backupPath" -ForegroundColor Yellow
    } else {
        Write-Host "Operation cancelled. Database was not modified." -ForegroundColor Yellow
    }
} else {
    Write-Host "No existing database found at: $dbPath" -ForegroundColor Green
    Write-Host "The app will create a new one on launch." -ForegroundColor Green
}

Write-Host ""
Write-Host "Press any key to continue..."
$null = $Host.UI.RawUI.ReadKey("NoEcho,IncludeKeyDown")
