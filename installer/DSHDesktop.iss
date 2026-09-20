#ifndef SourceDir
  #error SourceDir must point to a complete DSH Desktop Portable publish directory.
#endif

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#ifndef OutputDir
  #define OutputDir "Output"
#endif

#if FileExists(AddBackslash(SourceDir) + "DSHDesktop.exe")
#else
  #error SourceDir is missing DSHDesktop.exe.
#endif
#if FileExists(AddBackslash(SourceDir) + "DSHDesktop.dll")
#else
  #error SourceDir is missing DSHDesktop.dll.
#endif
#if FileExists(AddBackslash(SourceDir) + "runtime\active.json")
#else
  #error SourceDir is missing runtime\active.json; shell-only publishes cannot be installed.
#endif
#if FileExists(AddBackslash(SourceDir) + "LICENSE")
#else
  #error SourceDir is missing LICENSE.
#endif
#if FileExists(AddBackslash(SourceDir) + "NOTICE.md")
#else
  #error SourceDir is missing NOTICE.md.
#endif

[Setup]
AppId={{DF1EAE74-5DFE-4E95-82D4-9AA022B6D374}
AppName=DSH Desktop
AppVersion={#MyAppVersion}
AppPublisher=DSH Desktop contributors
AppPublisherURL=https://github.com/umrlll/dsh-desktop
DefaultDirName={localappdata}\Programs\DSHDesktop
DefaultGroupName=DSH Desktop
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\DSHDesktop.exe
OutputBaseFilename=DSHDesktop-Setup-{#MyAppVersion}
OutputDir={#OutputDir}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\DSH Desktop"; Filename: "{app}\DSHDesktop.exe"

[Run]
Filename: "{app}\DSHDesktop.exe"; Description: "Launch DSH Desktop"; Flags: nowait postinstall skipifsilent

; No uninstall deletion directive is intentionally present. Desktop user data lives under
; %LOCALAPPDATA%\DSHDesktop rather than {app}, and ordinary uninstall must retain it.
