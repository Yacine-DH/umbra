; Umbra installer (Inno Setup 6). Build with ..\build.ps1, which passes AppVersion and PublishDir.
#ifndef AppVersion
  #define AppVersion "1.15.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\dist\publish"
#endif

[Setup]
AppId={{6E1B5D2A-4C3F-4E8B-9A57-0D3C8F2B7A11}
AppName=Umbra
AppVersion={#AppVersion}
AppVerName=Umbra {#AppVersion}
AppPublisher=AmineChr54
AppPublisherURL=https://github.com/AmineChr54/umbra
AppSupportURL=https://github.com/AmineChr54/umbra/issues
AppUpdatesURL=https://github.com/AmineChr54/umbra/releases
; Per-user install: no admin rights, no UAC prompt.
PrivilegesRequired=lowest
ChangesAssociations=yes
DefaultDirName={localappdata}\Programs\Umbra
DisableProgramGroupPage=yes
DisableDirPage=auto
DisableReadyPage=yes
OutputDir=..\dist
OutputBaseFilename=UmbraSetup-{#AppVersion}
SetupIconFile=..\assets\umbra.ico
UninstallDisplayIcon={app}\Umbra.exe
UninstallDisplayName=Umbra
; Dark to match Umbra; an upgrade (a manual run over an existing install) skips the welcome page.
WizardStyle=modern dark
WizardSizePercent=100
DisableWelcomePage=yes
Compression=lzma2/ultra64
SolidCompression=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
CloseApplications=force
ChangesEnvironment=yes
RestartApplications=no
; A self-update (silent) keeps the startup/shortcut/PATH choices made at the first install.
UsePreviousTasks=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "startup"; Description: "Start Umbra when I sign in to Windows (recommended)"; GroupDescription: "Startup:"
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked
Name: "addtopath"; Description: "Add umbractl (command line and AI agent control) to PATH"; GroupDescription: "Automation:"

[InstallDelete]
Type: filesandordirs; Name: "{app}\widgets\sp500"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
; AppUserModelID must match ToastService.InstalledAumid: Windows needs it on a Start-menu shortcut to show umbra.notify toasts.
Name: "{autoprograms}\Umbra"; Filename: "{app}\Umbra.exe"; Comment: "Desktop widgets that live on your wallpaper"; AppUserModelID: "AmineChr54.Umbra"
Name: "{autodesktop}\Umbra"; Filename: "{app}\Umbra.exe"; Tasks: desktopicon

[Registry]
; Same value the in-app "Start with Windows" toggle reads and writes.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "Umbra"; ValueData: """{app}\Umbra.exe"" --background"; Tasks: startup; Flags: uninsdeletevalue
; Double-click on a .umbrawidget opens Umbra's install review (per user, no admin). The package is checked and reviewed before anything is installed.
Root: HKCU; Subkey: "Software\Classes\.umbrawidget"; ValueType: string; ValueName: ""; ValueData: "Umbra.Widget"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Umbra.Widget"; ValueType: string; ValueName: ""; ValueData: "Umbra widget"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\Umbra.Widget\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\Umbra.exe,0"
Root: HKCU; Subkey: "Software\Classes\Umbra.Widget\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\Umbra.exe"" ""%1"""
; umbractl on the user PATH (removed again on uninstall, see RemoveFromPath).
Root: HKCU; Subkey: "Environment"; ValueType: expandsz; ValueName: "Path"; ValueData: "{olddata};{app}"; Tasks: addtopath; Check: NeedsAddPath(ExpandConstant('{app}'))

[Run]
Filename: "{app}\Umbra.exe"; Description: "Launch Umbra"; Flags: nowait postinstall skipifsilent
; Self-update (Umbra ran us with /UPDATE=1): start the new version again, as the user rather than the installer's token.
Filename: "{app}\Umbra.exe"; Parameters: "--updated"; Flags: nowait runasoriginaluser; Check: IsSelfUpdate

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
const
  WebView2Key = 'SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  WebView2UserKey = 'Software\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';

var
  Installed: Boolean;

{ Umbra's own update runs us with /UPDATE=1 (see Updates.InstallerArguments). A manual run never has it. }
function IsSelfUpdate: Boolean;
begin
  Result := ExpandConstant('{param:UPDATE|0}') = '1';
end;

function WebView2Installed: Boolean;
var
  Version: String;
begin
  Result :=
    (RegQueryStringValue(HKLM, WebView2Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0')) or
    (RegQueryStringValue(HKCU, WebView2UserKey, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0'));
end;

function InitializeSetup: Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;
  if not WebView2Installed then
  begin
    if not WizardSilent and (MsgBox('Umbra needs the Microsoft Edge WebView2 Runtime, which is not installed on this PC.' + #13#10#13#10 +
              'Open the download page now? You can run this setup again afterwards.',
              mbConfirmation, MB_YESNO) = IDYES) then
      ShellExec('open', 'https://developer.microsoft.com/microsoft-edge/webview2/', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    Result := False;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssPostInstall then
    Installed := True;
end;

{ Never leave the user without Umbra: if a self-update stops before the new files are in place, start the version that is still there. }
procedure DeinitializeSetup;
var
  Exe: String;
  ResultCode: Integer;
begin
  if IsSelfUpdate and not Installed then
  begin
    try
      Exe := ExpandConstant('{app}\Umbra.exe');
      if FileExists(Exe) then
        Exec(Exe, '--updated', '', SW_SHOWNORMAL, ewNoWait, ResultCode);
    except
      // The app folder is not known when Setup stopped very early: nothing to start.
    end;
  end;
end;

procedure StopUmbra;
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Umbra.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  // An AI agent's MCP session (umbractl mcp) would keep the runtime files locked.
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM umbractl.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopUmbra;
  Result := '';
end;

function InitializeUninstall: Boolean;
begin
  StopUmbra;
  Result := True;
end;

function NeedsAddPath(Dir: String): Boolean;
var
  Paths: String;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Paths) then
    Paths := '';
  Result := Pos(';' + Uppercase(Dir) + ';', ';' + Uppercase(Paths) + ';') = 0;
end;

procedure RemoveFromPath(Dir: String);
var
  Paths: String;
  P: Integer;
begin
  if not RegQueryStringValue(HKCU, 'Environment', 'Path', Paths) then
    exit;
  P := Pos(';' + Uppercase(Dir), Uppercase(Paths));
  if P > 0 then
  begin
    Delete(Paths, P, Length(Dir) + 1);
    RegWriteExpandStringValue(HKCU, 'Environment', 'Path', Paths);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
    RemoveFromPath(ExpandConstant('{app}'));
  if (CurUninstallStep = usPostUninstall) and not UninstallSilent then
    if MsgBox('Also remove your Umbra settings and installed community widgets?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
    begin
      DelTree(ExpandConstant('{userappdata}\Umbra'), True, True, True);
      DelTree(ExpandConstant('{localappdata}\Umbra'), True, True, True);
    end;
end;
