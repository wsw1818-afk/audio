using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace AudioRecorder.Services;

/// <summary>
/// 워터마크 서비스 - 녹화 프레임에 텍스트 워터마크 추가
/// </summary>
public class WatermarkService : IDisposable
{
    private bool _isEnabled;
    private string _text = string.Empty;
    private string _position = "BottomRight";
    private float _opacity = 0.5f;
    private int _fontSize = 16;
    private bool _disposed;

    private Font? _font;
    private readonly object _lock = new();

    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    public string Text
    {
        get => _text;
        set
        {
            _text = value;
            _isEnabled = !string.IsNullOrWhiteSpace(value);
        }
    }

    public string Position
    {
        get => _position;
        set => _position = value;
    }

    public float Opacity
    {
        get => _opacity;
        set => _opacity = Math.Clamp(value, 0f, 1f);
    }

    public int FontSize
    {
        get => _fontSize;
        set
        {
            _fontSize = Math.Clamp(value, 8, 72);
            UpdateFont();
        }
    }

    public WatermarkService()
    {
        UpdateFont();
    }

    private void UpdateFont()
    {
        lock (_lock)
        {
            _font?.Dispose();
            _font = new Font("Segoe UI", _fontSize, FontStyle.Regular);
        }
    }

    /// <summary>
    /// 워터마크를 프레임에 그리기
    /// </summary>
    public void DrawWatermark(byte[] frameData, int width, int height)
    {
        if (!_isEnabled || string.IsNullOrWhiteSpace(_text)) return;

        try
        {
            lock (_lock)
            {
                using var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                var bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

                Marshal.Copy(frameData, 0, bmpData.Scan0, frameData.Length);
                bmp.UnlockBits(bmpData);

                using var g = Graphics.FromImage(bmp);
                g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
                g.SmoothingMode = SmoothingMode.AntiAlias;

                // 텍스트 크기 측정
                var textSize = g.MeasureString(_text, _font!);
                var (x, y) = CalculatePosition(width, height, textSize);

                // 반투명 배경
                var bgAlpha = (int)(128 * _opacity);
                using var bgBrush = new SolidBrush(Color.FromArgb(bgAlpha, 0, 0, 0));
                var bgRect = new RectangleF(x - 5, y - 2, textSize.Width + 10, textSize.Height + 4);
                g.FillRectangle(bgBrush, bgRect);

                // 텍스트 그리기
                var textAlpha = (int)(255 * _opacity);
                using var textBrush = new SolidBrush(Color.FromArgb(textAlpha, 255, 255, 255));
                g.DrawString(_text, _font!, textBrush, x, y);

                // 결과를 다시 frameData에 복사
                bmpData = bmp.LockBits(new Rectangle(0, 0, width, height),
                    ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
                Marshal.Copy(bmpData.Scan0, frameData, 0, frameData.Length);
                bmp.UnlockBits(bmpData);
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[Watermark] 워터마크 그리기 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 워터마크 위치 계산
    /// </summary>
    private (float X, float Y) CalculatePosition(int frameWidth, int frameHeight, SizeF textSize)
    {
        const int margin = 15;

        return _position switch
        {
            "TopLeft" => (margin, margin),
            "TopRight" => (frameWidth - textSize.Width - margin, margin),
            "BottomLeft" => (margin, frameHeight - textSize.Height - margin),
            "BottomRight" => (frameWidth - textSize.Width - margin, frameHeight - textSize.Height - margin),
            "Center" => ((frameWidth - textSize.Width) / 2, (frameHeight - textSize.Height) / 2),
            _ => (frameWidth - textSize.Width - margin, frameHeight - textSize.Height - margin)
        };
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _font?.Dispose();
            _font = null;
        }
    }
}
