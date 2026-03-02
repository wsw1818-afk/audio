using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace AudioRecorder.Views;

/// <summary>
/// 녹화 시작 전 카운트다운 창
/// </summary>
public partial class CountdownWindow : Window
{
    private readonly DispatcherTimer _timer;
    private int _remainingSeconds;
    private readonly int _totalSeconds;
    private readonly TaskCompletionSource<bool> _completionSource;

    public CountdownWindow(int seconds)
    {
        InitializeComponent();

        _totalSeconds = seconds;
        _remainingSeconds = seconds;
        _completionSource = new TaskCompletionSource<bool>();

        CountdownText.Text = _remainingSeconds.ToString();

        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _timer.Tick += Timer_Tick;
    }

    /// <summary>
    /// 카운트다운 시작 및 완료 대기
    /// </summary>
    public Task<bool> StartCountdownAsync()
    {
        Show();
        _timer.Start();
        return _completionSource.Task;
    }

    private void Timer_Tick(object? sender, EventArgs e)
    {
        _remainingSeconds--;

        if (_remainingSeconds <= 0)
        {
            _timer.Stop();
            _completionSource.TrySetResult(true);
            Close();
        }
        else
        {
            CountdownText.Text = _remainingSeconds.ToString();
            UpdateProgressRing();
        }
    }

    private void UpdateProgressRing()
    {
        // 진행률에 따라 색상 변경
        double progress = (double)_remainingSeconds / _totalSeconds;

        if (_remainingSeconds <= 1)
        {
            ProgressRing.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0x55, 0xFF, 0x55)); // 녹색
        }
        else if (_remainingSeconds <= 2)
        {
            ProgressRing.Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xFF, 0x55)); // 노란색
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _timer.Stop();
        _completionSource.TrySetResult(false); // 창이 강제로 닫힌 경우 (이미 완료됐으면 무시됨)
        base.OnClosed(e);
    }
}
