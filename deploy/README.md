# Deploying PuntersScraper.Web to a Windows Server

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
