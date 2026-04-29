# 코드 개선 요약 보고서

**작업일**: 2026-02-06  
**대상**: ChromeDrmCaptureService, ScreenRecordingEngine, EnhancedDxgiCaptureService

---

## 🎯 주요 개선 사항

### 1. Frame Data Race Condition 해결

#### ChromeDrmCaptureService
**문제**: FrameData 참조가 이벤트 수신자와 공유되어 Race Condition 발생

**해결**:
```csharp
// 변경 전 (위험)
FrameAvailable?.Invoke(this, new ChromeFrameEventArgs
{
    FrameData = frameData  // 동일 참조 공유
});

// 변경 후 (안전)
var frameDataCopy = new byte[size];
Buffer.BlockCopy(frameData, 0, frameDataCopy, 0, size);

FrameAvailable?.Invoke(this, new ChromeFrameEventArgs
{
    FrameData = frameDataCopy  // 복사본 전달
});
```

---

### 2. Thread-Safe Frame 카운터

#### ScreenRecordingEngine
**문제**: `_receivedFrameCount++`가 원자적이지 않아 Race Condition 가능

**해결**:
```csharp
// 변경 전
_receivedFrameCount++;

// 변경 후
var frameNumber = Interlocked.Increment(ref _receivedFrameCount);
```

---

### 3. 항상 복사본 사용 (Consistency)

#### ScreenRecordingEngine.ProcessFrame
**문제**: effects가 없을 때는 직접 전달하여 Race Condition 발생

**해결**:
```csharp
// 변경 전 (조걸적 복사)
if (needsEffects)
{
    Buffer.BlockCopy(frameData, 0, _frameBuffer, 0, ...);
    // 효과 적용
    _videoEncoder.WriteFrame(_frameBuffer);
}
else
{
    _videoEncoder.WriteFrame(frameData);  // 위험!
}

// 변경 후 (항상 복사)
Buffer.BlockCopy(frameData, 0, _frameBuffer, 0, ...);
// 효과 적용 (있을 때)
_videoEncoder.WriteFrame(_frameBuffer);
```

---

### 4. 성능 개선 - Lock 범위 최소화

#### EnhancedDxgiCaptureService
**문제**: 전체 CaptureFrame이 lock으로 감싸져 성능 저하

**해결**:
```csharp
// 변경 전
lock (_dxgiLock)
{
    captured = CaptureFrame();  // 긴 작업
    if (!captured)
    {
        CleanupDxgi();  // 초기화도 lock 남부
        InitializeDxgi();
    }
}

// 변경 후
lock (_dxgiLock)
{
    captured = CaptureFrame();  // 최소한의 작업만
}

if (!captured)
{
    lock (_dxgiLock)  // 필요할 때만
    {
        CleanupDxgi();
        InitializeDxgi();
    }
}
```

---

### 5. Dispose 패턴 개선

#### ScreenRecordingEngine
**문제**: UI 스레드에서 Dispose 호출 시 UI 멈춤

**해결**:
```csharp
// 새로운 DisposeAsync 메서드 추가
public async Task DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;

    // 인코딩 Task 취소 및 대기
    _encodingCts?.Cancel();
    if (_encodingTask != null)
    {
        await _encodingTask.WaitAsync(TimeSpan.FromSeconds(30));
    }

    await StopAsync().WaitAsync(TimeSpan.FromSeconds(10));
    // ... 리소스 정리
}
```

---

### 6. 백그라운드 인코딩 Task 관리

#### ScreenRecordingEngine
**문제**: Fire-and-forget Task로 인해 좀비 프로세스 발생 가능

**해결**:
```csharp
// 변경 전
_ = Task.Run(async () =>
{
    await EncodeAndMuxAsync(...);
});

// 변경 후
_encodingCts = new CancellationTokenSource();
_encodingTask = Task.Run(async () =>
{
    try
    {
        await EncodeAndMuxAsync(..., _encodingCts.Token);
    }
    catch (OperationCanceledException)
    {
        // 정상적인 취소 처리
    }
});

// Dispose 시 취소 및 대기
_encodingCts?.Cancel();
await _encodingTask.WaitAsync(TimeSpan.FromSeconds(30));
```

---

### 7. ChromeDrmCaptureService Dispose 개선

**문제**: StopCaptureAsync가 완료되기 전에 리소스 정리

**해결**:
```csharp
public async Task DisposeAsync()
{
    if (_disposed) return;
    _disposed = true;

    await StopCaptureAsync();  // 완료 대기
    _httpClient.Dispose();
}

public void Dispose()
{
    DisposeAsync().GetAwaiter().GetResult();
}
```

---

### 8. Memory Pool 사용

#### ChromeDrmCaptureService.ReceiveFullMessageAsync
**문제**: 큰 버퍼를 매번 할당

**해결**:
```csharp
// ArrayPool 사용
var buffer = ArrayPool<byte>.Shared.Rent(2 * 1024 * 1024);
try
{
    // 사용
}
finally
{
    ArrayPool<byte>.Shared.Return(buffer);
}
```

---

## 📊 개선 효과

| 항목 | 개선 전 | 개선 후 | 효과 |
|------|---------|---------|------|
| Frame Data 안정성 | Race Condition 위험 | 복사본 사용 | 안정성 ↑ |
| Thread Safety | 일반 변수 | Interlocked | 정확성 ↑ |
| Lock 범위 | 전체 캡처 | 최소 작업 | 성능 ↑ |
| Dispose 안전성 | 동기 대기 | 비동기 지원 | 응답성 ↑ |
| Task 관리 | Fire-and-forget | 명시적 관리 | 안정성 ↑ |
| 메모리 사용 | 매번 할당 | ArrayPool | 효율성 ↑ |

---

## 📁 수정된 파일 목록

1. **ChromeDrmCaptureService.cs**
   - Frame Data 복사본 전달
   - DisposeAsync 메서드 추가
   - ArrayPool 사용

2. **ScreenRecordingEngine.cs**
   - Interlocked frame counter
   - 항상 복사본 사용
   - DisposeAsync 메서드 추가
   - 인코딩 Task 관리 개선

3. **EnhancedDxgiCaptureService.cs**
   - Lock 범위 최소화
   - 동기화 개선

---

## ⚠️ Breaking Changes

### GetCurrentFrame() 사용 변경
```csharp
// ScreenRecordingEngine에서 이제 GetCurrentFrameCopy() 사용
_captureMode == CaptureMode.DXGI 
    ? _dxgiCapture.GetCurrentFrameCopy() 
    : _gdiCapture.GetCurrentFrameCopy()
```

### Dispose 사용 변경
```csharp
// 권장: 비동기 Dispose
await engine.DisposeAsync();

// 또는 기존 방식 (UI 스레드 주의)
engine.Dispose();
```

---

## 🎯 다음 단계 권장 사항

1. **테스트**: Race Condition이 해결되었는지 스트레스 테스트
2. **성능 측정**: Lock 범위 축소로 인한 FPS 향상 측정
3. **메모리 프로파일링**: ArrayPool 사용으로 GC 압력 감소 확인

---

*개선 작업 완료*
