@echo off
:: Thin wrapper so you can run this without typing "powershell -File" every time.
::
:: IMPORTANT: run it as ".\send-notice.bat ..." (with the ".\" prefix), not just "send-notice.bat".
:: PowerShell (the default shell in Windows Terminal/VS Code) never searches the current folder
:: for a script/exe unless you qualify the path with ".\" - typing the bare name gives "term ...
:: is not recognized" even though the file is right there. cmd.exe accepts either form, so ".\" is
:: the one that always works regardless of which shell you're in.
::
:: Usage: .\send-notice.bat -Title "Scheduled maintenance" -Message "Offline Friday 6-8pm AEST."
::        .\send-notice.bat -Clear
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0send-notice.ps1" %*
