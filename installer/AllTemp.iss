; AllTemp installer (Inno Setup 6). Built by build.ps1, which passes AppVersion, AppExe and PawnIoSetup.

#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif

[Setup]
AppId={{7A3C1E52-4B8D-4C2B-9E61-2F0A9D5C7B13}
AppName=AllTemp
AppVersion={#AppVersion}
AppVerName=AllTemp {#AppVersion}
AppPublisher=Imtiaj Sajin
AppPublisherURL=https://github.com/Imtiaj-Sajin/all-temp
AppSupportURL=https://github.com/Imtiaj-Sajin/all-temp/issues
DefaultDirName={autopf}\AllTemp
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
OutputDir=..\dist
OutputBaseFilename=AllTemp-Setup-{#AppVersion}
SetupIconFile=..\assets\AllTemp.ico
UninstallDisplayIcon={app}\AllTemp.exe
UninstallDisplayName=AllTemp
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "driver"; Description: "Install the PawnIO sensor driver (free, signed, open source; needed for CPU, RAM and motherboard temperatures)"; GroupDescription: "Sensor driver:"; Check: not PawnIoInstalled
Name: "autostart"; Description: "Start AllTemp when I sign in"; GroupDescription: "Options:"
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "Options:"; Flags: unchecked

[Files]
Source: "{#AppExe}"; DestDir: "{app}"; DestName: "AllTemp.exe"; Flags: ignoreversion
Source: "{#PawnIoSetup}"; DestDir: "{tmp}"; Flags: deleteafterinstall; Tasks: driver

[Icons]
Name: "{autoprograms}\AllTemp"; Filename: "{app}\AllTemp.exe"
Name: "{autodesktop}\AllTemp"; Filename: "{app}\AllTemp.exe"; Tasks: desktopicon

[Registry]
; Remember an unticked driver box so the app doesn't ask again on first launch.
Root: HKCU; Subkey: "Software\AllTemp"; ValueType: dword; ValueName: "DriverDeclined"; ValueData: 1; Tasks: not driver; Check: not PawnIoInstalled; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\AllTemp"; ValueType: none; ValueName: "DriverDeclined"; Flags: deletevalue uninsdeletekey; Tasks: driver

[Run]
Filename: "{tmp}\windowsdesktop-runtime.exe"; Parameters: "/install /quiet /norestart"; StatusMsg: "Installing Microsoft .NET 8 Desktop Runtime..."; Check: not DotNetDesktop8Installed; Flags: waituntilterminated
Filename: "{tmp}\PawnIO_setup.exe"; Parameters: "-install -silent"; StatusMsg: "Installing the PawnIO sensor driver..."; Tasks: driver; Flags: runhidden waituntilterminated
Filename: "{app}\AllTemp.exe"; Parameters: "--autostart on"; StatusMsg: "Setting up start with Windows..."; Tasks: autostart; Flags: runhidden waituntilterminated
; postinstall entries default to the original (non-admin) user, which can't start a requireAdministrator exe (error 740).
Filename: "{app}\AllTemp.exe"; Description: "Launch AllTemp"; Flags: nowait postinstall skipifsilent runascurrentuser

[UninstallRun]
Filename: "{sys}\taskkill.exe"; Parameters: "/f /im AllTemp.exe"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"
Filename: "{sys}\schtasks.exe"; Parameters: "/Delete /TN AllTemp /F"; Flags: runhidden waituntilterminated; RunOnceId: "RemoveLogonTask"

[UninstallDelete]
Type: filesandordirs; Name: "{userappdata}\AllTemp"

[Messages]
FinishedLabel=AllTemp is installed. It lives in the system tray; right-click the window or the tray icon for options.%n%nThe PawnIO driver stays installed if you uninstall AllTemp (other monitoring apps share it). Remove it from Settings > Apps if you no longer need it.

[Code]
const
  RuntimeUrl = 'https://aka.ms/dotnet/8.0/windowsdesktop-runtime-win-x64.exe';

var
  DownloadPage: TDownloadWizardPage;

function PawnIoInstalled: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\PawnIO');
end;

function DotNetDesktop8Installed: Boolean;
var
  FindRec: TFindRec;
begin
  Result := FindFirst(ExpandConstant('{commonpf64}\dotnet\shared\Microsoft.WindowsDesktop.App\8.*'), FindRec);
  if Result then
    FindClose(FindRec);
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage('Downloading .NET 8 Desktop Runtime',
    'AllTemp needs the Microsoft .NET 8 Desktop Runtime, which is not on this PC yet.', nil);
end;

function UpdateReadyMemo(Space, NewLine, MemoUserInfoInfo, MemoDirInfo, MemoTypeInfo, MemoComponentsInfo, MemoGroupInfo, MemoTasksInfo: String): String;
begin
  Result := MemoDirInfo + NewLine + NewLine;
  if MemoTasksInfo <> '' then
    Result := Result + MemoTasksInfo + NewLine + NewLine;
  if not DotNetDesktop8Installed then
    Result := Result + 'Also downloads and installs:' + NewLine + Space + 'Microsoft .NET 8 Desktop Runtime (about 56 MB, from microsoft.com)';
end;

// Download the runtime after the user clicks Install, only when it is missing.
function NextButtonClick(CurPageID: Integer): Boolean;
begin
  Result := True;
  if (CurPageID = wpReady) and not DotNetDesktop8Installed then
  begin
    DownloadPage.Clear;
    DownloadPage.Add(RuntimeUrl, 'windowsdesktop-runtime.exe', '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Log('Runtime download cancelled.')
        else
          SuppressibleMsgBox('Could not download the .NET 8 Desktop Runtime:' + #13#10 + GetExceptionMessage + #13#10#13#10 +
            'Check your internet connection, or install it from https://dotnet.microsoft.com/download/dotnet/8.0 and run setup again.',
            mbCriticalError, MB_OK, IDOK);
        Result := False;
      end;
    finally
      DownloadPage.Hide;
    end;
  end;
end;

// Close a running copy so its exe can be replaced.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Code: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /im AllTemp.exe', '', SW_HIDE, ewWaitUntilTerminated, Code);
  Result := '';
end;
