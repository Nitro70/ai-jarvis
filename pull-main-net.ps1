# Jarvis .NET edition: pull whatever's currently on main (CI artifacts from the
# latest workflow run on main branch). Bleeding-edge — gets you commits that
# haven't been tagged as a release yet.
#
# One-liner:
#   irm https://raw.githubusercontent.com/Nitro70/ai-jarvis/main/pull-main-net.ps1 | iex
#
# Differences from install-net.ps1:
#   - Skips the GitHub Releases API entirely.
#   - Pulls .exe artifacts from the latest successful CI workflow run on main
#     via `gh run download` (requires the `gh` CLI to be installed — fall back
#     to a friendly error if it's not).
#   - No confirmations, no version check, always applies.
#   - Stamps install-info.json's Version as 'main+YYYYMMDD-HHMM'.

#Requires -Version 5.1
$ErrorActionPreference = 'Stop'

$RepoOwner   = 'Nitro70'
$RepoName    = 'ai-jarvis'
$PointerPath = Join-Path $env:LOCALAPPDATA 'Jarvis-NET\install-info.json'

if (-not (Test-Path $PointerPath)) {
    Write-Host "No Jarvis .NET install found at $PointerPath." -ForegroundColor Red
    Write-Host "Run install-net.ps1 first to bootstrap, then this can update you."
    return
}

# gh CLI is required because CI artifacts (not releases) need auth.
$gh = Get-Command gh -ErrorAction SilentlyContinue
if (-not $gh) {
    Write-Host "This script needs the GitHub CLI (gh) to download workflow artifacts." -ForegroundColor Red
    Write-Host "Install with:  winget install GitHub.cli"
    Write-Host "Or use install-net.ps1 instead (which pulls from releases, no auth needed)."
    return
}

$existing = Get-Content $PointerPath -Raw | ConvertFrom-Json
$dir      = $existing.InstallDir
if (-not (Test-Path $dir)) {
    Write-Host "Install dir $dir is missing on disk." -ForegroundColor Red
    return
}

Write-Host "Pulling latest .NET-edition artifacts from main into $dir..." -ForegroundColor Cyan

# Find the latest successful workflow run on main and download its dotnet
# artifacts (the workflow uploads them as 'artifacts-dotnet').
$run = & gh run list --branch main --workflow installer.yml --json databaseId,conclusion --limit 5 |
       ConvertFrom-Json |
       Where-Object { $_.conclusion -eq 'success' } |
       Select-Object -First 1
if (-not $run) {
    Write-Host "Could not find a successful CI run on main." -ForegroundColor Red
    return
}

$tmpDir = Join-Path $env:TEMP "jarvis-net-main-$(Get-Random)"
New-Item -ItemType Directory -Path $tmpDir -Force | Out-Null
& gh run download $run.databaseId --repo "$RepoOwner/$RepoName" --name artifacts-dotnet --dir $tmpDir

foreach ($exe in @('Jarvis-NET.exe', 'JarvisSettings-NET.exe', 'JarvisInstaller-NET.exe')) {
    $src = Join-Path $tmpDir $exe
    if (-not (Test-Path $src)) { continue }
    $dst = Join-Path $dir $exe
    $new = "$dst.new"
    $old = "$dst.old"
    Copy-Item $src $new -Force
    try { if (Test-Path $old) { Remove-Item $old -Force } } catch {}
    try {
        if (Test-Path $dst) { Move-Item $dst $old -Force }
        Move-Item $new $dst -Force
        Write-Host "  replaced $exe"
    } catch {
        Write-Host "  (couldn't swap $exe — close it first)" -ForegroundColor Yellow
        try { Remove-Item $new -Force } catch {}
    }
}

$existing.Version = "main+$(Get-Date -Format 'yyyyMMdd-HHmm')"
$existing | ConvertTo-Json -Depth 10 | Set-Content $PointerPath -Encoding UTF8

Remove-Item $tmpDir -Recurse -Force -ErrorAction SilentlyContinue

Write-Host "Done. Restart Jarvis if it was open." -ForegroundColor Green
