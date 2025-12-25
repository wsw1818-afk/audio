using System;
using System.Drawing;
using System.Windows.Forms;

namespace AudioRecorder.Services;

public class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ContextMenuStrip _contextMenu;
    private bool _disposed;

    public event EventHandler? ShowWindowRequested;
    public event EventHandler? StartRecordingRequested;
    public event EventHandler? StopRecordingRequested;
    public event EventHandler? PauseRecordingRequested;
    public event EventHandler? ExitRequested;

    public TrayIconService()
    {
        // 컨텍스트 메뉴 생성
        _contextMenu = new ContextMenuStrip();
        _contextMenu.Items.Add("열기", null, (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("녹음 시작 (Ctrl+R)", null, (s, e) => StartRecordingRequested?.Invoke(this, EventArgs.Empty));
        _contextMenu.Items.Add("녹음 정지 (Ctrl+S)", null, (s, e) => StopRecordingRequested?.Invoke(this, EventArgs.Empty));
        _contextMenu.Items.Add("일시정지 (Ctrl+P)", null, (s, e) => PauseRecordingRequested?.Invoke(this, EventArgs.Empty));
        _contextMenu.Items.Add(new ToolStripSeparator());
        _contextMenu.Items.Add("종료", null, (s, e) => ExitRequested?.Invoke(this, EventArgs.Empty));

        // 트레이 아이콘 생성
        _notifyIcon = new NotifyIcon
        {
            Icon = SystemIcons.Application, // 기본 아이콘 (나중에 커스텀 아이콘으로 교체 가능)
            Text = "AudioRecorder Pro",
            Visible = true,
            ContextMenuStrip = _contextMenu
        };

        // 더블클릭으로 창 열기
        _notifyIcon.DoubleClick += (s, e) => ShowWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    public void UpdateIcon(bool isRecording, bool isPaused)
    {
        if (isRecording)
        {
            _notifyIcon.Text = isPaused ? "AudioRecorder Pro - 일시정지" : "AudioRecorder Pro - 녹음 중";
        }
        else
        {
            _notifyIcon.Text = "AudioRecorder Pro";
        }
    }

    public void ShowBalloonTip(string title, string text, ToolTipIcon icon = ToolTipIcon.Info)
    {
        _notifyIcon.ShowBalloonTip(3000, title, text, icon);
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _contextMenu.Dispose();
        }
    }
}
