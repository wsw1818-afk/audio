; AudioRecorder Pro - Inno Setup Script
; 이 스크립트로 설치 파일을 생성합니다.

#define MyAppName "AudioRecorder Pro"
#define MyAppVersion "1.2.0"
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
OutputDir=D:\OneDrive\코드작업\결과물\audio2
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

[Types]
Name: "full"; Description: "전체 설치 (GPU 가속 포함)"
Name: "compact"; Description: "기본 설치 (CPU만)"
Name: "custom"; Description: "사용자 지정"; Flags: iscustom

[Components]
Name: "main"; Description: "AudioRecorder Pro (필수)"; Types: full compact custom; Flags: fixed
Name: "ffmpeg"; Description: "FFmpeg (오디오 변환/화자분리)"; Types: full compact custom; Flags: fixed
Name: "whisper"; Description: "Whisper STT (음성→텍스트, CPU)"; Types: full compact custom; Flags: fixed
Name: "cuda"; Description: "NVIDIA GPU 가속 (CUDA, ~640MB)"; Types: full

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 옵션:"

[Files]
; 앱 메인 파일 (SingleFile publish)
Source: "..\src\AudioRecorder\AudioRecorder\publish\AudioRecorder.exe"; DestDir: "{app}"; Components: main; Flags: ignoreversion
Source: "..\src\AudioRecorder\AudioRecorder\publish\AudioRecorder.pdb"; DestDir: "{app}"; Components: main; Flags: ignoreversion

; 리소스 폴더 (언어팩 등)
Source: "..\src\AudioRecorder\AudioRecorder\publish\cs\*"; DestDir: "{app}\cs"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\de\*"; DestDir: "{app}\de"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\es\*"; DestDir: "{app}\es"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\fr\*"; DestDir: "{app}\fr"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\it\*"; DestDir: "{app}\it"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\ja\*"; DestDir: "{app}\ja"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\ko\*"; DestDir: "{app}\ko"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\pl\*"; DestDir: "{app}\pl"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\pt-BR\*"; DestDir: "{app}\pt-BR"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\ru\*"; DestDir: "{app}\ru"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\tr\*"; DestDir: "{app}\tr"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\zh-Hans\*"; DestDir: "{app}\zh-Hans"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\zh-Hant\*"; DestDir: "{app}\zh-Hant"; Components: main; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; FFmpeg
Source: "..\src\AudioRecorder\AudioRecorder\publish\ffmpeg.exe"; DestDir: "{app}"; Components: ffmpeg; Flags: ignoreversion

; Whisper CPU
Source: "..\src\AudioRecorder\AudioRecorder\publish\whisper-cli.exe"; DestDir: "{app}"; Components: whisper; Flags: ignoreversion
Source: "..\src\AudioRecorder\AudioRecorder\publish\whisper.dll"; DestDir: "{app}"; Components: whisper; Flags: ignoreversion
Source: "..\src\AudioRecorder\AudioRecorder\publish\ggml-base.dll"; DestDir: "{app}"; Components: whisper; Flags: ignoreversion
Source: "..\src\AudioRecorder\AudioRecorder\publish\ggml-cpu.dll"; DestDir: "{app}"; Components: whisper; Flags: ignoreversion
Source: "..\src\AudioRecorder\AudioRecorder\publish\ggml.dll"; DestDir: "{app}"; Components: whisper; Flags: ignoreversion

; Whisper CUDA (GPU 가속)
Source: "..\src\AudioRecorder\AudioRecorder\publish\whisper-cuda\*"; DestDir: "{app}\whisper-cuda"; Components: cuda; Flags: ignoreversion

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
