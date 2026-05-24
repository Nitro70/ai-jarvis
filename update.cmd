@echo off
REM Pulls the latest Jarvis release from GitHub and applies it in place.
REM Same logic as Jarvis Settings -> Updates tab. Keeps your config.yaml
REM and memory.md untouched.
powershell -NoProfile -ExecutionPolicy Bypass -Command "irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/install.ps1 | iex"
pause
