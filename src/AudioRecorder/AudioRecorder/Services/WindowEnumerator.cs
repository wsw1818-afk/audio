using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;
using AudioRecorder.Models;

namespace AudioRecorder.Services;

/// <summary>
/// 창 목록 열거 서비스
/// </summary>
public static class WindowEnumerator
{
    #region Win32 API

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc enumProc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern int GetWindowText(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr GetShellWindow();

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute, out bool pvAttribute, int cbAttribute);

    private const int DWMWA_CLOAKED = 14;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    #endregion

    /// <summary>
    /// 캡처 가능한 창 목록 가져오기
    /// </summary>
    public static ObservableCollection<WindowInfo> GetCaptureableWindows()
    {
        var windows = new ObservableCollection<WindowInfo>();
        var shellWindow = GetShellWindow();

        EnumWindows((hWnd, _) =>
        {
            // 셸 창 제외
            if (hWnd == shellWindow)
                return true;

            // 보이지 않는 창 제외
            if (!IsWindowVisible(hWnd))
                return true;

            // Cloaked 창 제외 (Windows 10+ UWP 숨김 창)
            if (DwmGetWindowAttribute(hWnd, DWMWA_CLOAKED, out bool isCloaked, Marshal.SizeOf<bool>()) == 0 && isCloaked)
                return true;

            // 창 제목 가져오기
            var length = GetWindowTextLength(hWnd);
            if (length == 0)
                return true;

            var builder = new StringBuilder(length + 1);
            GetWindowText(hWnd, builder, builder.Capacity);
            var title = builder.ToString();

            // 창 크기 가져오기
            GetWindowRect(hWnd, out RECT rect);
            var bounds = new Rectangle(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

            // 너무 작은 창 제외
            if (bounds.Width < 100 || bounds.Height < 100)
                return true;

            // 프로세스 정보 가져오기
            string processName = "Unknown";
            try
            {
                GetWindowThreadProcessId(hWnd, out uint processId);
                var process = Process.GetProcessById((int)processId);
                processName = process.ProcessName;
            }
            catch { }

            // 특정 시스템 창 제외
            if (IsSystemWindow(title, processName))
                return true;

            windows.Add(new WindowInfo
            {
                Handle = hWnd,
                Title = title,
                ProcessName = processName,
                Bounds = bounds
            });

            return true;
        }, IntPtr.Zero);

        return windows;
    }

    /// <summary>
    /// 시스템 창 여부 확인
    /// </summary>
    private static bool IsSystemWindow(string title, string processName)
    {
        // 제외할 프로세스
        var excludedProcesses = new[] { "TextInputHost", "ApplicationFrameHost", "SearchHost", "ShellExperienceHost" };
        if (excludedProcesses.Contains(processName, StringComparer.OrdinalIgnoreCase))
            return true;

        // 제외할 창 제목
        var excludedTitles = new[] { "Program Manager", "Windows Input Experience" };
        if (excludedTitles.Contains(title, StringComparer.OrdinalIgnoreCase))
            return true;

        return false;
    }

    /// <summary>
    /// 모니터 목록 가져오기
    /// </summary>
    public static ObservableCollection<MonitorInfo> GetMonitors()
    {
        var monitors = new ObservableCollection<MonitorInfo>();
        var allScreens = System.Windows.Forms.Screen.AllScreens;

        for (int i = 0; i < allScreens.Length; i++)
        {
            var screen = allScreens[i];
            monitors.Add(new MonitorInfo
            {
                Index = i,
                Name = screen.DeviceName,
                Bounds = new Rectangle(screen.Bounds.X, screen.Bounds.Y, screen.Bounds.Width, screen.Bounds.Height),
                IsPrimary = screen.Primary,
                DisplayName = screen.Primary ? $"주 모니터 ({screen.Bounds.Width}x{screen.Bounds.Height})"
                                              : $"모니터 {i + 1} ({screen.Bounds.Width}x{screen.Bounds.Height})"
            });
        }

        return monitors;
    }
}

/// <summary>
/// 모니터 정보
/// </summary>
public class MonitorInfo
{
    public int Index { get; set; }
    public string Name { get; set; } = string.Empty;
    public Rectangle Bounds { get; set; }
    public bool IsPrimary { get; set; }
    public string DisplayName { get; set; } = string.Empty;

    public override string ToString() => DisplayName;
}
