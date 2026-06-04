#!/usr/bin/env pwsh
param(
    [string]$Since,
    [string]$Until,
    [int]$Limit = 40,
    [string]$DbPath = "$env:APPDATA/ORBIT/library.db",
    [string]$LogsPath = "logs/*.log"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Convert-ToSqliteDate {
    param([string]$Value)
    if ([string]::IsNullOrWhiteSpace($Value)) {
        return $null
    }

    $parsed = [DateTime]::Parse($Value).ToUniversalTime()
    return $parsed.ToString("yyyy-MM-dd HH:mm:ss")
}

function Write-Section {
    param([string]$Title)
    Write-Host ""
    Write-Host "=== $Title ===" -ForegroundColor Cyan
}

Write-Host "Collecting strict-mode gate evidence..." -ForegroundColor Green

if (-not (Get-Command sqlite3 -ErrorAction SilentlyContinue)) {
    throw "sqlite3 is not installed or not on PATH."
}

if (-not (Test-Path $DbPath)) {
    throw "Database not found: $DbPath"
}

$sinceSql = Convert-ToSqliteDate -Value $Since
$untilSql = Convert-ToSqliteDate -Value $Until

$where = "1=1"
if ($sinceSql) { $where += " AND Timestamp >= '$sinceSql'" }
if ($untilSql) { $where += " AND Timestamp <= '$untilSql'" }

Write-Section "Window"
Write-Host "Since: $Since"
Write-Host "Until: $Until"
Write-Host "DB:    $DbPath"
Write-Host "Logs:  $LogsPath"

Write-Section "ActivityLogs (autodownload_*)"
$sqlAuto = "SELECT Timestamp, Action, substr(Details,1,240) FROM ActivityLogs WHERE Action LIKE 'autodownload_%' AND $where ORDER BY Timestamp DESC LIMIT $Limit;"
sqlite3 "$DbPath" "$sqlAuto"

Write-Section "ActivityLogs (ingestion_*)"
$sqlIngestion = "SELECT Timestamp, Action, substr(Details,1,240) FROM ActivityLogs WHERE Action LIKE 'ingestion_%' AND $where ORDER BY Timestamp DESC LIMIT $Limit;"
sqlite3 "$DbPath" "$sqlIngestion"

Write-Section "ActivityLogs (all recent actions)"
$sqlAll = "SELECT Timestamp, Action, substr(Details,1,180) FROM ActivityLogs WHERE $where ORDER BY Timestamp DESC LIMIT $Limit;"
sqlite3 "$DbPath" "$sqlAll"

Write-Section "Runtime log lines (strict/legacy signals)"
$patterns = @(
    "Strict-mode AutoDownload selected",
    "autodownload_search_started",
    "autodownload_candidate_found",
    "autodownload_selected",
    "autodownload_no_match",
    "ingestion_queued",
    "ingestion_started",
    "ingestion_completed",
    "ingestion_missing_detected",
    "FindBestMatchAsync",
    "DownloadDiscoveryService",
    "legacy",
    "Ingestion pending",
    "Indexing:",
    "Ready:",
    "Missing file detected"
)

Get-ChildItem -Path $LogsPath -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 10 |
    ForEach-Object {
        $file = $_.FullName
        foreach ($pattern in $patterns) {
            $hits = Select-String -Path $file -Pattern $pattern -SimpleMatch -ErrorAction SilentlyContinue
            if ($hits) {
                Write-Host ""
                Write-Host "[$($_.Name)] pattern='$pattern'" -ForegroundColor Yellow
                $hits | Select-Object -First 8 | ForEach-Object {
                    "{0}:{1}: {2}" -f $_.Path, $_.LineNumber, $_.Line.Trim()
                }
            }
        }
    }

Write-Section "Done"
Write-Host "Evidence capture complete."
