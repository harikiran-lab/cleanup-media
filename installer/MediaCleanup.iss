; Inno Setup script for Media Cleanup.
; Build with:  ISCC.exe /DAppVersion=1.2.1 installer\MediaCleanup.iss
; It packages the "publish" folder produced by scripts\publish.ps1.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define ServiceName "MediaCleanupService"
#define ServiceDisplayName "Media Cleanup Service"

[Setup]
AppId={{6F2C8A51-3B7E-4D0C-9E1A-5C4D7B9A2F10}
AppName=Media Cleanup
AppVersion={#AppVersion}
AppVerName=Media Cleanup {#AppVersion}
AppPublisher=Media Cleanup
VersionInfoVersion={#AppVersion}
DefaultDirName={autopf}\MediaCleanup
DefaultGroupName=Media Cleanup
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=MediaCleanup-Setup-{#AppVersion}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile=..\assets\app.ico
UninstallDisplayName=Media Cleanup
UninstallDisplayIcon={app}\MediaCleanup.Config.exe
CloseApplications=yes

[Files]
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Media Cleanup"; Filename: "{app}\MediaCleanup.Config.exe"
Name: "{autodesktop}\Media Cleanup"; Filename: "{app}\MediaCleanup.Config.exe"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"

[Run]
Filename: "{app}\MediaCleanup.Config.exe"; Description: "Open Media Cleanup to set up your folders"; Flags: postinstall nowait skipifsilent shellexec

[UninstallRun]
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden; RunOnceId: "DeleteService"

[Code]
function RunHidden(const FileName, Params: String): Integer;
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant(FileName), Params, '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
    Result := ResultCode
  else
    Result := -1;
end;

function ServiceExists: Boolean;
begin
  Result := RunHidden('{sys}\sc.exe', 'query {#ServiceName}') = 0;
end;

// The service keeps its files open, so it is stopped before they are replaced.
// "net stop" returns only once the service has really stopped.
function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  if ServiceExists then
    RunHidden('{sys}\net.exe', 'stop {#ServiceName}');
  Result := '';
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ServiceExe: String;
begin
  if CurStep <> ssPostInstall then
    Exit;

  // On an upgrade the service already exists and is left as it is, so the log-on
  // account chosen in Windows Services is kept.
  if not ServiceExists then
  begin
    ServiceExe := ExpandConstant('{app}\MediaCleanup.Service.exe');
    RunHidden('{sys}\sc.exe', 'create {#ServiceName} binPath= "\"' + ServiceExe + '\"" start= delayed-auto DisplayName= "{#ServiceDisplayName}"');
    RunHidden('{sys}\sc.exe', 'description {#ServiceName} "Watches download folders, cleans up media file names, MKV track names and subtitles, and moves finished downloads to their library folders."');
    RunHidden('{sys}\sc.exe', 'failure {#ServiceName} reset= 86400 actions= restart/60000/restart/60000/restart/60000');
  end;

  RunHidden('{sys}\net.exe', 'start {#ServiceName}');
end;
