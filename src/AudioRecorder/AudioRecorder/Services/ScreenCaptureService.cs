using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using AudioRecorder.Models;

namespace AudioRecorder.Services;

/// <summary>
/// 화면 캡처 서비스 - GDI+ 기반 (안정적, 호환성 높음)
/// </summary>
public class ScreenCaptureService : IDisposable
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

    // 커서 관련
    private bool _showCursor = true;

    // Win32 API for cursor
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(out CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIcon(IntPtr hDC, int x, int y, IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct CURSORINFO
    {
        public int cbSize;
        public int flags;
        public IntPtr hCursor;
        public POINT ptScreenPos;
    }

    private const int CURSOR_SHOWING = 0x00000001;

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
    /// 새 프레임 사용 가능 이벤트
    /// </summary>
    public event EventHandler<FrameEventArgs>? FrameAvailable;

    /// <summary>
    /// 오류 발생 이벤트
    /// </summary>
    public event EventHandler<CaptureErrorEventArgs>? ErrorOccurred;

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

        // 캡처 영역 설정
        Rectangle bounds = GetCaptureBounds(region);
        _frameWidth = bounds.Width;
        _frameHeight = bounds.Height;

        if (_frameWidth <= 0 || _frameHeight <= 0)
            throw new ArgumentException("캡처 영역이 유효하지 않습니다.");

        _isCapturing = true;
        _stopwatch.Restart();

        _captureThread = new Thread(() => CaptureLoop(bounds))
        {
            IsBackground = true,
            Priority = ThreadPriority.AboveNormal,
            Name = "ScreenCaptureThread"
        };
        _captureThread.Start();
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
    }

    /// <summary>
    /// 캡처 영역 계산
    /// </summary>
    private Rectangle GetCaptureBounds(CaptureRegion region)
    {
        switch (region.Type)
        {
            case CaptureRegionType.FullScreen:
                var screens = System.Windows.Forms.Screen.AllScreens;
                if (region.MonitorIndex >= 0 && region.MonitorIndex < screens.Length)
                {
                    var screen = screens[region.MonitorIndex];
                    return new Rectangle(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height);
                }
                // 기본: 주 모니터
                var primary = System.Windows.Forms.Screen.PrimaryScreen;
                return primary != null
                    ? new Rectangle(primary.Bounds.X, primary.Bounds.Y, primary.Bounds.Width, primary.Bounds.Height)
                    : new Rectangle(0, 0, 1920, 1080);

            case CaptureRegionType.Window:
                if (region.WindowHandle != IntPtr.Zero)
                {
                    if (GetWindowRect(region.WindowHandle, out RECT rect))
                    {
                        return new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);
                    }
                }
                throw new ArgumentException("창 핸들이 유효하지 않습니다.");

            case CaptureRegionType.CustomRegion:
                return region.Bounds;

            default:
                throw new ArgumentException("알 수 없는 캡처 유형입니다.");
        }
    }

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    /// <summary>
    /// 캡처 루프
    /// </summary>
    private void CaptureLoop(Rectangle bounds)
    {
        int frameInterval = 1000 / _frameRate;
        var frameStopwatch = new Stopwatch();

        // 재사용 가능한 비트맵
        using var bitmap = new Bitmap(bounds.Width, bounds.Height, PixelFormat.Format32bppArgb);
        using var graphics = Graphics.FromImage(bitmap);
        graphics.CompositingQuality = System.Drawing.Drawing2D.CompositingQuality.HighSpeed;
        graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;

        while (_isCapturing)
        {
            frameStopwatch.Restart();

            try
            {
                // 화면 캡처
                graphics.CopyFromScreen(bounds.Location, Point.Empty, bounds.Size, CopyPixelOperation.SourceCopy);

                // 커서 그리기
                if (_showCursor)
                {
                    DrawCursor(graphics, bounds);
                }

                // 프레임 데이터 추출
                var bitmapData = bitmap.LockBits(
                    new Rectangle(0, 0, bounds.Width, bounds.Height),
                    ImageLockMode.ReadOnly,
                    PixelFormat.Format32bppArgb);

                try
                {
                    int stride = bitmapData.Stride;
                    int size = stride * bounds.Height;

                    lock (_frameLock)
                    {
                        if (_currentFrame == null || _currentFrame.Length != size)
                        {
                            _currentFrame = new byte[size];
                        }
                        Marshal.Copy(bitmapData.Scan0, _currentFrame, 0, size);
                    }

                    _frameCount++;

                    // 이벤트 발생
                    FrameAvailable?.Invoke(this, new FrameEventArgs
                    {
                        FrameNumber = _frameCount,
                        Timestamp = _stopwatch.Elapsed,
                        Width = bounds.Width,
                        Height = bounds.Height,
                        Stride = stride
                    });
                }
                finally
                {
                    bitmap.UnlockBits(bitmapData);
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"캡처 오류: {ex.Message}");
                ErrorOccurred?.Invoke(this, new CaptureErrorEventArgs { Message = ex.Message });
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
    /// 커서 그리기
    /// </summary>
    private void DrawCursor(Graphics graphics, Rectangle captureArea)
    {
        try
        {
            CURSORINFO cursorInfo;
            cursorInfo.cbSize = Marshal.SizeOf<CURSORINFO>();

            if (GetCursorInfo(out cursorInfo) && (cursorInfo.flags & CURSOR_SHOWING) != 0)
            {
                // 커서 위치가 캡처 영역 내에 있는지 확인
                int cursorX = cursorInfo.ptScreenPos.X - captureArea.X;
                int cursorY = cursorInfo.ptScreenPos.Y - captureArea.Y;

                if (cursorX >= 0 && cursorX < captureArea.Width &&
                    cursorY >= 0 && cursorY < captureArea.Height)
                {
                    var hdc = graphics.GetHdc();
                    try
                    {
                        DrawIcon(hdc, cursorX, cursorY, cursorInfo.hCursor);
                    }
                    finally
                    {
                        graphics.ReleaseHdc(hdc);
                    }
                }
            }
        }
        catch { /* 커서 그리기 실패 무시 */ }
    }

    /// <summary>
    /// 현재 프레임 데이터 가져오기
    /// </summary>
    public byte[]? GetCurrentFrame()
    {
        lock (_frameLock)
        {
            if (_currentFrame == null) return null;
            var copy = new byte[_currentFrame.Length];
            Buffer.BlockCopy(_currentFrame, 0, copy, 0, _currentFrame.Length);
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

            Buffer.BlockCopy(_currentFrame, 0, buffer, 0, _currentFrame.Length);
            return true;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
    }
}

/// <summary>
/// 프레임 이벤트 인자
/// </summary>
public class FrameEventArgs : EventArgs
{
    public long FrameNumber { get; init; }
    public TimeSpan Timestamp { get; init; }
    public int Width { get; init; }
    public int Height { get; init; }
    public int Stride { get; init; }
}

/// <summary>
/// 캡처 오류 이벤트 인자
/// </summary>
public class CaptureErrorEventArgs : EventArgs
{
    public string Message { get; init; } = string.Empty;
}
