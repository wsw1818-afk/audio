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
        if (DataContext is not MainViewModel vm) return;

        // 설정 저장 (트레이 최소화/종료 전 항상 저장)
        vm.SaveSettings();

        // 압축 중이면 경고 메시지 표시
        if (vm.IsCompressingVideo)
        {
            var result = System.Windows.MessageBox.Show(
                "동영상 압축이 진행 중입니다.\n종료하면 압축이 취소되고 불완전한 파일이 생성될 수 있습니다.\n\n정말 종료하시겠습니까?",
                "압축 진행 중",
                MessageBoxButton.YesNo,
                MessageBoxImage.Warning);

            if (result == MessageBoxResult.No)
            {
                e.Cancel = true;
                return;
            }

            // 사용자가 종료를 선택하면 압축 취소
            vm.CancelVideoCompressionCommand.Execute(null);
            _isExiting = true;
        }

        // 명시적 종료 요청이면 그냥 종료
        if (_isExiting) return;

        // 녹음/녹화 중이면 그냥 종료 (데이터 손실 방지는 별도 처리 필요)
        if (vm.RecordingState != Models.RecordingState.Stopped) return;

        // CloseAction 설정에 따라 처리
        if (vm.CloseAction == Models.CloseAction.MinimizeToTray)
        {
            e.Cancel = true;
            Hide();
            _trayIcon.ShowBalloonTip("AudioRecorder Pro", "시스템 트레이로 최소화되었습니다.");
        }
        // ExitImmediately인 경우 그냥 종료됨
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

                case Key.B: // 북마크 추가
                    vm.AddBookmarkCommand.Execute(null);
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

        // 재생 중 단축키
        if (vm.IsPlaying)
        {
            switch (e.Key)
            {
                case Key.Left: // 5초 되감기
                    vm.Rewind5Command.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Right: // 5초 앞으로
                    vm.Forward5Command.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Up: // 배속 증가
                    vm.IncreasePlaybackSpeedCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.Down: // 배속 감소
                    vm.DecreasePlaybackSpeedCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.OemOpenBrackets: // [ 10초 되감기
                    vm.Rewind10Command.Execute(null);
                    e.Handled = true;
                    break;

                case Key.OemCloseBrackets: // ] 10초 앞으로
                    vm.Forward10Command.Execute(null);
                    e.Handled = true;
                    break;

                case Key.I: // 구간 시작 지점 설정
                    vm.SetExtractStartCommand.Execute(null);
                    e.Handled = true;
                    break;

                case Key.O: // 구간 끝 지점 설정 (Ctrl 없이)
                    if (Keyboard.Modifiers == ModifierKeys.None)
                    {
                        vm.SetExtractEndCommand.Execute(null);
                        e.Handled = true;
                    }
                    break;
            }
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
        }
    }

    // 화면 녹화 모드 버튼
    private void ScreenModeButton_Click(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[MainWindow] ScreenModeButton_Click 호출됨");
        if (DataContext is MainViewModel vm && vm.SwitchToScreenModeCommand.CanExecute(null))
        {
            Console.WriteLine("[MainWindow] SwitchToScreenModeCommand 실행");
            vm.SwitchToScreenModeCommand.Execute(null);
        }
    }

    // 녹음 시작 버튼 Click (UI Automation 지원을 위해)
    // InvokePattern.Invoke()는 Click 이벤트만 발생시키고 Command는 실행하지 않으므로
    // Click 핸들러에서 Command를 직접 실행
    private void StartRecordingButton_Click(object sender, RoutedEventArgs e)
    {
        Console.WriteLine("[MainWindow] StartRecordingButton_Click 호출됨");
        if (DataContext is MainViewModel vm && vm.StartRecordingCommand.CanExecute(null))
        {
            Console.WriteLine("[MainWindow] StartRecordingCommand 실행");
            vm.StartRecordingCommand.Execute(null);
        }
    }

    // 녹화 대상 선택 콤보박스 변경
    private void CaptureTargetCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        if (CaptureTargetCombo.SelectedItem is not System.Windows.Controls.ComboBoxItem item) return;

        var tag = item.Tag?.ToString();
        switch (tag)
        {
            case "FullScreen":
                vm.SelectFullScreenCommand.Execute(null);
                break;
            case "Monitor":
                vm.SelectMonitorCommand.Execute(null);
                break;
            case "Window":
                vm.SelectWindowCommand.Execute(null);
                break;
            case "Region":
                vm.SelectRegionCommand.Execute(null);
                break;
        }
    }

    // 옵션 패널 바깥 클릭 시 닫기
    private void OptionsOverlay_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is MainViewModel vm && vm.ToggleOptionsCommand.CanExecute(null))
        {
            vm.ToggleOptionsCommand.Execute(null);
        }
    }

    // 옵션 패널 내부 클릭 시 이벤트 전파 중지
    private void OptionsPanel_MouseDown(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
    }

    // 변환/압축 버튼 클릭 시 ContextMenu 열기
    private void ConvertButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }

    // 압축 버튼 클릭 시 ContextMenu 열기
    private void CompressButton_Click(object sender, RoutedEventArgs e)
    {
        if (sender is System.Windows.Controls.Button btn && btn.ContextMenu != null)
        {
            btn.ContextMenu.PlacementTarget = btn;
            btn.ContextMenu.IsOpen = true;
        }
    }
}
