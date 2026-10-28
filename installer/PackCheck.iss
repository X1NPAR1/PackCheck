; PackCheck setup (Inno Setup 6). Build: ISCC /DAppVersion=2.0.0 /DSourceDir=..\publish\PackCheck /DOutputDir=..\artifacts PackCheck.iss
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish\PackCheck"
#endif
#ifndef OutputDir
  #define OutputDir "..\artifacts"
#endif

[Setup]
AppId={{6C2E1A4F-7B3D-4E8A-9F21-5D3C8B7A1E64}
AppName=PackCheck
AppVersion={#AppVersion}
AppVerName=PackCheck {#AppVersion}
AppPublisher=X1NPAR1
AppPublisherURL=https://github.com/X1NPAR1/PackCheck
AppSupportURL=https://github.com/X1NPAR1/PackCheck/issues
AppUpdatesURL=https://github.com/X1NPAR1/PackCheck/releases
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\PackCheck
DefaultGroupName=PackCheck
DisableProgramGroupPage=yes
; Per-user install: no admin rights needed, so updates can run silently.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
LicenseFile=..\LICENSE
SetupIconFile=..\src\PackCheck.App\Assets\app.ico
UninstallDisplayIcon={app}\PackCheck.exe
UninstallDisplayName=PackCheck
WizardStyle=modern
WizardImageFile=wizard-large.bmp
WizardSmallImageFile=wizard-small.bmp
Compression=lzma2/ultra64
SolidCompression=yes
CloseApplications=force
RestartApplications=no
OutputDir={#OutputDir}
OutputBaseFilename=PackCheck-Setup-{#AppVersion}
ShowLanguageDialog=auto

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[CustomMessages]
en.StartWithWindows=Start PackCheck with Windows (in the tray)
tr.StartWithWindows=PackCheck'i Windows ile başlat (tepside)
de.StartWithWindows=PackCheck mit Windows starten (im Infobereich)
fr.StartWithWindows=Démarrer PackCheck avec Windows (zone de notification)
ru.StartWithWindows=Запускать PackCheck вместе с Windows (в трее)

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "{cm:StartWithWindows}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "install-type-exe.txt"; DestDir: "{app}"; DestName: "install-type.txt"; Flags: ignoreversion

[InstallDelete]
; Remove files of the previous version that no longer ship (keeps user data, which lives in %LOCALAPPDATA%\PackCheck).
Type: filesandordirs; Name: "{app}\*.dll"

[Icons]
Name: "{group}\PackCheck"; Filename: "{app}\PackCheck.exe"
Name: "{group}\{cm:UninstallProgram,PackCheck}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\PackCheck"; Filename: "{app}\PackCheck.exe"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "PackCheck"; ValueData: """{app}\PackCheck.exe"" --minimized"; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\PackCheck.exe"; Description: "{cm:LaunchProgram,PackCheck}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/IM PackCheck.exe /F"; Flags: runhidden; RunOnceId: "KillPackCheck"
