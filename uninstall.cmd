@echo off
REM Uninstalls Jarvis. Backs up your config + memory to Documents first
REM (you'll be asked). Doesn't touch Python or YouTube Music.
REM
REM cd to %TEMP% first so this cmd.exe isn't holding the install dir as
REM its working directory while the PS script tries to delete it.
cd /d "%TEMP%"
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/uninstall.ps1 | iex"
pause
