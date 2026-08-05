; Inno Setup script for Punters Meetings Scraper.
; Built via installer\build-punters-installer.ps1, which publishes the app to
; installer\publish-punters first and then invokes ISCC on this script.

#ifndef AppVersion
  #define AppVersion "2.5.0"
#endif

; S3 access/secret key baked into a fresh install's default settings.json (see [Code] below) —
; passed in at build time via ISCC's /D flag from build-punters-installer.ps1, never hardcoded
; here since this .iss file is public. Left blank, a built installer just ships with no default
; (same as before this existed) — the user fills them in via the app themselves.
#ifndef S3AccessKey
  #define S3AccessKey ""
#endif
#ifndef S3SecretKey
  #define S3SecretKey ""
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

[Code]
// Seeds a default settings.json (same shape AppSettings.cs itself saves) so a fresh install
// already has S3 access/secret keys configured, without the user having to type them into the
// app. Only writes it if nothing is there yet — an existing settings.json (a reinstall/upgrade
// on a machine that's already been configured, possibly with different keys/bucket) is never
// touched or overwritten.
procedure WriteDefaultSettingsIfMissing;
var
  SettingsDir, SettingsPath, Json: string;
begin
  SettingsDir := ExpandConstant('{localappdata}\PuntersScraper');
  SettingsPath := SettingsDir + '\settings.json';

  if FileExists(SettingsPath) then
    Exit;

  if not DirExists(SettingsDir) then
    ForceDirectories(SettingsDir);

  Json := '{"DownloadFolder":"","AutoExportAfterScrape":false,"UploadToS3":false,' +
    '"S3Endpoint":"https://s3.troyendata.com","S3AccessKey":"{#S3AccessKey}",' +
    '"S3SecretKey":"{#S3SecretKey}","S3BucketName":"punter-web-scraper","S3Folder":"pending"}';

  SaveStringToFile(SettingsPath, Json, False);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    WriteDefaultSettingsIfMissing;
end;
