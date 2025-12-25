using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using AudioRecorder.Models;
using WpfRectangle = System.Windows.Shapes.Rectangle;

namespace AudioRecorder.Views;

/// <summary>
/// 영역 선택 창
/// </summary>
public partial class RegionSelectorWindow : Window
{
    private System.Windows.Point _startPoint;
    private WpfRectangle? _selectionRect;
    private bool _isSelecting;

    /// <summary>
    /// 선택된 영역
    /// </summary>
    public CaptureRegion? SelectedRegion { get; private set; }

    /// <summary>
    /// 선택 완료 여부
    /// </summary>
    public bool IsRegionSelected { get; private set; }

    public RegionSelectorWindow()
    {
        InitializeComponent();

        // 전체 화면으로 설정 (모든 모니터)
        var virtualScreen = SystemParameters.VirtualScreenWidth;
        var virtualHeight = SystemParameters.VirtualScreenHeight;
        var virtualLeft = SystemParameters.VirtualScreenLeft;
        var virtualTop = SystemParameters.VirtualScreenTop;

        Left = virtualLeft;
        Top = virtualTop;
        Width = virtualScreen;
        Height = virtualHeight;

        // 이벤트 연결
        MouseDown += OnMouseDown;
        MouseMove += OnMouseMove;
        MouseUp += OnMouseUp;
        KeyDown += OnKeyDown;
    }

    private void OnMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            _startPoint = e.GetPosition(OverlayCanvas);
            _isSelecting = true;

            // 기존 선택 영역 제거
            if (_selectionRect != null)
            {
                OverlayCanvas.Children.Remove(_selectionRect);
            }

            // 새 선택 영역 생성
            _selectionRect = new WpfRectangle
            {
                Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(76, 175, 80)), // Primary 색상
                StrokeThickness = 2,
                StrokeDashArray = new DoubleCollection { 5, 3 },
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(50, 76, 175, 80))
            };

            Canvas.SetLeft(_selectionRect, _startPoint.X);
            Canvas.SetTop(_selectionRect, _startPoint.Y);
            OverlayCanvas.Children.Add(_selectionRect);

            SizeIndicator.Visibility = Visibility.Visible;
            CaptureMouse();
        }
    }

    private void OnMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_isSelecting || _selectionRect == null) return;

        var currentPoint = e.GetPosition(OverlayCanvas);

        // 선택 영역 크기 계산
        var x = Math.Min(_startPoint.X, currentPoint.X);
        var y = Math.Min(_startPoint.Y, currentPoint.Y);
        var width = Math.Abs(currentPoint.X - _startPoint.X);
        var height = Math.Abs(currentPoint.Y - _startPoint.Y);

        // 선택 영역 업데이트
        Canvas.SetLeft(_selectionRect, x);
        Canvas.SetTop(_selectionRect, y);
        _selectionRect.Width = width;
        _selectionRect.Height = height;

        // 크기 표시 업데이트
        SizeText.Text = $"{(int)width} x {(int)height}";
        Canvas.SetLeft(SizeIndicator, currentPoint.X + 10);
        Canvas.SetTop(SizeIndicator, currentPoint.Y + 10);
    }

    private void OnMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isSelecting || _selectionRect == null) return;

        _isSelecting = false;
        ReleaseMouseCapture();

        // 최소 크기 확인 (100x100)
        if (_selectionRect.Width < 100 || _selectionRect.Height < 100)
        {
            System.Windows.MessageBox.Show("선택 영역이 너무 작습니다. 최소 100x100 이상의 영역을 선택해주세요.",
                "영역 선택", MessageBoxButton.OK, MessageBoxImage.Warning);

            OverlayCanvas.Children.Remove(_selectionRect);
            _selectionRect = null;
            SizeIndicator.Visibility = Visibility.Collapsed;
            return;
        }

        // 선택 영역 저장
        SelectedRegion = new CaptureRegion
        {
            Type = CaptureRegionType.CustomRegion,
            Bounds = new System.Drawing.Rectangle(
                (int)(Canvas.GetLeft(_selectionRect) + Left),
                (int)(Canvas.GetTop(_selectionRect) + Top),
                (int)_selectionRect.Width,
                (int)_selectionRect.Height)
        };

        IsRegionSelected = true;
        DialogResult = true;
        Close();
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            IsRegionSelected = false;
            DialogResult = false;
            Close();
        }
    }
}
