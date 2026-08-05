<#
    Publishes a "Developer Note" banner to every running copy of PuntersScraper.App by updating
    notice.json and pushing it to GitHub. The app polls this file's raw content on startup (see
    src/PuntersScraper.App/Services/DeveloperNoticeChecker.cs) and shows it until the user
    explicitly dismisses it (there's no X — only an "I've read this" button). Unlike cutting a
    release, this needs no rebuild/reinstall: just this one file, pushed straight to main.

    A fresh Id (a timestamp, generated automatically below) is what makes a new note show again
    even to someone who already dismissed an earlier one.

    Usage (prefer send-notice.bat day-to-day — same args, less typing):
        .\send-notice.bat -Title "Scheduled maintenance" -Message "Offline Friday 6-8pm AEST for a database upgrade."
        .\send-notice.bat -Clear   # removes the current notice; nothing shown on next check

    Note the ".\" prefix above — PowerShell (the default shell in Windows Terminal/VS Code) never
    searches the current folder for a script unless you qualify it that way; the bare filename
    fails with "term ... is not recognized" even though the file is right there.
#>
param(
    [string]$Title,
    [string]$Message,
    [switch]$Clear
)

$ErrorActionPreference = "Stop"
$repoRoot = $PSScriptRoot
$noticePath = Join-Path $repoRoot "notice.json"

if ($Clear) {
    $notice = [ordered]@{ id = ""; title = ""; message = "" }
    $commitMessage = "Clear developer notice"
} else {
    if (-not $Title -or -not $Message) {
        throw "Usage: send-notice.ps1 -Title `"...`" -Message `"...`"  (or -Clear to remove the current notice)"
    }
    $id = Get-Date -Format "yyyyMMddHHmmss"
    $notice = [ordered]@{ id = $id; title = $Title; message = $Message }
    $commitMessage = "Developer note: $Title"
}

$notice | ConvertTo-Json | Set-Content -Path $noticePath -Encoding utf8

Write-Host "==> Pushing notice.json" -ForegroundColor Cyan
Push-Location $repoRoot
try {
    git add notice.json
    git commit -m $commitMessage
    if ($LASTEXITCODE -ne 0) { throw "git commit failed with exit code $LASTEXITCODE" }
    git push
    if ($LASTEXITCODE -ne 0) { throw "git push failed with exit code $LASTEXITCODE" }
} finally {
    Pop-Location
}

Write-Host "==> Done. Every running copy of the app will show this on next launch." -ForegroundColor Green
