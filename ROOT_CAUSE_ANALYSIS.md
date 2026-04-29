# 근본 원인 분석 - 왜 버그가 계속 나오는가?

**분석일**: 2026-02-06

---

## 🔴 근본 원인 1: 아키텍처 설계 결함

### 문제: "캡처 모드 전환" vs "이벤트 구독 영구"

```csharp
// Constructor - 한 번만 실행
public ScreenRecordingEngine(DeviceManager deviceManager)
{
    // 모든 캡처 서비스의 이벤트를 영구 구독
    _gdiCapture.FrameAvailable += OnFrameAvailable;      // 항상 구독
    _dxgiCapture.FrameAvailable += OnFrameAvailable;     // 항상 구독  
    _enhancedDxgiCapture.FrameAvailable += OnEnhancedFrameAvailable; // 항상 구독
    _chromeDrmCapture.FrameAvailable += OnChromeFrameAvailable;      // 항상 구독
}

// Start() - 특정 모드로만 시작
public void Start(ScreenRecordingOptions options)
{
    if (options.UseChromeDrmCapture && TryStartChromeDrmCapture(...))
        _captureMode = CaptureMode.ChromeDRM;  // Chrome만 시작
    else if (options.UseEnhancedDxgi && TryStartEnhancedDxgiCapture(...))
        _captureMode = CaptureMode.EnhancedDXGI;  // Enhanced만 시작
    // ...
}
```

**결과**:
- Chrome DRM으로 시작하도 GDI/DXGI 이벤트 핸들러는 여전히 구독 중
- 만약 버그로 인해 다른 캡처 서비스가 활성화되면 이벤트가 혼재됨
- `_captureMode`와 실제 이벤트 소스가 불일치할 수 있음

**실제 버그 시나리오**:
1. Chrome DRM 모드로 녹화 시작
2. Chrome CDP 연결 불안정으로 이벤트 지연
3. 이전에 중지되지 않은 GDI 캡처가 남아있으면 이벤트 발생
4. `OnFrameAvailable`이 호출되지만 `_captureMode`는 ChromeDRM
5. `_dxgiCapture.GetCurrentFrameCopy()`가 null 또는 오래된 데이터 반환

---

## 🔴 근본 원인 2: 비동기/동기 혼용 지옥

### 패턴 혼란

| 서비스 | Start | Stop | 남부 구현 |
|--------|-------|------|-----------|
| GDI | `void Start()` | `void Stop()` | 동기 |
| DXGI | `void Start()` | `void Stop()` | 동기 |
| Enhanced DXGI | `void Start()` | `void Stop()` | 동기 |
| Chrome DRM | `Task<bool> StartCaptureAsync()` | `Task StopCaptureAsync()` | **비동기** |
| ScreenRecordingEngine | `void Start()` | `Task StopAsync()` | 혼합 |

**문제**:
```csharp
// ScreenRecordingEngine.Start() - 동기 메서드
public void Start(ScreenRecordingOptions options)
{
    // Chrome DRM은 비동기인데 동기 컨텍스트에서 호출
    if (options.UseChromeDrmCapture && TryStartChromeDrmCapture(...))
    {
        // task.Wait()로 인해 UI 스레드가 블로킹됨
    }
}

private bool TryStartChromeDrmCapture(...)
{
    var task = _chromeDrmCapture.StartCaptureAsync(...);
    if (task.Wait(TimeSpan.FromSeconds(30)))  // ⚠️ 데드띵 위험!
    {
        return task.Result;
    }
}
```

**결과**:
- UI 스레드 블로킹
- 데드락 가능성
- 예외 처리 어려움

---

## 🔴 근본 원인 3: 상태 관리 미흡

### 상태 플래그 남용

```csharp
private volatile bool _isRecording;
private volatile bool _isPaused;
private volatile bool _isStopping;
private volatile bool _isEncoding;
private CaptureMode _captureMode;
```

**문제**:
1. **상태 간 불일치 가능**:
   ```csharp
   _isRecording = false;  // 녹화 중지
   _captureMode = CaptureMode.ChromeDRM;  // 하지만 모드는 그대로
   ```

2. **검사 타이밍 문제**:
   ```csharp
   if (State != RecordingState.Stopped || _isStopping)
       throw new InvalidOperationException("...");
   
   // 위 검사 통과 후
   _isStopping = true;  // 다른 스레드에서 이미 true로 설정됨
   ```

3. **상태 전이 누락**:
   ```csharp
   // Start()에서 예외 발생 시 _captureMode가 None이 아닐 수 있음
   catch (Exception ex)
   {
       Cleanup();
       // _captureMode = CaptureMode.None;  // 누락!
       throw;
   }
   ```

---

## 🔴 근본 원인 4: 이벤트 기반 아키텍처의 함정

### 문제: 이벤트 순서 보장 없음

```csharp
// 캡처 스레드 (높은 우선순위)
while (_isCapturing)
{
    CaptureFrame();
    FrameAvailable?.Invoke(this, new FrameEventArgs { ... });  // 이벤트 발생
}

// 처리 스레드 (UI 스레드)
void OnFrameAvailable(object? sender, FrameEventArgs e)
{
    ProcessFrame(e.Width, e.Height, ...);
    _videoEncoder.WriteFrame(_frameBuffer);  // FFmpeg 파이프에 쓰기
}
```

**문제 시나리오**:
1. 프레임 A 캡처 → 이벤트 발생 → 처리 대기
2. 프레임 B 캡처 → 이벤트 발생 → 처리 대기
3. UI 스레드가 바쁘면 이벤트가 큐에 쌓임
4. FFmpeg 파이프가 가득 차면 WriteFrame이 블로킹
5. 캡처 스레드는 계속 프레임을 생성 → 메모리 폭증

**결과**:
- 메모리 누수
- 프레임 드롭
- 동기화 문제

---

## 🔴 근본 원인 5: 리소스 생명주기 관리 실패

### Dispose 패턴 불일치

```csharp
public void Dispose()  // 동기 Dispose
{
    StopAsync().Wait(TimeSpan.FromSeconds(5));  // 비동기 대기
    // ChromeDrmCapture는 async Dispose가 필요
    _chromeDrmCapture?.Dispose();  // 하지만 동기로 호출
}
```

**문제**:
- ChromeDrmCapture는 WebSocket close가 필요
- WebSocket close는 async 작업
- 동기 Dispose에서 async 작업을 기다리면 데드락

---

## 🎯 종합 결론

버그가 반복적으로 나오는 이유는 **단순한 코딩 실수가 아니라 아키텍처 설계 결함** 때문입니다:

### 1. **잘못된 추상화**
- 4개의 캡처 서비스가 같은 인터페이스 없이 각각 다른 패턴 사용
- Chrome만 비동기, 나머지는 동기

### 2. **상태 기반 설계**
- 복잡한 상태 플래그(_isRecording, _isStopping 등) 관리
- 상태 간 전이가 명확하지 않음

### 3. **이벤트 기반의 비동기성**
- 이벤트 발생 순서와 처리 순서 보장 없음
- 백프레셔(backpressure) 처리 없음

### 4. **멀티스레딩 지옥**
- 캡처 스레드, 오디오 스레드, UI 스레드, FFmpeg 프로세스가 동시 실행
- 동기화 메커니즘이 일관적이지 않음

---

## 💡 근본적 해결책

### 1. **통합 인터페이스 설계**
```csharp
public interface ICaptureService
{
    Task<bool> StartAsync(CaptureOptions options, CancellationToken ct);
    Task StopAsync(CancellationToken ct);
    IAsyncEnumerable<FrameData> GetFramesAsync(CancellationToken ct);
}
```

### 2. **상태 머신 패턴**
```csharp
public enum RecordingState { Idle, Starting, Recording, Paused, Stopping, Stopped }

// 상태 전이가 명확하게 정의
Idle → Starting → Recording → Stopping → Stopped
```

### 3. **Producer-Consumer 패턴**
```csharp
// Channel을 사용한 백프레셔 처리
var frameChannel = Channel.CreateBounded<FrameData>(new BoundedChannelOptions(10));

// 캡처 스레드 (Producer)
await frameChannel.Writer.WriteAsync(frame);

// 처리 스레드 (Consumer)
await foreach (var frame in frameChannel.Reader.ReadAllAsync())
```

### 4. **통합 생명주기 관리**
```csharp
public interface IAsyncDisposable
{
    ValueTask DisposeAsync();
}
```

---

**결론: 현재 아키텍처로는 완벽한 버그 수정이 불가능합니다.**

지속적인 버그 수정은 일시적인 해결책일 뿐, 근본적인 아키텍처 재설계가 필요합니다.
