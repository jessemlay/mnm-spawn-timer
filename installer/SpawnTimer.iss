; Spawn Timer installer (Inno Setup 6.1+).
; Installs the small framework-dependent exe and, if needed, downloads and installs the
; .NET 9 Desktop Runtime from Microsoft first.
;
; Built by CI:  ISCC /DAppVersion=1.2.0 /DNumericVersion=1.2.0.0 /DSourceDir=<publish dir> /DOutputDir=<dir> SpawnTimer.iss
; Test switch:  SpawnTimer-Setup.exe /FORCEDOTNET=1   runs the runtime download/install even if it's already present.

#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#ifndef NumericVersion
  #define NumericVersion "0.0.0.0"
#endif
#ifndef SourceDir
  #define SourceDir "..\out\small"
#endif
#ifndef OutputDir
  #define OutputDir "..\dist"
#endif

#define AppName "Spawn Timer"
#define AppExe "SpawnTimer.exe"
#define RepoUrl "https://github.com/jessemlay/mnm-spawn-timer"
; Microsoft's permanent link to the latest .NET 9 Desktop Runtime (x64).
#define DotNetUrl "https://aka.ms/dotnet/9.0/windowsdesktop-runtime-win-x64.exe"
#define DotNetFile "windowsdesktop-runtime-9-win-x64.exe"

[Setup]
AppId={{8F3B2C71-5A4E-4D2B-9C1E-6A7D0B9E2F41}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher=jessemlay
AppPublisherURL={#RepoUrl}
AppSupportURL={#RepoUrl}/issues
AppUpdatesURL={#RepoUrl}/releases
VersionInfoVersion={#NumericVersion}
VersionInfoProductName={#AppName}
DefaultDirName={autopf}\Spawn Timer
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename=SpawnTimer-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; Installing the .NET runtime needs admin; the app itself is launched as the normal user (see [Run]).
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayIcon={app}\{#AppExe}
SetupIconFile=..\Assets\SpawnTimer.ico
UninstallDisplayName={#AppName}
; Setup stops the app itself (PrepareToInstall), so skip the Restart Manager prompt.
CloseApplications=no

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Start Spawn Timer when Windows starts"; GroupDescription: "Startup:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "SpawnTimer"; \
  ValueData: """{app}\{#AppExe}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; runasoriginaluser: don't start the overlay elevated just because setup was.
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "StopSpawnTimer"

[Code]
var
  DownloadPage: TDownloadWizardPage;
  DotNetDone: Boolean; { set once the runtime installer has run, so it never runs twice }

function ForceDotNet: Boolean;
begin
  Result := ExpandConstant('{param:FORCEDOTNET|0}') = '1';
end;

{ The runtime installs to %ProgramFiles%\dotnet\shared\Microsoft.WindowsDesktop.App\<version>. }
function DotNetDesktop9Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\9.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

function NeedsDotNet: Boolean;
begin
  Result := not DotNetDone and (ForceDotNet or not DotNetDesktop9Installed);
end;

function OnDownloadProgress(const Url, FileName: String; const Progress, ProgressMax: Int64): Boolean;
begin
  Result := True;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Installing .NET Desktop Runtime 9',
    'Spawn Timer needs the free .NET 9 Desktop Runtime from Microsoft. Downloading it now (about 58 MB)...',
    @OnDownloadProgress);
end;

{ Runs the downloaded runtime installer quietly. Returns '' on success, otherwise an error message. }
function RunDotNetInstaller(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
  Installer: String;
begin
  Result := '';
  Installer := ExpandConstant('{tmp}\{#DotNetFile}');
  Log('Running ' + Installer);
  if not Exec(Installer, '/install /quiet /norestart', '', SW_SHOW, ewWaitUntilTerminated, ResultCode) then
  begin
    Result := 'Could not start the .NET Desktop Runtime installer: ' + SysErrorMessage(ResultCode);
    exit;
  end;
  Log(Format('.NET installer exit code: %d', [ResultCode]));
  DotNetDone := True;
  { 0 = installed, 3010 = installed but needs a reboot, 1638 = a newer/same version is already installed. }
  if ResultCode = 3010 then
    NeedsRestart := True
  else if (ResultCode <> 0) and (ResultCode <> 1638) then
    Result := Format('The .NET Desktop Runtime installer failed (code %d).', [ResultCode]);

  if (Result = '') and not DotNetDesktop9Installed then
    Result := 'The .NET Desktop Runtime 9 still isn''t installed.';

  if Result <> '' then
    Result := Result + #13#10#13#10 + 'Install it from https://dotnet.microsoft.com/download/dotnet/9.0 ' +
      '(".NET Desktop Runtime", x64) and run this setup again.';
end;

{ Interactive installs: download with a progress page when leaving the Ready page. }
function NextButtonClick(CurPageID: Integer): Boolean;
var
  Error: String;
  NeedsRestart: Boolean;
begin
  Result := True;
  if (CurPageID <> wpReady) or not NeedsDotNet then
    exit;

  DownloadPage.Clear;
  DownloadPage.Add('{#DotNetUrl}', '{#DotNetFile}', '');
  DownloadPage.Show;
  try
    try
      DownloadPage.Download;
      DownloadPage.SetText('Installing .NET Desktop Runtime 9', 'This usually takes under a minute...');
      DownloadPage.ProgressBar.Style := npbstMarquee;
      NeedsRestart := False;
      Error := RunDotNetInstaller(NeedsRestart);
      if Error <> '' then
      begin
        SuppressibleMsgBox(Error, mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    except
      if not DownloadPage.AbortedByUser then
        SuppressibleMsgBox('Downloading the .NET Desktop Runtime failed: ' + AddPeriod(GetExceptionMessage) + #13#10#13#10 +
          'Check your internet connection and try again.', mbCriticalError, MB_OK, IDOK);
      Result := False;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

{ Stops a running copy before files are replaced. Also a fallback for the runtime in case the Ready page step
  didn't run it (Inno normally calls NextButtonClick even in silent mode; DotNetDone prevents a second run). }
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  Result := '';
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);

  if WizardSilent and NeedsDotNet then
  begin
    try
      DownloadTemporaryFile('{#DotNetUrl}', '{#DotNetFile}', '', nil);
    except
      Result := 'Downloading the .NET Desktop Runtime failed: ' + GetExceptionMessage;
      exit;
    end;
    Result := RunDotNetInstaller(NeedsRestart);
  end;
end;
