using System;
using System.Diagnostics;
using System.IO;
using System.Security.Principal;
using System.Threading;
using System.Windows.Threading;

namespace AudioRecorder;

public partial class App : System.Windows.Application
{
    private static Mutex? _mutex;
    private static bool _mutexOwned;
    private static readonly string LogDirectory =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "AudioRecorderPro", "logs");

    protected override void OnStartup(System.Windows.StartupEventArgs e)
    {
        // 사용자별 단일 인스턴스 (Local\ + SID)
        var mutexName = BuildMutexName();
        bool createdNew;
        try
        {
            _mutex = new Mutex(true, mutexName, out createdNew);
        }
        catch (AbandonedMutexException)
        {
            // 이전 인스턴스가 Release 없이 종료됨 → 현재 인스턴스가 소유권 인수
            createdNew = true;
        }

        if (!createdNew)
        {
            ActivateExistingInstance();
            _mutex?.Dispose();
            _mutex = null;
            Shutdown();
            return;
        }

        _mutexOwned = true;
        base.OnStartup(e);

        // WPF 바인딩 에러 로그: Debug 빌드에서만 경고 레벨로 살려 디버깅 가능하게
#if DEBUG
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Warning;
#else
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Off;
#endif

        DispatcherUnhandledException += OnDispatcherUnhandledException;
    }

    protected override void OnExit(System.Windows.ExitEventArgs e)
    {
        try
        {
            if (_mutex != null && _mutexOwned)
            {
                _mutex.ReleaseMutex();
            }
        }
        catch (ApplicationException)
        {
            // 소유권이 이미 사라졌거나 다른 스레드가 해제한 경우 무시
        }
        finally
        {
            _mutex?.Dispose();
            _mutex = null;
            _mutexOwned = false;
        }

        base.OnExit(e);
    }

    private static string BuildMutexName()
    {
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var sid = identity.User?.Value ?? "anon";
            return $"Local\\AudioRecorderPro_SingleInstance_{sid}";
        }
        catch
        {
            return "Local\\AudioRecorderPro_SingleInstance";
        }
    }

    private static void ActivateExistingInstance()
    {
        try
        {
            var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                if (process.Id != current.Id && process.MainWindowHandle != IntPtr.Zero)
                {
                    NativeMethods.SetForegroundWindow(process.MainWindowHandle);
                    if (NativeMethods.IsIconic(process.MainWindowHandle))
                        NativeMethods.ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                    break;
                }
            }
        }
        catch
        {
            // 활성화 실패는 무시 (UX 개선은 후속 Phase)
        }
    }

    private static class NativeMethods
    {
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool SetForegroundWindow(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool IsIconic(IntPtr hWnd);

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        var msg = e.Exception.Message ?? "";
        var type = e.Exception.GetType().Name ?? "";

        // 바인딩 관련 예외는 완전히 무시
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

        // 상세 예외는 로그파일에 기록 (경로/사용자명 노출 방지)
        WriteCrashLog(e.Exception);

        // 사용자에게는 일반화된 메시지만 표시 (경로 노출 방지)
        System.Windows.MessageBox.Show(
            "예기치 않은 오류가 발생했습니다.\n진단 로그가 앱 데이터 폴더에 저장되었습니다.",
            "오류",
            System.Windows.MessageBoxButton.OK,
            System.Windows.MessageBoxImage.Error);
        e.Handled = true;
    }

    private static void WriteCrashLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            var path = Path.Combine(LogDirectory, $"crash-{DateTime.Now:yyyyMMdd}.log");
            var entry = $"[{DateTime.Now:O}] {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}{Environment.NewLine}{Environment.NewLine}";
            File.AppendAllText(path, entry);
        }
        catch
        {
            // 로깅 실패는 무시
        }
    }
}
