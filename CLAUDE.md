# AudioRecorder Pro - 프로젝트 설정

## 프로젝트 정보
- **이름**: AudioRecorder Pro
- **설명**: Windows 11용 고품질 오디오 녹음기 (마이크 + 시스템 오디오 동시 녹음)
- **기술 스택**: C# .NET 8, WPF (MVVM), NAudio

## 프로젝트 경로
- **소스**: `h:\Claude_work\audio\src\AudioRecorder\AudioRecorder\`
- **출력**: `bin\Debug\net8.0-windows\` 또는 `bin\Release\net8.0-windows\`

## 빌드 명령어
```bash
# Debug 빌드
cd "h:\Claude_work\audio\src\AudioRecorder\AudioRecorder" && dotnet build

# Release 빌드
cd "h:\Claude_work\audio\src\AudioRecorder\AudioRecorder" && dotnet build -c Release

# 실행
cd "h:\Claude_work\audio\src\AudioRecorder\AudioRecorder" && dotnet run
```

## 주요 기능
- 🎙️ 마이크 녹음 (WASAPI Capture)
- 🔊 시스템 오디오 녹음 (WASAPI Loopback)
- 🔀 동시 녹음 + 믹싱 (SyncManager 기반 드리프트 보정)
- ▶️ 내장 오디오 플레이어
- 🎵 MP3 변환 (FFmpeg 필요)
- ⌨️ 키보드 단축키 (Ctrl+R/P/S/O)
- 🔔 시스템 트레이 아이콘

## FFmpeg 설정
FFmpeg가 다음 위치에 설치되어 있습니다:
- **전역**: `C:\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe`
- **앱 폴더**: `bin\Debug\net8.0-windows\ffmpeg.exe`

MP3 변환 기능을 사용하려면 ffmpeg.exe가 앱 폴더나 PATH에 있어야 합니다.

## 키보드 단축키
| 단축키 | 기능 |
|--------|------|
| Ctrl+R | 녹음 시작/정지 |
| Ctrl+P | 일시정지/재개 |
| Ctrl+S | 녹음 정지 |
| Ctrl+O | 출력 폴더 열기 |
| Esc | 재생 정지 |

## 출력 사양
- **WAV**: PCM 16-bit, 48kHz, Stereo
- **MP3**: 192kbps (FFmpeg 변환)

## 파일 구조
```
src/AudioRecorder/AudioRecorder/
├── Audio/                  # 오디오 처리 클래스
│   ├── AudioMixer.cs       # 오디오 믹싱
│   ├── AudioPlayer.cs      # 재생기
│   ├── DeviceManager.cs    # 장치 관리
│   ├── LevelMeter.cs       # 레벨 측정
│   ├── RecordingEngine.cs  # 녹음 엔진
│   ├── RingBuffer.cs       # 링 버퍼
│   └── SyncManager.cs      # 싱크 보정
├── Converters/             # XAML 컨버터
├── Models/                 # 데이터 모델
├── Services/               # 서비스 (트레이, MP3)
├── ViewModels/             # MVVM ViewModel
├── Views/                  # WPF 뷰
└── AudioRecorder.csproj
```

## 설정 저장 경로
- **설정 파일**: `%APPDATA%\AudioRecorder\settings.json`
- **최근 파일 목록**: `%APPDATA%\AudioRecorder\recent_files.json`
