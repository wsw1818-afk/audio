using System.Diagnostics;
using System.IO;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using AudioRecorder.Models;

namespace AudioRecorder.Audio;

/// <summary>
/// 녹음 엔진 - 마이크/시스템 오디오 캡처 및 믹싱
/// Ring Buffer와 SyncManager를 사용한 안정적인 동시 녹음 지원
/// </summary>
public class RecordingEngine : IDisposable
{
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveFileWriter? _writer;
    private WaveOutEvent? _silencePlayer;

    private readonly DeviceManager _deviceManager;
    private readonly LevelMeter _micLevelMeter = new();
    private readonly LevelMeter _systemLevelMeter = new();
    private readonly Stopwatch _stopwatch = new();
    private readonly SyncManager _syncManager = new(48000, 2);

    // Ring Buffer (500ms 버퍼 = 48000 * 2 * 0.5 = 48000 샘플)
    private readonly RingBuffer _micBuffer = new(48000);
    private readonly RingBuffer _systemBuffer = new(48000);

    private RecordingOptions _options = new();
    private string _currentFilePath = string.Empty;
    private bool _disposed;

    // 내부 포맷 (32-bit float, 48kHz, Stereo)
    private readonly WaveFormat _internalFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    // 출력 포맷 (16-bit PCM, 48kHz, Stereo)
    private readonly WaveFormat _outputFormat = new(48000, 16, 2);

    private readonly object _writeLock = new();
    private Thread? _mixingThread;
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private System.Threading.Timer? _flushTimer;
    private readonly AutoResetEvent _dataAvailableEvent = new(false);

    // 싱크 체크 간격
    private const int SYNC_CHECK_INTERVAL_MS = 5000;
    private DateTime _lastSyncCheck = DateTime.MinValue;

    // 레벨 업데이트 쓰로틀링
    private DateTime _lastLevelUpdate = DateTime.MinValue;
    private const int LEVEL_UPDATE_INTERVAL_MS = 50;

    public RecordingState State { get; private set; } = RecordingState.Stopped;
    public TimeSpan ElapsedTime => _stopwatch.Elapsed;
    public string CurrentFilePath => _currentFilePath;
    public LevelMeter MicLevelMeter => _micLevelMeter;
    public LevelMeter SystemLevelMeter => _systemLevelMeter;
    public SyncManager SyncManager => _syncManager;

    public float MicVolume { get; set; } = 1.0f;
    public float SystemVolume { get; set; } = 1.0f;

    public event EventHandler<LevelEventArgs>? LevelUpdated;
    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecordingErrorEventArgs>? ErrorOccurred;
    public event EventHandler<SyncDiagnostics>? SyncDiagnosticsUpdated;

    public RecordingEngine(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
    }

    /// <summary>
    /// 녹음 시작
    /// </summary>
    public void Start(RecordingOptions options)
    {
        if (State != RecordingState.Stopped)
            throw new InvalidOperationException("이미 녹음 중입니다.");

        _options = options;
        _currentFilePath = options.GetFullPath();

        // 출력 디렉토리 확인
        var dir = Path.GetDirectoryName(_currentFilePath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }

        try
        {
            // 상태 초기화
            _isPaused = false;

            // 버퍼 클리어
            _micBuffer.Clear();
            _systemBuffer.Clear();
            _syncManager.Reset();

            // WAV 파일 라이터 생성
            _writer = new WaveFileWriter(_currentFilePath, _outputFormat);

            // 주기적 Flush 타이머 (5초마다)
            _flushTimer = new System.Threading.Timer(_ =>
            {
                try { _writer?.Flush(); }
                catch { /* ignore */ }
            }, null, 5000, 5000);

            // 마이크 캡처 설정
            if (options.RecordMicrophone)
            {
                InitializeMicCapture(options.MicrophoneDeviceId);
            }

            // 시스템 오디오 캡처 설정
            if (options.RecordSystemAudio)
            {
                InitializeLoopbackCapture(options.OutputDeviceId);
            }

            // 믹싱 스레드 시작 (동시 녹음 시)
            if (options.RecordMicrophone && options.RecordSystemAudio)
            {
                _isRunning = true;
                _mixingThread = new Thread(MixingLoopWithSync)
                {
                    IsBackground = true,
                    Priority = ThreadPriority.AboveNormal
                };
                _mixingThread.Start();
            }

            // SyncManager 시작
            _syncManager.Start();

            // 캡처 시작
            _micCapture?.StartRecording();
            _loopbackCapture?.StartRecording();

            _stopwatch.Restart();
            State = RecordingState.Recording;
            OnStateChanged();
        }
        catch (Exception ex)
        {
            Cleanup();
            OnError($"녹음 시작 실패: {ex.Message}");
            throw;
        }
    }

    /// <summary>
    /// 녹음 중지
    /// </summary>
    public void Stop()
    {
        if (State == RecordingState.Stopped)
            return;

        // 상태 먼저 변경 (콜백에서 조기 종료하도록)
        State = RecordingState.Stopped;
        _isRunning = false;
        _isPaused = false;

        // 캡처 먼저 중지
        try { _micCapture?.StopRecording(); } catch { }
        try { _loopbackCapture?.StopRecording(); } catch { }
        try { _silencePlayer?.Stop(); } catch { }

        // 믹싱 스레드 종료 대기
        try { _dataAvailableEvent.Set(); } catch { }
        _mixingThread?.Join(2000);

        _syncManager.Stop();
        _stopwatch.Stop();
        _flushTimer?.Dispose();
        _flushTimer = null;

        // 남은 버퍼 데이터 플러시
        FlushRemainingBuffers();

        // 파일 닫기
        _writer?.Dispose();
        _writer = null;

        Cleanup();
        OnStateChanged();
    }

    /// <summary>
    /// 일시정지
    /// </summary>
    public void Pause()
    {
        if (State != RecordingState.Recording)
            return;

        _isPaused = true;
        _stopwatch.Stop();
        State = RecordingState.Paused;

        // 믹싱 스레드가 대기 상태에서 빠져나오도록 시그널
        _dataAvailableEvent.Set();

        OnStateChanged();
    }

    /// <summary>
    /// 재개
    /// </summary>
    public void Resume()
    {
        if (State != RecordingState.Paused)
            return;

        _isPaused = false;
        _stopwatch.Start();
        State = RecordingState.Recording;
        OnStateChanged();
    }

    private void InitializeMicCapture(string? deviceId)
    {
        MMDevice? device = null;

        if (!string.IsNullOrEmpty(deviceId))
        {
            device = _deviceManager.GetDeviceById(deviceId);
        }

        if (device == null)
        {
            var defaultDevice = _deviceManager.GetDefaultInputDevice();
            device = defaultDevice?.Device;
        }

        if (device == null)
        {
            throw new InvalidOperationException("마이크 장치를 찾을 수 없습니다.");
        }

        _micCapture = new WasapiCapture(device)
        {
            WaveFormat = _internalFormat
        };

        _micCapture.DataAvailable += OnMicDataAvailable;
        _micCapture.RecordingStopped += OnMicRecordingStopped;
    }

    private void InitializeLoopbackCapture(string? deviceId)
    {
        MMDevice? device = null;

        if (!string.IsNullOrEmpty(deviceId))
        {
            device = _deviceManager.GetDeviceById(deviceId);
        }

        if (device == null)
        {
            var defaultDevice = _deviceManager.GetDefaultOutputDevice();
            device = defaultDevice?.Device;
        }

        if (device == null)
        {
            throw new InvalidOperationException("출력 장치를 찾을 수 없습니다.");
        }

        _loopbackCapture = new WasapiLoopbackCapture(device);

        // Loopback이 무음 시에도 데이터 받도록 Silence 재생
        StartSilencePlayback(device);

        _loopbackCapture.DataAvailable += OnLoopbackDataAvailable;
        _loopbackCapture.RecordingStopped += OnLoopbackRecordingStopped;
    }

    private void StartSilencePlayback(MMDevice outputDevice)
    {
        try
        {
            var silence = new SilenceProvider(_internalFormat);
            _silencePlayer = new WaveOutEvent { DeviceNumber = -1 };
            _silencePlayer.Init(silence);
            _silencePlayer.Play();
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Silence 재생 시작 실패: {ex.Message}");
        }
    }

    private void OnMicDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || State == RecordingState.Stopped)
            return;

        // 레벨 미터는 항상 업데이트 (일시정지 중에도 미터 표시)
        _micLevelMeter.ProcessSamples(e.Buffer, e.BytesRecorded);
        ThrottledLevelUpdate();

        // 일시정지 중이면 버퍼에 쓰지 않음
        if (_isPaused || State != RecordingState.Recording)
            return;

        int sampleCount = e.BytesRecorded / 4;
        _syncManager.RecordMicData(sampleCount);

        // 동시 녹음 모드 - Ring Buffer 사용
        if (_options.RecordSystemAudio)
        {
            _micBuffer.WriteFromBytes(e.Buffer, e.BytesRecorded);
            try { _dataAvailableEvent.Set(); } catch { /* disposed */ }
        }
        else
        {
            // 마이크 단독 녹음
            WriteToFile(e.Buffer, e.BytesRecorded, MicVolume);
        }
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || State == RecordingState.Stopped)
            return;

        // 레벨 미터는 항상 업데이트 (일시정지 중에도 미터 표시)
        _systemLevelMeter.ProcessSamples(e.Buffer, e.BytesRecorded);
        ThrottledLevelUpdate();

        // 일시정지 중이면 버퍼에 쓰지 않음
        if (_isPaused || State != RecordingState.Recording)
            return;

        int sampleCount = e.BytesRecorded / 4;
        _syncManager.RecordSystemData(sampleCount);

        // 동시 녹음 모드 - Ring Buffer 사용
        if (_options.RecordMicrophone)
        {
            _systemBuffer.WriteFromBytes(e.Buffer, e.BytesRecorded);
            try { _dataAvailableEvent.Set(); } catch { /* disposed */ }
        }
        else
        {
            // 시스템 오디오 단독 녹음
            WriteToFile(e.Buffer, e.BytesRecorded, SystemVolume);
        }
    }

    private void ThrottledLevelUpdate()
    {
        var now = DateTime.Now;
        if ((now - _lastLevelUpdate).TotalMilliseconds >= LEVEL_UPDATE_INTERVAL_MS)
        {
            _lastLevelUpdate = now;
            OnLevelUpdated();
        }
    }

    /// <summary>
    /// 싱크 보정이 적용된 믹싱 루프
    /// </summary>
    private void MixingLoopWithSync()
    {
        const int CHUNK_SIZE = 1920; // 20ms at 48kHz stereo
        var micData = new float[CHUNK_SIZE];
        var sysData = new float[CHUNK_SIZE];
        var mixBuffer = new float[CHUNK_SIZE];

        try
        {
            while (_isRunning)
            {
                // 일시정지 상태면 대기
                if (_isPaused)
                {
                    try { _dataAvailableEvent.WaitOne(100); } catch { break; }
                    continue;
                }

                // 두 버퍼 모두 충분한 데이터가 있을 때만 처리
                int minAvailable = Math.Min(_micBuffer.Count, _systemBuffer.Count);

                if (minAvailable >= CHUNK_SIZE)
                {
                    // 싱크 체크 (5초마다)
                    if ((DateTime.Now - _lastSyncCheck).TotalMilliseconds > SYNC_CHECK_INTERVAL_MS)
                    {
                        PerformSyncCorrection();
                        _lastSyncCheck = DateTime.Now;

                        // 진단 정보 이벤트 발생
                        var diagnostics = _syncManager.GetDiagnostics();
                        SyncDiagnosticsUpdated?.Invoke(this, diagnostics);
                    }

                    // 데이터 읽기
                    int micRead = _micBuffer.Read(micData, 0, CHUNK_SIZE);
                    int sysRead = _systemBuffer.Read(sysData, 0, CHUNK_SIZE);

                    int samplesToMix = Math.Min(micRead, sysRead);

                    // 믹싱
                    for (int i = 0; i < samplesToMix; i++)
                    {
                        float micSample = micData[i] * MicVolume;
                        float sysSample = sysData[i] * SystemVolume;
                        mixBuffer[i] = Math.Clamp(micSample + sysSample, -1.0f, 1.0f);
                    }

                    // 파일 쓰기
                    WriteMixedToFile(mixBuffer, samplesToMix);
                }
                else
                {
                    // 데이터가 올 때까지 대기 (최대 20ms) - CPU 낭비 방지
                    try { _dataAvailableEvent.WaitOne(20); } catch { break; }
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"MixingLoop 오류: {ex.Message}");
        }
    }

    /// <summary>
    /// 싱크 보정 수행
    /// </summary>
    private void PerformSyncCorrection()
    {
        if (!_syncManager.NeedsCorrection())
            return;

        int correctionSamples = _syncManager.GetCorrectionSamples();

        if (correctionSamples > 0)
        {
            // 마이크가 앞섬 - 시스템에 무음 삽입
            _systemBuffer.InsertSilence(correctionSamples);
            Debug.WriteLine($"Sync correction: inserted {correctionSamples} silence samples to system buffer");
        }
        else if (correctionSamples < 0)
        {
            // 시스템이 앞섬 - 마이크에 무음 삽입
            _micBuffer.InsertSilence(-correctionSamples);
            Debug.WriteLine($"Sync correction: inserted {-correctionSamples} silence samples to mic buffer");
        }
    }

    /// <summary>
    /// 남은 버퍼 데이터 플러시
    /// </summary>
    private void FlushRemainingBuffers()
    {
        if (_writer == null) return;

        const int CHUNK_SIZE = 1920;
        var micData = new float[CHUNK_SIZE];
        var sysData = new float[CHUNK_SIZE];
        var mixBuffer = new float[CHUNK_SIZE];

        while (_micBuffer.Count > 0 || _systemBuffer.Count > 0)
        {
            int micRead = _micBuffer.Read(micData, 0, CHUNK_SIZE);
            int sysRead = _systemBuffer.Read(sysData, 0, CHUNK_SIZE);

            int samplesToMix = Math.Max(micRead, sysRead);

            for (int i = 0; i < samplesToMix; i++)
            {
                float micSample = i < micRead ? micData[i] * MicVolume : 0;
                float sysSample = i < sysRead ? sysData[i] * SystemVolume : 0;
                mixBuffer[i] = Math.Clamp(micSample + sysSample, -1.0f, 1.0f);
            }

            WriteMixedToFile(mixBuffer, samplesToMix);
        }
    }

    private void WriteToFile(byte[] buffer, int bytesRecorded, float volume)
    {
        if (_writer == null) return;

        int sampleCount = bytesRecorded / 4;
        var outputBuffer = new byte[sampleCount * 2];

        for (int i = 0; i < sampleCount; i++)
        {
            float sample = BitConverter.ToSingle(buffer, i * 4) * volume;
            sample = Math.Clamp(sample, -1.0f, 1.0f);
            short pcmSample = (short)(sample * 32767);
            BitConverter.TryWriteBytes(outputBuffer.AsSpan(i * 2), pcmSample);
        }

        lock (_writeLock)
        {
            _writer?.Write(outputBuffer, 0, outputBuffer.Length);
        }
    }

    private void WriteMixedToFile(float[] mixBuffer, int sampleCount)
    {
        if (_writer == null || sampleCount == 0) return;

        var outputBuffer = new byte[sampleCount * 2];

        for (int i = 0; i < sampleCount; i++)
        {
            float sample = Math.Clamp(mixBuffer[i], -1.0f, 1.0f);
            short pcmSample = (short)(sample * 32767);
            BitConverter.TryWriteBytes(outputBuffer.AsSpan(i * 2), pcmSample);
        }

        lock (_writeLock)
        {
            _writer?.Write(outputBuffer, 0, outputBuffer.Length);
        }
    }

    private void OnMicRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            OnError($"마이크 녹음 오류: {e.Exception.Message}");
        }
    }

    private void OnLoopbackRecordingStopped(object? sender, StoppedEventArgs e)
    {
        if (e.Exception != null)
        {
            OnError($"시스템 오디오 녹음 오류: {e.Exception.Message}");
        }
    }

    private void Cleanup()
    {
        _isRunning = false;
        _flushTimer?.Dispose();
        _flushTimer = null;

        _micCapture?.Dispose();
        _micCapture = null;

        _loopbackCapture?.Dispose();
        _loopbackCapture = null;

        _silencePlayer?.Dispose();
        _silencePlayer = null;

        _micLevelMeter.Reset();
        _systemLevelMeter.Reset();
        _micBuffer.Clear();
        _systemBuffer.Clear();
        _syncManager.Reset();
    }

    private void OnLevelUpdated()
    {
        LevelUpdated?.Invoke(this, new LevelEventArgs
        {
            MicLevel = _micLevelMeter.PeakLevel,
            MicLevelDb = _micLevelMeter.LevelDb,
            SystemLevel = _systemLevelMeter.PeakLevel,
            SystemLevelDb = _systemLevelMeter.LevelDb
        });
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs { State = State });
    }

    private void OnError(string message)
    {
        ErrorOccurred?.Invoke(this, new RecordingErrorEventArgs { Message = message });
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        Stop();
        _dataAvailableEvent.Dispose();
        _deviceManager.Dispose();
    }
}

public class LevelEventArgs : EventArgs
{
    public float MicLevel { get; init; }
    public float MicLevelDb { get; init; }
    public float SystemLevel { get; init; }
    public float SystemLevelDb { get; init; }
}

public class RecordingStateChangedEventArgs : EventArgs
{
    public RecordingState State { get; init; }
}

public class RecordingErrorEventArgs : EventArgs
{
    public string Message { get; init; } = string.Empty;
}

/// <summary>
/// 무음 제공자 - Loopback 캡처 활성화용
/// </summary>
public class SilenceProvider : IWaveProvider
{
    public WaveFormat WaveFormat { get; }

    public SilenceProvider(WaveFormat format)
    {
        WaveFormat = format;
    }

    public int Read(byte[] buffer, int offset, int count)
    {
        Array.Clear(buffer, offset, count);
        return count;
    }
}
