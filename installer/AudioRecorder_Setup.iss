; AudioRecorder Pro - Inno Setup Script

#define MyAppName "AudioRecorder Pro"
#define MyAppVersion "1.2.1"
#define MyAppPublisher "AudioRecorder"
#define MyAppURL "https://github.com/wsw1818-afk/audio"
#define MyAppExeName "AudioRecorder.exe"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes

OutputDir=D:\OneDrive\코드작업\결과물\audio2
OutputBaseFilename=AudioRecorder_Setup_{#MyAppVersion}
Compression=lzma2/ultra64
SolidCompression=yes

PrivilegesRequired=admin

WizardStyle=modern
WizardSizePercent=100
MinVersion=10.0

[Languages]
Name: "korean"; MessagesFile: "compiler:Languages\Korean.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: "startup"; Description: "Windows 시작 시 자동 실행"; GroupDescription: "추가 옵션:"

[Files]
; 앱 메인 파일
Source: "..\src\AudioRecorder\AudioRecorder\publish\AudioRecorder.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\src\AudioRecorder\AudioRecorder\publish\AudioRecorder.pdb"; DestDir: "{app}"; Flags: ignoreversion

; 리소스 폴더 (언어팩 등)
Source: "..\src\AudioRecorder\AudioRecorder\publish\cs\*"; DestDir: "{app}\cs"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\de\*"; DestDir: "{app}\de"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\es\*"; DestDir: "{app}\es"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\fr\*"; DestDir: "{app}\fr"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\it\*"; DestDir: "{app}\it"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\ja\*"; DestDir: "{app}\ja"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\ko\*"; DestDir: "{app}\ko"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\pl\*"; DestDir: "{app}\pl"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\pt-BR\*"; DestDir: "{app}\pt-BR"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\ru\*"; DestDir: "{app}\ru"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\tr\*"; DestDir: "{app}\tr"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\zh-Hans\*"; DestDir: "{app}\zh-Hans"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist
Source: "..\src\AudioRecorder\AudioRecorder\publish\zh-Hant\*"; DestDir: "{app}\zh-Hant"; Flags: ignoreversion recursesubdirs createallsubdirs skipifsourcedoesntexist

; FFmpeg
Source: "..\src\AudioRecorder\AudioRecorder\publish\ffmpeg.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#MyAppName}"; ValueData: """{app}\{#MyAppExeName}"""; Flags: uninsdeletevalue; Tasks: startup

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(MyAppName, '&', '&&')}}"; Flags: nowait postinstall skipifsilent

