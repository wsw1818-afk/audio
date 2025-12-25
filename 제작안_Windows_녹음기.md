# Windows 11 고급 녹음기 앱 제작안

**프로젝트명**: AudioRecorder Pro
**버전**: v1.0
**작성일**: 2025-12-23
**개발환경**: Windows 11, C# .NET 8, WPF (MVVM), NAudio

---

## 1. 개요 및 목표

### 1.1 프로젝트 목표
"곰오디오" 수준의 사용성과 안정성을 갖춘 Windows 11 녹음기 앱 개발

### 1.2 핵심 가치
- **듀얼 소스 녹음**: 마이크 + 시스템 오디오 동시 캡처 및 믹싱
- **안정성**: 30분 이상 장시간 녹음에서도 싱크 드리프트 최소화
- **사용 편의성**: 직관적인 UI와 원클릭 녹음

---

## 2. 기능 요구사항

### 2.1 필수 기능 (MVP)

| 기능 | 설명 | 우선순위 |
|------|------|----------|
| 시스템 오디오 녹음 | WASAPI Loopback으로 PC 재생음 캡처 | P0 |
| 마이크 녹음 | WASAPI Capture로 마이크 입력 캡처 | P0 |
| 동시 녹음 + 믹싱 | 두 소스를 실시간 믹싱하여 단일 파일 저장 | P0 |
| 녹음 제어 | Start / Stop / Pause / Resume | P0 |
| 볼륨 조절 | 마이크/시스템 각각 볼륨 슬라이더 | P0 |
| 레벨 미터 | 마이크/시스템 각각 Peak 레벨 실시간 표시 | P0 |
| 타이머 | 녹음 경과 시간 표시 (HH:MM:SS) | P0 |
| 파일 저장 | WAV (PCM 16-bit, 48kHz stereo) 저장 | P0 |
| 저장 경로 선택 | 사용자 지정 폴더 + 자동 파일명 | P0 |

### 2.2 선택 기능 (확장)

| 기능 | 설명 | 우선순위 |
|------|------|----------|
| 장치 선택 | 마이크/출력 장치 드롭다운 목록 | P1 |
| 간단 재생기 | 방금 녹음한 파일 바로 재생 | P1 |
| 최근 파일 목록 | 최근 녹음 5개 표시 + 클릭 재생 | P1 |
| MP3 변환 | FFmpeg 외부 호출로 WAV→MP3 변환 | P2 |
| 시스템 트레이 | 최소화 시 트레이 아이콘 | P2 |
| 단축키 | 전역 단축키 (녹음 시작/중지) | P2 |
| 파형 시각화 | 실시간 오디오 파형 표시 | P3 |

---

## 3. 화면 구성 (와이어프레임)

```
┌─────────────────────────────────────────────────────────────┐
│  🎙️ AudioRecorder Pro                           [_] [□] [X] │
├─────────────────────────────────────────────────────────────┤
│                                                             │
│   ┌─────────────────────────────────────────────────────┐   │
│   │                    00:15:32                         │   │
│   │                  (녹음 경과 시간)                    │   │
│   └─────────────────────────────────────────────────────┘   │
│                                                             │
│   ┌──────────────────────┐  ┌──────────────────────────┐   │
│   │  🎤 마이크           │  │  🔊 시스템 오디오        │   │
│   │  [▓▓▓▓▓▓░░░░] -12dB │  │  [▓▓▓▓▓▓▓▓░░] -6dB     │   │
│   │  볼륨: ────●──── 80% │  │  볼륨: ──────●── 100%   │   │
│   │  [v] 녹음 포함       │  │  [v] 녹음 포함          │   │
│   └──────────────────────┘  └──────────────────────────┘   │
│                                                             │
│   장치 선택:                                                │
│   마이크: [Realtek Microphone ▼]                            │
│   출력:   [Speakers (Realtek) ▼]                            │
│                                                             │
│         ┌─────┐  ┌─────┐  ┌─────┐                          │
│         │ ⏺️  │  │ ⏸️  │  │ ⏹️  │                          │
│         │녹음 │  │일시 │  │정지 │                          │
│         └─────┘  └─────┘  └─────┘                          │
│                                                             │
│   저장 경로: [C:\Users\...\Recordings    ] [📁 변경]        │
│   파일명:    Recording_20251223_153200.wav                  │
│                                                             │
├─────────────────────────────────────────────────────────────┤
│  📂 최근 녹음 파일                                          │
│  ├─ Recording_20251223_150000.wav  (5:32)  [▶️ 재생]        │
│  ├─ Recording_20251223_143000.wav  (12:15) [▶️ 재생]        │
│  └─ Recording_20251223_120000.wav  (3:45)  [▶️ 재생]        │
├─────────────────────────────────────────────────────────────┤
│  상태: 녹음 중... │ WAV 48kHz/16bit/Stereo │ 파일: 25.3 MB  │
└─────────────────────────────────────────────────────────────┘
```

---

## 4. 아키텍처 설계

### 4.1 모듈 구성도

```
┌─────────────────────────────────────────────────────────────────┐
│                         Presentation Layer                       │
│  ┌───────────────┐  ┌───────────────┐  ┌───────────────┐        │
│  │  MainWindow   │  │  ViewModels   │  │  Converters   │        │
│  │   (XAML)      │  │  (MVVM)       │  │               │        │
│  └───────────────┘  └───────────────┘  └───────────────┘        │
├─────────────────────────────────────────────────────────────────┤
│                         Application Layer                        │
│  ┌───────────────┐  ┌───────────────┐  ┌───────────────┐        │
│  │ Recording     │  │ Playback      │  │ Settings      │        │
│  │ Service       │  │ Service       │  │ Service       │        │
│  └───────────────┘  └───────────────┘  └───────────────┘        │
├─────────────────────────────────────────────────────────────────┤
│                         Core Audio Layer                         │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │ Device       │ │ Recording    │ │ Audio        │             │
│  │ Manager      │ │ Engine       │ │ Mixer        │             │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │ Level        │ │ File         │ │ Sync         │             │
│  │ Meter        │ │ Writer       │ │ Manager      │             │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
├─────────────────────────────────────────────────────────────────┤
│                         Infrastructure Layer                     │
│  ┌──────────────┐ ┌──────────────┐ ┌──────────────┐             │
│  │ NAudio       │ │ FFmpeg       │ │ Settings     │             │
│  │ Wrapper      │ │ Wrapper      │ │ Repository   │             │
│  └──────────────┘ └──────────────┘ └──────────────┘             │
└─────────────────────────────────────────────────────────────────┘
```

### 4.2 핵심 모듈 상세

#### DeviceManager
```csharp
// 역할: 오디오 장치 열거, 선택, 변경 감지
public interface IDeviceManager
{
    IReadOnlyList<AudioDevice> GetInputDevices();   // 마이크 목록
    IReadOnlyList<AudioDevice> GetOutputDevices();  // 출력장치 목록
    AudioDevice GetDefaultInputDevice();
    AudioDevice GetDefaultOutputDevice();
    event EventHandler<DeviceChangedEventArgs> DeviceChanged;
}
```

#### RecordingEngine
```csharp
// 역할: 오디오 캡처 핵심 엔진
public interface IRecordingEngine
{
    RecordingState State { get; }
    TimeSpan ElapsedTime { get; }

    void Initialize(RecordingOptions options);
    void Start();
    void Pause();
    void Resume();
    void Stop();

    event EventHandler<AudioDataEventArgs> MicDataAvailable;
    event EventHandler<AudioDataEventArgs> SystemDataAvailable;
    event EventHandler<LevelEventArgs> LevelUpdated;
}
```

#### AudioMixer
```csharp
// 역할: 두 오디오 스트림 실시간 믹싱
public interface IAudioMixer
{
    WaveFormat OutputFormat { get; }
    float MicVolume { get; set; }      // 0.0 ~ 1.0
    float SystemVolume { get; set; }   // 0.0 ~ 1.0

    void AddMicSamples(byte[] data, int count);
    void AddSystemSamples(byte[] data, int count);
    int ReadMixed(byte[] buffer, int offset, int count);
}
```

#### SyncManager (핵심!)
```csharp
// 역할: 두 스트림 간 싱크 드리프트 보정
public interface ISyncManager
{
    void Reset();
    void RecordMicTimestamp(long samplePosition, DateTime captureTime);
    void RecordSystemTimestamp(long samplePosition, DateTime captureTime);
    int CalculateDriftSamples();  // 드리프트 샘플 수 계산
    byte[] GetSilencePadding(int samples);  // 패딩용 무음 생성
}
```

#### FileWriter
```csharp
// 역할: WAV 파일 쓰기 (스레드 세이프)
public interface IFileWriter : IDisposable
{
    string FilePath { get; }
    long BytesWritten { get; }
    TimeSpan Duration { get; }

    void Initialize(string path, WaveFormat format);
    void Write(byte[] data, int offset, int count);
    void Flush();
}
```

---

## 5. 오디오 파이프라인 설계

### 5.1 데이터 흐름

```
┌──────────────────┐     ┌──────────────────┐
│  WasapiCapture   │     │ WasapiLoopback   │
│  (Microphone)    │     │ (System Audio)   │
│  48kHz/32bit/2ch │     │ 48kHz/32bit/2ch  │
└────────┬─────────┘     └────────┬─────────┘
         │                        │
         ▼                        ▼
┌──────────────────┐     ┌──────────────────┐
│  Resampler       │     │  Resampler       │
│  (필요시)         │     │  (필요시)         │
│  → 48kHz/32bit   │     │  → 48kHz/32bit   │
└────────┬─────────┘     └────────┬─────────┘
         │                        │
         ▼                        ▼
┌──────────────────┐     ┌──────────────────┐
│  Volume Control  │     │  Volume Control  │
│  × MicVolume     │     │  × SystemVolume  │
└────────┬─────────┘     └────────┬─────────┘
         │                        │
         ▼                        ▼
┌──────────────────┐     ┌──────────────────┐
│  Level Meter     │     │  Level Meter     │
│  Peak Detection  │     │  Peak Detection  │
└────────┬─────────┘     └────────┬─────────┘
         │                        │
         └──────────┬─────────────┘
                    ▼
         ┌──────────────────┐
         │   Ring Buffer    │
         │  (Sync Manager)  │
         │  드리프트 보정    │
         └────────┬─────────┘
                  ▼
         ┌──────────────────┐
         │   Audio Mixer    │
         │  Mic + System    │
         │  → 단일 스트림    │
         └────────┬─────────┘
                  ▼
         ┌──────────────────┐
         │  Format Convert  │
         │  32bit → 16bit   │
         └────────┬─────────┘
                  ▼
         ┌──────────────────┐
         │  File Writer     │
         │  WAV PCM 16bit   │
         │  48kHz Stereo    │
         └──────────────────┘
```

### 5.2 포맷 통일 전략

| 단계 | 포맷 | 비고 |
|------|------|------|
| 캡처 | 장치 네이티브 (보통 32-bit float) | WASAPI 기본 |
| 내부 처리 | 32-bit float, 48kHz, Stereo | 믹싱 품질 |
| 저장 | 16-bit PCM, 48kHz, Stereo | 호환성 |

### 5.3 싱크 드리프트 해결 전략

**문제**: 마이크와 시스템 오디오의 클록 소스가 다르면 장시간 녹음 시 싱크가 어긋남
**조사 결과**: [NAudio GitHub Issue #710](https://github.com/naudio/NAudio/issues/710)에서 20분 녹음 시 약 1분 드리프트 보고

#### 해결책 1: 공통 타임스탬프 기반 동기화 (채택)

```csharp
// 타임스탬프 기반 동기화
public class TimestampSyncManager
{
    private readonly Stopwatch _masterClock = new();
    private readonly ConcurrentQueue<(long samples, long ticks)> _micTimestamps;
    private readonly ConcurrentQueue<(long samples, long ticks)> _sysTimestamps;

    public void OnMicData(int sampleCount)
    {
        _micTimestamps.Enqueue((_micSamplePosition, _masterClock.ElapsedTicks));
        _micSamplePosition += sampleCount;
    }

    public void OnSystemData(int sampleCount)
    {
        _sysTimestamps.Enqueue((_sysSamplePosition, _masterClock.ElapsedTicks));
        _sysSamplePosition += sampleCount;
    }

    // 드리프트 계산 및 보정
    public int GetDriftCorrection()
    {
        // 마스터 클록 기준으로 두 스트림의 예상 샘플 수 비교
        // 차이가 발생하면 느린 쪽에 무음 패딩 또는 빠른 쪽 샘플 드롭
    }
}
```

#### 해결책 2: Ring Buffer + 정기 보정

```csharp
// 링 버퍼로 일정량 버퍼링 후 정기적으로 싱크 체크
public class SyncedRingBuffer
{
    private const int BUFFER_MS = 500;  // 500ms 버퍼
    private const int SYNC_CHECK_INTERVAL_MS = 5000;  // 5초마다 체크

    // 5초마다 두 버퍼의 타임스탬프 비교
    // 드리프트 발생 시 샘플 단위로 미세 조정
}
```

#### 해결책 3: Silence 재생으로 Loopback 활성 유지

```csharp
// WASAPI Loopback은 재생 중인 오디오가 없으면 DataAvailable 발생 안 함
// 무음을 지속 재생하여 안정적인 콜백 확보
private WaveOutEvent _silencePlayer;
private void EnsureLoopbackActive()
{
    var silence = new SilenceProvider(new WaveFormat(48000, 32, 2));
    _silencePlayer = new WaveOutEvent();
    _silencePlayer.Init(silence.ToSampleProvider());
    _silencePlayer.Play();
}
```

### 5.4 권장 구현 순서

1. **해결책 3 먼저 적용**: Loopback이 항상 데이터를 제공하도록 보장
2. **해결책 1 적용**: 타임스탬프 기반 드리프트 감지
3. **해결책 2 적용**: 큰 드리프트 발생 시 Ring Buffer로 보정

---

## 6. 에러 및 예외 처리

### 6.1 예외 케이스 정리

| 케이스 | 증상 | 대응 |
|--------|------|------|
| 장치 없음 | 마이크/출력장치가 없는 PC | 해당 소스 비활성화, 사용자에게 알림 |
| 장치 권한 거부 | 마이크 접근 차단 | 설정 > 개인정보 안내 메시지 |
| 녹음 중 장치 분리 | USB 마이크 분리 등 | 녹음 일시정지 + 사용자에게 알림 + 재연결 대기 |
| 장치 변경 | 기본 장치가 바뀜 | IMMNotificationClient로 감지, 옵션에 따라 전환 |
| 디스크 공간 부족 | 녹음 중 용량 초과 | 사전 경고(1GB 미만), 자동 정지 |
| 파일 쓰기 실패 | 경로 권한, 파일 잠금 | 자동 대체 경로(임시폴더) 시도 |
| 앱 강제 종료 | 녹음 중 크래시 | 주기적 Flush로 데이터 손실 최소화 |
| 시스템 절전 | 절전 모드 진입 | 녹음 중일 때 절전 방지 요청 |

### 6.2 에러 복구 전략

```csharp
public class ResilientRecordingEngine
{
    private const int MAX_RETRY = 3;
    private const int RETRY_DELAY_MS = 1000;

    // 녹음 중 장치 끊김 처리
    private async Task HandleDeviceDisconnected(AudioSource source)
    {
        _state = RecordingState.Paused;
        NotifyUser($"{source} 장치 연결이 끊어졌습니다.");

        for (int i = 0; i < MAX_RETRY; i++)
        {
            await Task.Delay(RETRY_DELAY_MS);
            if (TryReconnectDevice(source))
            {
                _state = RecordingState.Recording;
                return;
            }
        }

        // 재연결 실패 시 해당 소스 없이 계속 또는 정지
        AskUserToContinue();
    }

    // 파일 쓰기 실패 복구
    private void HandleWriteError(Exception ex)
    {
        // 임시 폴더로 대체 시도
        var tempPath = Path.Combine(Path.GetTempPath(), _currentFileName);
        try
        {
            SwitchOutputPath(tempPath);
            NotifyUser($"저장 경로를 임시 폴더로 변경했습니다: {tempPath}");
        }
        catch
        {
            StopRecording();
            ShowError("녹음 파일 저장에 실패했습니다.");
        }
    }
}
```

### 6.3 안정성 보장 기법

```csharp
// 주기적 Flush (5초마다)
private Timer _flushTimer;
private void InitFlushTimer()
{
    _flushTimer = new Timer(_ =>
    {
        _fileWriter?.Flush();
    }, null, 5000, 5000);
}

// 절전 모드 방지
[DllImport("kernel32.dll")]
static extern uint SetThreadExecutionState(uint esFlags);
const uint ES_CONTINUOUS = 0x80000000;
const uint ES_SYSTEM_REQUIRED = 0x00000001;
const uint ES_DISPLAY_REQUIRED = 0x00000002;

private void PreventSleep()
{
    SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
}
```

---

## 7. 테스트 시나리오

### 7.1 단위 테스트

| 모듈 | 테스트 항목 |
|------|------------|
| DeviceManager | 장치 열거, 기본 장치 가져오기, 변경 이벤트 |
| AudioMixer | 볼륨 적용, 두 스트림 믹싱, 무음 처리 |
| SyncManager | 드리프트 계산, 패딩 생성 |
| FileWriter | WAV 헤더, 데이터 쓰기, 파일 무결성 |
| LevelMeter | Peak 계산, dB 변환 |

### 7.2 통합 테스트

| 시나리오 | 성공 기준 |
|----------|----------|
| 마이크 단독 1분 녹음 | 파일 생성, 재생 가능, 음질 정상 |
| 시스템 오디오 단독 1분 녹음 | 파일 생성, 재생 가능, 음질 정상 |
| 동시 녹음 1분 | 두 소스 모두 청취 가능, 싱크 일치 |
| 동시 녹음 30분 | 싱크 드리프트 < 500ms |
| 일시정지/재개 | 끊김 없이 연속 녹음 |
| 녹음 중 장치 분리 | 적절한 에러 처리, 데이터 손실 없음 |

### 7.3 수용 기준 (Acceptance Criteria)

```gherkin
Feature: 동시 녹음 기능

Scenario: 마이크와 시스템 오디오 동시 녹음
  Given 마이크와 출력 장치가 정상 연결됨
  And 두 소스 모두 "녹음 포함" 체크됨
  When 사용자가 녹음 버튼 클릭
  And 마이크로 말하면서 동시에 유튜브 재생
  And 5분 후 정지 버튼 클릭
  Then WAV 파일이 생성됨
  And 파일 재생 시 마이크 음성과 유튜브 소리가 모두 들림
  And 두 소리의 싱크가 맞음 (드리프트 < 100ms)

Scenario: 30분 장시간 녹음 싱크 안정성
  Given 동시 녹음 설정됨
  When 30분간 녹음
  Then 녹음 시작 부분과 끝 부분의 싱크 차이 < 500ms
  And 메모리 사용량 증가 < 100MB
  And CPU 사용률 < 10%

Scenario: 녹음 중 마이크 분리
  Given USB 마이크로 녹음 중
  When 마이크 USB 케이블 분리
  Then 녹음 일시정지
  And "마이크 연결 끊김" 알림 표시
  And 마이크 재연결 시 자동 재개 옵션 제공
```

---

## 8. 마일스톤 및 일정

### Phase 1: MVP (기본 녹음 기능)

**목표**: 핵심 녹음 기능 동작

| 태스크 | 설명 |
|--------|------|
| 1.1 프로젝트 셋업 | .NET 8 WPF 프로젝트, NAudio 패키지 |
| 1.2 기본 UI | 메인 윈도우 레이아웃, 버튼 배치 |
| 1.3 DeviceManager | 장치 열거, 기본 장치 선택 |
| 1.4 마이크 녹음 | WasapiCapture로 마이크 단독 녹음 |
| 1.5 시스템 녹음 | WasapiLoopbackCapture로 시스템 오디오 녹음 |
| 1.6 FileWriter | WAV 파일 저장 |
| 1.7 녹음 제어 | Start/Stop 기능 |

**산출물**: 마이크 또는 시스템 오디오 단독 녹음 가능한 앱

### Phase 2: 동시 녹음 + 믹싱

**목표**: 두 소스 동시 녹음 및 싱크 안정화

| 태스크 | 설명 |
|--------|------|
| 2.1 AudioMixer | 두 스트림 믹싱 엔진 |
| 2.2 SyncManager | 타임스탬프 기반 싱크 관리 |
| 2.3 Ring Buffer | 버퍼링 및 드리프트 보정 |
| 2.4 동시 녹음 통합 | 믹싱 + 싱크 + 파일 저장 연결 |
| 2.5 볼륨 조절 | 마이크/시스템 개별 볼륨 |
| 2.6 싱크 테스트 | 30분 녹음 안정성 검증 |

**산출물**: 동시 녹음 + 믹싱 기능 완성

### Phase 3: UI 완성 + 사용성

**목표**: 사용자 친화적 UI

| 태스크 | 설명 |
|--------|------|
| 3.1 레벨 미터 | Peak 레벨 실시간 표시 |
| 3.2 타이머 | 경과 시간 표시 |
| 3.3 일시정지 | Pause/Resume 기능 |
| 3.4 장치 선택 UI | 드롭다운으로 장치 선택 |
| 3.5 저장 경로 | 경로 선택 다이얼로그 |
| 3.6 에러 처리 | 장치 분리, 권한 오류 등 |

**산출물**: 완성된 MVP 앱

### Phase 4: 확장 기능

**목표**: 추가 편의 기능

| 태스크 | 설명 |
|--------|------|
| 4.1 간단 재생기 | 방금 녹음한 파일 재생 |
| 4.2 최근 파일 | 최근 녹음 목록 |
| 4.3 MP3 변환 | FFmpeg 연동 |
| 4.4 시스템 트레이 | 최소화 시 트레이 |
| 4.5 단축키 | 전역 단축키 |

**산출물**: 완성된 v1.0 앱

---

## 9. 구현 리스크 및 대응책

### 9.1 기술적 리스크

| 리스크 | 심각도 | 발생 가능성 | 대응책 |
|--------|--------|------------|--------|
| **싱크 드리프트** | 높음 | 높음 | 타임스탬프 동기화 + Ring Buffer + 정기 보정 |
| **WASAPI Loopback 무음 시 콜백 없음** | 중간 | 높음 | SilenceProvider로 지속 재생 |
| **장치별 포맷 불일치** | 중간 | 중간 | MediaFoundation Resampler로 통일 |
| **메모리 누수 (장시간 녹음)** | 높음 | 중간 | 객체 풀링, 주기적 GC 모니터링 |
| **파일 손상 (비정상 종료)** | 높음 | 낮음 | 5초마다 Flush, 임시 파일 복구 메커니즘 |

### 9.2 싱크 드리프트 상세 대응

**원인 분석**:
- 마이크와 시스템 오디오는 다른 클록 소스 사용
- WASAPI Loopback은 재생 중인 오디오가 없으면 데이터 제공 안 함
- 버퍼 언더런/오버런으로 샘플 손실 가능

**다층 방어 전략**:

```
Layer 1: Silence 재생
   └─ Loopback 콜백 안정화

Layer 2: 공통 마스터 클록
   └─ Stopwatch로 두 스트림 타임스탬프 기록

Layer 3: Ring Buffer
   └─ 500ms 버퍼로 일시적 지연 흡수

Layer 4: 정기 보정 (5초마다)
   └─ 드리프트 감지 시 샘플 패딩/드롭

Layer 5: 경고 알림
   └─ 드리프트 > 100ms 시 사용자에게 알림
```

### 9.3 MP3 변환 리스크

| 항목 | 리스크 | 대응책 |
|------|--------|--------|
| FFmpeg 배포 | 라이선스, 파일 크기 | 외부 실행 방식, 사용자가 별도 설치 |
| 인코딩 시간 | 대용량 파일 시 오래 걸림 | 백그라운드 처리 + 진행률 표시 |
| 품질 설정 | 사용자 혼란 | 기본값(192kbps) 제공, 고급 옵션 숨김 |

---

## 10. 프로젝트 구조

```
AudioRecorder/
├── AudioRecorder.sln
├── src/
│   └── AudioRecorder/
│       ├── AudioRecorder.csproj
│       ├── App.xaml
│       ├── App.xaml.cs
│       │
│       ├── Views/
│       │   ├── MainWindow.xaml
│       │   ├── MainWindow.xaml.cs
│       │   └── Controls/
│       │       ├── LevelMeterControl.xaml
│       │       └── RecentFilesControl.xaml
│       │
│       ├── ViewModels/
│       │   ├── MainViewModel.cs
│       │   ├── RecordingViewModel.cs
│       │   └── DeviceViewModel.cs
│       │
│       ├── Models/
│       │   ├── AudioDevice.cs
│       │   ├── RecordingOptions.cs
│       │   └── RecordingState.cs
│       │
│       ├── Services/
│       │   ├── IRecordingService.cs
│       │   ├── RecordingService.cs
│       │   ├── IPlaybackService.cs
│       │   ├── PlaybackService.cs
│       │   └── ISettingsService.cs
│       │
│       ├── Audio/
│       │   ├── DeviceManager.cs
│       │   ├── RecordingEngine.cs
│       │   ├── AudioMixer.cs
│       │   ├── SyncManager.cs
│       │   ├── LevelMeter.cs
│       │   ├── FileWriter.cs
│       │   └── Resampler.cs
│       │
│       ├── Infrastructure/
│       │   ├── FFmpegWrapper.cs
│       │   └── SettingsRepository.cs
│       │
│       ├── Converters/
│       │   └── LevelToWidthConverter.cs
│       │
│       └── Resources/
│           ├── Styles.xaml
│           └── Icons/
│
└── tests/
    └── AudioRecorder.Tests/
        ├── Audio/
        │   ├── AudioMixerTests.cs
        │   └── SyncManagerTests.cs
        └── Services/
            └── RecordingServiceTests.cs
```

---

## 11. 기술 스택 상세

| 영역 | 기술 | 버전 | 용도 |
|------|------|------|------|
| 런타임 | .NET | 8.0 | 기반 프레임워크 |
| UI | WPF | .NET 8 내장 | 데스크톱 UI |
| MVVM | CommunityToolkit.Mvvm | 8.x | MVVM 패턴 |
| 오디오 | NAudio | 2.2.x | WASAPI 캡처/재생 |
| DI | Microsoft.Extensions.DI | 8.x | 의존성 주입 |
| 설정 | System.Text.Json | 내장 | JSON 설정 저장 |
| MP3 | FFmpeg | 외부 | WAV→MP3 변환 |

---

## 12. 참고 자료

### 공식 문서
- [NAudio GitHub](https://github.com/naudio/NAudio)
- [NAudio WASAPI Loopback 문서](https://github.com/naudio/NAudio/blob/master/Docs/WasapiLoopbackCapture.md)
- [Microsoft WASAPI 문서](https://docs.microsoft.com/en-us/windows/win32/coreaudio/wasapi)

### 관련 이슈
- [NAudio Issue #710 - Loopback Sync Drift](https://github.com/naudio/NAudio/issues/710)
- [NAudio Issue #1110 - Mic + Speaker Recording Blanks](https://github.com/naudio/NAudio/issues/1110)

### 해결책 참고
- [Mark Heath - Recording and Playing Audio](https://markheath.net/post/how-to-record-and-play-audio-at-same)
- [OurCodeWorld - System Audio Recording](https://ourcodeworld.com/articles/read/702/how-to-record-the-audio-from-the-sound-card-system-audio-with-c-using-naudio-in-winforms)

---

## 13. 결론 및 다음 단계

### 13.1 제작안 요약

- **목표**: 마이크 + 시스템 오디오 동시 녹음이 가능한 안정적인 Windows 녹음기
- **핵심 과제**: 싱크 드리프트 문제 해결 (타임스탬프 동기화 + Ring Buffer)
- **기술 스택**: .NET 8 + WPF + NAudio
- **개발 단계**: 4개 Phase (MVP → 동시 녹음 → UI 완성 → 확장 기능)

### 13.2 승인 요청 사항

1. **전체 제작안 승인**
2. **Phase 1 (MVP) 개발 착수 승인**
3. **추가 요구사항 또는 수정 사항** 확인

---

**작성자**: Claude (Senior Windows/Audio Engineer & PM)
**검토 요청**: 사용자
