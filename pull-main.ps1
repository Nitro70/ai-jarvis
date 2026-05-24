# Jarvis: pull the latest *unreleased* code from main, no version
# comparison, no confirmations. For development / bleeding-edge use.
#
# One-liner:
#   irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/pull-main.ps1 | iex
#
# Difference from install.ps1:
#   - Skips the GitHub Releases API entirely (no version check).
#   - Downloads from codeload.github.com (bypasses raw.githubusercontent
#     CDN cache — gets pushes within seconds, not 5 minutes).
#   - Doesn't try to swap JarvisSettings.exe (no built artifact on
#     untagged commits — just refreshes Python source).
#   - No prompts. Always applies.
#   - Stamps the installed version as 'main+YYYYMMDD-HHMM' so the Settings
#     'Updates' tab will still show a real release as 'available' when you
#     want to drop back onto a stable tag.

#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$RepoOwner   = 'Nitro70'
$RepoName    = 'ai-jarvis'
$PointerPath = Join-Path $env:LOCALAPPDATA 'Jarvis\install-info.json'

if (-not (Test-Path $PointerPath)) {
    Write-Host "No Jarvis install found at $PointerPath." -ForegroundColor Red
    Write-Host "Run install.ps1 first to bootstrap, then this can update you."
    return
}

$existing = Get-Content $PointerPath -Raw | ConvertFrom-Json
$dir      = $existing.InstallDir
if (-not (Test-Path $dir)) {
    Write-Host "Install dir $dir is missing on disk." -ForegroundColor Red
    return
}

Write-Host "Pulling latest main into $dir..." -ForegroundColor Cyan

# codeload.github.com is the URL GitHub itself uses for archive downloads;
# it doesn't sit behind the raw.githubusercontent CDN, so pushes show up
# within seconds.
$zipUrl  = "https://codeload.github.com/$RepoOwner/$RepoName/zip/refs/heads/main"
$zipPath = Join-Path $env:TEMP "jarvis-main-$(Get-Random).zip"
$tmpDir  = Join-Path $env:TEMP "jarvis-main-extract-$(Get-Random)"

Invoke-WebRequest -Uri $zipUrl -OutFile $zipPath -UseBasicParsing
Expand-Archive -Path $zipPath -DestinationPath $tmpDir -Force

$inner = (Get-ChildItem $tmpDir | Where-Object PSIsContainer | Select-Object -First 1).FullName

# Files we deliberately don't overwrite.
$skip = @(
    'config.yaml', 'config.local.yaml',
    'memory.md',
    'jarvis.log',
    '.install-stamp',
    '.env', '.ytmd_token',
    'JarvisSettings.exe', 'JarvisSettings.exe.old',
    'JarvisInstaller.exe'
)

$copied = 0
Get-ChildItem $inner -Recurse | ForEach-Object {
    if ($skip -contains $_.Name) { return }
    $target = Join-Path $dir $_.FullName.Substring($inner.Length + 1)
    if ($_.PSIsContainer) {
        if (-not (Test-Path $target)) { New-Item -ItemType Directory -Path $target -Force | Out-Null }
    } else {
        $parent = Split-Path $target -Parent
        if (-not (Test-Path $parent)) { New-Item -ItemType Directory -Path $parent -Force | Out-Null }
        try {
            Copy-Item $_.FullName $target -Force
            $copied++
        } catch {
            Write-Host "  (skipped ${target}: $($_.Exception.Message))" -ForegroundColor Yellow
        }
    }
}

# Bump install-info.json to mark this is a main-branch build, not a release.
$existing.Version = "main+$(Get-Date -Format 'yyyyMMdd-HHmm')"
$existing | ConvertTo-Json -Depth 10 | Set-Content $PointerPath -Encoding UTF8

Remove-Item $zipPath, $tmpDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "✓ Pulled main ($copied files) -> $($existing.Version)" -ForegroundColor Green
Write-Host "  (config.yaml, memory.md, .env preserved; Settings.exe NOT touched)"
