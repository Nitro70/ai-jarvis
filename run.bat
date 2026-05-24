@echo off
REM ============================================================
REM  Jarvis launcher for Windows.
REM
REM  Designed to "just work" for non-developers:
REM    - If no compatible Python (3.10-3.13) is found, downloads
REM      and silently installs Python 3.12 per-user (no admin
REM      prompt, no PATH surgery required).
REM    - Installs requirements ONCE (or when requirements*.txt
REM      change). Sub-second on subsequent launches.
REM    - Copies config.example.yaml -> config.yaml on first run.
REM    - Then launches jarvis.py with whatever args you passed.
REM
REM  Usage:
REM    run.bat                  default mode from config.yaml
REM    run.bat --text           text mode (no mic)
REM    run.bat --voice          voice mode (wake word + STT)
REM    run.bat --debug          mirror logs to terminal
REM
REM  First run installs requirements-all.txt (everything). To use
REM  a smaller profile, set JARVIS_REQS before running:
REM    set JARVIS_REQS=requirements.txt ^&^& run.bat --text
REM ============================================================
setlocal EnableExtensions EnableDelayedExpansion

cd /d "%~dp0"

REM ============================================================
REM  1. Find (or install) a compatible Python.
REM
REM  We need 3.10-3.13. Upper bound is because pygame (used for
REM  TTS playback) has no wheels for 3.14 yet, and Python 3.14
REM  removed distutils so it can't even build from source.
REM ============================================================

set "PY_INSTALLER_VER=3.12.8"
set "PY_INSTALLER_URL=https://www.python.org/ftp/python/3.12.8/python-3.12.8-amd64.exe"
set "PY_USER_EXE=%LocalAppData%\Programs\Python\Python312\python.exe"
set "PYEXE="

REM 1a. Did we (or the user) previously install Python 3.12 per-user?
call :try_py "%PY_USER_EXE%"

REM 1b. Anything called `python` on PATH? (skips the Microsoft Store stub)
if not defined PYEXE for /f "delims=" %%P in ('where python 2^>nul') do call :try_py "%%P"

REM 1c. Ask the `py` launcher about each version we accept.
if not defined PYEXE for %%V in (3.12 3.13 3.11 3.10) do call :try_py_launcher %%V

REM 1d. Last resort: install Python 3.12 ourselves.
if not defined PYEXE call :install_python

if not defined PYEXE (
    echo.
    echo [run.bat] Could not find or install a compatible Python.
    echo           Please install Python 3.12 manually from:
    echo             https://www.python.org/downloads/release/python-3128/
    echo           Make sure to tick "Add python.exe to PATH" in the installer.
    echo.
    pause
    exit /b 1
)

echo [run.bat] Using Python: !PYEXE!

REM ============================================================
REM  2. Requirements check.
REM  Hash all requirements*.txt and compare to a stamp. Fast path:
REM  hash matches -> skip pip entirely (sub-second). Slow path
REM  runs only on first launch or after editing a requirements
REM  file.
REM ============================================================

if not defined JARVIS_REQS set "JARVIS_REQS=requirements-all.txt"
set "STAMP=.install-stamp"
set "NEED_INSTALL=0"

REM Hash via temp file - avoids cmd's nightmare quoting rules for paths-with-spaces inside for /f.
"!PYEXE!" -c "import hashlib,glob;h=hashlib.sha256();[h.update(open(f,'rb').read()) for f in sorted(glob.glob('requirements*.txt'))];print(h.hexdigest())" > "%TEMP%\jarvis-reqhash.txt" 2>nul
set /p REQ_HASH=<"%TEMP%\jarvis-reqhash.txt"
del /q "%TEMP%\jarvis-reqhash.txt" 2>nul

if not exist "%STAMP%" (
    set "NEED_INSTALL=1"
) else (
    set /p STAMP_HASH=<"%STAMP%"
    if not "!STAMP_HASH!"=="!REQ_HASH!" set "NEED_INSTALL=1"
)

if "!NEED_INSTALL!"=="1" (
    echo [run.bat] Installing dependencies from %JARVIS_REQS% ^(one-time, may take a few minutes^)...
    "!PYEXE!" -m pip install --disable-pip-version-check --upgrade pip >nul 2>&1
    "!PYEXE!" -m pip install --disable-pip-version-check -q -r "%JARVIS_REQS%"
    if errorlevel 1 (
        echo.
        echo [run.bat] pip install failed. See output above.
        echo           If the failure mentions pygame, your Python is too new.
        echo           Re-run with:  del .install-stamp ^&^& run.bat
        echo.
        pause
        exit /b 1
    )
    > "%STAMP%" echo !REQ_HASH!
    echo [run.bat] Dependencies installed.
)

REM ============================================================
REM  3. Config check.
REM ============================================================

if not exist "config.yaml" (
    echo [run.bat] No config.yaml found - copying from config.example.yaml.
    echo           Edit config.yaml to pick your LLM backend / paste keys.
    copy /y "config.example.yaml" "config.yaml" >nul
)

REM ============================================================
REM  4. Launch.
REM ============================================================

"!PYEXE!" jarvis.py %*
exit /b %errorlevel%


REM ============================================================
REM  Subroutines
REM ============================================================

:try_py
REM Sets PYEXE if %1 is a usable python.exe (version 3.10-3.13).
if defined PYEXE exit /b 0
set "_cand=%~1"
if "%_cand%"=="" exit /b 0
REM Skip the Microsoft Store App Execution Alias - it just opens the Store.
echo %_cand% | findstr /i "\\WindowsApps\\" >nul && exit /b 0
if not exist "%_cand%" exit /b 0
set "_ver="
"%_cand%" --version > "%TEMP%\jarvis-pyver.txt" 2>&1
if errorlevel 1 (
    del /q "%TEMP%\jarvis-pyver.txt" 2>nul
    exit /b 0
)
for /f "usebackq tokens=2 delims= " %%V in ("%TEMP%\jarvis-pyver.txt") do set "_ver=%%V"
del /q "%TEMP%\jarvis-pyver.txt" 2>nul
if not defined _ver exit /b 0
set "_maj="
set "_min="
for /f "tokens=1,2 delims=." %%a in ("!_ver!") do (
    set "_maj=%%a"
    set "_min=%%b"
)
if not "!_maj!"=="3" exit /b 0
if !_min! LSS 10 exit /b 0
if !_min! GTR 13 exit /b 0
set "PYEXE=%_cand%"
exit /b 0


:try_py_launcher
REM Sets PYEXE by asking `py -X.Y` where its python.exe lives.
if defined PYEXE exit /b 0
where py >nul 2>&1
if errorlevel 1 exit /b 0
py -%1 -c "import sys;print(sys.executable)" > "%TEMP%\jarvis-pylauncher.txt" 2>nul
if errorlevel 1 (
    del /q "%TEMP%\jarvis-pylauncher.txt" 2>nul
    exit /b 0
)
for /f "usebackq delims=" %%P in ("%TEMP%\jarvis-pylauncher.txt") do call :try_py "%%P"
del /q "%TEMP%\jarvis-pylauncher.txt" 2>nul
exit /b 0


:install_python
echo.
echo [run.bat] No compatible Python (3.10-3.13) found on this system.
echo [run.bat] Downloading Python %PY_INSTALLER_VER% installer...
echo           (One-time, per-user install. No admin prompt.)
echo.

set "INSTALLER=%TEMP%\jarvis-python-%PY_INSTALLER_VER%-installer.exe"
if exist "%INSTALLER%" del /q "%INSTALLER%" 2>nul

REM curl.exe ships with Windows 10 (build 17063+) and Windows 11.
where curl >nul 2>&1
if errorlevel 1 goto :install_python_ps
curl -L --fail --show-error -o "%INSTALLER%" "%PY_INSTALLER_URL%"
if errorlevel 1 goto :install_python_ps
goto :install_python_run

:install_python_ps
echo [run.bat] curl failed or unavailable - falling back to PowerShell...
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
    "try { [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12; Invoke-WebRequest -Uri '%PY_INSTALLER_URL%' -OutFile '%INSTALLER%' -UseBasicParsing } catch { Write-Host $_; exit 1 }"
if errorlevel 1 (
    echo [run.bat] Failed to download Python installer.
    echo           Check your internet connection, then install Python 3.12 from:
    echo             https://www.python.org/downloads/release/python-3128/
    exit /b 0
)

:install_python_run
if not exist "%INSTALLER%" (
    echo [run.bat] Download produced no file. Aborting.
    exit /b 0
)
echo [run.bat] Running Python installer silently (this takes ~30-60 seconds)...
"%INSTALLER%" /quiet InstallAllUsers=0 PrependPath=1 Include_test=0 Include_launcher=0 Shortcuts=0 AssociateFiles=0
set "_rc=%errorlevel%"
del /q "%INSTALLER%" 2>nul
if not "%_rc%"=="0" (
    echo [run.bat] Python installer exited with code %_rc%.
    echo           Try installing manually from:
    echo             https://www.python.org/downloads/release/python-3128/
    exit /b 0
)
call :try_py "%PY_USER_EXE%"
if defined PYEXE (
    echo [run.bat] Python %PY_INSTALLER_VER% installed successfully.
) else (
    echo [run.bat] Installer reported success but python.exe not found at:
    echo             %PY_USER_EXE%
)
exit /b 0
