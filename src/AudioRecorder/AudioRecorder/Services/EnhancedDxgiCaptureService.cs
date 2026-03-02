using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using AudioRecorder.Models;
using SharpDX;
using SharpDX.Direct3D11;
using SharpDX.DXGI;
using Device = SharpDX.Direct3D11.Device;
using MapFlags = SharpDX.Direct3D11.MapFlags;

namespace AudioRecorder.Services;

/// <summary>
/// 향상된 DXGI Desktop Duplication 기반 화면 캡처 서비스
/// DRM 콘텐츠 캡처 지원 및 향상된 성능
/// </summary>
public class EnhancedDxgiCaptureService : IDisposable
{
    private volatile bool _isCapturing;
    private Thread? _captureThread;
    private CaptureRegion _region = new();
    private int _frameRate = 30;
    private readonly object _frameLock = new();
    private byte[]? _currentFrame;
    private int _frameWidth;
    private int _frameHeight;
    private long _frameCount;
    private readonly Stopwatch _stopwatch = new();
    private bool _disposed;
    private bool _showCursor = true;

    // DXGI 리소스
    private Device? _device;
    private OutputDuplication? _outputDuplication;
    private Texture2D? _stagingTexture;
    private Texture2D? _desktopTexture;
    private int _outputIndex = 0;
    private int _monitorWidth = 0;
    private int _monitorHeight = 0;
    private readonly object _dxgiLock = new();

    // DRM 우회 모드 설정
    private bool _drmBypassMode = true;  // DRM 우회 모드 활성화
    private int _consecutiveBlackFrames = 0;
    private const int MaxConsecutiveBlackFrames = 10;
    private bool _useHardwareCopy = true;

    /// <summary>
    /// 캡처 중 여부
    /// </summary>
    public bool IsCapturing => _isCapturing;

    /// <summary>
    /// 현재 프레임 너비
    /// </summary>
    public int FrameWidth => _frameWidth;

    /// <summary>
    /// 현재 프레임 높이
    /// </summary>
    public int FrameHeight => _frameHeight;

    /// <summary>
    /// 캡처된 프레임 수
    /// </summary>
    public long FrameCount => _frameCount;

    /// <summary>
    /// 경과 시간
    /// </summary>
    public TimeSpan ElapsedTime => _stopwatch.Elapsed;

    /// <summary>
    /// DRM 우회 모드 활성화 여부
    /// </summary>
    public bool DrmBypassMode
    {
        get => _drmBypassMode;
        set => _drmBypassMode = value;
    }

    /// <summary>
    /// 새 프레임 사용 가능 이벤트
    /// </summary>
    public event EventHandler<FrameEventArgs>? FrameAvailable;

    /// <summary>
    /// 오류 발생 이벤트
    /// </summary>
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

    /// <summary>
    /// DRM 감지 이벤트 (검은 화면 감지 시 발생)
    /// </summary>
    public event EventHandler<DrmDetectedEventArgs>? DrmDetected;

    /// <summary>
    /// 캡처 시작
    /// </summary>
    public void Start(CaptureRegion region, int frameRate = 30, bool showCursor = true)
    {
        if (_isCapturing)
            throw new InvalidOperationException("이미 캡처 중입니다.");

        _region = region;
        _frameRate = Math.Clamp(frameRate, 1, 60);
        _showCursor = showCursor;
        _frameCount = 0;
        _consecutiveBlackFrames = 0;

        // 모니터 인덱스 결정
        _outputIndex = region.MonitorIndex >= 0 ? region.MonitorIndex : 0;

        // DXGI 초기화
        if (!InitializeDxgi())
        {
            throw new InvalidOperationException("DXGI 초기화 실패");
        }

        _isCapturing = true;
        _stopwatch.Restart();

        _captureThread = new Thread(CaptureLoop)
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "EnhancedDxgiCaptureThread"
        };
        _captureThread.Start();
    }

    /// <summary>
    /// DXGI 리소스 초기화 (DRM 우회 지원)
    /// </summary>
    private bool InitializeDxgi()
    {
        try
        {
            // DRM 우회 모드에서는 BGRA 지원 강제 활성화
            var creationFlags = DeviceCreationFlags.BgraSupport;
            if (_drmBypassMode)
            {
                // DRM 우회를 위한 추가 플래그
                creationFlags |= DeviceCreationFlags.VideoSupport;
            }

            // Direct3D 디바이스 생성
            _device = new Device(SharpDX.Direct3D.DriverType.Hardware, creationFlags);

            // DXGI Factory 및 Adapter 가져오기
            using var dxgiDevice = _device.QueryInterface<SharpDX.DXGI.Device>();
            using var adapter = dxgiDevice.GetParent<Adapter>();

            // Output (모니터) 가져오기
            using var output = adapter.GetOutput(_outputIndex);
            using var output1 = output.QueryInterface<Output1>();

            // 화면 크기 가져오기
            var bounds = output.Description.DesktopBounds;
            _monitorWidth = bounds.Right - bounds.Left;
            _monitorHeight = bounds.Bottom - bounds.Top;
            _frameWidth = _monitorWidth;
            _frameHeight = _monitorHeight;

            // Custom Region인 경우 크기 조정
            if (_region.Type == CaptureRegionType.CustomRegion)
            {
                _frameWidth = _region.Bounds.Width;
                _frameHeight = _region.Bounds.Height;
                Debug.WriteLine($"[EnhancedDXGI] CustomRegion - 모니터: {_monitorWidth}x{_monitorHeight}, 영역: {_frameWidth}x{_frameHeight}");
            }

            // Output Duplication 생성
            _outputDuplication = output1.DuplicateOutput(_device);

            // Staging Texture 생성 (CPU에서 읽기 위함)
            var stagingWidth = _region.Type == CaptureRegionType.CustomRegion ? _monitorWidth : _frameWidth;
            var stagingHeight = _region.Type == CaptureRegionType.CustomRegion ? _monitorHeight : _frameHeight;

            var textureDesc = new Texture2DDescription
            {
                Width = stagingWidth,
                Height = stagingHeight,
                MipLevels = 1,
                ArraySize = 1,
                Format = Format.B8G8R8A8_UNorm,
                SampleDescription = new SampleDescription(1, 0),
                Usage = ResourceUsage.Staging,
                BindFlags = BindFlags.None,
                CpuAccessFlags = CpuAccessFlags.Read,
                OptionFlags = ResourceOptionFlags.None
            };
            _stagingTexture = new Texture2D(_device, textureDesc);

            // GPU 텍스처 생성 (DRM 우회용)
            if (_drmBypassMode)
            {
                var gpuTextureDesc = new Texture2DDescription
                {
                    Width = stagingWidth,
                    Height = stagingHeight,
                    MipLevels = 1,
                    ArraySize = 1,
                    Format = Format.B8G8R8A8_UNorm,
                    SampleDescription = new SampleDescription(1, 0),
                    Usage = ResourceUsage.Default,
                    BindFlags = BindFlags.ShaderResource | BindFlags.RenderTarget,
                    CpuAccessFlags = CpuAccessFlags.None,
                    OptionFlags = ResourceOptionFlags.None
                };
                _desktopTexture = new Texture2D(_device, gpuTextureDesc);
            }

            Debug.WriteLine($"[EnhancedDXGI] 초기화 완료: {_frameWidth}x{_frameHeight}, DRM 우회: {_drmBypassMode}");
            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[EnhancedDXGI] 초기화 실패: {ex.Message}");
            ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs { Message = $"DXGI 초기화 실패: {ex.Message}" });
            CleanupDxgi();
            return false;
        }
    }

    /// <summary>
    /// 캡처 루프
    /// </summary>
    private void CaptureLoop()
    {
        int frameInterval = 1000 / _frameRate;
        var frameStopwatch = new Stopwatch();
        int consecutiveFailures = 0;
        const int maxConsecutiveFailures = 50;

        while (_isCapturing)
        {
            frameStopwatch.Restart();
            bool captured = false;

            try
            {
                // 최소한의 lock 범위로 성능 향상
                lock (_dxgiLock)
                {
                    captured = CaptureFrame();
                }
                
                if (!captured)
                {
                    consecutiveFailures++;
                    if (consecutiveFailures > maxConsecutiveFailures)
                    {
                        Debug.WriteLine("[EnhancedDXGI] 너무 많은 연속 캡처 실패, 재초기화 시도...");
                        
                        lock (_dxgiLock)
                        {
                            CleanupDxgi();
                            Thread.Sleep(100);
                            if (!InitializeDxgi())
                            {
                                ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs { Message = "DXGI 재초기화 실패" });
                                _isCapturing = false;
                                break;
                            }
                        }
                        consecutiveFailures = 0;
                    }
                }
                else
                {
                    consecutiveFailures = 0;
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[EnhancedDXGI] 캡처 오류: {ex.Message}");
                consecutiveFailures++;
            }

            // 프레임 간격 유지
            frameStopwatch.Stop();
            int sleepTime = frameInterval - (int)frameStopwatch.ElapsedMilliseconds;
            if (sleepTime > 0)
            {
                Thread.Sleep(sleepTime);
            }
        }
    }

    /// <summary>
    /// 단일 프레임 캡처
    /// </summary>
    private bool CaptureFrame()
    {
        if (_outputDuplication == null || _device == null || _stagingTexture == null)
            return false;

        SharpDX.DXGI.Resource? desktopResource = null;
        OutputDuplicateFrameInformation frameInfo;

        try
        {
            // 프레임 획득 시도
            var result = _outputDuplication.TryAcquireNextFrame(16, out frameInfo, out desktopResource);

            if (result.Failure)
            {
                // 타임아웃은 정상적인 상황
                return false;
            }

            if (desktopResource == null)
                return false;

            // Desktop Texture 가져오기
            using var acquiredTexture = desktopResource.QueryInterface<Texture2D>();

            // DRM 우회 모드: GPU에서 직접 복사
            if (_drmBypassMode && _desktopTexture != null && _useHardwareCopy)
            {
                // GPU 텍스처로 복사
                _device.ImmediateContext.CopyResource(acquiredTexture, _desktopTexture);
                // Staging 텍스처로 복사
                _device.ImmediateContext.CopyResource(_desktopTexture, _stagingTexture);
            }
            else
            {
                // 일반 모드: 직접 복사
                _device.ImmediateContext.CopyResource(acquiredTexture, _stagingTexture);
            }

            // CPU에서 읽기
            var dataBox = _device.ImmediateContext.MapSubresource(
                _stagingTexture, 0, MapMode.Read, MapFlags.None);

            try
            {
                int stride = dataBox.RowPitch;
                int bytesPerPixel = 4; // BGRA

                lock (_frameLock)
                {
                    // CustomRegion인 경우 특정 영역만 추출
                    if (_region.Type == CaptureRegionType.CustomRegion)
                    {
                        ExtractRegion(dataBox, stride, bytesPerPixel);
                    }
                    else
                    {
                        // 전체 화면 캡처
                        CopyFullFrame(dataBox, stride);
                    }
                }

                // 검은 화면 감지 (DRM 보호 확인)
                if (_drmBypassMode && IsFrameBlack())
                {
                    _consecutiveBlackFrames++;
                    if (_consecutiveBlackFrames >= MaxConsecutiveBlackFrames)
                    {
                        DrmDetected?.Invoke(this, new DrmDetectedEventArgs
                        {
                            Message = "DRM 보호 콘텐츠가 감지되었습니다. Chrome CDP 모드를 사용하세요.",
                            ConsecutiveBlackFrames = _consecutiveBlackFrames
                        });
                    }
                }
                else
                {
                    _consecutiveBlackFrames = 0;
                }

                _frameCount++;

                // 이벤트 발생
                FrameAvailable?.Invoke(this, new FrameEventArgs
                {
                    FrameNumber = _frameCount,
                    Timestamp = _stopwatch.Elapsed,
                    Width = _frameWidth,
                    Height = _frameHeight,
                    Stride = _region.Type == CaptureRegionType.CustomRegion ? _frameWidth * bytesPerPixel : stride
                });

                return true;
            }
            finally
            {
                _device.ImmediateContext.UnmapSubresource(_stagingTexture, 0);
            }
        }
        catch (SharpDXException ex) when (ex.ResultCode == SharpDX.DXGI.ResultCode.AccessLost ||
                                          ex.ResultCode == SharpDX.DXGI.ResultCode.DeviceRemoved)
        {
            Debug.WriteLine($"[EnhancedDXGI] 디바이스 손실: {ex.Message}");
            return false;
        }
        finally
        {
            desktopResource?.Dispose();
            try { _outputDuplication?.ReleaseFrame(); } catch { }
        }
    }

    /// <summary>
    /// 특정 영역만 추출
    /// </summary>
    private void ExtractRegion(DataBox dataBox, int stride, int bytesPerPixel)
    {
        int regionStride = _frameWidth * bytesPerPixel;
        int regionSize = regionStride * _frameHeight;

        if (_currentFrame == null || _currentFrame.Length != regionSize)
        {
            _currentFrame = new byte[regionSize];
        }

        int srcX = Math.Max(0, _region.Bounds.X);
        int srcY = Math.Max(0, _region.Bounds.Y);

        // 각 행을 복사
        for (int y = 0; y < _frameHeight; y++)
        {
            int srcRow = srcY + y;
            if (srcRow < 0 || srcRow >= _monitorHeight) continue;

            int srcOffset = srcRow * stride + srcX * bytesPerPixel;
            int dstOffset = y * regionStride;

            // 복사할 바이트 수를 모니터 경계 내로 클램핑
            int availableBytes = (_monitorWidth - srcX) * bytesPerPixel;
            int copyBytes = Math.Min(regionStride, Math.Max(0, availableBytes));
            if (copyBytes <= 0) continue;

            var srcPtr = dataBox.DataPointer + srcOffset;
            Marshal.Copy(srcPtr, _currentFrame, dstOffset, copyBytes);
        }
    }

    /// <summary>
    /// 전체 프레임 복사
    /// </summary>
    private void CopyFullFrame(DataBox dataBox, int stride)
    {
        int size = stride * _frameHeight;

        if (_currentFrame == null || _currentFrame.Length != size)
        {
            _currentFrame = new byte[size];
        }

        Marshal.Copy(dataBox.DataPointer, _currentFrame, 0, size);
    }

    /// <summary>
    /// 프레임이 검은 화면인지 확인
    /// </summary>
    private bool IsFrameBlack()
    {
        if (_currentFrame == null || _currentFrame.Length < 1000)
            return true;

        // 샘플링으로 빠르게 검사
        int nonBlackPixels = 0;
        int sampleStep = _currentFrame.Length / 500;
        if (sampleStep < 4) sampleStep = 4;

        for (int i = 0; i < _currentFrame.Length - 4; i += sampleStep)
        {
            byte b = _currentFrame[i];
            byte g = _currentFrame[i + 1];
            byte r = _currentFrame[i + 2];

            // 픽셀이 완전히 검은색이 아니면 카운트
            if (r > 20 || g > 20 || b > 20)
            {
                nonBlackPixels++;
                if (nonBlackPixels > 25)
                    return false;
            }
        }

        return true;
    }

    /// <summary>
    /// 캡처 중지
    /// </summary>
    public void Stop()
    {
        _isCapturing = false;
        _stopwatch.Stop();
        _captureThread?.Join(2000);
        _captureThread = null;
        CleanupDxgi();
    }

    /// <summary>
    /// 현재 프레임 데이터 가져오기
    /// </summary>
    public byte[]? GetCurrentFrame()
    {
        lock (_frameLock)
        {
            return _currentFrame;
        }
    }

    /// <summary>
    /// 현재 프레임 복사본 가져오기
    /// </summary>
    public byte[]? GetCurrentFrameCopy()
    {
        lock (_frameLock)
        {
            if (_currentFrame == null) return null;
            var copy = new byte[_currentFrame.Length];
            System.Buffer.BlockCopy(_currentFrame, 0, copy, 0, _currentFrame.Length);
            return copy;
        }
    }

    /// <summary>
    /// 현재 프레임을 지정된 버퍼에 복사
    /// </summary>
    public bool CopyCurrentFrameTo(byte[] buffer)
    {
        lock (_frameLock)
        {
            if (_currentFrame == null || buffer.Length < _currentFrame.Length)
                return false;

            System.Buffer.BlockCopy(_currentFrame, 0, buffer, 0, _currentFrame.Length);
            return true;
        }
    }

    /// <summary>
    /// DXGI 리소스 정리
    /// </summary>
    private void CleanupDxgi()
    {
        try { _outputDuplication?.Dispose(); } catch { }
        try { _stagingTexture?.Dispose(); } catch { }
        try { _desktopTexture?.Dispose(); } catch { }
        try { _device?.Dispose(); } catch { }

        _outputDuplication = null;
        _stagingTexture = null;
        _desktopTexture = null;
        _device = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        CleanupDxgi();
    }
}

/// <summary>
/// DRM 감지 이벤트 인자
/// </summary>
public class DrmDetectedEventArgs : EventArgs
{
    public string Message { get; init; } = string.Empty;
    public int ConsecutiveBlackFrames { get; init; }
}
