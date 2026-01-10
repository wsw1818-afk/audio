using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using AudioRecorder.Models;

namespace AudioRecorder.Audio;

/// <summary>
/// 고성능 녹음 엔진 - 마이크/시스템 오디오 캡처 및 믹싱
/// Ring Buffer와 SyncManager를 사용한 안정적인 동시 녹음 지원
/// 버퍼 재사용으로 GC 압력 최소화
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
    // 출력 포맷 (24-bit PCM, 48kHz, Stereo) - 스튜디오급 품질
    private readonly WaveFormat _outputFormat = new(48000, 24, 2);

    private readonly object _writeLock = new();
    private Thread? _mixingThread;
    private volatile bool _isRunning;
    private volatile bool _isPaused;
    private System.Threading.Timer? _flushTimer;
    private readonly AutoResetEvent _dataAvailableEvent = new(false);

    // 싱크 체크 간격 (Environment.TickCount64 사용 - DateTime.Now보다 빠름)
    private const int SYNC_CHECK_INTERVAL_MS = 5000;
    private long _lastSyncCheckTick;

    // 레벨 업데이트 쓰로틀링 (Environment.TickCount64 사용)
    private long _lastLevelUpdateTick;
    private const int LEVEL_UPDATE_INTERVAL_MS = 50;

    // 재사용 버퍼 (GC 압력 최소화)
    private const int CHUNK_SIZE = 1920; // 20ms at 48kHz stereo
    private readonly float[] _mixMicData = new float[CHUNK_SIZE];
    private readonly float[] _mixSysData = new float[CHUNK_SIZE];
    private readonly float[] _mixBuffer = new float[CHUNK_SIZE];
    private readonly byte[] _outputBuffer = new byte[CHUNK_SIZE * 3]; // 24-bit = 3 bytes per sample

    // 단독 녹음용 재사용 버퍼
    private byte[]? _soloOutputBuffer;

    // 기록된 바이트 수 추적 (UI에서 파일 I/O 없이 크기 표시용)
    private long _bytesWritten;

    public RecordingState State { get; private set; } = RecordingState.Stopped;
    public long BytesWritten => _bytesWritten;
    public TimeSpan ElapsedTime => _stopwatch.Elapsed;
    public string CurrentFilePath => _currentFilePath;
    public RecordingFormat TargetFormat { get; private set; } = RecordingFormat.WAV;
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
        TargetFormat = options.Format;

        // 항상 WAV로 먼저 녹음 (FLAC/MP3는 녹음 후 변환)
        _currentFilePath = Path.Combine(options.OutputDirectory, $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

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
            _bytesWritten = 0;

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
            try { _dataAvailableEvent.Set(); } catch (ObjectDisposedException) { /* 정상 종료 */ }
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
            try { _dataAvailableEvent.Set(); } catch (ObjectDisposedException) { /* 정상 종료 */ }
        }
        else
        {
            // 시스템 오디오 단독 녹음
            WriteToFile(e.Buffer, e.BytesRecorded, SystemVolume);
        }
    }

    private void ThrottledLevelUpdate()
    {
        long now = Environment.TickCount64;
        if (now - _lastLevelUpdateTick >= LEVEL_UPDATE_INTERVAL_MS)
        {
            _lastLevelUpdateTick = now;
            OnLevelUpdated();
        }
    }

    /// <summary>
    /// 싱크 보정이 적용된 믹싱 루프 (재사용 버퍼 사용)
    /// </summary>
    private void MixingLoopWithSync()
    {
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
                    long now = Environment.TickCount64;
                    if (now - _lastSyncCheckTick > SYNC_CHECK_INTERVAL_MS)
                    {
                        PerformSyncCorrection();
                        _lastSyncCheckTick = now;

                        // 진단 정보 이벤트 발생
                        var diagnostics = _syncManager.GetDiagnostics();
                        SyncDiagnosticsUpdated?.Invoke(this, diagnostics);
                    }

                    // 데이터 읽기 (재사용 버퍼)
                    int micRead = _micBuffer.Read(_mixMicData, 0, CHUNK_SIZE);
                    int sysRead = _systemBuffer.Read(_mixSysData, 0, CHUNK_SIZE);

                    int samplesToMix = Math.Min(micRead, sysRead);

                    // 믹싱 (벡터화 가능한 루프)
                    float micVol = MicVolume;
                    float sysVol = SystemVolume;
                    for (int i = 0; i < samplesToMix; i++)
                    {
                        float mixed = _mixMicData[i] * micVol + _mixSysData[i] * sysVol;
                        _mixBuffer[i] = Math.Clamp(mixed, -1.0f, 1.0f);
                    }

                    // 파일 쓰기 (재사용 버퍼)
                    WriteMixedToFile(_mixBuffer, samplesToMix);
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
    /// 남은 버퍼 데이터 플러시 (재사용 버퍼 사용)
    /// </summary>
    private void FlushRemainingBuffers()
    {
        if (_writer == null) return;

        float micVol = MicVolume;
        float sysVol = SystemVolume;

        while (_micBuffer.Count > 0 || _systemBuffer.Count > 0)
        {
            int micRead = _micBuffer.Read(_mixMicData, 0, CHUNK_SIZE);
            int sysRead = _systemBuffer.Read(_mixSysData, 0, CHUNK_SIZE);

            int samplesToMix = Math.Max(micRead, sysRead);

            for (int i = 0; i < samplesToMix; i++)
            {
                float micSample = i < micRead ? _mixMicData[i] * micVol : 0;
                float sysSample = i < sysRead ? _mixSysData[i] * sysVol : 0;
                _mixBuffer[i] = Math.Clamp(micSample + sysSample, -1.0f, 1.0f);
            }

            WriteMixedToFile(_mixBuffer, samplesToMix);
        }
    }

    private void WriteToFile(byte[] buffer, int bytesRecorded, float volume)
    {
        if (_writer == null) return;

        int sampleCount = bytesRecorded / 4;
        int outputSize = sampleCount * 3; // 24-bit = 3 bytes per sample

        // 필요시 버퍼 재할당 (일반적으로 한 번만 발생)
        if (_soloOutputBuffer == null || _soloOutputBuffer.Length < outputSize)
        {
            _soloOutputBuffer = new byte[outputSize];
        }

        // Span 기반 변환 (제로카피)
        var floatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
        var outputSpan = _soloOutputBuffer.AsSpan(0, outputSize);

        // 24-bit 변환 (float -> 24-bit PCM) - Span으로 최적화
        for (int i = 0; i < sampleCount; i++)
        {
            float sample = Math.Clamp(floatSpan[i] * volume, -1.0f, 1.0f);
            int sample24 = (int)(sample * 8388607); // 2^23 - 1
            int byteIndex = i * 3;
            outputSpan[byteIndex] = (byte)sample24;
            outputSpan[byteIndex + 1] = (byte)(sample24 >> 8);
            outputSpan[byteIndex + 2] = (byte)(sample24 >> 16);
        }

        lock (_writeLock)
        {
            _writer?.Write(_soloOutputBuffer, 0, outputSize);
            _bytesWritten += outputSize;
        }
    }

    private void WriteMixedToFile(float[] mixBuffer, int sampleCount)
    {
        if (_writer == null || sampleCount == 0) return;

        int outputSize = sampleCount * 3; // 24-bit = 3 bytes per sample

        // 24-bit 변환 (float -> 24-bit PCM) - Span으로 최적화
        var outputSpan = _outputBuffer.AsSpan(0, outputSize);
        for (int i = 0; i < sampleCount; i++)
        {
            int sample24 = (int)(mixBuffer[i] * 8388607); // 2^23 - 1
            int byteIndex = i * 3;
            outputSpan[byteIndex] = (byte)sample24;
            outputSpan[byteIndex + 1] = (byte)(sample24 >> 8);
            outputSpan[byteIndex + 2] = (byte)(sample24 >> 16);
        }

        lock (_writeLock)
        {
            _writer?.Write(_outputBuffer, 0, outputSize);
            _bytesWritten += outputSize;
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

        // 단독 녹음용 버퍼 정리 (메모리 해제)
        _soloOutputBuffer = null;
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
        // DeviceManager는 외부에서 주입받으므로 여기서 Dispose하지 않음
        // MainViewModel에서 DeviceManager의 수명을 관리
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
