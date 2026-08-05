<#
    Builds a self-contained win-x64 publish of PuntersScraper.App and compiles it
    into a single Inno Setup installer (PuntersScraperSetup-<version>.exe).

    Usage:
        powershell installer/build-punters-installer.ps1
        powershell installer/build-punters-installer.ps1 -Version 1.2.0
#>
param(
    [string]$Version = "2.5.0",
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$installerDir = Join-Path $repoRoot "installer"
$publishDir = Join-Path $installerDir "publish-punters"
$outputDir = Join-Path $installerDir "output"
$appProject = Join-Path $repoRoot "src\PuntersScraper.App\PuntersScraper.App.csproj"
$issScript = Join-Path $installerDir "PuntersScraper.iss"

$isccPath = (Get-Command "ISCC.exe" -ErrorAction SilentlyContinue).Path
if (-not $isccPath) {
    $defaultIscc = "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe"
    if (Test-Path $defaultIscc) {
        $isccPath = $defaultIscc
    } else {
        throw "ISCC.exe (Inno Setup Compiler) not found. Install Inno Setup 6 from https://jrsoftware.org/isinfo.php or add it to PATH."
    }
}

Write-Host "==> Publishing $appProject (self-contained win-x64, v$Version)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $appProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "==> Compiling installer with Inno Setup" -ForegroundColor Cyan
if (-not (Test-Path $outputDir)) { New-Item -ItemType Directory -Path $outputDir | Out-Null }

& $isccPath "/DAppVersion=$Version" "/O$outputDir" $issScript
if ($LASTEXITCODE -ne 0) { throw "ISCC.exe failed with exit code $LASTEXITCODE" }

Write-Host "==> Done. Installer written to $outputDir" -ForegroundColor Green
Get-ChildItem $outputDir -Filter "PuntersScraperSetup-*.exe" | ForEach-Object { Write-Host "    $($_.FullName)" }
