; Inno Setup script for Punters Meetings Scraper.
; Built via installer\build-punters-installer.ps1, which publishes the app to
; installer\publish-punters first and then invokes ISCC on this script.

#ifndef AppVersion
  #define AppVersion "2.5.0"
#endif

#define AppName "Punters Meetings Scraper"
#define AppExeName "PuntersScraper.App.exe"
#define AppPublisher "Troyendata"

[Setup]
AppId={{9F2E4C1A-6B8D-4E2F-9A3C-5D7B1E0F4A62}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
DefaultDirName={userpf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputBaseFilename=PuntersScraperSetup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#AppExeName}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"

[Files]
Source: "publish-punters\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "powershell.exe"; \
    Parameters: "-NoProfile -ExecutionPolicy Bypass -File ""{app}\playwright.ps1"" install chromium"; \
    StatusMsg: "Downloading the Chromium browser used for scraping (this needs an internet connection and can take a minute)..."; \
    Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExeName}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent
