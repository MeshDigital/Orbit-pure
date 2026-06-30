#Requires -Version 5.1
<#
.SYNOPSIS
  ORBIT — EDMFormer Windows Installer (PowerShell)
.DESCRIPTION
  Installs the EDMFormer phrase-detection microservice into a conda environment.
  Run from any directory; paths are resolved relative to this script.
  Requires: conda (Miniconda/Anaconda), git, ~4GB disk.
.EXAMPLE
  powershell -ExecutionPolicy Bypass -File Tools\install_edmformer.ps1
#>

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

Write-Host ""
Write-Host "=======================================================" -ForegroundColor Magenta
Write-Host "  ORBIT -- EDMFormer Windows Installer (PowerShell)" -ForegroundColor Magenta
Write-Host "=======================================================" -ForegroundColor Magenta

# ── 0. Verify conda ────────────────────────────────────────────────────────────
Write-Step "[0] Checking conda..."
$condaCmd = Get-Command conda -ErrorAction SilentlyContinue
if (-not $condaCmd) {
    Write-Fail "conda not found in PATH.`nInstall Miniconda: https://www.anaconda.com/download/success`nThen re-open this terminal and run the script again."
}
Write-OK "conda found: $($condaCmd.Source)"

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
