using System.Windows;
using System.Windows.Input;
using AudioRecorder.Services;
using AudioRecorder.ViewModels;

namespace AudioRecorder.Views;

public partial class MainWindow : Window
{
    private readonly TrayIconService _trayIcon;
    private bool _isExiting;

    public MainWindow()
    {
        InitializeComponent();
        Closing += OnClosing;
        Closed += OnClosed;

        // 키보드 단축키 등록
        KeyDown += OnKeyDown;

        // 시스템 트레이 아이콘 설정
        _trayIcon = new TrayIconService();
        _trayIcon.ShowWindowRequested += (s, e) => ShowAndActivate();
        _trayIcon.StartRecordingRequested += (s, e) => ExecuteCommand(vm => vm.StartRecordingCommand);
        _trayIcon.StopRecordingRequested += (s, e) => ExecuteCommand(vm => vm.StopRecordingCommand);
        _trayIcon.PauseRecordingRequested += (s, e) =>
        {
            if (DataContext is MainViewModel vm)
            {
                if (vm.PauseRecordingCommand.CanExecute(null))
                    vm.PauseRecordingCommand.Execute(null);
                else if (vm.ResumeRecordingCommand.CanExecute(null))
                    vm.ResumeRecordingCommand.Execute(null);
            }
        };
        _trayIcon.ExitRequested += (s, e) =>
        {
            _isExiting = true;
            Close();
        };
    }

    private void ShowAndActivate()
    {
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExecuteCommand(Func<MainViewModel, System.Windows.Input.ICommand> commandSelector)
    {
        if (DataContext is MainViewModel vm)
        {
            var cmd = commandSelector(vm);
            if (cmd.CanExecute(null))
                cmd.Execute(null);
        }
    }

    private void OnClosing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // 녹음 중이 아니고 종료 요청이 아니면 트레이로 최소화
        if (!_isExiting && DataContext is MainViewModel vm && vm.RecordingState == Models.RecordingState.Stopped)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip("AudioRecorder Pro", "시스템 트레이로 최소화되었습니다.");
        }
    }

    private void OnKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        // Ctrl 조합 단축키
        if (Keyboard.Modifiers == ModifierKeys.Control)
        {
            switch (e.Key)
            {
                case Key.R: // 녹음 시작/정지
                    if (vm.StartRecordingCommand.CanExecute(null))
                        vm.StartRecordingCommand.Execute(null);
                    else if (vm.StopRecordingCommand.CanExecute(null))
                        vm.StopRecordingCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.P: // 일시정지/재개
                    if (vm.PauseRecordingCommand.CanExecute(null))
                        vm.PauseRecordingCommand.Execute(null);
                    else if (vm.ResumeRecordingCommand.CanExecute(null))
                        vm.ResumeRecordingCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.S: // 정지
                    if (vm.StopRecordingCommand.CanExecute(null))
                        vm.StopRecordingCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.O: // 출력 폴더 열기
                    vm.OpenOutputDirectoryCommand.Execute(null);
                    e.Handled = true;
                    break;
            }
        }

        // Escape 키로 재생 정지
        if (e.Key == Key.Escape && vm.IsPlaying)
        {
            vm.StopPlaybackCommand.Execute(null);
            e.Handled = true;
        }
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        _trayIcon.Dispose();

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.Dispose();
        }
    }

    // 윈도우 드래그 이동
    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed)
            DragMove();
    }

    // 최소화 버튼
    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    // 최대화/복원 버튼
    private void MaximizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState == WindowState.Maximized
            ? WindowState.Normal
            : WindowState.Maximized;
    }

    // 닫기 버튼
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    // 오디오 녹음 모드 버튼
    private void AudioModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SwitchToAudioModeCommand.CanExecute(null))
        {
            vm.SwitchToAudioModeCommand.Execute(null);
            UpdateModePanel(false);
        }
    }

    // 화면 녹화 모드 버튼
    private void ScreenModeButton_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.SwitchToScreenModeCommand.CanExecute(null))
        {
            vm.SwitchToScreenModeCommand.Execute(null);
            UpdateModePanel(true);
        }
    }

    // 모드 패널 전환
    private void UpdateModePanel(bool isScreenMode)
    {
        AudioOptionsPanel.Visibility = isScreenMode ? Visibility.Collapsed : Visibility.Visible;
        ScreenOptionsPanel.Visibility = isScreenMode ? Visibility.Visible : Visibility.Collapsed;

        // 레이아웃 강제 갱신
        AudioOptionsPanel.UpdateLayout();
        ScreenOptionsPanel.UpdateLayout();
    }
}
