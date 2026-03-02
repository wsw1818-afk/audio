using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace AudioRecorder.Services;

/// <summary>
/// 전역 핫키 서비스 - 창이 최소화되어 있어도 작동
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private const int WM_HOTKEY = 0x0312;

    // Modifiers
    private const uint MOD_CTRL = 0x0002;
    private const uint MOD_SHIFT = 0x0004;
    private const uint MOD_ALT = 0x0001;

    // Virtual Keys
    private const uint VK_S = 0x53;  // S key
    private const uint VK_R = 0x52;  // R key

    // Hotkey IDs
    private const int HOTKEY_STOP = 1;  // Ctrl+Shift+S: 녹화 정지 + 창 복원

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _disposed;

    public event EventHandler? StopRecordingRequested;

    public void RegisterHotkeys(Window window)
    {
        var helper = new WindowInteropHelper(window);
        _windowHandle = helper.Handle;

        if (_windowHandle == IntPtr.Zero)
        {
            // 창이 아직 초기화되지 않은 경우, Loaded 이벤트에서 다시 시도
            window.Loaded += (s, e) =>
            {
                _windowHandle = new WindowInteropHelper(window).Handle;
                RegisterHotkeysInternal();
            };
        }
        else
        {
            RegisterHotkeysInternal();
        }
    }

    private void RegisterHotkeysInternal()
    {
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(WndProc);

        // Ctrl+Shift+S: 녹화 정지
        bool result = RegisterHotKey(_windowHandle, HOTKEY_STOP, MOD_CTRL | MOD_SHIFT, VK_S);
        Console.WriteLine($">>> GlobalHotkey Ctrl+Shift+S registered: {result}");
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            Console.WriteLine($">>> GlobalHotkey received: id={id}");

            if (id == HOTKEY_STOP)
            {
                Console.WriteLine(">>> Ctrl+Shift+S pressed - StopRecordingRequested");
                StopRecordingRequested?.Invoke(this, EventArgs.Empty);
                handled = true;
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;

            if (_windowHandle != IntPtr.Zero)
            {
                UnregisterHotKey(_windowHandle, HOTKEY_STOP);
            }

            _source?.RemoveHook(WndProc);
        }
    }
}
