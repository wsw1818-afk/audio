using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace AudioRecorder.Services;

/// <summary>
/// 녹화 영역 주위에 테두리를 표시하는 서비스
/// </summary>
public class RecordingBorderService : IDisposable
{
    private Window? _borderWindow;
    private readonly DispatcherTimer? _blinkTimer;
    private bool _isVisible = true;
    private readonly int _borderThickness = 6;  // 더 두꺼운 테두리
    private readonly System.Windows.Media.Color _borderColor = System.Windows.Media.Color.FromRgb(0xFF, 0x00, 0x00);

    public bool IsEnabled { get; set; }
    public bool BlinkEnabled { get; set; } = true;

    public RecordingBorderService()
    {
        _blinkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(500)
        };
        _blinkTimer.Tick += (s, e) =>
        {
            if (BlinkEnabled && _borderWindow != null)
            {
                _isVisible = !_isVisible;
                _borderWindow.Opacity = _isVisible ? 1.0 : 0.3;
            }
        };
    }

    /// <summary>
    /// 녹화 영역 주위에 테두리 창 표시
    /// </summary>
    public void Show(System.Drawing.Rectangle region)
    {
        System.Diagnostics.Debug.WriteLine($"[RecordingBorder] Show() 호출됨 - IsEnabled:{IsEnabled}, Region: {region.X},{region.Y},{region.Width},{region.Height}");

        if (!IsEnabled) return;

        // UI 스레드에서 실행
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            System.Diagnostics.Debug.WriteLine("[RecordingBorder] Dispatcher.Invoke 시작");
            Hide();

            // 테두리는 항상 녹화 영역 안쪽에 표시
            // 전체 화면/영역 모두 동일하게 처리
            double windowLeft = region.X;
            double windowTop = region.Y;
            double windowWidth = region.Width;
            double windowHeight = region.Height;
            double innerWidth = region.Width - (_borderThickness * 2);
            double innerHeight = region.Height - (_borderThickness * 2);

            _borderWindow = new Window
            {
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = System.Windows.Media.Brushes.Transparent,
                Topmost = true,
                ShowInTaskbar = false,
                Left = windowLeft,
                Top = windowTop,
                Width = windowWidth,
                Height = windowHeight,
                ResizeMode = ResizeMode.NoResize,
                ShowActivated = false
            };

            // 마우스 클릭 통과 설정
            _borderWindow.SourceInitialized += (s, e) =>
            {
                SetWindowClickThrough(_borderWindow);
            };

            // 테두리 사각형 생성
            var canvas = new Canvas();

            // 상단 테두리
            var topBorder = CreateBorderRectangle(windowWidth, _borderThickness);
            Canvas.SetLeft(topBorder, 0);
            Canvas.SetTop(topBorder, 0);
            canvas.Children.Add(topBorder);

            // 하단 테두리
            var bottomBorder = CreateBorderRectangle(windowWidth, _borderThickness);
            Canvas.SetLeft(bottomBorder, 0);
            Canvas.SetTop(bottomBorder, windowHeight - _borderThickness);
            canvas.Children.Add(bottomBorder);

            // 좌측 테두리
            var leftBorder = CreateBorderRectangle(_borderThickness, innerHeight);
            Canvas.SetLeft(leftBorder, 0);
            Canvas.SetTop(leftBorder, _borderThickness);
            canvas.Children.Add(leftBorder);

            // 우측 테두리
            var rightBorder = CreateBorderRectangle(_borderThickness, innerHeight);
            Canvas.SetLeft(rightBorder, windowWidth - _borderThickness);
            Canvas.SetTop(rightBorder, _borderThickness);
            canvas.Children.Add(rightBorder);

            _borderWindow.Content = canvas;
            _borderWindow.Show();
            System.Diagnostics.Debug.WriteLine($"[RecordingBorder] 창 표시됨 - Left:{_borderWindow.Left}, Top:{_borderWindow.Top}, W:{_borderWindow.Width}, H:{_borderWindow.Height}");

            if (BlinkEnabled)
            {
                _blinkTimer?.Start();
            }
        });
    }

    private System.Windows.Shapes.Rectangle CreateBorderRectangle(double width, double height)
    {
        return new System.Windows.Shapes.Rectangle
        {
            Width = width,
            Height = height,
            Fill = new SolidColorBrush(_borderColor)
        };
    }

    /// <summary>
    /// 창을 마우스 클릭 통과 설정
    /// </summary>
    private void SetWindowClickThrough(Window window)
    {
        var hwnd = new WindowInteropHelper(window).Handle;
        var extendedStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            extendedStyle | NativeMethods.WS_EX_TRANSPARENT | NativeMethods.WS_EX_LAYERED);
    }

    /// <summary>
    /// 테두리 창 숨기기
    /// </summary>
    public void Hide()
    {
        System.Windows.Application.Current.Dispatcher.Invoke(() =>
        {
            _blinkTimer?.Stop();
            _borderWindow?.Close();
            _borderWindow = null;
        });
    }

    public void Dispose()
    {
        Hide();
        _blinkTimer?.Stop();
    }

    private static class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const int WS_EX_TRANSPARENT = 0x00000020;
        public const int WS_EX_LAYERED = 0x00080000;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int GetWindowLong(IntPtr hwnd, int index);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
    }
}
