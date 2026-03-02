using System.IO;
using System.Windows;
using AudioRecorder.Models;
using AudioRecorder.Services;

namespace AudioRecorder.Views;

/// <summary>
/// 녹취록 변환 다이얼로그
/// </summary>
public partial class TranscriptionWindow : Window
{
    private readonly SpeechToTextService _sttService;
    private readonly string _audioPath;
    private TranscriptionResult? _result;
    private CancellationTokenSource? _cts;

    public TranscriptionWindow(string audioPath, AudioConversionService audioConversion)
    {
        InitializeComponent();

        _audioPath = audioPath;
        _sttService = new SpeechToTextService(audioConversion);

        // 이벤트 연결
        _sttService.ProgressChanged += OnProgressChanged;
        _sttService.TranscriptionCompleted += OnTranscriptionCompleted;

        // 파일명 표시
        FileNameText.Text = $"파일: {Path.GetFileName(audioPath)} ({new FileInfo(audioPath).Length / (1024.0 * 1024):F1}MB)";

        // whisper.cpp 사용 가능 여부 체크
        if (!_sttService.IsLocalEngineAvailable)
        {
            StatusText.Text = "⚠ whisper.cpp가 설치되지 않았습니다. 앱 폴더에 whisper-cli.exe를 복사하세요.";
        }
        else
        {
            StatusText.Text = _sttService.IsGpuAccelerated
                ? "✅ GPU 가속 모드 (NVIDIA CUDA)"
                : "💻 CPU 모드";
        }
    }

    /// <summary>
    /// 변환 시작
    /// </summary>
    private async void TranscribeButton_Click(object sender, RoutedEventArgs e)
    {
        var options = BuildOptions();

        if (!_sttService.IsLocalEngineAvailable)
        {
            System.Windows.MessageBox.Show(
                "whisper-cli.exe를 앱 폴더에 복사하세요.\n\nhttps://github.com/ggerganov/whisper.cpp/releases 에서 다운로드",
                "whisper.cpp 미설치",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            return;
        }

        // UI 상태 전환
        SetTranscribing(true);

        _cts = new CancellationTokenSource();

        try
        {
            _result = await _sttService.TranscribeAsync(_audioPath, options, _cts.Token);

            if (_result != null)
            {
                // 결과 표시
                ResultTextBox.Text = _result.FormattedText;
                ResultInfoText.Text = $"{_result.Segments.Count}개 구간 | " +
                                     $"{_result.DetectedSpeakers.Count}명 화자 | " +
                                     $"{_result.Duration:hh\\:mm\\:ss} | " +
                                     $"처리: {_result.ProcessingTime:mm\\:ss}";

                // 내보내기 버튼 활성화
                CopyButton.IsEnabled = true;
                ExportTxtButton.IsEnabled = true;
                ExportSrtButton.IsEnabled = true;
            }
        }
        finally
        {
            SetTranscribing(false);
            _cts?.Dispose();
            _cts = null;
        }
    }

    /// <summary>
    /// 변환 취소
    /// </summary>
    private void CancelButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        _sttService.Cancel();
        StatusText.Text = "취소 중...";
    }

    /// <summary>
    /// 클립보드 복사
    /// </summary>
    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result != null)
        {
            System.Windows.Clipboard.SetText(TranscriptionExportService.FormatForClipboard(_result));
            StatusText.Text = "클립보드에 복사되었습니다.";
        }
    }

    /// <summary>
    /// TXT 내보내기
    /// </summary>
    private async void ExportTxtButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        await ExportWithDialogAsync(TranscriptionExportService.ExportFormat.TXT);
    }

    /// <summary>
    /// SRT 내보내기
    /// </summary>
    private async void ExportSrtButton_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null) return;
        await ExportWithDialogAsync(TranscriptionExportService.ExportFormat.SRT);
    }

    /// <summary>
    /// 파일 저장 다이얼로그로 내보내기
    /// </summary>
    private async Task ExportWithDialogAsync(TranscriptionExportService.ExportFormat format)
    {
        if (_result == null) return;

        var ext = TranscriptionExportService.GetExtension(format);
        var displayName = TranscriptionExportService.GetDisplayName(format);
        var defaultFileName = Path.GetFileNameWithoutExtension(_audioPath) + "_녹취록" + ext;

        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "녹취록 내보내기",
            Filter = $"{displayName}|*{ext}|모든 파일|*.*",
            FileName = defaultFileName,
            InitialDirectory = Path.GetDirectoryName(_audioPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
        };

        if (dialog.ShowDialog() == true)
        {
            var success = await TranscriptionExportService.ExportAsync(_result, dialog.FileName, format);
            if (success)
                StatusText.Text = $"저장 완료: {dialog.FileName}";
            else
                StatusText.Text = "저장 실패";
        }
    }

    /// <summary>
    /// 닫기
    /// </summary>
    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Close();
    }

    /// <summary>
    /// UI에서 옵션 수집
    /// </summary>
    private SttEngineOptions BuildOptions()
    {
        var options = new SttEngineOptions
        {
            Engine = SttEngineType.WhisperLocal,
            ModelSize = WhisperModelSize.LargeV3Turbo,
            EnableDiarization = DiarizationCheck.IsChecked == true,
            EnableWordTimestamps = WordTimestampCheck.IsChecked == true,
        };

        // 언어
        if (LanguageCombo.SelectedItem is System.Windows.Controls.ComboBoxItem langItem)
        {
            options.Language = langItem.Tag?.ToString() ?? "auto";
        }

        // 최대 화자 수
        var speakerMap = new[] { 2, 3, 4, 6, 8 };
        if (MaxSpeakersCombo.SelectedIndex >= 0 && MaxSpeakersCombo.SelectedIndex < speakerMap.Length)
            options.MaxSpeakers = speakerMap[MaxSpeakersCombo.SelectedIndex];

        return options;
    }

    /// <summary>
    /// 변환 중/대기 UI 전환
    /// </summary>
    private void SetTranscribing(bool isTranscribing)
    {
        TranscribeButton.IsEnabled = !isTranscribing;
        TranscribeButton.Content = isTranscribing ? "변환 중..." : "▶ 변환 시작";
        CancelButton.Visibility = isTranscribing ? Visibility.Visible : Visibility.Collapsed;
        ProgressPanel.Visibility = isTranscribing ? Visibility.Visible : Visibility.Collapsed;
        LanguageCombo.IsEnabled = !isTranscribing;
    }

    /// <summary>
    /// 진행 상태 업데이트 (UI 스레드)
    /// </summary>
    private void OnProgressChanged(object? sender, SttProgressEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            StatusText.Text = e.Status;
            ProgressBar.Value = e.Progress;
        });
    }

    /// <summary>
    /// 변환 완료 처리 (UI 스레드)
    /// </summary>
    private void OnTranscriptionCompleted(object? sender, TranscriptionCompletedEventArgs e)
    {
        Dispatcher.Invoke(() =>
        {
            if (!e.Success)
            {
                StatusText.Text = $"❌ {e.ErrorMessage}";
                ResultTextBox.Text = e.ErrorMessage ?? "변환에 실패했습니다.";
            }
        });
    }

    protected override void OnClosed(EventArgs e)
    {
        _cts?.Cancel();
        _sttService.ProgressChanged -= OnProgressChanged;
        _sttService.TranscriptionCompleted -= OnTranscriptionCompleted;
        _sttService.Dispose();
        base.OnClosed(e);
    }
}
