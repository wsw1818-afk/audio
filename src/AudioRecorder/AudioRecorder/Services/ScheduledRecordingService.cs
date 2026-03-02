using System.Diagnostics;
using System.Windows.Threading;

namespace AudioRecorder.Services;

/// <summary>
/// 예약 녹화 서비스 - 지정된 시간에 녹화 시작/종료
/// </summary>
public class ScheduledRecordingService : IDisposable
{
    private readonly DispatcherTimer _checkTimer;
    private DateTime? _scheduledStartTime;
    private DateTime? _scheduledEndTime;
    private bool _isWaiting;
    private bool _disposed;

    /// <summary>
    /// 예약된 시작 시간
    /// </summary>
    public DateTime? ScheduledStartTime
    {
        get => _scheduledStartTime;
        set => _scheduledStartTime = value;
    }

    /// <summary>
    /// 예약된 종료 시간
    /// </summary>
    public DateTime? ScheduledEndTime
    {
        get => _scheduledEndTime;
        set => _scheduledEndTime = value;
    }

    /// <summary>
    /// 대기 중 여부
    /// </summary>
    public bool IsWaiting => _isWaiting;

    /// <summary>
    /// 예약된 녹화가 설정되어 있는지 여부
    /// </summary>
    public bool HasSchedule => _scheduledStartTime.HasValue;

    /// <summary>
    /// 예약된 시작까지 남은 시간
    /// </summary>
    public TimeSpan? TimeUntilStart => _scheduledStartTime.HasValue
        ? _scheduledStartTime.Value - DateTime.Now
        : null;

    /// <summary>
    /// 예약된 종료까지 남은 시간
    /// </summary>
    public TimeSpan? TimeUntilEnd => _scheduledEndTime.HasValue
        ? _scheduledEndTime.Value - DateTime.Now
        : null;

    // 이벤트
    public event EventHandler? ScheduledStartTriggered;
    public event EventHandler? ScheduledEndTriggered;
    public event EventHandler<ScheduleStatusEventArgs>? StatusChanged;

    public ScheduledRecordingService()
    {
        _checkTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _checkTimer.Tick += CheckTimer_Tick;
    }

    /// <summary>
    /// 예약 녹화 설정
    /// </summary>
    public void Schedule(DateTime? startTime, DateTime? endTime)
    {
        _scheduledStartTime = startTime;
        _scheduledEndTime = endTime;

        if (startTime.HasValue && startTime.Value > DateTime.Now)
        {
            _isWaiting = true;
            _checkTimer.Start();

            var remaining = startTime.Value - DateTime.Now;
            Debug.WriteLine($"[ScheduledRecording] 예약 설정됨: 시작 {startTime:HH:mm:ss} ({remaining.TotalMinutes:F1}분 후)");

            StatusChanged?.Invoke(this, new ScheduleStatusEventArgs
            {
                Status = ScheduleStatus.Waiting,
                TimeRemaining = remaining,
                Message = $"녹화 시작까지 {FormatTimeRemaining(remaining)}"
            });
        }
    }

    /// <summary>
    /// 예약 취소
    /// </summary>
    public void Cancel()
    {
        _checkTimer.Stop();
        _isWaiting = false;
        _scheduledStartTime = null;
        _scheduledEndTime = null;

        Debug.WriteLine("[ScheduledRecording] 예약 취소됨");

        StatusChanged?.Invoke(this, new ScheduleStatusEventArgs
        {
            Status = ScheduleStatus.Cancelled,
            Message = "예약이 취소되었습니다"
        });
    }

    /// <summary>
    /// 녹화 시작 후 종료 타이머 활성화
    /// </summary>
    public void StartEndTimer()
    {
        if (_scheduledEndTime.HasValue && _scheduledEndTime.Value > DateTime.Now)
        {
            if (!_checkTimer.IsEnabled)
            {
                _checkTimer.Start();
            }

            var remaining = _scheduledEndTime.Value - DateTime.Now;
            Debug.WriteLine($"[ScheduledRecording] 종료 타이머 활성화: {_scheduledEndTime:HH:mm:ss} ({remaining.TotalMinutes:F1}분 후)");
        }
    }

    private void CheckTimer_Tick(object? sender, EventArgs e)
    {
        var now = DateTime.Now;

        // 시작 시간 체크
        if (_isWaiting && _scheduledStartTime.HasValue)
        {
            var remaining = _scheduledStartTime.Value - now;

            if (remaining <= TimeSpan.Zero)
            {
                _isWaiting = false;
                Debug.WriteLine("[ScheduledRecording] 예약 시작 시간 도달!");

                ScheduledStartTriggered?.Invoke(this, EventArgs.Empty);

                StatusChanged?.Invoke(this, new ScheduleStatusEventArgs
                {
                    Status = ScheduleStatus.Started,
                    Message = "예약된 녹화가 시작되었습니다"
                });
            }
            else
            {
                // 남은 시간 업데이트
                StatusChanged?.Invoke(this, new ScheduleStatusEventArgs
                {
                    Status = ScheduleStatus.Waiting,
                    TimeRemaining = remaining,
                    Message = $"녹화 시작까지 {FormatTimeRemaining(remaining)}"
                });
            }
        }

        // 종료 시간 체크
        if (_scheduledEndTime.HasValue)
        {
            var remaining = _scheduledEndTime.Value - now;

            if (remaining <= TimeSpan.Zero)
            {
                _checkTimer.Stop();
                Debug.WriteLine("[ScheduledRecording] 예약 종료 시간 도달!");

                ScheduledEndTriggered?.Invoke(this, EventArgs.Empty);

                StatusChanged?.Invoke(this, new ScheduleStatusEventArgs
                {
                    Status = ScheduleStatus.Ended,
                    Message = "예약된 녹화가 종료되었습니다"
                });

                // 예약 초기화
                _scheduledStartTime = null;
                _scheduledEndTime = null;
            }
        }
    }

    private static string FormatTimeRemaining(TimeSpan remaining)
    {
        if (remaining.TotalHours >= 1)
        {
            return $"{(int)remaining.TotalHours}시간 {remaining.Minutes}분 {remaining.Seconds}초";
        }
        else if (remaining.TotalMinutes >= 1)
        {
            return $"{remaining.Minutes}분 {remaining.Seconds}초";
        }
        else
        {
            return $"{remaining.Seconds}초";
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _checkTimer.Stop();
    }
}

/// <summary>
/// 예약 상태 이벤트 인자
/// </summary>
public class ScheduleStatusEventArgs : EventArgs
{
    public ScheduleStatus Status { get; init; }
    public TimeSpan? TimeRemaining { get; init; }
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 예약 상태
/// </summary>
public enum ScheduleStatus
{
    Waiting,
    Started,
    Ended,
    Cancelled
}
