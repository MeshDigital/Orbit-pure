param(
    [string]$Runtime = "win-x64",
    [string]$Configuration = "Release",
    [string]$Version = "",
    [switch]$Lite,
    [switch]$SkipInstaller
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

    if (-not $SkipInstaller -and -not $Lite) {
        $isccPath = $null
        $isccCommand = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
        if ($isccCommand) { $isccPath = $isccCommand.Source }

        if (-not $isccPath) {
            $candidatePaths = @(
                "$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe",
                "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
                "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
            )
            $isccPath = $candidatePaths | Where-Object { Test-Path $_ } | Select-Object -First 1
        }

        if (-not $isccPath) {
            Write-Host "Inno Setup (ISCC.exe) not found — skipping installer build. Install via 'winget install JRSoftware.InnoSetup' to enable it." -ForegroundColor Yellow
        }
        else {
            $issScript = Join-Path $PSScriptRoot "ORBIT.iss"
            & $isccPath $issScript "/DMyAppVersion=$Version"
            if ($LASTEXITCODE -ne 0) {
                throw "Inno Setup compilation failed with exit code $LASTEXITCODE"
            }
            $setupPath = Join-Path $artifactsRoot ("ORBIT-Setup-{0}-win-x64.exe" -f $Version)
            Write-Host "  Installer:      $setupPath" -ForegroundColor Green
        }
    }
}
finally {
    Pop-Location
}