; Shima Windows Installer (Inno Setup 6)
; Supports dual-RID packaging via preprocessor defines:
;   /DAppVersion=1.0.0 /DAppFileVersion=1.0.0.0 /DRid=win-x64 /DSourceDir=... /DOutputDir=...

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

#ifndef AppFileVersion
  #define AppFileVersion "1.0.0.0"
#endif

#ifndef Rid
  #define Rid "win-x64"
#endif

#ifndef SourceDir
  #define SourceDir "..\..\artifacts\publish\" + Rid
#endif

#ifndef OutputDir
  #define OutputDir "..\..\artifacts\releases\" + Rid
#endif

#ifndef SetupIcon
  #define SetupIcon "..\..\src\Shima.App\Assets\Shima.ico"
#endif

#define AppName "Shima"
#define AppPublisher "Shima"
#define AppExeName "Shima.App.exe"
#define AppUrl "https://github.com/chrstn-922/shima"

#if Rid == "win-arm64"
  #define ArchAllowed "arm64"
  #define Arch64BitMode "arm64"
#elif Rid == "win-x64"
  #define ArchAllowed "x64compatible"
  #define Arch64BitMode "x64compatible"
#else
  #error Unsupported Rid define. Expected 'win-x64' or 'win-arm64'.
#endif

[Setup]
AppId={{8F6D7C2A-4D53-4E7A-9B3C-2C8E4B7F1E9A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}/releases
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed={#ArchAllowed}
ArchitecturesInstallIn64BitMode={#Arch64BitMode}
MinVersion=10.0.17763
OutputDir={#OutputDir}
OutputBaseFilename=Shima-Setup-{#AppVersion}-{#Rid}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
SetupIconFile={#SetupIcon}
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
CloseApplications=yes
CloseApplicationsFilter=Shima.App.exe,Shima.AgentBridge.exe
RestartApplications=no
VersionInfoVersion={#AppFileVersion}
VersionInfoTextVersion={#AppVersion}
VersionInfoCompany={#AppPublisher}
VersionInfoProductName={#AppName}
VersionInfoDescription=Shima Installer ({#Rid})

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#SourceDir}\Assets\*.png"; DestDir: "{app}\Assets"; Flags: ignoreversion
Source: "{#SourceDir}\resources.pri"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Run]
; Step 1 (Elevated): If the bundled Sparse MSIX is signed with a self-signed certificate not yet trusted,
; trust its signer certificate in LocalMachine\TrustedPeople (and LocalMachine\Root when self-signed).
; Runs only on Windows 10 2004 (Build 19041) or newer and never aborts setup if unsigned or restricted.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\sparse-identity\register-sparse-identity.ps1"" -TrustEmbeddedCert -TrustOnly -BestEffort -PackagePath ""{app}\sparse-identity\Shima.Identity-{#Rid}.msix"""; \
  StatusMsg: "Preparing Shima Notification Inbox identity certificate..."; \
  Check: IsSparseIdentitySupportedOs; \
  Flags: runhidden waituntilterminated

; Step 2 (Original logged-in user): Register Sparse MSIX identity against {app} so Add-AppxPackage registers
; for the active desktop user rather than an elevated admin token. Best-effort so setup completes cleanly
; even if policy blocks sideloading or the MSIX is unsigned.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\sparse-identity\register-sparse-identity.ps1"" -BestEffort -PackagePath ""{app}\sparse-identity\Shima.Identity-{#Rid}.msix"" -ExternalLocation ""{app}"""; \
  StatusMsg: "Registering Shima Notification Inbox identity..."; \
  Check: IsSparseIdentitySupportedOs; \
  Flags: runasoriginaluser runhidden waituntilterminated

; Step 3 (Finish page, checked by default): Launch Shima after registration under the logged-in user.
Filename: "{app}\{#AppExeName}"; \
  Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; \
  Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Unregister the Sparse MSIX identity before deleting files while preserving %LOCALAPPDATA%\Shima user data.
; Inno Setup does not allow runasoriginaluser in [UninstallRun]; register-sparse-identity.ps1 handles
; current-user and elevated -AllUsers removal cleanly.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; \
  Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\sparse-identity\register-sparse-identity.ps1"" -Unregister -BestEffort"; \
  RunOnceId: "UnregisterShimaSparseIdentity"; \
  Check: HasUnregisterScript; \
  Flags: runhidden waituntilterminated

[Code]
function IsSparseIdentitySupportedOs(): Boolean;
var
  Version: TWindowsVersion;
begin
  GetWindowsVersionEx(Version);
  Result := (Version.Major > 10) or ((Version.Major = 10) and (Version.Build >= 19041));
end;

function HasUnregisterScript(): Boolean;
begin
  Result := FileExists(ExpandConstant('{app}\sparse-identity\register-sparse-identity.ps1'));
end;

procedure StopRunningShima();
var
  ResultCode: Integer;
begin
  if Exec(ExpandConstant('{sys}\taskkill.exe'), '/IM Shima.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) then
  begin
    if ResultCode = 0 then
    begin
      Sleep(800);
    end;
  end;

  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Shima.App.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM Shima.AgentBridge.exe', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(250);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopRunningShima();
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usUninstall then
  begin
    StopRunningShima();
  end;
end;
