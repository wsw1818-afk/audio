# 버그 분석 보고서 - AudioRecorder DRM 캡처 기능

**분석일**: 2026-02-06
**대상 코드**: ChromeDrmCaptureService, EnhancedDxgiCaptureService, ScreenRecordingEngine

---

## 🚨 Critical 버그 (심각)

### 1. **ChromeDrmCaptureService - Fire-and-Forget Task 누수**
**위치**: `StartCaptureAsync()` 메서드 (라인 281)

```csharp
// 스크린캐스트 이벤트 핸들러 설정
_ = Task.Run(() => HandleScreencastEventsAsync());
```

**문제**:
- `HandleScreencastEventsAsync()`가 예외를 throw하면 무시됨
- Task가 완료되기 전에 객체가 Dispose되면 예외 발생
- 스레드 풀 고갈 가능성

**재현 시나리오**:
1. 캡처 시작
2. WebSocket 연결이 불안정한 상태
3. HandleScreencastEventsAsync()에서 예외 발생
4. 예외가 무시되고 캡처가 멈춤

**해결 방안**:
```csharp
private CancellationTokenSource? _screencastCts;
private Task? _screencastTask;

// 시작 시
_screencastCts = new CancellationTokenSource();
_screencastTask = Task.Run(() => HandleScreencastEventsAsync(_screencastCts.Token));

// 중지 시
_screencastCts?.Cancel();
try { await (_screencastTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
```

---

### 2. **EnhancedDxgiCaptureService - DXGI 리소스 경쟁 조건**
**위치**: `CaptureLoop()` 메서드 (라인 229-276)

**문제**:
- `CleanupDxgi()`와 `InitializeDxgi()`가 연속 실패 시 동시에 호출될 수 있음
- `_device`, `_outputDuplication` 등이 null 체크와 사용 사이에 변경될 수 있음

```csharp
// 위험한 코드 패턴
if (consecutiveFailures > maxConsecutiveFailures)
{
    CleanupDxgi();  // 여기서 null로 설정
    Thread.Sleep(100);
    if (!InitializeDxgi())  // 하지만 다른 스레드에서 접근 가능
    {
        break;
    }
}
```

**해결 방안**:
```csharp
private readonly object _dxgiLock = new();

private void CaptureLoop()
{
    while (_isCapturing)
    {
        lock (_dxgiLock)  // 동기화 추가
        {
            if (!CaptureFrame()) { ... }
        }
    }
}
```

---

### 3. **ScreenRecordingEngine - 잘못된 _useDxgi 플래그 사용**
**위치**: `OnFrameAvailable` 메서드 (라인 600+)

```csharp
private void OnFrameAvailable(object? sender, FrameEventArgs e)
{
    ProcessFrame(e.Width, e.Height, () =>
    {
        var frameData = _useDxgi ? _dxgiCapture.GetCurrentFrame() : _gdiCapture.GetCurrentFrame();
        return frameData;
    });
}
```

**문제**:
- `_useDxgi` 필드가 존재하지 않음 (컴파일 에러)
- `_captureMode`를 사용해야 함

**영향**: 코드가 컴파일되지 않음

---

## ⚠️ High 버그 (높음)

### 4. **ChromeDrmCaptureService - 메모리 누수 (Bitmap 처리)**
**위치**: `ProcessScreencastFrameAsync()` (라인 346-396)

```csharp
private Task ProcessScreencastFrameAsync(string base64Data)
{
    using var bitmap = new Bitmap(ms);  // Bitmap 생성
    // ... 처리 ...
    FrameAvailable?.Invoke(this, new ChromeFrameEventArgs
    {
        FrameData = frameData  // byte[] 복사본 전달
    });
}
```

**문제**:
- `frameData`는 `Bitmap.LockBits()`에서 가져온 데이터의 복사본
- 매 프레임마다 새 byte[] 할당 (GC 압력)
- 30fps 기준 초당 30번 할당

**성능 영향**: 
- 1920x1080 @ 30fps = 약 250MB/s 메모리 할당
- GC가 빈번하게 발생하여 프레임 드롭

**해결 방안**:
```csharp
// 버퍼 풀링 사용
private readonly ConcurrentQueue<byte[]> _frameBufferPool = new();
private byte[] RentBuffer(int size)
{
    if (_frameBufferPool.TryDequeue(out var buffer) && buffer.Length >= size)
        return buffer;
    return new byte[size];
}
```

---

### 5. **EnhancedDxgiCaptureService - 잘못된 검은 화면 감지 로직**
**위치**: `IsFrameBlack()` (라인 442-467)

```csharp
private bool IsFrameBlack()
{
    // ...
    for (int i = 0; i < _currentFrame.Length - 4; i += sampleStep)
    {
        byte b = _currentFrame[i];
        byte g = _currentFrame[i + 1];
        byte r = _currentFrame[i + 2];
        // ...
    }
}
```

**문제**:
- BGRA 형식에서 인덱스 순서가 잘못됨 (B, G, R, A 순)
- 실제로는 R, G, B 순서로 읽고 있음
- 알파 채널을 무시하고 있음

**정정**:
```csharp
// BGRA 형식
byte b = _currentFrame[i];
byte g = _currentFrame[i + 1];
byte r = _currentFrame[i + 2];
byte a = _currentFrame[i + 3];  // 알파 무시 또는 확인

// 검은색 판단
if (r > 20 || g > 20 || b > 20 || a < 255)  // 알파도 확인
```

---

### 6. **ScreenRecordingEngine - Chrome DRM 중지 시 데드락**
**위치**: `StopCapture()` (라인 295)

```csharp
private void StopCapture()
{
    switch (_captureMode)
    {
        case CaptureMode.ChromeDRM:
            _ = _chromeDrmCapture.StopCaptureAsync();  // Fire-and-forget
            break;
    }
}
```

**문제**:
- `StopCaptureAsync()`가 async이지만 await 없이 호출
- WebSocket close가 완료되기 전에 다음 코드 실행
- 동기화 문제 발생 가능

**해결 방안**:
```csharp
private async Task StopCaptureAsync()
{
    switch (_captureMode)
    {
        case CaptureMode.ChromeDRM:
            await _chromeDrmCapture.StopCaptureAsync();
            break;
        // ...
    }
}
```

---

## ⚡ Medium 버그 (중간)

### 7. **ChromeDrmCaptureService - WebSocket 버퍼 크기 문제**
**위치**: `ReceiveFullMessageAsync()` (라인 620-635)

```csharp
var buffer = new byte[256 * 1024]; // 256KB 버퍼
```

**문제**:
- 4K 화면의 JPEG 데이터는 256KB를 초과할 수 있음
- Base64 인코딩 시 데이터 크기가 33% 증가
- 큰 화면에서 데이터 손실 가능

**권장 수정**:
```csharp
var buffer = new byte[2 * 1024 * 1024]; // 2MB 버퍼
```

---

### 8. **EnhancedDxgiCaptureService - _adapterIndex 미사용**
**위치**: 클래스 선언부

```csharp
private int _adapterIndex = 0;  // 선언됨
private int _outputIndex = 0;
```

**문제**:
- `_adapterIndex`는 선언되었지만 `InitializeDxgi()`에서 사용되지 않음
- 여러 GPU 시스템에서 원하는 GPU를 선택할 수 없음

**해결 방안**:
```csharp
private bool InitializeDxgi()
{
    using var factory = new Factory1();
    using var adapter = factory.GetAdapter(_adapterIndex);
    _device = new Device(adapter, creationFlags);
    // ...
}
```

---

### 9. **ScreenRecordingEngine - Dispose 패턴 문제**
**위치**: `Dispose()` 메서드

```csharp
public void Dispose()
{
    if (_disposed) return;
    _disposed = true;

    StopAsync().Wait(TimeSpan.FromSeconds(5));  // 동기 대기
    // ...
}
```

**문제**:
- UI 스레드에서 Dispose() 호출 시 UI가 5초간 멈춤
- 데드락 가능성

**해결 방안**:
```csharp
public async Task DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;

    await StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    // ...
}
```

---

## 📋 Low 버그 (낮음)

### 10. **ChromeDrmCaptureService - StatusChanged 이벤트 null 체크 누락**
**위치**: 전체적으로 scattered

```csharp
StatusChanged?.Invoke("메시지");  // 대부분 ?. 사용
```

**문제**:
- 일부 위치에서 직접 Invoke 호출 (null 참조 가능)
- 이벤트가 제거된 후 호출되면 NRE

---

### 11. **EnhancedDxgiCaptureService - _showCursor 미구현**
**위치**: 클래스 전체

**문제**:
- `_showCursor` 필드는 있지만 실제로 사용되지 않음
- 커서 캡처 여부를 제어할 수 없음

---

### 12. **ScreenRecordingEngine - OnFrameAvailable에서의 경쟁 조건**
**위치**: 프레임 처리 로직

```csharp
private void OnFrameAvailable(object? sender, FrameEventArgs e)
{
    _receivedFrameCount++;
    if (_receivedFrameCount % 30 == 0)
    {
        Debug.WriteLine($"... 모드: {CaptureMethod}");
    }
}
```

**문제**:
- `_receivedFrameCount`는 long이지만 volatile이 아님
- 다중 스레드에서 증가 시 값이 유실될 수 있음

**해결 방안**:
```csharp
private long _receivedFrameCount;
private long _processedFrameCount;

Interlocked.Increment(ref _receivedFrameCount);
```

---

## 🔧 권장 개선 사항

### 메모리 관리
| 항목 | 현재 | 권장 |
|------|------|------|
| 프레임 버퍼 | 매 프레임 할당 | ArrayPool 사용 |
| Bitmap 처리 | Using 문 | 버퍼 풀링 |
| WebSocket 버퍼 | 256KB | 2MB |

### 동기화
| 항목 | 현재 | 권장 |
|------|------|------|
| DXGI 리소스 | Lock 없음 | lock(_dxgiLock) |
| Frame 카운트 | 일반 long | Interlocked |
| Dispose | 동기 | 비동기 |

### 에러 처리
| 항목 | 현재 | 권장 |
|------|------|------|
| Fire-and-forget | `_ = Task.Run` | 명시적 Task 관리 |
| WebSocket 오류 | Catch 무시 | 재연결 로직 |
| Chrome 종료 | 감지 없음 | Process.Exited 이벤트 |

---

## 📊 버그 심각도 요약

| 심각도 | 개수 | 주요 항목 |
|--------|------|-----------|
| 🔴 Critical | 3 | Task 누수, 경쟁 조건, 컴파일 에러 |
| 🟠 High | 3 | 메모리 누수, 검은 화면 감지, 데드락 |
| 🟡 Medium | 3 | 버퍼 크기, GPU 선택, Dispose |
| 🟢 Low | 3 | null 체크, 미구현 기능, 경쟁 조건 |

---

## 🎯 우선 수정 순위

1. **Critical #3**: `_useDxgi` 컴파일 에러 (즉시)
2. **Critical #1**: ChromeDrm Task 관리 (높음)
3. **High #4**: 메모리 누수 (높음)
4. **High #6**: Chrome DRM 중지 데드락 (중간)
5. **Medium #7**: WebSocket 버퍼 (중간)

---

*분석 완료: 버그 수정을 시작하시겠습니까?*
