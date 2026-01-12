; AudioRecorder Pro - Inno Setup Script
; 이 스크립트로 설치 파일을 생성합니다.

#define MyAppName "AudioRecorder Pro"
#define MyAppVersion "1.0.0"
#define MyAppPublisher "AudioRecorder"
#define MyAppURL "https://github.com/wsw1818-afk/audio"
#define MyAppExeName "AudioRecorder.exe"

[Setup]
; 앱 정보
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

; 설치 경로
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

; 출력 설정
OutputDir=..\output
OutputBaseFilename=AudioRecorder_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

; 권한 설정
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog

; UI 설정
WizardStyle=modern
WizardSizePercent=100

; 최소 Windows 버전 (Windows 10 이상)
MinVersion=10.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 옵션:"

[Files]
; 앱 파일들
Source: "..\src\AudioRecorder\AudioRecorder\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

; FFmpeg (선택적 - 앱 폴더에 있으면 포함)
Source: "..\src\AudioRecorder\AudioRecorder\publish\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
; 시작 메뉴
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"

; 바탕화면 아이콘
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; 시작 프로그램 등록
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
; 설치 후 실행 옵션
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

[Code]
// .NET 8 Desktop Runtime 체크
function IsDotNet8DesktopInstalled: Boolean;
var
  ResultCode: Integer;
begin
  // dotnet --list-runtimes로 확인
  Result := Exec('dotnet', '--list-runtimes', '', SW_HIDE, ewWaitUntilTerminated, ResultCode) and (ResultCode = 0);

  // 더 정확한 체크를 위해 레지스트리 확인
  if not Result then
    Result := RegKeyExists(HKLM, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64\sharedfx\Microsoft.WindowsDesktop.App\8.0');
end;

function InitializeSetup(): Boolean;
var
  ErrorCode: Integer;
begin
  Result := True;

  // .NET 8 Desktop Runtime 체크
  if not IsDotNet8DesktopInstalled() then
  begin
    if MsgBox('.NET 8 Desktop Runtime이 설치되어 있지 않습니다.' + #13#10 + #13#10 +
              '지금 다운로드 페이지를 열어서 설치하시겠습니까?' + #13#10 +
              '(설치 후 이 설치 프로그램을 다시 실행하세요)',
              mbConfirmation, MB_YESNO) = IDYES then
    begin
      ShellExec('open', 'https://dotnet.microsoft.com/download/dotnet/8.0', '', '', SW_SHOWNORMAL, ewNoWait, ErrorCode);
    end;
    Result := False;
  end;
end;
