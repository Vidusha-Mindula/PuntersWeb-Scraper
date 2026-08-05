<#
    Run this ON the server whenever a new PuntersScraper.Web-deploy.zip is dropped in. It stops
    the running app, extracts the new build over the existing install folder (this does NOT
    touch appsettings.Production.json, since that file isn't part of the zip), then restarts it.

    Usage (from anywhere - defaults assume the zip and install folder used so far):
        powershell -ExecutionPolicy Bypass -File .\update.ps1
        powershell -ExecutionPolicy Bypass -File .\update.ps1 -ZipPath D:\PuntersScraper.Web-deploy.zip -InstallDir D:\PuntersScraperWeb -TaskName PuntersScraperWeb
#>
param(
    [string]$ZipPath = "D:\PuntersScraper.Web-deploy.zip",
    [string]$InstallDir = "D:\PuntersScraperWeb",
    [string]$TaskName = "PuntersScraperWeb"
)

$ErrorActionPreference = "Stop"

if (-not (Test-Path $ZipPath)) {
    throw "Zip not found at $ZipPath - copy the new PuntersScraper.Web-deploy.zip there first, or pass -ZipPath."
}

Write-Host "==> Stopping $TaskName and any leftover process..." -ForegroundColor Cyan
Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
Get-Process PuntersScraper.Web -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Seconds 2

Write-Host "==> Extracting $ZipPath into $InstallDir..." -ForegroundColor Cyan
Expand-Archive -Path $ZipPath -DestinationPath $InstallDir -Force

Write-Host "==> Starting $TaskName..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName $TaskName
Start-Sleep -Seconds 3

$proc = Get-Process PuntersScraper.Web -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "==> Done. Process is running (PID $($proc.Id))." -ForegroundColor Green
} else {
    Write-Warning "Process did not start - check 'Get-ScheduledTaskInfo -TaskName $TaskName' for LastTaskResult."
}
