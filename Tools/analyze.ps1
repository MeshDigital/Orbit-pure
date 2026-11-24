$paths = Get-Content filepaths.txt
$existing = 0; $missing = 0; $roots = @{}
foreach ($p in $paths) {
    if (Test-Path $p) { $existing++ } else { $missing++ }
    $parts = $p.Split('\')
    if ($parts.Length -ge 5) {
        $root = ($parts[0..4] -join '\')
        $roots[$root] = [int]$roots[$root] + 1
    }
}
Write-Host "Total: $($paths.Count)"
Write-Host "Existing: $existing"
Write-Host "Missing: $missing"
Write-Host "Top Roots:"
$roots.GetEnumerator() | Sort-Object Value -Descending | Select-Object -First 10 | ForEach-Object { Write-Host "$($_.Value) - $($_.Name)" }
