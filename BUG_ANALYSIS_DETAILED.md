# 상세 버그 분석 보고서 (2차)

**분석일**: 2026-02-06  
**분석 대상**: ChromeDrmCaptureService, ScreenRecordingEngine, VideoEncoderService

---

## 🔴 Critical - Frame Data Race Condition

### 1. ChromeDrmCaptureService - Frame Data 참조 공유
**위치**: `ProcessScreencastFrameAsync()` (라인 352-401)

```csharp
lock (_frameLock)
{
    _currentFrame = frameData;  // 참조 저장
}

FrameAvailable?.Invoke(this, new ChromeFrameEventArgs
{
    FrameData = frameData  // 동일한 참조를 이벤트로 전달
});
```

**문제**:
- `frameData`는 메서드 내에서 생성된 배열
- `_currentFrame`과 `ChromeFrameEventArgs.FrameData`가 동일한 배열 참조를 가짐
- 다음 프레임이 도착하면 `_currentFrame`이 새 배열로 교첵됨
- 이벤트 수신자가 아직 처리 중일 때 데이터가 변경될 위험

**영향**:
- ScreenRecordingEngine에서 프레임 데이터 처리 중 화면이 깨질 수 있음
- 인코딩된 비디오에 아티팩트 발생

**해결**:
```csharp
// 복사본을 만들어서 전달
var frameDataCopy = new byte[size];
Buffer.BlockCopy(frameData, 0, frameDataCopy, 0, size);

FrameAvailable?.Invoke(this, new ChromeFrameEventArgs
{
    FrameData = frameDataCopy  // 복사본 전달
});
```

---

### 2. ScreenRecordingEngine - 비동기 없는 Frame 카운터
**위치**: `ProcessFrame()` (라인 842-896)

```csharp
private void ProcessFrame(int frameWidth, int frameHeight, Func<byte[]?> getFrameData)
{
    _receivedFrameCount++;  // 일반 증가 연산
```

**문제**:
- `_receivedFrameCount`는 long이지만 volatile도 아니고 Interlocked도 사용하지 않음
- 다중 스레드(GDI, DXGI, Chrome 각각의 캡처 스레드)에서 동시 접근 가능
- 값 유실 또는 일관성 없는 값 가능

**해결**:
```csharp
private long _receivedFrameCount;

private void ProcessFrame(...)
{
    var frameNumber = Interlocked.Increment(ref _receivedFrameCount);
    if (frameNumber % 30 == 0) { ... }
```

---

### 3. VideoEncoderService - Frame Buffer Race Condition
**위치**: `WriteFrame()` (추정)

**문제**:
- ScreenRecordingEngine의 `ProcessFrame`에서 effects가 필요 없을 때:
```csharp
else
{
    var frameData = getFrameData();  // 남부 버퍼 참조
    if (frameData != null)
    {
        _videoEncoder.WriteFrame(frameData);  // 비동기로 인코딩
    }
}
```
- `getFrameData()`는 캡처 서비스의 남부 버퍼를 반환
- 다음 프레임이 캡처되면 해당 버퍼의 내용이 덮어쓰여짐
- FFmpeg가 아직 이전 프레임을 인코딩 중일 때 데이터가 변경됨

**영향**:
- 인코딩된 비디오에 티어링(Tearing) 현상
- 프레임 간 화면이 섞임

**해결**:
- effects 유무와 상관없이 항상 복사본 사용
- 또는 프레임 큐를 두고 순차적 처리

---

## 🟠 High - 비동기/동기 패턴 충돌

### 4. ScreenRecordingEngine - StopAsync 동기 대기
**위치**: `Dispose()` 메서드 (미확인, 추정)

```csharp
public void Dispose()
{
    StopAsync().Wait(TimeSpan.FromSeconds(5));
}
```

**문제**:
- UI 스레드에서 Dispose 호출 시 5초간 UI 멈춤
- 데드락 가능성 (StopAsync 남부에서 UI 스레드 호출 대기 시)

**해결**:
```csharp
public async Task DisposeAsync()
{
    await StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    // ...
}
```

---

### 5. ChromeDrmCaptureService - Dispose 미완료
**위치**: `Dispose()` (라인 664-671)

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    _ = StopCaptureAsync();  // Fire-and-forget
    _httpClient.Dispose();
}
```

**문제**:
- `StopCaptureAsync()`가 완료되기 전에 `_httpClient`가 Dispose됨
- WebSocket이 아직 연결 중일 수 있음
- 리소스 정리가 불완전

**해결**:
```csharp
public async Task DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;

    await StopCaptureAsync();
    _httpClient.Dispose();
}

// IDisposable.Dispose는 동기 버전으로
public void Dispose()
{
    DisposeAsync().GetAwaiter().GetResult();
}
```

---

### 6. ScreenRecordingEngine - 백그라운드 인코딩 Task 누수
**위치**: `StopAsync()` (라인 831-857)

```csharp
_isEncoding = true;
_ = Task.Run(async () =>
{
    try
    {
        await EncodeAndMuxAsync(recordedDuration, recordedFrameCount);
    }
    catch (Exception ex)
    {
        // ...
    }
    finally
    {
        _isEncoding = false;
    }
});
```

**문제**:
- 인코딩 Task가 완료되기 전에 Engine이 Dispose되면
- FFmpeg 프로세스가 좀비 프로세스로 남을 수 있음
- `_isEncoding` 플래그가 잘못된 상태를 나타낼 수 있음

**해결**:
```csharp
private CancellationTokenSource? _encodingCts;
private Task? _encodingTask;

// 시작 시
_encodingCts = new CancellationTokenSource();
_encodingTask = Task.Run(() => EncodeAndMuxAsync(..., _encodingCts.Token));

// Dispose 시
_encodingCts?.Cancel();
await (_encodingTask?.WaitAsync(TimeSpan.FromSeconds(30)) ?? Task.CompletedTask);
```

---

## 🟡 Medium - 성능 병목현상

### 7. EnhancedDxgiCaptureService - 과도한 Lock 범위
**위치**: `CaptureLoop()` (라인 243-266)

```csharp
lock (_dxgiLock)
{
    if (!CaptureFrame())  // 전체 캡처 과정이 lock 남부
    {
        consecutiveFailures++;
        if (consecutiveFailures > maxConsecutiveFailures)
        {
            CleanupDxgi();
            Thread.Sleep(100);
            InitializeDxgi();  // 초기화도 lock 남부
        }
    }
}
```

**문제**:
- `CaptureFrame()` 전체가 lock으로 감싸져 있음
- 프레임 캡처(16ms 타임아웃 포함) 동안 다른 스레드가 대기
- 성능 저하 및 프레임 드롭

**해결**:
```csharp
// 필요한 부분만 lock
bool captured;
lock (_dxgiLock)
{
    captured = CaptureFrame();
}

if (!captured)
{
    consecutiveFailures++;
    // 재초기화는 별도 처리
}
```

---

### 8. ProcessFrame - 불필요한 조건부 로직
**위치**: `ProcessFrame()` (라인 856-896)

```csharp
if (needsEffects)
{
    // 복사 후 효과 적용
    Buffer.BlockCopy(frameData, 0, _frameBuffer, 0, ...);
    // ... 효과 적용
    _videoEncoder.WriteFrame(_frameBuffer);
}
else
{
    // 직접 전달 (위험!)
    _videoEncoder.WriteFrame(frameData);
}
```

**문제**:
- effects가 없을 때는 복사 없이 직접 전달하여 성능은 좋지만 race condition 발생
- 코드 복잡성 증가

**해결**:
```csharp
// 항상 복사본 사용 (안전하고 단순)
var frameData = getFrameData();
if (frameData == null) return;

Buffer.BlockCopy(frameData, 0, _frameBuffer, 0, ...);

if (_mouseClickHighlight.IsEnabled)
    _mouseClickHighlight.DrawEffects(_frameBuffer, ...);

_videoEncoder.WriteFrame(_frameBuffer);
```

---

## 📊 요약

| 범주 | 심각도 | 개수 | 주요 문제 |
|------|--------|------|-----------|
| Race Condition | Critical | 3 | Frame data 공유, 카운터, 버퍼 |
| 비동기/동기 | High | 3 | Dispose, Task 관리 |
| 성능 | Medium | 2 | Lock 범위, 조건부 로직 |

**총 8개 버그 발견**

---

## 🎯 우선순위

1. **Critical #1**: Frame data race condition (ChromeDrm)
2. **Critical #2**: Frame counter race condition
3. **Critical #3**: VideoEncoder frame buffer race
4. **High #5**: ChromeDrm Dispose 미완료
5. **High #6**: 백그라운드 인코딩 Task 누수
