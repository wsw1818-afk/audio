using System.Windows;
using System.Windows.Input;
using AudioRecorder.Models;
using AudioRecorder.Services;

namespace AudioRecorder.Views;

/// <summary>
/// 창 선택 다이얼로그
/// </summary>
public partial class WindowPickerDialog : Window
{
    /// <summary>
    /// 선택된 창
    /// </summary>
    public WindowInfo? SelectedWindow { get; private set; }

    /// <summary>
    /// 선택된 캡처 영역
    /// </summary>
    public CaptureRegion? SelectedRegion { get; private set; }

    public WindowPickerDialog()
    {
        InitializeComponent();
        LoadWindows();
    }

    private void LoadWindows()
    {
        WindowList.ItemsSource = WindowEnumerator.GetCaptureableWindows();
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e)
    {
        LoadWindows();
    }

    private void SelectButton_Click(object sender, RoutedEventArgs e)
    {
        SelectCurrentWindow();
    }

    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void WindowList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        SelectCurrentWindow();
    }

    private void SelectCurrentWindow()
    {
        if (WindowList.SelectedItem is WindowInfo windowInfo)
        {
            SelectedWindow = windowInfo;
            SelectedRegion = new CaptureRegion
            {
                Type = CaptureRegionType.Window,
                WindowHandle = windowInfo.Handle,
                WindowTitle = windowInfo.Title,
                Bounds = windowInfo.Bounds
            };

            DialogResult = true;
            Close();
        }
        else
        {
            System.Windows.MessageBox.Show("창을 선택해주세요.", "창 선택", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
}
