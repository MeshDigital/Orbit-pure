@echo off
setlocal enabledelayedexpansion

echo =======================================================
echo  ORBIT -- EDMFormer Windows Installer
echo =======================================================
echo.

REM ── Paths ──────────────────────────────────────────────────────────────────
set "TOOLS_DIR=%~dp0"
set "EDMFORMER_DIR=%TOOLS_DIR%EDMFormer"
set "SONGFORMER_DIR=%EDMFORMER_DIR%\src\SongFormer"
set "CKPTS_DIR=%SONGFORMER_DIR%\ckpts"
set "ENV_NAME=edmformer"
set "REPO_URL=https://github.com/25ohms/EDMFormer"

REM ── 0. Verify conda ────────────────────────────────────────────────────────
where conda >nul 2>&1
if errorlevel 1 (
    echo ERROR: conda not found in PATH.
    echo.
    echo Please install Miniconda first:
    echo   https://www.anaconda.com/download/success
    echo.
    echo After installing, open a NEW terminal and run this script again.
    pause & exit /b 1
)
echo [0] conda found.

REM ── 1. Clone EDMFormer if needed ───────────────────────────────────────────
echo.
if exist "%EDMFORMER_DIR%\.git" (
    echo [1/6] EDMFormer already cloned -- skipping.
) else (
    echo [1/6] Cloning EDMFormer...
    git clone %REPO_URL% "%EDMFORMER_DIR%"
    if errorlevel 1 (
        echo ERROR: git clone failed. Check your internet connection.
        pause & exit /b 1
    )
    echo       Cloned.
)

REM ── Copy ORBIT's Windows-specific files into the EDMFormer tree ────────────
echo       Copying ORBIT Windows patches into EDMFormer...
copy /Y "%TOOLS_DIR%edmformer_requirements_windows.txt" "%EDMFORMER_DIR%\requirements_windows.txt" >nul
copy /Y "%TOOLS_DIR%edmformer_fetch_pretrained.py" "%SONGFORMER_DIR%\utils\fetch_pretrained_windows.py" >nul
copy /Y "%TOOLS_DIR%edmformer_server.py" "%TOOLS_DIR%edmformer_server.py" >nul

REM ── 2. Remove broken env if it exists ─────────────────────────────────────
echo.
echo [2/6] Removing any existing broken environment...
conda remove -n %ENV_NAME% --all -y >nul 2>&1
echo       Done.

REM ── 3. Create fresh env ────────────────────────────────────────────────────
echo.
echo [3/6] Creating conda environment "%ENV_NAME%" (Python 3.10)...
conda create -n %ENV_NAME% python=3.10 pip -y
if errorlevel 1 (
    echo ERROR: conda env creation failed.
    pause & exit /b 1
)
echo       Created.

REM ── 4. Install PyTorch from Windows wheel index (no Triton) ───────────────
echo.
echo [4/6] Installing PyTorch 2.4.0 (Windows CUDA 12.1, no Triton)...
conda run -n %ENV_NAME% pip install ^
    torch==2.4.0 torchaudio==2.4.0 ^
    --index-url https://download.pytorch.org/whl/cu121 ^
    --no-deps
if errorlevel 1 (
    echo       CUDA install failed -- trying CPU-only fallback...
    conda run -n %ENV_NAME% pip install ^
        torch==2.4.0 torchaudio==2.4.0 ^
        --index-url https://download.pytorch.org/whl/cpu ^
        --no-deps
    if errorlevel 1 (
        echo ERROR: PyTorch install failed.
        pause & exit /b 1
    )
    echo       CPU-only PyTorch installed. Inference will work but be slower.
) else (
    echo       PyTorch CUDA installed.
)

REM ── 5. Install remaining dependencies ─────────────────────────────────────
echo.
echo [5/6] Installing remaining Python packages...
conda run -n %ENV_NAME% pip install ^
    -r "%EDMFORMER_DIR%\requirements_windows.txt" ^
    --no-warn-script-location
if errorlevel 1 (
    echo       Some packages failed -- retrying with relaxed constraints...
    conda run -n %ENV_NAME% pip install ^
        -r "%EDMFORMER_DIR%\requirements_windows.txt" ^
        --no-warn-script-location --ignore-requires-python
)
echo       Done.

REM ── 6. Download model weights ──────────────────────────────────────────────
echo.
echo [6/6] Downloading model weights (~4GB total).
echo       Supports resume -- safe to re-run if connection drops.
echo.
mkdir "%CKPTS_DIR%\MusicFM" >nul 2>&1
conda run -n %ENV_NAME% python "%SONGFORMER_DIR%\utils\fetch_pretrained_windows.py"
if errorlevel 1 (
    echo.
    echo WARNING: Automatic download did not complete.
    echo Re-run this script to resume -- downloads are resumable.
    echo.
    echo Or download manually and place in %CKPTS_DIR%:
    echo   MusicFM\pretrained_msd.pt  -- https://huggingface.co/minzwon/MusicFM/resolve/main/pretrained_msd.pt
    echo   MusicFM\msd_stats.json     -- https://huggingface.co/minzwon/MusicFM/resolve/main/msd_stats.json
    echo   SongFormer.safetensors     -- https://huggingface.co/ASLP-lab/SongFormer/resolve/main/SongFormer.safetensors
)

REM ── Verify ────────────────────────────────────────────────────────────────
echo.
echo =======================================================
echo  Verifying installation...
echo =======================================================
conda run -n %ENV_NAME% python -c ^
    "import torch; print('torch', torch.__version__, '-- CUDA:', torch.cuda.is_available()); import librosa; print('librosa OK'); import muq; print('muq OK'); import fastapi; print('fastapi OK')"
if errorlevel 1 (
    echo WARNING: Verification incomplete. Some packages may need attention.
) else (
    echo.
    echo =======================================================
    echo  INSTALLATION COMPLETE
    echo =======================================================
    echo.
    echo To start the ORBIT AI phrase detection service:
    echo.
    echo   conda activate %ENV_NAME%
    echo   python "%TOOLS_DIR%edmformer_server.py"
    echo.
    echo ORBIT will detect it automatically at http://127.0.0.1:7774
)
echo.
pause
