using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace AudioRecorder.Services;

/// <summary>
/// 웹캠 오버레이 서비스 - 녹화 프레임에 웹캠 영상을 오버레이
/// </summary>
public class WebcamOverlayService : IDisposable
{
    // DirectShow 인터페이스 대신 Windows Media Foundation 사용을 위한 설정
    private bool _isEnabled;
    private string _position = "BottomRight";
    private string _size = "Small";
    private bool _disposed;

    // 웹캠 크기 설정
    private static readonly Dictionary<string, (int Width, int Height)> SizePresets = new()
    {
        { "Small", (160, 120) },
        { "Medium", (240, 180) },
        { "Large", (320, 240) }
    };

    // 현재 웹캠 프레임
    private byte[]? _currentFrame;
    private int _frameWidth;
    private int _frameHeight;
    private readonly object _frameLock = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public string Position
    {
        get => _position;
        set => _position = value;
    }

    public string Size
    {
        get => _size;
        set => _size = value;
    }

    /// <summary>
    /// 웹캠 오버레이를 프레임에 그리기
    /// </summary>
    public void DrawOverlay(byte[] frameData, int width, int height)
    {
        if (!_isEnabled || _currentFrame == null) return;

        try
        {
            lock (_frameLock)
            {
                if (_currentFrame == null) return;

                var (overlayWidth, overlayHeight) = SizePresets.GetValueOrDefault(_size, (160, 120));
                var (x, y) = CalculatePosition(width, height, overlayWidth, overlayHeight);

                // 웹캠 프레임을 메인 프레임에 오버레이 (간단한 복사)
                // 실제 구현에서는 웹캠 캡처 및 스케일링 필요
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                Marshal.Copy(frameData, 0, bmpData.Scan0, frameData.Length);
                bmp.UnlockBits(bmpData);

                // 웹캠 오버레이 그리기 (플레이스홀더 - 실제 웹캠 구현 필요)
                using var g = Graphics.FromImage(bmp);
                using var brush = new SolidBrush(Color.FromArgb(128, 128, 128, 128));
                g.FillEllipse(brush, x, y, overlayWidth, overlayHeight);

                // 결과를 다시 frameData에 복사
                bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(bmpData.Scan0, frameData, 0, frameData.Length);
                bmp.UnlockBits(bmpData);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[WebcamOverlay] 오버레이 그리기 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 오버레이 위치 계산
    /// </summary>
    private (int X, int Y) CalculatePosition(int frameWidth, int frameHeight, int overlayWidth, int overlayHeight)
    {
        const int margin = 20;

        return _position switch
        {
            "TopLeft" => (margin, margin),
            "TopRight" => (frameWidth - overlayWidth - margin, margin),
            "BottomLeft" => (margin, frameHeight - overlayHeight - margin),
            "BottomRight" => (frameWidth - overlayWidth - margin, frameHeight - overlayHeight - margin),
            _ => (frameWidth - overlayWidth - margin, frameHeight - overlayHeight - margin)
        };
    }

    /// <summary>
    /// 웹캠 프레임 업데이트 (외부에서 호출)
    /// </summary>
    public void UpdateFrame(byte[] frame, int width, int height)
    {
        lock (_frameLock)
        {
            _currentFrame = frame;
            _frameWidth = width;
            _frameHeight = height;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_frameLock)
        {
            _currentFrame = null;
        }
    }
}
