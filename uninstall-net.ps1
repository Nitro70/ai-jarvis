# Jarvis .NET edition uninstaller.
#
# One-liner:
#   irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/uninstall-net.ps1 | iex
#
# Removes the .NET edition install dir, Start Menu shortcuts, and pointer
# file. Backs up config.yaml + memory.md to Documents first (you'll be asked).
# Does NOT touch the Python edition (separate install dir + pointer).

#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Yes,
    [switch]$NoBackup
)

$ErrorActionPreference = 'Stop'

$PointerPath = Join-Path $env:LOCALAPPDATA 'Jarvis-NET\install-info.json'

function Write-Info($m) { Write-Host $m -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host $m -ForegroundColor Green }
function Write-Warn($m) { Write-Host $m -ForegroundColor Yellow }
function Write-Err($m)  { Write-Host $m -ForegroundColor Red }

if (-not (Test-Path $PointerPath)) {
    Write-Warn "No Jarvis .NET install found (no install-info.json at $PointerPath)."
    return
}

try {
    $existing = Get-Content $PointerPath -Raw | ConvertFrom-Json
} catch {
    Write-Err "install-info.json is corrupted: $($_.Exception.Message)"
    return
}

$installDir = $existing.InstallDir
Write-Host ""
Write-Info "Found Jarvis .NET install:"
Write-Host "  Location: $installDir"
Write-Host "  Version:  $($existing.Version)"
Write-Host ""

if (-not $Yes) {
    $confirm = Read-Host "Uninstall Jarvis .NET from the above location? [y/N]"
    if ($confirm -ne 'y' -and $confirm -ne 'Y') {
        Write-Host "Cancelled."
        return
    }
}

# Optional backup
$backupDir = $null
if (-not $NoBackup -and (Test-Path $installDir)) {
    $doBackup = $true
    if (-not $Yes) {
        $b = Read-Host "Back up config.yaml + memory.md to Documents first? [Y/n]"
        if ($b -eq 'n' -or $b -eq 'N') { $doBackup = $false }
    }
    if ($doBackup) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backupDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "Jarvis-NET-backup-$stamp"
        $copied = 0
        foreach ($f in @('config.yaml', 'config.local.yaml', 'memory.md')) {
            $src = Join-Path $installDir $f
            if (Test-Path $src) {
                if ($copied -eq 0) {
                    New-Item -ItemType Directory -Path $backupDir -Force | Out-Null
                }
                Copy-Item $src $backupDir -Force
                Write-Host "  backed up $f"
                $copied++
            }
        }
        if ($copied -gt 0) { Write-Ok "Backup folder: $backupDir" }
        else { $backupDir = $null; Write-Host "  (nothing to back up)" }
    }
}

# Kill running .NET edition processes
Write-Info "Stopping any running Jarvis .NET processes..."
$killCount = 0
foreach ($name in @('Jarvis-NET', 'JarvisSettings-NET', 'JarvisInstaller-NET')) {
    Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
        try { $_.Kill(); $_.WaitForExit(3000); $killCount++ } catch {}
    }
}
if ($killCount -gt 0) { Write-Host "  killed $killCount process(es)" }
Start-Sleep -Milliseconds 500

# Remove install dir
if (Test-Path $installDir) {
    Write-Info "Removing $installDir..."
    $removed = $false
    for ($i = 0; $i -lt 3 -and -not $removed; $i++) {
        try {
            Remove-Item $installDir -Recurse -Force -ErrorAction Stop
            $removed = $true
        } catch {
            if ($i -eq 2) {
                Write-Err "Couldn't fully remove install dir: $($_.Exception.Message)"
                Write-Host "Close any open File Explorer windows showing the install folder,"
                Write-Host "then delete it manually:  $installDir"
            } else { Start-Sleep -Milliseconds 500 }
        }
    }
    if ($removed) { Write-Ok "  removed" }
}

# Start Menu shortcuts (we install them under "Jarvis-NET\")
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Jarvis-NET'
if (Test-Path $startMenu) {
    try { Remove-Item $startMenu -Recurse -Force; Write-Ok "Removed Start Menu shortcuts." }
    catch { Write-Warn "Couldn't remove Start Menu folder: $($_.Exception.Message)" }
}

# Pointer file + parent dir
try { Remove-Item $PointerPath -Force } catch {}
$pointerDir = Split-Path $PointerPath -Parent
if (Test-Path $pointerDir) {
    if ((Get-ChildItem $pointerDir -Force | Measure-Object).Count -eq 0) {
        try { Remove-Item $pointerDir -Force } catch {}
    }
}

Write-Host ""
Write-Ok "Jarvis .NET edition uninstalled."
Write-Host ""
Write-Host "Not removed (intentionally):"
Write-Host "  - Python edition (separate install, use uninstall.ps1 for that)"
Write-Host "  - YouTube Music app (Windows Settings > Apps if you want)"
if ($backupDir) {
    Write-Host ""
    Write-Host "Your config + memory backup:  $backupDir"
}
