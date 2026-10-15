; -----------------------------------------------------------------------------
;  WSL USB Manager - Inno Setup installer script
;  Copyright (c) 2026 KabukiFlux
;  Licensed under the MIT License. See LICENSE in the project root.
; -----------------------------------------------------------------------------
;  Build:  powershell -ExecutionPolicy Bypass -File installer\build-installer.ps1
;  or:     iscc /DAppVersion=1.0.0 installer\WslUsbManager.iss   (after dotnet publish to .\publish)

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif
#define AppName "WSL USB Manager"
#define AppExe "WslUsbManager.exe"
#define AppPublisher "KabukiFlux"
#define AppUrl "https://github.com/KabukiFlux/WSL_USB_MANAGER"

[Setup]
AppId={{8C1E4B7A-5D2F-4E6B-9A3C-2F7D1B0E6A51}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
AppCopyright=Copyright (c) 2026 {#AppPublisher}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
LicenseFile=..\LICENSE
OutputDir=..\dist
OutputBaseFilename=WslUsbManager-{#AppVersion}-setup-x64
SetupIconFile=..\WslUsbManager\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
; Per-machine by default; the user can choose a per-user install (no admin) in the first page.
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog
CloseApplications=yes
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} installer

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "..\publish\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

[Code]
// usbipd-win is required at runtime; it is not bundled, only checked for.
function UsbipdInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\usbipd-win\usbipd.exe')) or
            RegKeyExists(HKLM64, 'SOFTWARE\usbipd-win');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if (CurStep = ssPostInstall) and (not UsbipdInstalled) and (not WizardSilent) then
    MsgBox('usbipd-win was not found.' + #13#10#13#10 +
           'WSL USB Manager needs it to share USB devices with WSL2. Install it with:' + #13#10 +
           '    winget install --exact --id dorssel.usbipd-win' + #13#10#13#10 +
           'or from https://github.com/dorssel/usbipd-win/releases',
           mbInformation, MB_OK);
end;
