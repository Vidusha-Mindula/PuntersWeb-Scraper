@echo off
setlocal enabledelayedexpansion

:: Cuts a full release of BOTH the desktop App and the Web app in one step, using the same
:: version number for each - runs installer\release.bat then deploy\release-web.bat in turn, so
:: the two release scripts (and their tags/artifacts) never drift apart. See those two scripts
:: for what each one actually does (build, gh release create, VERSION bump).
::
:: Usage:
::     release-all.bat            (uses the version in the VERSION file at repo root)
::     release-all.bat 3.14.0     (overrides it, used for both releases)
::
:: Same prerequisites as the two scripts this calls: working tree clean and pushed, GitHub CLI
:: (gh) installed and signed in.

set "ROOT=%~dp0"
set "VERSION_ARG=%~1"

echo ==============================================
echo  Releasing App + Web
echo ==============================================

echo.
echo ==^> App release...
if "%VERSION_ARG%"=="" (
    call "%ROOT%installer\release.bat"
) else (
    call "%ROOT%installer\release.bat" %VERSION_ARG%
)
if errorlevel 1 (
    echo ERROR: App release failed - stopping before the Web release.
    exit /b 1
)

echo.
echo ==^> Web release...
if "%VERSION_ARG%"=="" (
    call "%ROOT%deploy\release-web.bat"
) else (
    call "%ROOT%deploy\release-web.bat" %VERSION_ARG%
)
if errorlevel 1 (
    echo ERROR: Web release failed. The App release above already succeeded and is live.
    exit /b 1
)

echo.
echo ==============================================
echo  Done. Both the App and Web releases are live.
echo ==============================================
