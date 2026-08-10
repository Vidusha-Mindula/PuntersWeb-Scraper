@echo off
setlocal enabledelayedexpansion

:: Cuts a release of PuntersScraper.Web: builds the self-contained win-x64 deploy zip (via
:: publish-web.ps1), then creates a matching GitHub Release (tag + uploaded zip + auto-generated
:: notes) via the GitHub CLI.
::
:: Tagged web-vX.Y.Z (NOT vX.Y.Z) and marked --prerelease so it never becomes GitHub's
:: "/releases/latest" - PuntersScraper.App's UpdateChecker polls that exact endpoint to find new
:: App versions (see src/PuntersScraper.App/Services/UpdateChecker.cs) and expects a vX.Y.Z tag.
:: If a Web release became "latest" instead, the App's update check would silently go quiet
:: until the next App release. Shares the SAME version number as the App - both read/write the
:: repo-root VERSION file (the one installer\release.bat also uses) - only the git tag prefix
:: and the GitHub "latest" flag differ between the two release scripts.
::
:: Usage:
::     deploy\release-web.bat            (uses the version in the repo-root VERSION file)
::     deploy\release-web.bat 1.1.0      (overrides it, and updates VERSION to match)
::
:: Requires the working tree to be clean (everything already committed AND pushed) so the release
:: always matches something actually in git history. First run also needs the GitHub CLI (gh) --
:: this script installs it via winget if missing, and signs you in via a browser if needed.

set "DEPLOY_DIR=%~dp0"
set "REPO_ROOT=%DEPLOY_DIR%.."
set "VERSION_FILE=%REPO_ROOT%\VERSION"

if "%~1"=="" (
    if not exist "%VERSION_FILE%" (
        echo Usage: release-web.bat VERSION
        echo Example: release-web.bat 1.1.0
        echo Or create a VERSION file at the repo root containing the version to release.
        exit /b 1
    )
    set /p VERSION=<"%VERSION_FILE%"
) else (
    set "VERSION=%~1"
)

set "ZIP_PATH=%DEPLOY_DIR%PuntersScraper.Web-deploy.zip"

echo ==============================================
echo  Releasing Punters Scraper Web v%VERSION%
echo ==============================================

pushd "%REPO_ROOT%" || exit /b 1

:: --- 1. Working tree must be clean and match what's already on GitHub ---
set "DIRTY="
for /f "delims=" %%L in ('git status --porcelain 2^>nul') do set "DIRTY=1"
if defined DIRTY (
    echo.
    echo ERROR: You have uncommitted changes. Commit and push them first, then re-run this script.
    git status --short
    popd
    exit /b 1
)

git fetch origin --quiet
for /f "delims=" %%L in ('git rev-parse HEAD') do set "LOCAL_HEAD=%%L"
for /f "delims=" %%L in ('git rev-parse origin/main 2^>nul') do set "REMOTE_HEAD=%%L"
if not "%LOCAL_HEAD%"=="%REMOTE_HEAD%" (
    echo.
    echo ERROR: Local main doesn't match origin/main - push your commits first, then re-run this script.
    popd
    exit /b 1
)

:: --- 2. GitHub CLI must be installed ---
where gh >nul 2>nul
if errorlevel 1 (
    echo gh CLI not found - installing via winget...
    winget install --id GitHub.cli -e --source winget
    if errorlevel 1 (
        echo ERROR: Failed to install gh CLI. Install it manually from https://cli.github.com and re-run.
        popd
        exit /b 1
    )
    echo.
    echo gh CLI installed. Close and reopen this terminal so PATH picks it up, then re-run this script.
    popd
    exit /b 0
)

:: --- 3. Must be signed in ---
gh auth status >nul 2>nul
if errorlevel 1 (
    echo Not signed in to GitHub CLI - opening browser sign-in...
    gh auth login --web -h github.com
    if errorlevel 1 (
        echo ERROR: gh auth login failed.
        popd
        exit /b 1
    )
)

:: --- 4. Build the deploy zip ---
echo.
echo ==^> Building Web deploy package v%VERSION%...
powershell -NoProfile -ExecutionPolicy Bypass -File "%DEPLOY_DIR%publish-web.ps1" -Version %VERSION%
if errorlevel 1 (
    echo ERROR: Web publish failed.
    popd
    exit /b 1
)

if not exist "%ZIP_PATH%" (
    echo ERROR: Expected zip not found at %ZIP_PATH%
    popd
    exit /b 1
)

:: --- 5. Create the GitHub Release (--prerelease so it never becomes /releases/latest - see the
::        note at the top of this file for why that matters) ---
echo.
echo ==^> Creating GitHub Release web-v%VERSION%...
gh release create web-v%VERSION% "%ZIP_PATH%" --title "Web v%VERSION%" --generate-notes --prerelease
if errorlevel 1 (
    echo ERROR: gh release create failed. If a web-v%VERSION% tag was left behind, remove it with:
    echo     git push --delete origin web-v%VERSION% ^&^& git tag -d web-v%VERSION%
    echo before retrying.
    popd
    exit /b 1
)

:: --- 6. Keep the VERSION file in sync with what was just released ---
echo %VERSION%> "%VERSION_FILE%"
git diff --quiet -- "%VERSION_FILE%"
if errorlevel 1 (
    git add "%VERSION_FILE%"
    git commit -m "Bump Web version to %VERSION%" --quiet
    git push origin main --quiet
    if errorlevel 1 (
        echo WARNING: Release succeeded, but committing/pushing the VERSION bump failed.
        echo Commit and push "%VERSION_FILE%" manually so it stays in sync.
    )
)

popd
echo.
echo ==============================================
echo  Done. web-v%VERSION% is live on GitHub Releases
echo  (as a pre-release, so it won't interfere with the
echo  desktop App's own update checks). Download the zip
echo  from the release page and deploy per deploy/README.md.
echo ==============================================
