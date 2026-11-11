; Windows installer for CD Inventory, built with Inno Setup 6 (https://jrsoftware.org/isinfo.php).
;
; GitHub Actions builds this automatically (see .github/workflows/build.yml).
; To build it on your own PC instead:
;   1. dotnet publish AvaloniaCDInventory.csproj -c Release -r win-x64 --self-contained true -o publish
;   2. "C:\Program Files (x86)\Inno Setup 6\ISCC.exe" /DAppVersion=2.2.0 installer\CDInventory.iss
; The installer appears in installer\Output\.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\publish"
#endif

#define AppName "CD Inventory"
#define AppExeName "AvaloniaCDInventory.exe"
#define AppPublisher "jeffplax"
#define AppURL "https://github.com/jeffplax/AvaloniaCDInventory"

[Setup]
; Never change AppId: it's how Windows recognizes a new version as an upgrade of the same app.
AppId={{76674D4E-3291-490C-9D4B-3B743218C642}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppURL}
AppSupportURL={#AppURL}/issues
AppUpdatesURL={#AppURL}/releases

; Install for the current user by default (AppData\Local\Programs, no admin rights needed).
; Setup offers "Install for all users" too, which goes to Program Files and asks for admin rights.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes

; 64-bit Windows 10 or later, matching the win-x64 build
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0

; If the app is open during an upgrade, offer to close it
CloseApplications=yes
RestartApplications=no

; How the installer and its uninstall entry look
WizardStyle=modern
SetupIconFile=..\Assets\AppIcon.ico
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}

OutputDir=Output
OutputBaseFilename=CDInventory-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
; Everything from dotnet publish. The app is self-contained, so .NET doesn't need to be installed.
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent

; Uninstalling removes only the program files. Your inventory database and your Discogs token
; (%APPDATA%\AvaloniaCDInventory) live elsewhere and are left untouched.
