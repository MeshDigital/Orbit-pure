#Requires -Version 5.1
<#
.SYNOPSIS
  ORBIT — EDMFormer Windows Installer (PowerShell)
.DESCRIPTION
  Installs the EDMFormer phrase-detection microservice into a conda environment.
  Run from any directory; paths are resolved relative to this script.
  Requires: conda (Miniconda/Anaconda), git, ~4GB disk — both are installed
  automatically (per-user, no admin rights) if missing and consent is given.
.PARAMETER AutoInstallConda
  Install Miniconda silently, without prompting, if conda isn't already on PATH.
  Pass this only after the user has already consented (e.g. from ORBIT's Settings
  dialog). When this switch is absent, a missing conda still prompts interactively.
.PARAMETER AutoInstallGit
  Same as -AutoInstallConda, but for Git for Windows.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\install_edmformer.ps1
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\install_edmformer.ps1 -AutoInstallConda -AutoInstallGit
#>
param(
    [switch]$AutoInstallConda,
    [switch]$AutoInstallGit
)

$ErrorActionPreference = 'Stop'
$ProgressPreference    = 'SilentlyContinue'   # faster Invoke-WebRequest

# ── Paths ─────────────────────────────────────────────────────────────────────
$ScriptDir      = Split-Path -Parent $MyInvocation.MyCommand.Path
$EdmformerDir   = Join-Path $ScriptDir "EDMFormer"
$SongFormerDir  = Join-Path $EdmformerDir "src\SongFormer"
$CkptsDir       = Join-Path $SongFormerDir "ckpts"
$EnvName        = "edmformer"
$RepoUrl        = "https://github.com/25ohms/EDMFormer"

function Write-Step([string]$msg) { Write-Host "`n$msg" -ForegroundColor Cyan }
function Write-OK([string]$msg)   { Write-Host "  $msg" -ForegroundColor Green }
function Write-Warn([string]$msg) { Write-Host "  WARNING: $msg" -ForegroundColor Yellow }
function Write-Fail([string]$msg) { Write-Host "`nERROR: $msg" -ForegroundColor Red; Read-Host "Press Enter to exit"; exit 1 }

function Confirm-Consent([string]$prompt) {
    Write-Host ""
    Write-Host "  $prompt" -ForegroundColor Yellow
    $answer = Read-Host "  Continue? [Y/n]"
    return ($answer -eq '' -or $answer -match '^[Yy]')
}

# Prepend a newly-installed tool's directories to the CURRENT process's PATH so the
# rest of this script (and any child `conda`/`git` invocations) can find it immediately —
# a fresh install doesn't update variables already loaded into this running process.
function Add-ToSessionPath([string[]]$paths) {
    $env:Path = ($paths -join ';') + ';' + $env:Path
}

function Install-MinicondaSilently {
    Write-Host "  Downloading Miniconda installer..." -ForegroundColor Gray
    $installerUrl  = "https://repo.anaconda.com/miniconda/Miniconda3-latest-Windows-x86_64.exe"
    $installerPath = Join-Path $env:TEMP "Miniconda3-latest-Windows-x86_64.exe"
    $targetDir     = Join-Path $env:USERPROFILE "Miniconda3"

    try {
        Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath
    } catch {
        Write-Fail "Failed to download Miniconda: $($_.Exception.Message)"
    }

    Write-Host "  Installing Miniconda to $targetDir (this PC user only, no admin needed)..." -ForegroundColor Gray
    # Official silent-install flags: JustMe = no admin required; AddToPath registers it for
    # this user's future shells (this script also patches its own session below since that
    # registration doesn't affect the already-running process).
    $installArgs = @(
        "/InstallationType=JustMe",
        "/RegisterPython=0",
        "/AddToPath=1",
        "/S",
        "/D=$targetDir"
    )
    $proc = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru
    Remove-Item $installerPath -ErrorAction SilentlyContinue

    if ($proc.ExitCode -ne 0) {
        Write-Fail "Miniconda installer exited with code $($proc.ExitCode)."
    }

    Add-ToSessionPath @($targetDir, (Join-Path $targetDir "Scripts"), (Join-Path $targetDir "condabin"))
}

function Install-GitSilently {
    Write-Host "  Looking up the latest Git for Windows release..." -ForegroundColor Gray
    try {
        $release = Invoke-RestMethod -Uri "https://api.github.com/repos/git-for-windows/git/releases/latest" -Headers @{ "User-Agent" = "ORBIT-Installer" }
        $asset   = $release.assets | Where-Object { $_.name -like "*64-bit.exe" } | Select-Object -First 1
        if (-not $asset) { Write-Fail "Could not find a Git for Windows 64-bit installer in the latest release." }
    } catch {
        Write-Fail "Failed to look up the latest Git for Windows release: $($_.Exception.Message)"
    }

    $installerPath = Join-Path $env:TEMP $asset.name
    Write-Host "  Downloading $($asset.name)..." -ForegroundColor Gray
    try {
        Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $installerPath
    } catch {
        Write-Fail "Failed to download Git for Windows: $($_.Exception.Message)"
    }

    # Installed under LOCALAPPDATA (always user-writable) so no admin elevation is required —
    # the default Program Files location would need it.
    $targetDir = Join-Path $env:LOCALAPPDATA "Programs\Git"
    Write-Host "  Installing Git to $targetDir (this PC user only, no admin needed)..." -ForegroundColor Gray
    $installArgs = @(
        "/VERYSILENT",
        "/NORESTART",
        "/NOCANCEL",
        "/SP-",
        "/SUPPRESSMSGBOXES",
        "/DIR=$targetDir"
    )
    $proc = Start-Process -FilePath $installerPath -ArgumentList $installArgs -Wait -PassThru
    Remove-Item $installerPath -ErrorAction SilentlyContinue

    if ($proc.ExitCode -ne 0) {
        Write-Fail "Git installer exited with code $($proc.ExitCode)."
    }

    Add-ToSessionPath @((Join-Path $targetDir "cmd"), (Join-Path $targetDir "bin"))
}

Write-Host ""
Write-Host "=======================================================" -ForegroundColor Magenta
Write-Host "  ORBIT -- EDMFormer Windows Installer (PowerShell)" -ForegroundColor Magenta
Write-Host "=======================================================" -ForegroundColor Magenta

# ── 0. Verify prerequisites (conda, git) ───────────────────────────────────────
Write-Step "[0] Checking prerequisites..."

$condaCmd = Get-Command conda -ErrorAction SilentlyContinue
if (-not $condaCmd) {
    $shouldInstall = $AutoInstallConda -or (Confirm-Consent "Conda (Miniconda) was not found. ORBIT can download and install it automatically (~80MB, per-user, no admin rights).")
    if (-not $shouldInstall) {
        Write-Fail "conda not found in PATH.`nInstall Miniconda: https://www.anaconda.com/download/success`nThen re-open this terminal and run the script again."
    }
    Install-MinicondaSilently
    $condaCmd = Get-Command conda -ErrorAction SilentlyContinue
    if (-not $condaCmd) {
        Write-Fail "Miniconda was installed but conda still isn't on PATH. Close this terminal, open a new one, and re-run the script."
    }
    Write-OK "Miniconda installed: $($condaCmd.Source)"
} else {
    Write-OK "conda found: $($condaCmd.Source)"
}

$gitCmd = Get-Command git -ErrorAction SilentlyContinue
if (-not $gitCmd) {
    $shouldInstall = $AutoInstallGit -or (Confirm-Consent "Git was not found. ORBIT can download and install Git for Windows automatically (~50MB, per-user, no admin rights).")
    if (-not $shouldInstall) {
        Write-Fail "git not found in PATH.`nInstall Git for Windows: https://git-scm.com/download/win`nThen re-open this terminal and run the script again."
    }
    Install-GitSilently
    $gitCmd = Get-Command git -ErrorAction SilentlyContinue
    if (-not $gitCmd) {
        Write-Fail "Git was installed but isn't on PATH. Close this terminal, open a new one, and re-run the script."
    }
    Write-OK "Git installed: $($gitCmd.Source)"
} else {
    Write-OK "git found: $($gitCmd.Source)"
}

# ── 1. Clone EDMFormer ────────────────────────────────────────────────────────
Write-Step "[1/6] EDMFormer source..."
if (Test-Path (Join-Path $EdmformerDir ".git")) {
    Write-OK "Already cloned — skipping."
} else {
    Write-Host "  Cloning $RepoUrl ..."
    git clone $RepoUrl $EdmformerDir
    if ($LASTEXITCODE -ne 0) { Write-Fail "git clone failed. Check your internet connection." }
    Write-OK "Cloned."
}

# Copy ORBIT's Windows-specific files into the EDMFormer tree
Copy-Item (Join-Path $ScriptDir "edmformer_requirements_windows.txt") `
          (Join-Path $EdmformerDir "requirements_windows.txt") -Force
Copy-Item (Join-Path $ScriptDir "edmformer_fetch_pretrained.py") `
          (Join-Path $SongFormerDir "utils\fetch_pretrained_windows.py") -Force
Write-OK "ORBIT Windows patches applied."

# ── 2. Remove broken env ──────────────────────────────────────────────────────
Write-Step "[2/6] Cleaning up any existing broken environment..."
conda remove -n $EnvName --all -y 2>$null
Write-OK "Done."

# ── 3. Create fresh env ───────────────────────────────────────────────────────
Write-Step "[3/6] Creating conda environment '$EnvName' (Python 3.10)..."
conda create -n $EnvName python=3.10 pip -y
if ($LASTEXITCODE -ne 0) { Write-Fail "conda env creation failed." }
Write-OK "Environment created."

# ── 4. Install PyTorch from Windows CUDA wheel (no Triton) ───────────────────
Write-Step "[4/6] Installing PyTorch 2.4.0 (Windows CUDA 12.1, no Triton)..."
$torchArgs = @(
    "pip", "install",
    "torch==2.4.0", "torchaudio==2.4.0",
    "--index-url", "https://download.pytorch.org/whl/cu121",
    "--no-deps"
)
conda run -n $EnvName @torchArgs
if ($LASTEXITCODE -ne 0) {
    Write-Warn "CUDA wheels failed — falling back to CPU-only PyTorch..."
    $torchArgs = @(
        "pip", "install",
        "torch==2.4.0", "torchaudio==2.4.0",
        "--index-url", "https://download.pytorch.org/whl/cpu",
        "--no-deps"
    )
    conda run -n $EnvName @torchArgs
    if ($LASTEXITCODE -ne 0) { Write-Fail "PyTorch install failed." }
    Write-Warn "CPU-only PyTorch installed. Inference will be slower but functional."
} else {
    Write-OK "PyTorch CUDA installed."
}

# ── 5. Install remaining packages ─────────────────────────────────────────────
Write-Step "[5/6] Installing remaining Python packages..."
$reqFile = Join-Path $EdmformerDir "requirements_windows.txt"
conda run -n $EnvName pip install -r $reqFile --no-warn-script-location
if ($LASTEXITCODE -ne 0) {
    Write-Warn "Some packages failed — retrying with relaxed constraints..."
    conda run -n $EnvName pip install -r $reqFile --no-warn-script-location --ignore-requires-python
}
Write-OK "Packages installed."

# ── 6. Download model weights ─────────────────────────────────────────────────
Write-Step "[6/6] Downloading model weights (~4GB total)..."
Write-Host "  Resumable download — safe to re-run if connection drops." -ForegroundColor Gray

New-Item -ItemType Directory -Force -Path (Join-Path $CkptsDir "MusicFM") | Out-Null
$fetchScript = Join-Path $SongFormerDir "utils\fetch_pretrained_windows.py"
conda run -n $EnvName python $fetchScript
if ($LASTEXITCODE -ne 0) {
    Write-Warn "Download did not complete. Re-run this script to resume."
    Write-Host ""
    Write-Host "  Manual download URLs:" -ForegroundColor Yellow
    Write-Host "    pretrained_msd.pt  : https://huggingface.co/minzwon/MusicFM/resolve/main/pretrained_msd.pt"
    Write-Host "    msd_stats.json     : https://huggingface.co/minzwon/MusicFM/resolve/main/msd_stats.json"
    Write-Host "    SongFormer.sft     : https://huggingface.co/ASLP-lab/SongFormer/resolve/main/SongFormer.safetensors"
    Write-Host "  Place in: $CkptsDir"
}

# ── Verification ──────────────────────────────────────────────────────────────
Write-Step "Verifying installation..."
$verifyCode = @"
import torch
print(f'torch {torch.__version__} -- CUDA: {torch.cuda.is_available()}')
import librosa; print('librosa OK')
import muq; print('muq OK')
import fastapi; print('fastapi OK')
print('All checks passed.')
"@
conda run -n $EnvName python -c $verifyCode
if ($LASTEXITCODE -ne 0) {
    Write-Warn "Verification incomplete — check output above."
} else {
    Write-Host ""
    Write-Host "=======================================================" -ForegroundColor Green
    Write-Host "  INSTALLATION COMPLETE" -ForegroundColor Green
    Write-Host "=======================================================" -ForegroundColor Green
    Write-Host ""
    Write-Host "  To start the ORBIT AI phrase detection service:" -ForegroundColor White
    Write-Host ""
    Write-Host "    conda activate $EnvName" -ForegroundColor Yellow
    Write-Host "    python `"$ScriptDir\edmformer_server.py`"" -ForegroundColor Yellow
    Write-Host ""
    Write-Host "  ORBIT detects it automatically at http://127.0.0.1:7774" -ForegroundColor Gray
}

Write-Host ""
Read-Host "Press Enter to close"
