# Jarvis installer / updater for Windows PowerShell.
#
# One-liner to run from any terminal:
#   irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/install.ps1 | iex
#
# What it does:
#   - If Jarvis is already installed (detected via the install-info.json
#     pointer at %LocalAppData%\Jarvis\install-info.json), updates it
#     in place: downloads the latest tagged source zip, extracts over
#     the install dir, swaps JarvisSettings.exe, bumps the recorded
#     version. config.yaml and memory.md are NOT touched.
#   - If Jarvis is NOT installed, downloads JarvisInstaller.exe from
#     the latest GitHub release and launches the wizard.
#
# Requires Windows PowerShell 5.1+ (default on Windows 10/11).

#Requires -Version 5.1
[CmdletBinding()]
param(
    [switch]$Force,  # apply even if already up to date
    [switch]$Quiet   # less chatter
)

$ErrorActionPreference = 'Stop'

$RepoOwner   = 'Nitro70'
$RepoName    = 'ai-jarvis'
$PointerPath = Join-Path $env:LOCALAPPDATA 'Jarvis\install-info.json'

function Write-Info($msg) {
    if (-not $Quiet) { Write-Host $msg -ForegroundColor Cyan }
}
function Write-Ok($msg)   { Write-Host $msg -ForegroundColor Green }
function Write-Warn($msg) { Write-Host $msg -ForegroundColor Yellow }

function Parse-SemVer([string]$tag) {
    if (-not $tag) { return $null }
    $s = $tag.Trim().TrimStart('v','V')
    $dash = $s.IndexOf('-')
    if ($dash -ge 0) { $s = $s.Substring(0, $dash) }
    try { return [Version]$s } catch { return $null }
}

function Get-LatestRelease {
    $h = @{ 'User-Agent' = 'jarvis-update/1.0' }
    Invoke-RestMethod `
        -Uri "https://api.github.com/repos/$RepoOwner/$RepoName/releases/latest" `
        -Headers $h
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
    Write-Info "No existing Jarvis install found. Downloading installer..."
    $installerUrl  = "https://github.com/$RepoOwner/$RepoName/releases/latest/download/JarvisInstaller.exe"
    $installerPath = Join-Path $env:TEMP "JarvisInstaller-$latestTag.exe"
    Invoke-WebRequest -Uri $installerUrl -OutFile $installerPath -UseBasicParsing
    Write-Ok "Launching installer..."
    Start-Process $installerPath
    return
}

Write-Ok "Installed: $($existing.Version)  ($($existing.InstallDir))"

# -------------------------------------------------------------------
# 3b. Already on the latest version -> bail unless -Force
# -------------------------------------------------------------------
$installedVer = Parse-SemVer $existing.Version
if (-not $Force -and $installedVer -and $latestVer -and $latestVer -le $installedVer) {
    Write-Ok "Already up to date."
    return
}

# -------------------------------------------------------------------
# 4. Download + extract source
# -------------------------------------------------------------------
Write-Info "Updating $($existing.Version) -> $latestTag..."

$zipUrl      = "https://github.com/$RepoOwner/$RepoName/archive/refs/tags/$latestTag.zip"
$zipPath     = Join-Path $env:TEMP "jarvis-update-$latestTag.zip"
$extractPath = Join-Path $env:TEMP "jarvis-update-$latestTag"

Write-Info "  downloading source zip..."
Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing

Write-Info "  extracting..."
if (Test-Path $extractPath) { Remove-Item -Recurse -Force $extractPath }
Expand-Archive -Path $zipPath -DestinationPath $extractPath -Force

# GitHub wraps the archive in "<repo>-<tag>/"; find that single inner dir.
$inner = Get-ChildItem $extractPath | Where-Object PSIsContainer | Select-Object -First 1
if (-not $inner) {
    throw "Extracted archive had no inner directory — unexpected layout."
}

# Files we deliberately don't overwrite. Must stay in sync with the
# matching list in pull-main.ps1 and Updater._preservedNames in C#.
$skipNames = @(
    'config.yaml', 'config.local.yaml',
    'memory.md',
    'jarvis.log',
    '.install-stamp',
    '.env', '.ytmd_token',
    'JarvisSettings.exe', 'JarvisSettings.exe.old', 'JarvisSettings.exe.new',
    'JarvisInstaller.exe'
)

Write-Info "  copying files to $($existing.InstallDir)..."
$copied = 0
$skipped = 0
Get-ChildItem $inner.FullName -Recurse | ForEach-Object {
    $rel = $_.FullName.Substring($inner.FullName.Length + 1)
    if ($skipNames -contains $_.Name) { $skipped++; return }
    $target = Join-Path $existing.InstallDir $rel
    if ($_.PSIsContainer) {
        if (-not (Test-Path $target)) {
            New-Item -ItemType Directory -Path $target -Force | Out-Null
        }
    } else {
        $parent = Split-Path $target -Parent
        if (-not (Test-Path $parent)) {
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
        }
        try {
            Copy-Item $_.FullName -Destination $target -Force
            $copied++
        } catch {
            Write-Warn "  (skipped ${rel}: $($_.Exception.Message))"
        }
    }
}
Write-Info "  $copied files copied, $skipped preserved"

# -------------------------------------------------------------------
# 5. Self-replace JarvisSettings.exe via rename-while-running trick
# -------------------------------------------------------------------
$settingsAsset = $release.assets | Where-Object { $_.name -eq 'JarvisSettings.exe' } | Select-Object -First 1
if ($settingsAsset) {
    Write-Info "  updating JarvisSettings.exe..."
    $curExe = Join-Path $existing.InstallDir 'JarvisSettings.exe'
    $newExe = "$curExe.new"
    $oldExe = "$curExe.old"

    Invoke-WebRequest -Uri $settingsAsset.browser_download_url -OutFile $newExe -UseBasicParsing

    try { if (Test-Path $oldExe) { Remove-Item $oldExe -Force } } catch {}
    try {
        if (Test-Path $curExe) { Move-Item $curExe $oldExe -Force }
        Move-Item $newExe $curExe -Force
    } catch {
        Write-Warn "  (couldn't swap Settings.exe — close it if it's running, then rerun)"
        try { Remove-Item $newExe -Force } catch {}
    }
}

# -------------------------------------------------------------------
# 6. Bump the version in install-info.json
# -------------------------------------------------------------------
$existing.Version = if ($latestVer) { $latestVer.ToString(3) } else { $latestTag.TrimStart('v') }
$existing | ConvertTo-Json -Depth 10 | Set-Content $PointerPath -Encoding UTF8

# -------------------------------------------------------------------
# 7. Cleanup
# -------------------------------------------------------------------
Remove-Item $zipPath     -Force -ErrorAction SilentlyContinue
Remove-Item $extractPath -Recurse -Force -ErrorAction SilentlyContinue

Write-Host ""
Write-Ok "Updated to $latestTag."
Write-Host "If Jarvis Settings was open during this update, close + reopen it to load the new version."
