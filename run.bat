@echo off
REM ============================================================
REM  Jarvis launcher for Windows.
REM  - Verifies Python 3.10+ is on PATH.
REM  - Installs requirements ONCE (or when requirements*.txt change).
REM  - Then just runs jarvis.py with whatever args you passed.
REM
REM  Usage:
REM    run.bat                  default mode from config.yaml
REM    run.bat --text           text mode
REM    run.bat --voice          voice mode
REM    run.bat --debug          mirror logs to terminal
REM
REM  First run installs requirements-all.txt (everything). To use a
REM  smaller profile, set JARVIS_REQS before running:
REM    set JARVIS_REQS=requirements.txt ^&^& run.bat --text
REM ============================================================
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

REM ---- 1. Python check ---------------------------------------
where python >nul 2>&1
if errorlevel 1 (
    echo [run.bat] Python is not on PATH.
    echo           Install Python 3.10+ from https://www.python.org/downloads/
    echo           and tick "Add python.exe to PATH" in the installer.
    exit /b 1
)

for /f "tokens=2 delims= " %%V in ('python --version 2^>^&1') do set "PYVER=%%V"
for /f "tokens=1,2 delims=." %%a in ("!PYVER!") do (
    set "PYMAJOR=%%a"
    set "PYMINOR=%%b"
)
if !PYMAJOR! LSS 3 goto :pyold
if !PYMAJOR! EQU 3 if !PYMINOR! LSS 10 goto :pyold
goto :pyok
:pyold
echo [run.bat] Python !PYVER! is too old. Need 3.10 or newer.
exit /b 1
:pyok

REM ---- 2. Requirements check ---------------------------------
REM We hash all requirements*.txt and compare to a stamp. Fast path:
REM hash matches -> skip pip entirely (sub-second). Slow path runs
REM only on first launch or after editing a requirements file.
if not defined JARVIS_REQS set "JARVIS_REQS=requirements-all.txt"
set "STAMP=.install-stamp"
set "NEED_INSTALL=0"

for /f "delims=" %%H in ('python -c "import hashlib,glob;h=hashlib.sha256();[h.update(open(f,'rb').read()) for f in sorted(glob.glob('requirements*.txt'))];print(h.hexdigest())"') do set "REQ_HASH=%%H"

if not exist "%STAMP%" (
    set "NEED_INSTALL=1"
) else (
    set /p STAMP_HASH=<"%STAMP%"
    if not "!STAMP_HASH!"=="!REQ_HASH!" set "NEED_INSTALL=1"
)

if "!NEED_INSTALL!"=="1" (
    echo [run.bat] Installing dependencies from %JARVIS_REQS% ^(one-time, may take a few minutes^)...
    python -m pip install --disable-pip-version-check -q -r "%JARVIS_REQS%"
    if errorlevel 1 (
        echo [run.bat] pip install failed. See output above.
        exit /b 1
    )
    > "%STAMP%" echo !REQ_HASH!
    echo [run.bat] Dependencies installed.
)

REM ---- 3. Config check ---------------------------------------
if not exist "config.yaml" (
    echo [run.bat] No config.yaml found — copying from config.example.yaml.
    echo           Edit config.yaml to pick your LLM backend / paste keys.
    copy /y "config.example.yaml" "config.yaml" >nul
)

REM ---- 4. Launch ---------------------------------------------
python jarvis.py %*
exit /b %errorlevel%
