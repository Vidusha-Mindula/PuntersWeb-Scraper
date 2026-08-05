@echo off
:: Thin wrapper so you can run this without typing "powershell -File" every time.
:: Usage: send-notice.bat -Title "Scheduled maintenance" -Message "Offline Friday 6-8pm AEST."
::        send-notice.bat -Clear
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0send-notice.ps1" %*
