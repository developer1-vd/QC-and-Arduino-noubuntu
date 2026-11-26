; VFlasher setup wizard (Inno Setup 6).
;
; Build after publishing the app, from the repository root:
;   dotnet publish windows/src/VFlasher -c Release -r win-x64 -o windows/publish/win-x64
;   iscc /DArch=x64 windows\installer\vflasher.iss
; Use win-arm64 and /DArch=arm64 for Windows on Arm.

#ifndef Arch
  #define Arch "x64"
#endif
#define AppVersion "0.1.0"
#define PublishDir "..\publish\win-" + Arch

[Setup]
AppId={{6E1C3B0A-6A55-4C7E-9D0B-5C1E2F7A9B31}
AppName=VFlasher
AppVersion={#AppVersion}
AppPublisher=VFlasher (unofficial, not affiliated with Qualcomm or Arduino)
DefaultDirName={autopf}\VFlasher
DefaultGroupName=VFlasher
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ChangesEnvironment=yes
OutputDir=..\publish
OutputBaseFilename=vflasher-setup-{#AppVersion}-{#Arch}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\vflasher.exe
LicenseFile=..\..\LICENSE
#if Arch == "arm64"
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#else
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#endif

[Tasks]
Name: "addtopath"; Description: "Add VFlasher to PATH (so you can type ""vflasher"" in any terminal)"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\vflasher.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\..\images\*"; DestDir: "{app}\images"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\..\README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme

[Icons]
Name: "{group}\VFlasher"; Filename: "{app}\vflasher.exe"
Name: "{autodesktop}\VFlasher"; Filename: "{app}\vflasher.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\vflasher.exe"; Description: "{cm:LaunchProgram,VFlasher}"; Flags: nowait postinstall skipifsilent shellexec

[Registry]
Root: HKLM; Subkey: "SYSTEM\CurrentControlSet\Control\Session Manager\Environment"; \
  ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; \
  Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Code]
const
  EnvKey = 'SYSTEM\CurrentControlSet\Control\Session Manager\Environment';

function NeedsAddPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Path) then
  begin
    Result := True;
    exit;
  end;
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

procedure RemoveFromPath(Dir: string);
var
  Path: string;
  P: Integer;
begin
  if not RegQueryStringValue(HKLM, EnvKey, 'Path', Path) then
    exit;
  P := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';');
  if P = 0 then
    exit;
  { P is 1-based in ';' + Path + ';', which lines up with the ';' before Dir in Path. }
  if P = 1 then
    Delete(Path, 1, Length(Dir) + 1)
  else
    Delete(Path, P - 1, Length(Dir) + 1);
  RegWriteExpandStringValue(HKLM, EnvKey, 'Path', Path);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
end;
