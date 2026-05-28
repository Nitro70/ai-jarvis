@echo off
REM Local build helper for the .NET edition. Produces:
REM   publish\app\Jarvis-NET.exe
REM Requires .NET 8 SDK (winget install Microsoft.DotNet.SDK.8).
setlocal
cd /d "%~dp0"

where dotnet >nul 2>&1
if errorlevel 1 (
    echo .NET SDK not found. Install with:
    echo     winget install Microsoft.DotNet.SDK.8
    exit /b 1
)

dotnet restore Jarvis.NET.sln || exit /b 1

dotnet publish src\Jarvis.App\Jarvis.App.csproj ^
    -c Release -r win-x64 --self-contained ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o publish\app || exit /b 1

echo.
echo Built: %CD%\publish\app\Jarvis-NET.exe
