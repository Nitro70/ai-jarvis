# Jarvis uninstaller for Windows PowerShell.
#
# One-liner to run from any terminal:
#   irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/uninstall.ps1 | iex
#
# What it does:
#   - Locates your install via %LocalAppData%\Jarvis\install-info.json
#   - Asks whether to back up config.yaml + memory.md to Documents first
#   - Kills any running JarvisSettings.exe + python running jarvis.py
#   - Removes the install directory
#   - Removes Start Menu shortcuts (Jarvis + Jarvis Settings)
#   - Removes the install-info.json pointer
#
# What it does NOT touch:
#   - Python 3.12 (you might use it for other things)
#   - YouTube Music desktop app (uninstall via Windows Settings if you want)

#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Yes,           # skip confirmation prompts
    [switch]$NoBackup       # don't back up config.yaml + memory.md
)

$ErrorActionPreference = 'Stop'

$PointerPath = Join-Path $env:LOCALAPPDATA 'Jarvis\install-info.json'

function Write-Info($m) { Write-Host $m -ForegroundColor Cyan }
function Write-Ok($m)   { Write-Host $m -ForegroundColor Green }
function Write-Warn($m) { Write-Host $m -ForegroundColor Yellow }
function Write-Err($m)  { Write-Host $m -ForegroundColor Red }

# -------------------------------------------------------------------
# 1. Find install
# -------------------------------------------------------------------
if (-not (Test-Path $PointerPath)) {
    Write-Warn "No Jarvis install found (no install-info.json at $PointerPath)."
    Write-Host "If you installed to a custom location and didn't use the official"
    Write-Host "installer, delete the folder manually."
    return
}

try {
    $existing = Get-Content $PointerPath -Raw | ConvertFrom-Json
} catch {
    Write-Err "install-info.json is corrupted: $($_.Exception.Message)"
    return
}

$installDir = $existing.InstallDir
$version    = $existing.Version
Write-Host ""
Write-Info "Found Jarvis install:"
Write-Host "  Location: $installDir"
Write-Host "  Version:  $version"
Write-Host ""

if (-not (Test-Path $installDir)) {
    Write-Warn "Install dir doesn't exist on disk. Cleaning up pointer only."
    Remove-Item $PointerPath -Force -ErrorAction SilentlyContinue
    return
}

# -------------------------------------------------------------------
# 2. Confirm
# -------------------------------------------------------------------
if (-not $Yes) {
    $confirm = Read-Host "Uninstall Jarvis from the above location? [y/N]"
    if ($confirm -ne 'y' -and $confirm -ne 'Y') {
        Write-Host "Cancelled."
        return
    }
}

# -------------------------------------------------------------------
# 3. Optional backup of user-authored files
# -------------------------------------------------------------------
$backupDir = $null
if (-not $NoBackup) {
    $doBackup = $true
    if (-not $Yes) {
        $b = Read-Host "Back up config.yaml + memory.md to Documents first? [Y/n]"
        if ($b -eq 'n' -or $b -eq 'N') { $doBackup = $false }
    }
    if ($doBackup) {
        $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
        $backupDir = Join-Path ([Environment]::GetFolderPath('MyDocuments')) "Jarvis-backup-$stamp"
        $copied = 0
        foreach ($f in @('config.yaml', 'config.local.yaml', 'memory.md', 'jarvis.log')) {
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
        if ($copied -gt 0) {
            Write-Ok "Backup folder: $backupDir"
        } else {
            $backupDir = $null
            Write-Host "  (nothing to back up)"
        }
    }
}

# -------------------------------------------------------------------
# 4. Kill running Jarvis processes
# -------------------------------------------------------------------
Write-Info "Stopping any running Jarvis processes..."
$killCount = 0
Get-Process -Name 'JarvisSettings' -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(3000); $killCount++ } catch {}
}
# Find python processes whose command line points at this install (jarvis.py)
try {
    Get-CimInstance Win32_Process -Filter "Name = 'python.exe' OR Name = 'pythonw.exe'" -ErrorAction SilentlyContinue |
        Where-Object {
            $cl = $_.CommandLine
            $cl -and ($cl -like "*jarvis.py*" -or $cl -like "*$installDir*")
        } | ForEach-Object {
            try {
                Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue
                $killCount++
            } catch {}
        }
} catch {}
if ($killCount -gt 0) { Write-Host "  killed $killCount process(es)" }

# Give Windows a moment to release file handles.
Start-Sleep -Milliseconds 500

# -------------------------------------------------------------------
# 5. Remove install dir
# -------------------------------------------------------------------
Write-Info "Removing $installDir..."
# Try a few times in case files are momentarily locked.
$removed = $false
for ($i = 0; $i -lt 3 -and -not $removed; $i++) {
    try {
        Remove-Item $installDir -Recurse -Force -ErrorAction Stop
        $removed = $true
    } catch {
        if ($i -eq 2) {
            Write-Err "Couldn't fully remove install dir: $($_.Exception.Message)"
            Write-Host "Close any open File Explorer windows showing the install folder,"
            Write-Host "and any open terminals that have it as their working directory,"
            Write-Host "then delete it manually:"
            Write-Host "  $installDir"
        } else {
            Start-Sleep -Milliseconds 500
        }
    }
}
if ($removed) { Write-Ok "  removed" }

# -------------------------------------------------------------------
# 6. Start Menu shortcuts
# -------------------------------------------------------------------
$startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Jarvis'
if (Test-Path $startMenu) {
    try {
        Remove-Item $startMenu -Recurse -Force
        Write-Ok "Removed Start Menu shortcuts."
    } catch {
        Write-Warn "Couldn't remove Start Menu folder: $($_.Exception.Message)"
    }
}

# -------------------------------------------------------------------
# 7. Pointer file
# -------------------------------------------------------------------
try { Remove-Item $PointerPath -Force } catch {}
$pointerDir = Split-Path $PointerPath -Parent
if (Test-Path $pointerDir) {
    if ((Get-ChildItem $pointerDir -Force | Measure-Object).Count -eq 0) {
        try { Remove-Item $pointerDir -Force } catch {}
    }
}

# -------------------------------------------------------------------
# 8. Summary
# -------------------------------------------------------------------
Write-Host ""
Write-Ok "Jarvis uninstalled."
Write-Host ""
Write-Host "Not removed (intentionally):"
Write-Host "  - Python 3.12 (might be used by other apps)"
Write-Host "  - YouTube Music desktop app (uninstall via Windows Settings > Apps)"
Write-Host "  - Whisper model cache at %USERPROFILE%\.cache\huggingface\hub\"
if ($backupDir) {
    Write-Host ""
    Write-Host "Your config + memory backup:"
    Write-Host "  $backupDir"
}
