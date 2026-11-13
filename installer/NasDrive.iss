; Inno Setup script for NasDrive. Build on Windows with Inno Setup 6:
;   iscc /DArch=x64 /DVersion=0.3.1 installer\NasDrive.iss
; Expects dist\win-x64\ (from scripts/build.sh) and installer\deps\winfsp.msi
; (download the latest release from https://github.com/winfsp/winfsp/releases).

#ifndef Version
  #define Version "0.3.1"
#endif
#ifndef Arch
  #define Arch "x64"
#endif

[Setup]
AppId={{6B4C9F0E-7E6B-4B7A-9A1E-4E1D5C2B8A10}
AppName=NasDrive
AppVersion={#Version}
AppPublisher=NasDrive contributors
AppPublisherURL=https://github.com/JoshuaHKU/NasDrive-For-Win11
DefaultDirName={autopf}\NasDrive
DefaultGroupName=NasDrive
DisableProgramGroupPage=yes
OutputDir=..\dist
OutputBaseFilename=NasDrive-{#Version}-{#Arch}-setup
SetupIconFile=..\gui\NasDrive\Assets\NasDrive.ico
UninstallDisplayIcon={app}\NasDrive.exe
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
; WinFsp needs admin rights to install its driver.
PrivilegesRequired=admin
#if Arch == "x64"
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
#else
ArchitecturesAllowed=arm64
ArchitecturesInstallIn64BitMode=arm64
#endif
LicenseFile=..\LICENSE
CloseApplications=yes

[Languages]
Name: "chinesesimplified"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "autostart"; Description: "开机自动启动 / Start with Windows"; GroupDescription: "{cm:AdditionalIcons}"

[Files]
Source: "..\dist\win-{#Arch}\NasDrive.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\win-{#Arch}\nasdrive-engine.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\dist\win-{#Arch}\*.dll"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist
Source: "..\LICENSE"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\THIRD-PARTY-NOTICES.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "deps\winfsp.msi"; Flags: dontcopy

[Icons]
Name: "{group}\NasDrive"; Filename: "{app}\NasDrive.exe"
Name: "{autodesktop}\NasDrive"; Filename: "{app}\NasDrive.exe"; Tasks: desktopicon

[Registry]
; Per user autostart is managed by the app itself; this only seeds it.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "NasDrive"; \
  ValueData: """{app}\NasDrive.exe"" --minimized"; Tasks: autostart; Flags: uninsdeletevalue

[Run]
Filename: "{app}\NasDrive.exe"; Description: "{cm:LaunchProgram,NasDrive}"; Flags: nowait postinstall skipifsilent runasoriginaluser

[UninstallRun]
; Unmount all drives and stop the engine before files are removed.
Filename: "{cmd}"; Parameters: "/c taskkill /f /im NasDrive.exe & taskkill /f /im nasdrive-engine.exe"; Flags: runhidden; RunOnceId: "StopNasDrive"

[Code]
function WinFspInstalled(): Boolean;
var
  Dir: String;
begin
  Result := RegQueryStringValue(HKLM, 'SOFTWARE\WOW6432Node\WinFsp', 'InstallDir', Dir) or
            RegQueryStringValue(HKLM, 'SOFTWARE\WinFsp', 'InstallDir', Dir);
end;

// Installs WinFsp before NasDrive's files are copied and fails the setup if it
// does not succeed, instead of silently continuing without a working driver.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  Msi, LogFile: String;
  Code: Integer;
begin
  Result := '';
  // A running NasDrive ignores close requests (it hides to the tray) and keeps
  // its files locked, so stop it and the engine before files are replaced.
  // Mounted drives disappear; NasDrive reconnects auto-connect drives on start.
  Exec(ExpandConstant('{cmd}'), '/c taskkill /f /im NasDrive.exe & taskkill /f /im nasdrive-engine.exe', '',
       SW_HIDE, ewWaitUntilTerminated, Code);
  Sleep(1500);
  if WinFspInstalled() then Exit;
  ExtractTemporaryFile('winfsp.msi');
  Msi := ExpandConstant('{tmp}\winfsp.msi');
  LogFile := ExpandConstant('{%TEMP}\NasDrive-winfsp-install.log');
  WizardForm.PreparingLabel.Caption := 'Installing WinFsp...';
  if not Exec('msiexec.exe', '/i "' + Msi + '" /qn /norestart /l*v "' + LogFile + '"', '',
              SW_HIDE, ewWaitUntilTerminated, Code) then
    Code := -1;
  Log(Format('WinFsp msiexec exit code %d, log %s', [Code, LogFile]));
  if Code = 3010 then
    NeedsRestart := True
  else if (Code <> 0) or not WinFspInstalled() then
    Result := Format('WinFsp 安装失败（错误码 %d）。NasDrive 需要 WinFsp 才能映射盘符。'#13#10 +
                     'WinFsp installation failed (code %d). Log: %s', [Code, Code, LogFile]);
end;
