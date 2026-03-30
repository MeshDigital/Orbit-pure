param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$Lite
)

$ErrorActionPreference = "Stop"

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot "..")
$projectFile = Join-Path $repoRoot "SLSKDONET.csproj"

if (-not (Test-Path $projectFile)) {
    throw "Could not find project file at $projectFile"
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $projectXml = Get-Content $projectFile -Raw
    $versionMatch = [regex]::Match($projectXml, "<Version>([^<]+)</Version>")
    if (-not $versionMatch.Success) {
        throw "Could not determine version from SLSKDONET.csproj"
    }

    $Version = $versionMatch.Groups[1].Value.Trim()
}

$artifactsRoot = Join-Path $repoRoot "artifacts"
$publishRoot = Join-Path $artifactsRoot "alpha\$Runtime\publish"
$flavorSuffix = if ($Lite) { "-lite" } else { "" }
$zipPath = Join-Path $artifactsRoot ("ORBIT-{0}-{1}{2}.zip" -f $Version, $Runtime, $flavorSuffix)
$manifestPath = Join-Path $artifactsRoot ("ORBIT-{0}-{1}{2}-manifest.txt" -f $Version, $Runtime, $flavorSuffix)

if (Test-Path $publishRoot) {
    Remove-Item $publishRoot -Recurse -Force
}

if (Test-Path $zipPath) {
    Remove-Item $zipPath -Force
}

if (Test-Path $manifestPath) {
    Remove-Item $manifestPath -Force
}

New-Item -ItemType Directory -Force -Path $publishRoot | Out-Null

Push-Location $repoRoot
try {
    dotnet publish $projectFile `
        -c $Configuration `
        -r $Runtime `
        --self-contained true `
        -p:PublishSingleFile=true `
        -p:IncludeNativeLibrariesForSelfExtract=true `
        -p:EnableCompressionInSingleFile=true `
        -o $publishRoot

    Get-ChildItem -Path $publishRoot -Filter *.pdb -Recurse | Remove-Item -Force

    if ($Lite)
    {
        $essentiaModelsPath = Join-Path $publishRoot "Tools\Essentia\models"
        if (Test-Path $essentiaModelsPath)
        {
            # Remove large optional .pb model files to produce a smaller tester package.
            Get-ChildItem $essentiaModelsPath -Filter *.pb -File -Recurse | Remove-Item -Force
        }
    }

    @(
        "ORBIT alpha package"
        "Version: $Version"
        "Runtime: $Runtime"
        "Configuration: $Configuration"
        "Flavor: $(if ($Lite) { 'Lite' } else { 'Full' })"
        "Published: $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
        ""
        "Entry point: ORBIT.exe"
        "Notes: appsettings.json is required at runtime; Tools and Data are preserved intentionally."
        "Notes: Lite flavor removes Tools/Essentia/models/*.pb and may disable advanced analysis features."
    ) | Set-Content -Path $manifestPath

    Compress-Archive -Path (Join-Path $publishRoot "*") -DestinationPath $zipPath -Force

    Write-Host "Alpha package created:" -ForegroundColor Green
    Write-Host "  Publish folder: $publishRoot"
    Write-Host "  Zip package:    $zipPath"
    Write-Host "  Manifest:       $manifestPath"
}
finally {
    Pop-Location
}