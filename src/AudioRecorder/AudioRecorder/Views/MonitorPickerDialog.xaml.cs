using System.Windows;
using System.Windows.Forms;
using System.Windows.Input;
using AudioRecorder.Models;

namespace AudioRecorder.Views;

/// <summary>
/// 모니터 정보 클래스
/// </summary>
public class MonitorInfo
{
    public int Index { get; set; }
    public string DisplayName { get; set; } = "";
    public string Resolution { get; set; } = "";
    public string Position { get; set; } = "";
    public System.Drawing.Rectangle Bounds { get; set; }
    public bool IsPrimary { get; set; }
    public Visibility IsPrimaryVisibility => IsPrimary ? Visibility.Visible : Visibility.Collapsed;
}

/// <summary>
/// 모니터 선택 다이얼로그
/// </summary>
public partial class MonitorPickerDialog : Window
{
    /// <summary>
    /// 선택된 모니터 정보
    /// </summary>
    public MonitorInfo? SelectedMonitor { get; private set; }

    /// <summary>
    /// 선택된 캡처 영역
    /// </summary>
    public CaptureRegion? SelectedRegion { get; private set; }

    public MonitorPickerDialog()
    {
        InitializeComponent();
        LoadMonitors();
    }

    private void LoadMonitors()
    {
        var monitors = new List<MonitorInfo>();
        var screens = Screen.AllScreens;

        for (int i = 0; i < screens.Length; i++)
        {
            var screen = screens[i];
            monitors.Add(new MonitorInfo
            {
                Index = i,
                DisplayName = screen.Primary ? $"모니터 {i + 1} (기본)" : $"모니터 {i + 1}",
                Resolution = $"{screen.Bounds.Width} x {screen.Bounds.Height}",
                Position = $"위치: ({screen.Bounds.X}, {screen.Bounds.Y})",
                Bounds = screen.Bounds,
                IsPrimary = screen.Primary
            });
        }

        MonitorList.ItemsSource = monitors;

        // 기본 모니터 선택
        var primaryIndex = monitors.FindIndex(m => m.IsPrimary);
        if (primaryIndex >= 0)
            MonitorList.SelectedIndex = primaryIndex;
        else if (monitors.Count > 0)
            MonitorList.SelectedIndex = 0;
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCurrentMonitor();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void MonitorList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SelectCurrentMonitor();
    }

    private void SelectCurrentMonitor()
    {
        if (MonitorList.SelectedItem is MonitorInfo monitorInfo)
        {
            SelectedMonitor = monitorInfo;
            SelectedRegion = new CaptureRegion
            {
                Type = CaptureRegionType.FullScreen,
                MonitorIndex = monitorInfo.Index,
                Bounds = monitorInfo.Bounds
            };

            DialogResult = true;
            Close();
        }
        else
        {
            System.Windows.MessageBox.Show("모니터를 선택해주세요.", "모니터 선택", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
