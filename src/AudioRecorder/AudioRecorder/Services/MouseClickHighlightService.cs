using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;

namespace AudioRecorder.Services;

/// <summary>
/// 마우스 클릭 강조 표시 서비스
/// 마우스 클릭을 감지하고 프레임에 강조 효과를 그립니다.
/// </summary>
public class MouseClickHighlightService : IDisposable
{
    #region Win32 API

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X;
        public int Y;
    }

    private const int VK_LBUTTON = 0x01;
    private const int VK_RBUTTON = 0x02;
    private const int VK_MBUTTON = 0x04;

    #endregion

    private readonly List<ClickEffect> _activeEffects = new();
    private readonly object _lock = new();
    private bool _isEnabled;
    private bool _disposed;

    // 클릭 효과 설정
    private readonly Color _leftClickColor = Color.FromArgb(180, 255, 100, 100);   // 빨간색 (좌클릭)
    private readonly Color _rightClickColor = Color.FromArgb(180, 100, 100, 255);  // 파란색 (우클릭)
    private readonly Color _middleClickColor = Color.FromArgb(180, 100, 255, 100); // 녹색 (휠클릭)
    private const int EFFECT_RADIUS = 25;
    private const int EFFECT_DURATION_MS = 300;

    // 마우스 상태 추적
    private bool _wasLeftPressed;
    private bool _wasRightPressed;
    private bool _wasMiddlePressed;

    /// <summary>
    /// 마우스 클릭 강조 활성화 여부
    /// </summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set => _isEnabled = value;
    }

    /// <summary>
    /// 마우스 상태 업데이트 (프레임마다 호출)
    /// </summary>
    public void Update()
    {
        if (!_isEnabled) return;

        // 현재 마우스 상태 확인
        bool isLeftPressed = (GetAsyncKeyState(VK_LBUTTON) & 0x8000) != 0;
        bool isRightPressed = (GetAsyncKeyState(VK_RBUTTON) & 0x8000) != 0;
        bool isMiddlePressed = (GetAsyncKeyState(VK_MBUTTON) & 0x8000) != 0;

        // 마우스 위치 가져오기
        GetCursorPos(out POINT cursorPos);

        lock (_lock)
        {
            // 좌클릭 감지 (눌림 상태로 변경될 때)
            if (isLeftPressed && !_wasLeftPressed)
            {
                _activeEffects.Add(new ClickEffect(cursorPos.X, cursorPos.Y, _leftClickColor, DateTime.Now));
            }

            // 우클릭 감지
            if (isRightPressed && !_wasRightPressed)
            {
                _activeEffects.Add(new ClickEffect(cursorPos.X, cursorPos.Y, _rightClickColor, DateTime.Now));
            }

            // 휠클릭 감지
            if (isMiddlePressed && !_wasMiddlePressed)
            {
                _activeEffects.Add(new ClickEffect(cursorPos.X, cursorPos.Y, _middleClickColor, DateTime.Now));
            }

            // 만료된 효과 제거
            var now = DateTime.Now;
            _activeEffects.RemoveAll(e => (now - e.StartTime).TotalMilliseconds > EFFECT_DURATION_MS);
        }

        // 상태 업데이트
        _wasLeftPressed = isLeftPressed;
        _wasRightPressed = isRightPressed;
        _wasMiddlePressed = isMiddlePressed;
    }

    /// <summary>
    /// 프레임에 클릭 효과 그리기
    /// </summary>
    /// <param name="frameData">프레임 데이터 (BGRA 형식)</param>
    /// <param name="width">프레임 너비</param>
    /// <param name="height">프레임 높이</param>
    /// <param name="captureX">캡처 영역 X 오프셋</param>
    /// <param name="captureY">캡처 영역 Y 오프셋</param>
    public void DrawEffects(byte[] frameData, int width, int height, int captureX = 0, int captureY = 0)
    {
        if (!_isEnabled) return;

        List<ClickEffect> effectsToDraw;
        lock (_lock)
        {
            if (_activeEffects.Count == 0) return;
            effectsToDraw = new List<ClickEffect>(_activeEffects);
        }

        // Bitmap 생성하여 효과 그리기
        using var bitmap = new Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        // frameData를 Bitmap에 복사
        var bmpData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.WriteOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        Marshal.Copy(frameData, 0, bmpData.Scan0, frameData.Length);
        bitmap.UnlockBits(bmpData);

        // Graphics로 효과 그리기
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;

            var now = DateTime.Now;
            foreach (var effect in effectsToDraw)
            {
                DrawClickEffect(g, effect, now, captureX, captureY);
            }
        }

        // 수정된 Bitmap을 다시 frameData에 복사
        bmpData = bitmap.LockBits(
            new Rectangle(0, 0, width, height),
            System.Drawing.Imaging.ImageLockMode.ReadOnly,
            System.Drawing.Imaging.PixelFormat.Format32bppArgb);

        Marshal.Copy(bmpData.Scan0, frameData, 0, frameData.Length);
        bitmap.UnlockBits(bmpData);
    }

    private void DrawClickEffect(Graphics g, ClickEffect effect, DateTime now, int captureX, int captureY)
    {
        double elapsed = (now - effect.StartTime).TotalMilliseconds;
        double progress = Math.Min(1.0, elapsed / EFFECT_DURATION_MS);

        // 애니메이션: 원이 커지면서 투명해짐
        int radius = (int)(EFFECT_RADIUS * (0.5 + progress * 0.5));
        int alpha = (int)(effect.Color.A * (1.0 - progress));

        if (alpha <= 0) return;

        // 화면 좌표를 캡처 영역 좌표로 변환
        int x = effect.X - captureX;
        int y = effect.Y - captureY;

        // 캡처 영역 밖이면 그리지 않음
        if (x < -radius || y < -radius) return;

        var color = Color.FromArgb(alpha, effect.Color.R, effect.Color.G, effect.Color.B);

        // 원형 그라데이션 효과
        using var path = new GraphicsPath();
        path.AddEllipse(x - radius, y - radius, radius * 2, radius * 2);

        using var brush = new PathGradientBrush(path)
        {
            CenterColor = color,
            SurroundColors = new[] { Color.FromArgb(0, effect.Color.R, effect.Color.G, effect.Color.B) }
        };

        g.FillEllipse(brush, x - radius, y - radius, radius * 2, radius * 2);

        // 테두리
        using var pen = new Pen(Color.FromArgb(alpha, 255, 255, 255), 2);
        g.DrawEllipse(pen, x - radius, y - radius, radius * 2, radius * 2);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        lock (_lock)
        {
            _activeEffects.Clear();
        }
    }

    /// <summary>
    /// 클릭 효과 정보
    /// </summary>
    private class ClickEffect
    {
        public int X { get; }
        public int Y { get; }
        public Color Color { get; }
        public DateTime StartTime { get; }

        public ClickEffect(int x, int y, Color color, DateTime startTime)
        {
            X = x;
            Y = y;
            Color = color;
            StartTime = startTime;
        }
    }
}
