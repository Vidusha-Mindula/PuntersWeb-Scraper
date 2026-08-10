# Deploying PuntersScraper.Web to a Windows Server

## Versioning and releases

The Web project shares the same version number as the desktop App — both read/write the
repo-root `VERSION` file. Only the release mechanics differ: the App uses
`installer/release.bat` and tags `vX.Y.Z`; the Web project uses `deploy/release-web.bat` below
and tags `web-vX.Y.Z`.

To cut a release (build the deploy zip, tag it, and publish it to GitHub Releases):

```powershell
deploy\release-web.bat
```

This tags the release `web-vX.Y.Z` (not `vX.Y.Z`) and marks it `--prerelease` on GitHub
deliberately: `PuntersScraper.App`'s `UpdateChecker` polls `/releases/latest` for its own updates
and expects a `vX.Y.Z` tag (see `src/PuntersScraper.App/Services/UpdateChecker.cs`). If a Web
release became GitHub's "latest" instead, the App's update check would silently go quiet until
the next App release. Bump the repo-root `VERSION` file (or pass a version directly:
`deploy\release-web.bat 1.1.0`) before running it.

## Docker (build-artifact only, not for running the app)

`docker-compose.yml` / `deploy/Dockerfile.web` build a versioned image of the win-x64 publish
output, tagged from the repo-root `VERSION` file:

```powershell
powershell deploy/docker-build.ps1
```

This exists purely so the build has a version-tagged, content-addressable record (e.g. in a
registry) — **the image is never meant to be `docker run`**. The scraper's bot-detection
workaround needs a real, non-headless Chromium window in an interactive Windows desktop session
(see below), which no standard container provides, the same reason this can't run as a Windows
Service either.

## 1. Build the deploy package (run here, on this machine)

```powershell
powershell deploy/publish-web.ps1
```

This produces `deploy/PuntersScraper.Web-deploy.zip` — a self-contained win-x64 build (no
separate .NET install needed on the server) with `server-setup.ps1` already included inside it.

## 2. Copy it to the server

Copy `PuntersScraper.Web-deploy.zip` to the target Windows Server any way you like (RDP
clipboard/drag-drop, a network share, etc.) and extract it to wherever you want the app to
live, e.g. `C:\Apps\PuntersScraperWeb\`.

## 3. Run the setup script — ON THE SERVER, in an elevated PowerShell prompt

```powershell
cd C:\Apps\PuntersScraperWeb
powershell -ExecutionPolicy Bypass .\server-setup.ps1
```

It will:
- Install Playwright's Chromium browser
- Prompt you for the admin username/password for the web UI's login (written to
  `appsettings.Production.json`, server-local only — never copy this file back into the repo)
- Open a firewall rule for the port (default `5011`)
- Register a Scheduled Task that starts the app automatically

## The one thing that can't be scripted away

This scraper only reliably gets past Punters' bot-detection using a **real (non-headless)
Chromium window positioned off-screen** — not true headless mode. A real window can only exist
inside an **interactive desktop session**. Windows Services run in the non-interactive Session 0
and cannot create one at all, which is why `server-setup.ps1` deliberately registers a
**Scheduled Task tied to your logon**, not a service.

In practice that means: after setup, log in to the server over RDP as the account the task runs
under, then **disconnect without logging off** (Start menu → your account → Disconnect, not Sign
out). A disconnected RDP session keeps its desktop alive, so the task keeps running. If that
account fully logs off, the scraper stops until it logs back in (the task is set to
auto-restart at the next logon).

## Verifying it's up

From another machine: `http://<server-address>:5011` — you should land on the login page.
