@echo off
:: ORBIT — EDMFormer Microservice Installer
:: Installs the EDMFormer phrase-detection service into a conda env.
:: Requires: conda, git, ~8GB disk (model weights), ideally an NVIDIA GPU.

setlocal
set ENV_NAME=edmformer
set REPO_URL=https://github.com/25ohms/EDMFormer
set CLONE_DIR=%~dp0EDMFormer

echo =======================================================
echo  ORBIT — EDMFormer Installation
echo =======================================================

:: Step 1: Clone EDMFormer if not already present
if exist "%CLONE_DIR%\.git" (
    echo [1/5] EDMFormer already cloned at %CLONE_DIR%, skipping.
) else (
    echo [1/5] Cloning EDMFormer into %CLONE_DIR% ...
    git clone %REPO_URL% "%CLONE_DIR%"
    if errorlevel 1 ( echo ERROR: git clone failed & exit /b 1 )
)

:: Step 2: Create conda environment
echo [2/5] Creating conda environment "%ENV_NAME%" (Python 3.10) ...
call conda create -n %ENV_NAME% python=3.10 -y
if errorlevel 1 ( echo ERROR: conda create failed & exit /b 1 )

:: Step 3: Install Python dependencies
echo [3/5] Installing Python dependencies ...
call conda activate %ENV_NAME%
pip install -r "%CLONE_DIR%\requirements.txt"
if errorlevel 1 ( echo ERROR: pip install requirements failed & exit /b 1 )
pip install fastapi "uvicorn[standard]" pydantic
if errorlevel 1 ( echo ERROR: pip install fastapi failed & exit /b 1 )

:: Step 4: Download pretrained model weights
echo [4/5] Downloading pretrained model weights (~4GB) ...
pushd "%CLONE_DIR%\src\SongFormer"
python utils/fetch_pretrained.py
if errorlevel 1 ( echo ERROR: fetch_pretrained.py failed & exit /b 1 )
popd

:: Step 5: Done
echo [5/5] Installation complete!
echo.
echo To start the EDMFormer service:
echo   conda activate %ENV_NAME%
echo   python "%~dp0edmformer_server.py"
echo.
echo The service will listen on http://127.0.0.1:7774
echo ORBIT will automatically detect it and use it for phrase detection.
echo.
pause
