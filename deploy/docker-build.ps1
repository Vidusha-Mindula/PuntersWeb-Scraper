<#
    Builds a versioned Docker image of PuntersScraper.Web purely as a tagged build artifact --
    it is NOT meant to be run in production (see deploy/Dockerfile.web for why: the scraper needs
    a real, non-headless Chromium window in an interactive Windows desktop session, which no
    standard container provides).

    Reads the version from the repo-root VERSION file (the same one installer/release.bat and
    deploy/release-web.bat use - App and Web share a version number), tags the image with it,
    and also tags :latest for convenience.

    Usage:
        powershell deploy/docker-build.ps1
        powershell deploy/docker-build.ps1 -Version 1.1.0   (overrides the VERSION file)
#>
param(
    [string]$Version
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot

if (-not $Version) {
    $versionFile = Join-Path $repoRoot "VERSION"
    if (Test-Path $versionFile) {
        $Version = (Get-Content $versionFile -Raw).Trim()
    } else {
        throw "No -Version given and no VERSION file found at $versionFile."
    }
}

$env:VERSION = $Version

Write-Host "==> Building puntersscraper-web:$Version" -ForegroundColor Cyan
Push-Location $repoRoot
try {
    docker compose build
    if ($LASTEXITCODE -ne 0) { throw "docker compose build failed with exit code $LASTEXITCODE" }

    docker tag "puntersscraper-web:$Version" "puntersscraper-web:latest"
    if ($LASTEXITCODE -ne 0) { throw "docker tag failed with exit code $LASTEXITCODE" }
}
finally {
    Pop-Location
}

Write-Host "==> Done. Tagged puntersscraper-web:$Version and puntersscraper-web:latest" -ForegroundColor Green
Write-Host "    This image is a build artifact, not a runnable service - see deploy/Dockerfile.web."
