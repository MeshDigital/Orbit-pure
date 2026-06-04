$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..')
$memoryDir = Join-Path $repoRoot 'DOCS/memory'
$indexPath = Join-Path $memoryDir 'MEMORY_INDEX.md'

if (-not (Test-Path $memoryDir)) {
    Write-Error "Memory directory not found: $memoryDir"
}

if (-not (Test-Path $indexPath)) {
    Write-Error "Memory index not found: $indexPath"
}

$statusPattern = '(^>\s*Status:|^\*\*Status\*\*:|^Status:)'
$memoryFiles = Get-ChildItem $memoryDir -Filter '*.md' | Sort-Object Name

$missingStatus = @()
foreach ($file in $memoryFiles) {
    if (-not (Select-String -Path $file.FullName -Pattern $statusPattern -Quiet)) {
        $missingStatus += $file.Name
    }
}

if ($missingStatus.Count -gt 0) {
    Write-Host 'Missing status header in:'
    foreach ($name in $missingStatus) {
        Write-Host " - $name"
    }
    exit 1
}

$expected = $memoryFiles |
    Where-Object { $_.Name -ne 'MEMORY_INDEX.md' } |
    Select-Object -ExpandProperty Name

$indexContent = Get-Content $indexPath -Raw
$indexed = [regex]::Matches($indexContent, '\[[^\]]+\.md\]\(([^)]+\.md)\)') |
    ForEach-Object { [System.IO.Path]::GetFileName($_.Groups[1].Value) } |
    Sort-Object -Unique

$missingInIndex = @($expected | Where-Object { $_ -notin $indexed })
$extraInIndex = @($indexed | Where-Object { $_ -notin $expected })

if ($missingInIndex.Count -gt 0 -or $extraInIndex.Count -gt 0) {
    if ($missingInIndex.Count -gt 0) {
        Write-Host 'Missing from MEMORY_INDEX.md:'
        foreach ($name in $missingInIndex) {
            Write-Host " - $name"
        }
    }

    if ($extraInIndex.Count -gt 0) {
        Write-Host 'Listed in MEMORY_INDEX.md but not found on disk:'
        foreach ($name in $extraInIndex) {
            Write-Host " - $name"
        }
    }

    exit 1
}

Write-Host 'All DOCS/memory files contain a status header.'
Write-Host 'MEMORY_INDEX.md coverage is valid for DOCS/memory/*.md.'
