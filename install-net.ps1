# Jarvis .NET edition installer / updater for Windows PowerShell.
#
# One-liner to run from any terminal:
#   irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/install-net.ps1 | iex
#
# What it does:
#   - If Jarvis .NET edition is already installed (detected via the pointer
#     file at %LocalAppData%\Jarvis-NET\install-info.json), updates the
#     three .exes in place (Jarvis-NET.exe, JarvisSettings-NET.exe, and
#     optionally JarvisInstaller-NET.exe) from the latest GitHub release.
#     config.yaml and memory.md are NOT touched.
#   - If Jarvis .NET edition is NOT installed, downloads JarvisInstaller-NET.exe
#     from the latest GitHub release and launches the wizard.
#
# This is the .NET edition's counterpart to install.ps1 (which manages
# the Python edition). Both editions can be installed side by side; their
# install dirs, config files, and pointer files don't overlap.

#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Force,
    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'

$RepoOwner   = 'Nitro70'
$RepoName    = 'ai-jarvis'
$PointerPath = Join-Path $env:LOCALAPPDATA 'Jarvis-NET\install-info.json'

# Asset names attached to GitHub releases for the .NET edition.
$JarvisExe    = 'Jarvis-NET.exe'
$SettingsExe  = 'JarvisSettings-NET.exe'
$InstallerExe = 'JarvisInstaller-NET.exe'

function Write-Info($m) { if (-not $Quiet) { Write-Host $m -ForegroundColor Cyan } }
function Write-Ok($m)   { Write-Host $m -ForegroundColor Green }
function Write-Warn($m) { Write-Host $m -ForegroundColor Yellow }

function Parse-SemVer([string]$tag) {
    if (-not $tag) { return $null }
    $s = $tag.Trim().TrimStart('v','V')
    $dash = $s.IndexOf('-')
    if ($dash -ge 0) { $s = $s.Substring(0, $dash) }
    try { return [Version]$s } catch { return $null }
}

function Get-LatestRelease {
    $h = @{ 'User-Agent' = 'jarvis-net-update/1.0' }
    Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest" `
        -Headers $h
}

function Find-Asset($release, [string]$name) {
    $release.assets | Where-Object { $_.name -eq $name } | Select-Object -First 1
}

function Swap-Exe([string]$installDir, [string]$exeName, [string]$downloadUrl) {
    # Windows allows renaming a running .exe but not overwriting/deleting one.
    # So we: 1) download to .exe.new, 2) rename current → .exe.old, 3) rename
    # .exe.new → .exe. The next launch of the .exe (or this script on next run)
    # cleans up the .exe.old leftover.
    $cur = Join-Path $installDir $exeName
    $new = "$cur.new"
    $old = "$cur.old"

    Write-Info "  downloading $exeName..."
    Invoke-WebRequest -Uri $downloadUrl -OutFile $new -UseBasicParsing

    try { if (Test-Path $old) { Remove-Item $old -Force } } catch {}
    try {
        if (Test-Path $cur) { Move-Item $cur $old -Force }
        Move-Item $new $cur -Force
        return $true
    } catch {
        Write-Warn "  (couldn't swap $exeName — close it first if it's running)"
        try { Remove-Item $new -Force } catch {}
        return $false
    }
}

# -------------------------------------------------------------------
# 1. Find existing install (if any)
# -------------------------------------------------------------------
$existing = $null
if (Test-Path $PointerPath) {
    try { $existing = Get-Content $PointerPath -Raw | ConvertFrom-Json } catch {}
}

# -------------------------------------------------------------------
# 2. Look up the latest release
# -------------------------------------------------------------------
Write-Info "Checking GitHub for latest release..."
$release   = Get-LatestRelease
$latestTag = $release.tag_name
$latestVer = Parse-SemVer $latestTag
Write-Ok   "Latest:   $latestTag"

# -------------------------------------------------------------------
# 3a. No existing install -> bootstrap with the installer wizard
# -------------------------------------------------------------------
if (-not $existing -or -not (Test-Path $existing.InstallDir)) {
    Write-Info "No existing Jarvis .NET install found. Downloading installer..."
    $installerAsset = Find-Asset $release $InstallerExe
    if (-not $installerAsset) {
        Write-Warn "Latest release has no $InstallerExe attached. Falling back to portable mode:"
        Write-Warn "downloading $JarvisExe to your Desktop."
        $jarvisAsset = Find-Asset $release $JarvisExe
        if (-not $jarvisAsset) {
            throw "Release $latestTag has neither $InstallerExe nor $JarvisExe attached."
        }
        $dest = Join-Path ([Environment]::GetFolderPath('Desktop')) $JarvisExe
        Invoke-WebRequest -Uri $jarvisAsset.browser_download_url -OutFile $dest -UseBasicParsing
        Write-Ok "Downloaded to $dest. Double-click to run."
        return
    }
    $installerPath = Join-Path $env:TEMP "$InstallerExe-$latestTag.exe"
    Invoke-WebRequest -Uri $installerAsset.browser_download_url -OutFile $installerPath -UseBasicParsing
    Write-Ok "Launching installer..."
    Start-Process $installerPath
    return
}

Write-Ok "Installed: $($existing.Version)  ($($existing.InstallDir))"

# -------------------------------------------------------------------
# 3b. Already on the latest version -> bail unless -Force
# -------------------------------------------------------------------
$installedVer = Parse-SemVer $existing.Version
$brokenSentinel = $existing.Version -eq '1.0.0' -or $existing.Version -eq '1.0.0.0'
if (-not $Force -and -not $brokenSentinel -and $installedVer -and $latestVer -and $latestVer -le $installedVer) {
    Write-Ok "Already up to date."
    return
}

# -------------------------------------------------------------------
# 4. Swap each shipped exe
# -------------------------------------------------------------------
Write-Info "Updating $($existing.Version) -> $latestTag..."
$dir = $existing.InstallDir

$jarvisAsset    = Find-Asset $release $JarvisExe
$settingsAsset  = Find-Asset $release $SettingsExe
$installerAsset = Find-Asset $release $InstallerExe

if ($jarvisAsset)    { Swap-Exe $dir $JarvisExe    $jarvisAsset.browser_download_url    | Out-Null }
else                 { Write-Warn "  (no $JarvisExe in release — skipping)" }

if ($settingsAsset)  { Swap-Exe $dir $SettingsExe  $settingsAsset.browser_download_url  | Out-Null }
else                 { Write-Warn "  (no $SettingsExe in release — skipping)" }

# Installer exe is optional in the install dir — copy if release has it.
if ($installerAsset) { Swap-Exe $dir $InstallerExe $installerAsset.browser_download_url | Out-Null }

# -------------------------------------------------------------------
# 5. Bump pointer file
# -------------------------------------------------------------------
$existing.Version = if ($latestVer) { $latestVer.ToString(3) } else { $latestTag.TrimStart('v') }
$existing.LastLaunched = (Get-Date).ToUniversalTime().ToString("o")
$existing | ConvertTo-Json -Depth 10 | Set-Content $PointerPath -Encoding UTF8

Write-Host ""
Write-Ok "Updated to $latestTag."
Write-Host "If Jarvis or Jarvis Settings were open, close + reopen them to load the new version."
