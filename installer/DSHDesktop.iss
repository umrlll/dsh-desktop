#ifndef SourceDir
  #error SourceDir must point to a complete DSH Desktop Portable publish directory.
#endif

#ifndef MyAppVersion
  #define MyAppVersion "0.0.0-dev"
#endif

#ifndef OutputDir
  #define OutputDir "Output"
#endif

#ifndef MyAppId
#define MyAppId "{{DF1EAE74-5DFE-4E95-82D4-9AA022B6D374}"
#endif

#ifndef DshDesktopRegistryKey
#define DshDesktopRegistryKey "Software\DSHDesktop"
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
AppId={#MyAppId}
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

[Registry]
; Keep the installed semantic version outside {app}. It is removed with a normal uninstall,
; while application data under %LOCALAPPDATA%\DSHDesktop remains untouched.
Root: HKCU; Subkey: "{#DshDesktopRegistryKey}"; ValueType: string; ValueName: "InstalledVersion"; ValueData: "{#MyAppVersion}"; Flags: uninsdeletekeyifempty

[Icons]
Name: "{autoprograms}\DSH Desktop"; Filename: "{app}\DSHDesktop.exe"

[Run]
Filename: "{app}\DSHDesktop.exe"; Description: "Launch DSH Desktop"; Flags: nowait postinstall skipifsilent

; No uninstall deletion directive is intentionally present. Desktop user data lives under
; %LOCALAPPDATA%\DSHDesktop rather than {app}, and ordinary uninstall must retain it.

[UninstallDelete]
; User data is removed only when the user explicitly runs unins*.exe /PURGEUSERDATA.
; Ordinary interactive and silent uninstall keeps it for recovery and later reinstall.
Type: filesandordirs; Name: "{localappdata}\DSHDesktop"; Check: ShouldPurgeUserData

[Code]
type
  TVersionParts = record
    Major: Integer;
    Minor: Integer;
    Patch: Integer;
    Prerelease: String;
    Valid: Boolean;
  end;

function IsDigits(const Value: String): Boolean;
var
  Index: Integer;
begin
  Result := Length(Value) > 0;
  for Index := 1 to Length(Value) do
    if (Value[Index] < '0') or (Value[Index] > '9') then
    begin
      Result := False;
      exit;
    end;
end;

function NormalizeNumericIdentifier(const Value: String): String;
var
  Index: Integer;
begin
  Index := 1;
  while (Index < Length(Value)) and (Value[Index] = '0') do
    Index := Index + 1;
  Result := Copy(Value, Index, MaxInt);
end;

function CompareNumericIdentifier(const Left, Right: String): Integer;
var
  NormalLeft, NormalRight: String;
begin
  NormalLeft := NormalizeNumericIdentifier(Left);
  NormalRight := NormalizeNumericIdentifier(Right);
  if Length(NormalLeft) <> Length(NormalRight) then
  begin
    if Length(NormalLeft) > Length(NormalRight) then Result := 1 else Result := -1;
    exit;
  end;
  Result := CompareStr(NormalLeft, NormalRight);
end;

function NextPrereleaseIdentifier(var Remaining: String): String;
var
  Separator: Integer;
begin
  Separator := Pos('.', Remaining);
  if Separator = 0 then
  begin
    Result := Remaining;
    Remaining := '';
  end
  else
  begin
    Result := Copy(Remaining, 1, Separator - 1);
    Delete(Remaining, 1, Separator);
  end;
end;

function ParseVersion(const Value: String): TVersionParts;
var
  Core, Segment, Remaining: String;
  Separator, Suffix: Integer;
begin
  Result.Valid := False;
  Core := Trim(Value);
  Suffix := Pos('+', Core);
  if Suffix > 0 then Delete(Core, Suffix, MaxInt);
  Suffix := Pos('-', Core);
  if Suffix > 0 then
  begin
    Result.Prerelease := Copy(Core, Suffix + 1, MaxInt);
    Delete(Core, Suffix, MaxInt);
  end;

  Remaining := Core;
  Separator := Pos('.', Remaining);
  if Separator = 0 then exit;
  Segment := Copy(Remaining, 1, Separator - 1);
  if not IsDigits(Segment) then exit;
  Result.Major := StrToIntDef(Segment, -1);
  Delete(Remaining, 1, Separator);

  Separator := Pos('.', Remaining);
  if Separator = 0 then exit;
  Segment := Copy(Remaining, 1, Separator - 1);
  if not IsDigits(Segment) then exit;
  Result.Minor := StrToIntDef(Segment, -1);
  Delete(Remaining, 1, Separator);

  if not IsDigits(Remaining) then exit;
  Result.Patch := StrToIntDef(Remaining, -1);
  if (Result.Major < 0) or (Result.Minor < 0) or (Result.Patch < 0) then exit;
  Result.Valid := True;
end;

function CompareVersions(const Left, Right: String): Integer;
var
  LeftParts, RightParts: TVersionParts;
  LeftId, RightId: String;
  LeftNumeric, RightNumeric: Boolean;
begin
  LeftParts := ParseVersion(Left);
  RightParts := ParseVersion(Right);
  if not LeftParts.Valid or not RightParts.Valid then
  begin
    Result := 0;
    exit;
  end;
  if LeftParts.Major <> RightParts.Major then
  begin
    if LeftParts.Major > RightParts.Major then Result := 1 else Result := -1;
    exit;
  end;
  if LeftParts.Minor <> RightParts.Minor then
  begin
    if LeftParts.Minor > RightParts.Minor then Result := 1 else Result := -1;
    exit;
  end;
  if LeftParts.Patch <> RightParts.Patch then
  begin
    if LeftParts.Patch > RightParts.Patch then Result := 1 else Result := -1;
    exit;
  end;
  if (LeftParts.Prerelease = '') and (RightParts.Prerelease = '') then
  begin
    Result := 0;
    exit;
  end;
  if LeftParts.Prerelease = '' then
  begin
    Result := 1;
    exit;
  end;
  if RightParts.Prerelease = '' then
  begin
    Result := -1;
    exit;
  end;

  while (LeftParts.Prerelease <> '') and (RightParts.Prerelease <> '') do
  begin
    LeftId := NextPrereleaseIdentifier(LeftParts.Prerelease);
    RightId := NextPrereleaseIdentifier(RightParts.Prerelease);
    LeftNumeric := IsDigits(LeftId);
    RightNumeric := IsDigits(RightId);
    if LeftNumeric and RightNumeric then Result := CompareNumericIdentifier(LeftId, RightId)
    else if LeftNumeric then Result := -1
    else if RightNumeric then Result := 1
    else Result := CompareStr(LeftId, RightId);
    if Result <> 0 then exit;
  end;
  if LeftParts.Prerelease = RightParts.Prerelease then Result := 0
  else if LeftParts.Prerelease = '' then Result := -1
  else Result := 1;
end;

function InitializeSetup(): Boolean;
var
  InstalledVersion: String;
  InstalledParts, IncomingParts: TVersionParts;
begin
  Result := True;
  IncomingParts := ParseVersion('{#MyAppVersion}');
  if not IncomingParts.Valid then
  begin
    MsgBox(
      'This installer has an invalid semantic version and cannot continue.',
      mbError,
      MB_OK);
    Result := False;
    Exit;
  end;

  if RegQueryStringValue(HKCU, '{#DshDesktopRegistryKey}', 'InstalledVersion', InstalledVersion) then
  begin
    InstalledParts := ParseVersion(InstalledVersion);
    if not InstalledParts.Valid then
    begin
      MsgBox(
        'The installed DSH Desktop version record cannot be verified. Uninstall the existing application before continuing.',
        mbError,
        MB_OK);
      Result := False;
      Exit;
    end;
    if CompareVersions(InstalledVersion, '{#MyAppVersion}') > 0 then
    begin
      MsgBox(
        'A newer DSH Desktop version (' + InstalledVersion + ') is already installed. Downgrades are blocked.',
        mbError,
        MB_OK);
      Result := False;
    end;
  end;
end;

function ShouldPurgeUserData(): Boolean;
var
  Index: Integer;
begin
  Result := False;
  for Index := 1 to ParamCount do
    if CompareStr(Uppercase(ParamStr(Index)), '/PURGEUSERDATA') = 0 then
    begin
      Result := True;
      Exit;
    end;
end;
