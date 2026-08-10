; Inno Setup script for the eslee QuickSend per-user Windows installer.
;
; Build:
;   "%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\eslee-quicksend.iss
;
; The app stays an unpackaged, self-contained WinUI 3 build; this installer only places
; the existing publish folder somewhere stable and registers shortcuts. User data lives
; under %LOCALAPPDATA%\eslee\QuickSend, outside the install directory, so updating or
; uninstalling never touches the database, certificate or trusted devices.

#define AppName "eslee QuickSend"
#define AppVersion "0.0.2"
#define AppPublisher "eslee"
#define AppExeName "eslee QuickSend.exe"
#define PublishDir "..\src\QuickSend.Windows\bin\Release\net10.0-windows10.0.26100.0\win-x64\publish"

[Setup]
AppId={{7E4C1B6A-3F58-4C2E-9D7B-4A1E6C0F2B93}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion=0.0.2.0
VersionInfoProductName={#AppName}
VersionInfoCompany={#AppPublisher}
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=no
; Per-user install: no administrator prompt and no machine-wide changes.
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\artifacts\installer
OutputBaseFilename=eslee-QuickSend-Windows-x64-v{#AppVersion}-Setup
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
SetupIconFile=eslee-quicksend-setup.ico
UninstallDisplayIcon={app}\{#AppExeName}
UninstallDisplayName={#AppName}
CloseApplications=yes
CloseApplicationsFilter=*.exe
RestartApplications=no

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "바탕화면에 바로가기 만들기"; GroupDescription: "추가 작업:"; Flags: unchecked
Name: "autostart"; Description: "Windows 시작 시 {#AppName} 자동 실행"; GroupDescription: "추가 작업:"

[Files]
; The whole publish folder is required: the EXE alone cannot start without the
; WinUI resources and the self-contained runtime that sit beside it.
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{group}\{#AppName} 제거"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Auto-start is per-user and starts hidden in the tray. Uninstalling removes the value.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; \
    ValueName: "eslee QuickSend"; ValueData: """{app}\{#AppExeName}"" --startup"; \
    Flags: uninsdeletevalue; Tasks: autostart
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: none; \
    ValueName: "eslee QuickSend"; Flags: deletevalue uninsdeletevalue; Tasks: not autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{#AppName} 실행"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
Type: filesandordirs; Name: "{app}"

[Code]
// The default uninstall keeps settings, trusted devices and transfer history. Removing
// user data is an explicit opt-in, because it destroys the device identity that peers
// have already trusted.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  DataDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    DataDir := ExpandConstant('{localappdata}\eslee\QuickSend');
    if DirExists(DataDir) then
    begin
      if MsgBox('설정, 신뢰 기기, 전송 기록도 모두 삭제할까요?' + #13#10 +
                '아니요를 선택하면 프로그램만 제거하고 사용자 데이터는 그대로 둡니다.',
                mbConfirmation, MB_YESNO or MB_DEFBUTTON2) = IDYES then
        DelTree(DataDir, True, True, True);
    end;
  end;
end;
