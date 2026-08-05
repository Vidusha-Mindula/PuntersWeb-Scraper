<#
    Run this ON the target Windows Server, from inside the extracted PuntersScraper.Web
    publish folder (the folder containing PuntersScraper.Web.exe). It does NOT need to be run
    as Administrator for most steps, but registering the Scheduled Task and opening the
    firewall rule do - run this from an elevated PowerShell prompt.

    IMPORTANT - read before running:
    This scraper only reliably gets past Punters' bot-detection using a REAL (non-headless)
    Chromium window positioned off-screen, not true headless mode. A real window can only be
    created inside an interactive desktop session (WinSta0) - a plain Windows Service (which
    Windows runs in the non-interactive Session 0) CANNOT do this; Chromium will fail to launch
    at all. So this script deliberately does NOT register a Windows Service. Instead it creates
    a Scheduled Task set to run only in YOUR interactive logon session.

    That means: after this script finishes, you (or whichever account you choose via
    -TaskUser) need to stay logged in for the app to keep running - e.g. log in over RDP and
    then DISCONNECT without logging off (this keeps the session's desktop alive), rather than
    fully signing out. If the account logs off completely, the app stops until it logs back in
    (the task is also set to start automatically at that logon).

    Usage:
        powershell -ExecutionPolicy Bypass .\server-setup.ps1
        powershell -ExecutionPolicy Bypass .\server-setup.ps1 -Port 5011 -TaskUser MYSERVER\svc-scraper
#>
param(
    [int]$Port = 5011,
    [string]$TaskUser = "$env:USERDOMAIN\$env:USERNAME",
    [string]$TaskName = "PuntersScraperWeb"
)

$ErrorActionPreference = "Stop"
$installDir = $PSScriptRoot

Write-Host "==> Installing the Chromium browser used for scraping..." -ForegroundColor Cyan
$playwrightScript = Join-Path $installDir "playwright.ps1"
if (Test-Path $playwrightScript) {
    & $playwrightScript install chromium
} else {
    Write-Warning "playwright.ps1 not found in $installDir - skipping. If scraping fails later with a 'browser not found' error, run PuntersScraper.Web.exe once by hand first, or install Playwright's browsers manually."
}

Write-Host "==> Setting the listen port for this deployment" -ForegroundColor Cyan
# Admin login has no built-in default (Program.cs refuses to start without one) - set it via
# the Admin__Username / Admin__Password environment variables, or add an "Admin" section to
# appsettings.Production.json yourself. Not written here, so this script doesn't overwrite it.
$prodSettingsPath = Join-Path $installDir "appsettings.Production.json"
$prodSettings = @{ Urls = "http://0.0.0.0:$Port" } | ConvertTo-Json -Depth 5
Set-Content -Path $prodSettingsPath -Value $prodSettings -Encoding utf8
Write-Host "    Wrote $prodSettingsPath."

Write-Host "==> Opening firewall for TCP port $Port..." -ForegroundColor Cyan
$ruleName = "PuntersScraperWeb-$Port"
if (-not (Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue)) {
    New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Protocol TCP -LocalPort $Port -Action Allow | Out-Null
}

Write-Host "==> Registering the Scheduled Task '$TaskName' (runs only in an interactive logon, not as a background service)..." -ForegroundColor Cyan
$exePath = Join-Path $installDir "PuntersScraper.Web.exe"
$action = New-ScheduledTaskAction -Execute $exePath -WorkingDirectory $installDir
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $TaskUser
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -DontStopOnIdleEnd `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -RestartCount 999 -RestartInterval (New-TimeSpan -Minutes 1) `
    -MultipleInstances IgnoreNew

Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Trigger $trigger -Settings $settings `
    -Description "Punters Meetings Scraper web UI - must run in an interactive logon session (real off-screen browser window, not a background service)." `
    | Out-Null

Write-Host "==> Starting it now (in this session)..." -ForegroundColor Cyan
Start-ScheduledTask -TaskName $TaskName

Write-Host ""
Write-Host "==> Done." -ForegroundColor Green
Write-Host "    Browse to http://<this-server>:$Port from another machine to check it's reachable."
Write-Host "    Reminder: keep $TaskUser logged in (RDP-disconnect is fine, full log-off is not)"
Write-Host "    for the scraper to keep working - see the comment block at the top of this script."
