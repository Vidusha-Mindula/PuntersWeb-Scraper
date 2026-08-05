@echo off
REM Double-click this on the server whenever a new PuntersScraper.Web-deploy.zip has been
REM copied to D:\ - it stops the running app, applies the new build, and restarts it.
REM Assumes the defaults set up so far (zip at D:\PuntersScraper.Web-deploy.zip, install
REM folder D:\PuntersScraperWeb, task name PuntersScraperWeb). Edit update.ps1's defaults,
REM or pass args here, if any of those change.

powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0update.ps1" %*
pause
