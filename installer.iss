#define MyAppName "MediaDebrid"
#ifndef MyAppVersion
  #define MyAppVersion "0.0.0"
#endif
#ifndef Arch
  #define Arch "x64"
#endif
#define MyArch Arch
#if MyArch == "x64"
  #define ArchISS "x64compatible"
#else
  #define ArchISS "arm64"
#endif
#define MyAppPublisher "zyr-ux"
#define MyAppURL "https://github.com/zyr-ux/MediaDebrid-cli"
#define MyAppExeName "mediadebrid.exe"

[Setup]
AppId={{86EB6333-6622-4589-99E8-3195C51A9A32}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} v{#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

SetupIconFile=Logos/logo-dark-win.ico
WizardStyle=modern

DefaultDirName={localappdata}\Programs\{#MyAppName}
PrivilegesRequired=lowest

DisableProgramGroupPage=yes
OutputDir=.\Output
OutputBaseFilename=mediadebrid-win-installer-{#MyArch}

Compression=lzma
SolidCompression=yes

ArchitecturesAllowed={#ArchISS}
ArchitecturesInstallIn64BitMode={#ArchISS}

ChangesEnvironment=yes
UninstallDisplayIcon={app}\{#MyAppExeName}

CloseApplications=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "publish\win-{#MyArch}\mediadebrid.exe"; DestDir: "{app}"; Flags: ignoreversion

[Registry]
Root: HKCU; Subkey: "Environment"; \
  ValueType: expandsz; ValueName: "Path"; \
  ValueData: "{olddata}{code:AppendPath}"

[Code]
function NeedsAddPath(Dir: string): Boolean;
var
  Path: string;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    Path := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Path) + ';') = 0;
end;

function AppendPath(Param: string): string;
begin
  if NeedsAddPath(ExpandConstant('{app}')) then
    Result := ';' + ExpandConstant('{app}')
  else
    Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Path: string;
  Dir: string;
begin
  if CurUninstallStep = usUninstall then
  begin
    Dir := ExpandConstant('{app}');
    if RegQueryStringValue(HKCU, 'Environment', 'Path', Path) then
    begin
      StringChangeEx(Path, ';' + Dir, '', True);
      StringChangeEx(Path, Dir + ';', '', True);
      StringChangeEx(Path, Dir, '', True);
      StringChangeEx(Path, ';;', ';', True);
      RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Path);
    end;
  end;
end;