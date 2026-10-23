; Per-user installer for Mini mouse hub, built with Inno Setup 6 by .github/workflows/release.yml.
; It installs to %LOCALAPPDATA%\Programs\Mini mouse hub without admin rights. The app then updates
; itself in that folder from GitHub releases, so the installer is only needed once.
;
; Build: ISCC.exe /DAppVersion=0.3.1 /DSourceExe=path\to\MiniMouseHub.exe installer\MiniMouseHub.iss

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceExe
  #define SourceExe "..\publish\MiniMouseHub.exe"
#endif

#define AppName "Mini mouse hub"
#define AppExe "MiniMouseHub.exe"

[Setup]
; Never change AppId: Windows and Installation.cs find the installation by it.
AppId={{60C6DDED-CE8E-4927-A75C-03870D332825}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=Durim-KLMND
AppPublisherURL=https://github.com/Durim-KLMND/mini-mouse-hub
AppSupportURL=https://github.com/Durim-KLMND/mini-mouse-hub/issues
AppUpdatesURL=https://github.com/Durim-KLMND/mini-mouse-hub/releases
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\{#AppName}
DisableProgramGroupPage=yes
DisableDirPage=yes
DisableReadyPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=..\src\MiniMouseHub.App\Assets\app.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
OutputBaseFilename=MiniMouseHub-Setup
Compression=lzma2
SolidCompression=yes
; The app runs in the tray. If it's running from the install folder, close it so the exe can be replaced.
CloseApplications=force
RestartApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Add a desktop shortcut"; Flags: unchecked
Name: "startup"; Description: "Start Mini mouse hub with Windows (you can change this later in Settings)"

[Files]
Source: "{#SourceExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{userdesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; Same value the app's own "Start with Windows" setting writes (StartWithWindows.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MiniMouseHub"; ValueData: """{app}\{#AppExe}"" --minimized"; Tasks: startup

[Run]
Filename: "{app}\{#AppExe}"; Description: "Open Mini mouse hub"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; The app hides in the tray instead of closing, so stop it before its files are removed.
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im {#AppExe}"; Flags: runhidden; RunOnceId: "StopApp"

[UninstallDelete]
; Left behind by the app's self-update (Updater.cs).
Type: files; Name: "{app}\MiniMouseHub.old.exe"
Type: files; Name: "{app}\MiniMouseHub.new.exe"
Type: dirifempty; Name: "{app}"

[Code]
const
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  SettingsFolder = '{localappdata}\MiniMouseHub';
  RuntimeUrl = 'https://dotnet.microsoft.com/download/dotnet/8.0';

{ The app needs the .NET 8 Desktop Runtime. Installing that takes admin rights, so only point to it. }
function HasDesktopRuntime: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  ErrorCode: Integer;
begin
  if (CurStep = ssPostInstall) and not HasDesktopRuntime and not WizardSilent then
  begin
    if MsgBox('Mini mouse hub needs the .NET 8 Desktop Runtime, which isn''t installed on this PC.' + #13#10#13#10 +
              'Open the download page now? Pick ".NET Desktop Runtime" for Windows x64.',
              mbConfirmation, MB_YESNO) = IDYES then
      ShellExecAsOriginalUser('open', RuntimeUrl, '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Command: String;
begin
  if CurUninstallStep = usUninstall then
  begin
    { Remove the startup entry, but only if it points at this installation. }
    if RegQueryStringValue(HKCU, RunKey, 'MiniMouseHub', Command) and
       (Pos(Lowercase(ExpandConstant('{app}')), Lowercase(Command)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'MiniMouseHub');
  end;

  if (CurUninstallStep = usPostUninstall) and DirExists(ExpandConstant(SettingsFolder)) and not UninstallSilent then
  begin
    if MsgBox('Also delete your Mini mouse hub settings?' + #13#10#13#10 +
              'Choose No to keep them for next time. Settings saved on your devices are not affected.',
              mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
      DelTree(ExpandConstant(SettingsFolder), True, True, True);
  end;
end;
