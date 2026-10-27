; Installer for vibe PAR (Inno Setup 6).
; Built by scripts\build-release.ps1:
;   ISCC /DAppVersion=2.0.0 /DSourceDir=<staged release folder> /DOutputDir=<out> installer\PARScope.iss
;
; Installs the scope per user (no admin) to a folder of the user's choice, and the vPilot plugin into
; vPilot's Plugins folder, which is detected automatically and can be overridden. Both folders are
; remembered, so updates (the scope runs this installer with /SILENT /UPDATE) go to the same places.
; The plugin DLL is only replaced when its file version changes, so an update does not fail on a DLL
; that a running vPilot holds open: bump <Version> in RadarStreamerPlugin.csproj when the plugin changes.
;
; Npcap (for the CRC feed) is optional and not bundled: its free license does not allow redistribution.
; When the task is chosen, the current installer is downloaded from npcap.com and run with its own wizard,
; so the user accepts Npcap's license there. The task is hidden when Npcap is already installed.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#ifndef SourceDir
  #error SourceDir (the staged release folder) is required
#endif
#ifndef OutputDir
  #define OutputDir "."
#endif

[Setup]
AppId={{5E7000D9-60D6-4FE7-812F-13569B2E4EF6}
AppName=vibe PAR
AppVersion={#AppVersion}
AppVerName=vibe PAR {#AppVersion}
AppPublisher=vibe PAR
AppPublisherURL=https://github.com/yanjz124/VATSIM-PAR-Scope
AppSupportURL=https://github.com/yanjz124/VATSIM-PAR-Scope/issues
DefaultDirName={autopf}\vibe PAR
DefaultGroupName=vibe PAR
DisableProgramGroupPage=yes
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
UsePreviousAppDir=yes
UsePreviousTasks=yes
CloseApplications=yes
RestartApplications=no
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
WizardStyle=modern
SetupIconFile=..\src\PARScope.App\Assets\PARScope.ico
UninstallDisplayIcon={app}\PARScope.exe
OutputDir={#OutputDir}
OutputBaseFilename=PARScope-Setup-{#AppVersion}
Compression=lzma2/max
SolidCompression=yes

[Tasks]
Name: "plugin"; Description: "Install the vPilot plugin (live VATSIM traffic, including 5 Hz Velocity positions)"
Name: "npcap"; Description: "Download and install Npcap from npcap.com (lets the CRC feed run without administrator rights)"; Flags: unchecked; Check: NpcapWanted
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\PARScope.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\TrafficProbe.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#SourceDir}\runtimes\*"; DestDir: "{app}\runtimes"; Flags: ignoreversion recursesubdirs
; A copy of the plugin next to the scope, for installing into vPilot by hand.
Source: "{#SourceDir}\Plugin\*"; DestDir: "{app}\Plugin"; Flags: ignoreversion
; The plugin itself. The .ini holds the user's settings, so it is only written the first time.
Source: "{#SourceDir}\Plugin\RadarStreamerPlugin.dll"; DestDir: "{code:PluginDir}"; Tasks: plugin
Source: "{#SourceDir}\Plugin\RadarStreamerPlugin.ini"; DestDir: "{code:PluginDir}"; Tasks: plugin; Flags: onlyifdoesntexist uninsneveruninstall

[Icons]
Name: "{group}\vibe PAR"; Filename: "{app}\PARScope.exe"
Name: "{group}\Traffic Probe"; Filename: "{app}\TrafficProbe.exe"
Name: "{group}\Uninstall vibe PAR"; Filename: "{uninstallexe}"
Name: "{autodesktop}\vibe PAR"; Filename: "{app}\PARScope.exe"; Tasks: desktopicon

[Run]
Filename: "{app}\PARScope.exe"; Description: "{cm:LaunchProgram,vibe PAR}"; Flags: nowait postinstall skipifsilent
; After an in-app update the installer runs silently; start the scope again.
Filename: "{app}\PARScope.exe"; Flags: nowait; Check: IsUpdate

[Code]
const
  NpcapFallback = 'npcap-1.90.exe';

var
  PluginPage: TInputDirWizardPage;
  DownloadPage: TDownloadWizardPage;
  NpcapFile: String;

function IsUpdate: Boolean;
begin
  Result := Pos('/UPDATE', UpperCase(GetCmdTail)) > 0;
end;

{ vPilot installs per user under %LOCALAPPDATA%\vPilot; look there and in its uninstall entry. }
function DetectVPilot: String;
var
  Names: TArrayOfString;
  I: Integer;
  Name, Location: String;
begin
  Result := '';
  if FileExists(ExpandConstant('{localappdata}\vPilot\vPilot.exe')) then
  begin
    Result := ExpandConstant('{localappdata}\vPilot');
    Exit;
  end;
  if RegGetSubkeyNames(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
      if RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + Names[I], 'DisplayName', Name)
         and (Pos('vpilot', Lowercase(Name)) > 0)
         and RegQueryStringValue(HKCU, 'Software\Microsoft\Windows\CurrentVersion\Uninstall\' + Names[I], 'InstallLocation', Location)
         and DirExists(Location) then
      begin
        Result := RemoveBackslashUnlessRoot(Location);
        Exit;
      end;
end;

function NpcapInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{sys}\Npcap\wpcap.dll'))
    or RegKeyExists(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst')
    or RegKeyExists(HKLM32, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\NpcapInst');
end;

function NpcapWanted: Boolean;
begin
  Result := not IsUpdate and not NpcapInstalled;
end;

{ The current installer's name from npcap.com, e.g. npcap-1.90.exe; the pinned one if the page can't be read. }
function LatestNpcap: String;
var
  Page: AnsiString;
  I, J: Integer;
begin
  Result := NpcapFallback;
  try
    if DownloadTemporaryFile('https://npcap.com/', 'npcap-home.html', '', nil) > 0 then
      if LoadStringFromFile(ExpandConstant('{tmp}\npcap-home.html'), Page) then
      begin
        I := Pos('dist/npcap-', Page);
        if I > 0 then
        begin
          I := I + Length('dist/');
          J := I;
          while (J <= Length(Page)) and (Pos(Page[J], '0123456789.-npcaexNPCAEX') > 0) do J := J + 1;
          if Copy(Page, J - 4, 4) = '.exe' then Result := Copy(Page, I, J - I);
        end;
      end;
  except
    Log('Could not read npcap.com: ' + GetExceptionMessage);
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not WizardIsTaskSelected('npcap') then Exit;
  NpcapFile := LatestNpcap;
  DownloadPage.Clear;
  DownloadPage.Add('https://npcap.com/dist/' + NpcapFile, NpcapFile, '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox('Npcap could not be downloaded (' + GetExceptionMessage + ').' + #13#10 +
          'vibe PAR will still be installed; you can install Npcap later from npcap.com.', mbInformation, MB_OK, IDOK);
      NpcapFile := '';
    end;
  finally
    DownloadPage.Hide;
  end;
end;

procedure CurStepChanged(CurStep: TSetupStep);
var
  Code: Integer;
begin
  if (CurStep <> ssPostInstall) or (NpcapFile = '') then Exit;
  WizardForm.StatusLabel.Caption := 'Installing Npcap (follow its setup window)...';
  { Npcap's installer asks for administrator rights itself; ShellExec lets Windows show the prompt. }
  if not ShellExec('', ExpandConstant('{tmp}\' + NpcapFile), '', '', SW_SHOW, ewWaitUntilTerminated, Code) then
    SuppressibleMsgBox('Npcap setup did not start: ' + SysErrorMessage(Code) + '. You can install it later from npcap.com.',
      mbInformation, MB_OK, IDOK);
end;

procedure InitializeWizard;
var
  VPilot, Default: String;
begin
  PluginPage := CreateInputDirPage(wpSelectTasks,
    'vPilot plugin', 'Where should the vPilot plugin be installed?',
    'The plugin goes in vPilot''s Plugins folder. Close vPilot before installing; it loads the plugin at start-up.',
    False, '');
  PluginPage.Add('vPilot Plugins folder:');
  DownloadPage := CreateDownloadPage('Downloading Npcap', 'Getting the current Npcap installer from npcap.com.', nil);
  VPilot := DetectVPilot;
  if VPilot <> '' then Default := VPilot + '\Plugins'
  else Default := ExpandConstant('{localappdata}\vPilot\Plugins');
  PluginPage.Values[0] := GetPreviousData('PluginDir', Default);
  if VPilot = '' then
    PluginPage.SubCaptionLabel.Caption := PluginPage.SubCaptionLabel.Caption + #13#10#13#10 +
      'vPilot was not found. Install vPilot first, or browse to its Plugins folder.';
end;

function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = PluginPage.ID) and not WizardIsTaskSelected('plugin');
end;

function PluginDir(Param: String): String;
begin
  Result := PluginPage.Values[0];
end;

procedure RegisterPreviousData(PreviousDataKey: Integer);
begin
  SetPreviousData(PreviousDataKey, 'PluginDir', PluginPage.Values[0]);
end;
