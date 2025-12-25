using System.Diagnostics;
using System.Windows.Threading;

namespace AudioRecorder;

public partial class App : System.Windows.Application
{
    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        base.OnStartup(e);

        // WPF 바인딩 에러 로그 비활성화
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Off;

        // 전역 예외 처리
        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var msg = e.Exception.Message ?? "";
        var type = e.Exception.GetType().Name ?? "";

        // 바인딩 관련 예외는 완전히 무시 (메시지박스 표시 안함)
        if (msg.Contains("TwoWay") ||
            msg.Contains("OneWayToSource") ||
            msg.Contains("Binding") ||
            msg.Contains("binding") ||
            msg.Contains("RecordingInfo") ||
            msg.Contains("Formatted") ||
            type.Contains("Xaml") ||
            type.Contains("Binding"))
        {
            e.Handled = true;
            return;
        }

        // 그 외 예외만 표시
        System.Windows.MessageBox.Show(
            $"Error: {msg}",
            "Error",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }
}
