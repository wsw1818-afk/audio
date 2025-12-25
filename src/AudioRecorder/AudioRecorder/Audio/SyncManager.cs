using System.Diagnostics;

namespace AudioRecorder.Audio;

/// <summary>
/// 싱크 관리자 - 마이크와 시스템 오디오 스트림 간 드리프트 보정
/// </summary>
public class SyncManager
{
    private readonly Stopwatch _masterClock = new();
    private readonly object _lock = new();

    private long _micSamplePosition;
    private long _systemSamplePosition;
    private long _lastMicTicks;
    private long _lastSystemTicks;

    private readonly int _sampleRate;
    private readonly int _channels;

    // 드리프트 임계값 (샘플 단위)
    private const int DRIFT_THRESHOLD_SAMPLES = 480; // 10ms at 48kHz
    private const int MAX_CORRECTION_SAMPLES = 960;  // 20ms at 48kHz

    public SyncManager(int sampleRate = 48000, int channels = 2)
    {
        _sampleRate = sampleRate;
        _channels = channels;
    }

    /// <summary>
    /// 마스터 클록 시작
    /// </summary>
    public void Start()
    {
        lock (_lock)
        {
            _masterClock.Restart();
            _micSamplePosition = 0;
            _systemSamplePosition = 0;
            _lastMicTicks = 0;
            _lastSystemTicks = 0;
        }
    }

    /// <summary>
    /// 마스터 클록 정지
    /// </summary>
    public void Stop()
    {
        lock (_lock)
        {
            _masterClock.Stop();
        }
    }

    /// <summary>
    /// 리셋
    /// </summary>
    public void Reset()
    {
        lock (_lock)
        {
            _masterClock.Reset();
            _micSamplePosition = 0;
            _systemSamplePosition = 0;
            _lastMicTicks = 0;
            _lastSystemTicks = 0;
        }
    }

    /// <summary>
    /// 마이크 데이터 수신 기록
    /// </summary>
    public void RecordMicData(int sampleCount)
    {
        lock (_lock)
        {
            _micSamplePosition += sampleCount;
            _lastMicTicks = _masterClock.ElapsedTicks;
        }
    }

    /// <summary>
    /// 시스템 오디오 데이터 수신 기록
    /// </summary>
    public void RecordSystemData(int sampleCount)
    {
        lock (_lock)
        {
            _systemSamplePosition += sampleCount;
            _lastSystemTicks = _masterClock.ElapsedTicks;
        }
    }

    /// <summary>
    /// 현재 드리프트 계산 (샘플 단위)
    /// 양수: 마이크가 시스템보다 앞섬
    /// 음수: 시스템이 마이크보다 앞섬
    /// </summary>
    public int CalculateDrift()
    {
        lock (_lock)
        {
            // 경과 시간 기준 예상 샘플 수 계산
            double elapsedSeconds = _masterClock.Elapsed.TotalSeconds;
            long expectedSamples = (long)(elapsedSeconds * _sampleRate * _channels);

            // 실제 수신 샘플과 비교
            long micDelta = _micSamplePosition - expectedSamples;
            long sysDelta = _systemSamplePosition - expectedSamples;

            // 두 스트림 간 차이
            return (int)(micDelta - sysDelta);
        }
    }

    /// <summary>
    /// 드리프트 보정이 필요한지 확인
    /// </summary>
    public bool NeedsCorrection()
    {
        return Math.Abs(CalculateDrift()) > DRIFT_THRESHOLD_SAMPLES;
    }

    /// <summary>
    /// 보정에 필요한 샘플 수 계산
    /// </summary>
    public int GetCorrectionSamples()
    {
        int drift = CalculateDrift();
        if (Math.Abs(drift) <= DRIFT_THRESHOLD_SAMPLES)
            return 0;

        // 최대 보정량 제한
        return Math.Clamp(drift, -MAX_CORRECTION_SAMPLES, MAX_CORRECTION_SAMPLES);
    }

    /// <summary>
    /// 무음 패딩 생성 (32-bit float)
    /// </summary>
    public byte[] CreateSilencePadding(int samples)
    {
        int byteCount = samples * 4; // 32-bit float = 4 bytes
        return new byte[byteCount];
    }

    /// <summary>
    /// 진단 정보 가져오기
    /// </summary>
    public SyncDiagnostics GetDiagnostics()
    {
        lock (_lock)
        {
            return new SyncDiagnostics
            {
                ElapsedTime = _masterClock.Elapsed,
                MicSamplePosition = _micSamplePosition,
                SystemSamplePosition = _systemSamplePosition,
                Drift = CalculateDrift(),
                DriftMs = CalculateDrift() / (float)(_sampleRate * _channels) * 1000
            };
        }
    }
}

public class SyncDiagnostics
{
    public TimeSpan ElapsedTime { get; init; }
    public long MicSamplePosition { get; init; }
    public long SystemSamplePosition { get; init; }
    public int Drift { get; init; }
    public float DriftMs { get; init; }

    public override string ToString()
    {
        return $"Elapsed: {ElapsedTime:mm\\:ss\\.fff}, Mic: {MicSamplePosition}, Sys: {SystemSamplePosition}, Drift: {DriftMs:F1}ms";
    }
}
