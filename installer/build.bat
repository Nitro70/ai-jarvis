@echo off
REM Local build helper. Produces:
REM   publish\installer\JarvisInstaller.exe
REM   publish\settings\JarvisSettings.exe
REM Requires .NET 8 SDK on PATH (or newer that can target net8.0-windows).
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK not found. Install with:
    echo     winget install Microsoft.DotNet.SDK.8
    exit /b 1
)

dotnet restore Jarvis.Installer.sln || exit /b 1

dotnet publish src\Jarvis.Settings\Jarvis.Settings.csproj ^
    -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o publish\settings || exit /b 1

dotnet publish src\Jarvis.Installer\Jarvis.Installer.csproj ^
    -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o publish\installer || exit /b 1

echo.
echo Built:
echo   %CD%\publish\installer\JarvisInstaller.exe
echo   %CD%\publish\settings\JarvisSettings.exe
