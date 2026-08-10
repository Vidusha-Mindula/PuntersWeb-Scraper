<#
    Publishes PuntersScraper.Web as a self-contained win-x64 build (no separate .NET runtime
    install needed on the server) and zips it up ready to copy to the target Windows Server.

    Usage:
        powershell deploy/publish-web.ps1                  (reads version from the repo-root VERSION file)
        powershell deploy/publish-web.ps1 -Version 1.1.0   (overrides it)
#>
param(
    [string]$Version,
    [string]$Configuration = "Release"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$deployDir = Join-Path $repoRoot "deploy"
$publishDir = Join-Path $deployDir "publish-web"
$webProject = Join-Path $repoRoot "src\PuntersScraper.Web\PuntersScraper.Web.csproj"
$zipPath = Join-Path $deployDir "PuntersScraper.Web-deploy.zip"

if (-not $Version) {
    $versionFile = Join-Path $repoRoot "VERSION"
    if (Test-Path $versionFile) {
        $Version = (Get-Content $versionFile -Raw).Trim()
    } else {
        throw "No -Version given and no VERSION file found at $versionFile."
    }
}

Write-Host "==> Publishing $webProject (self-contained win-x64, v$Version)" -ForegroundColor Cyan
if (Test-Path $publishDir) { Remove-Item $publishDir -Recurse -Force }

dotnet publish $webProject `
    -c $Configuration `
    -r win-x64 `
    --self-contained true `
    -p:Version=$Version `
    -o $publishDir

if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }

Write-Host "==> Copying server-setup.ps1 and update.ps1/update.bat into the publish folder" -ForegroundColor Cyan
Copy-Item (Join-Path $deployDir "server-setup.ps1") $publishDir -Force
Copy-Item (Join-Path $deployDir "update.ps1") $publishDir -Force
Copy-Item (Join-Path $deployDir "update.bat") $publishDir -Force

Write-Host "==> Zipping for transfer" -ForegroundColor Cyan
if (Test-Path $zipPath) { Remove-Item $zipPath -Force }
Compress-Archive -Path "$publishDir\*" -DestinationPath $zipPath

Write-Host "==> Done." -ForegroundColor Green
Write-Host "    Copy this to the server and extract it: $zipPath"
Write-Host "    Then, ON THE SERVER, run server-setup.ps1 from inside the extracted folder."
