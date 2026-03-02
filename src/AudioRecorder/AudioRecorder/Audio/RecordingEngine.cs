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

    // 자동 분할 녹음 관련
    private bool _autoSplitEnabled;
    private int _autoSplitIntervalMinutes;
    private int _segmentIndex;
    private string _baseFilePath = string.Empty; // 첫 번째 세그먼트의 기본 경로 (확장자 제외)
    private TimeSpan _nextSplitTime;
    private readonly List<string> _completedSegments = new();

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

    public int SegmentIndex => _segmentIndex;
    public IReadOnlyList<string> CompletedSegments => _completedSegments;

    public event EventHandler<LevelEventArgs>? LevelUpdated;
    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecordingErrorEventArgs>? ErrorOccurred;
    public event EventHandler<SyncDiagnostics>? SyncDiagnosticsUpdated;
    public event EventHandler<SegmentCompletedEventArgs>? SegmentCompleted;

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

        // 자동 분할 설정 초기화
        _autoSplitEnabled = options.AutoSplitEnabled;
        _autoSplitIntervalMinutes = options.AutoSplitIntervalMinutes;
        _segmentIndex = 1;
        _completedSegments.Clear();

        // 항상 WAV로 먼저 녹음 (FLAC/MP3는 녹음 후 변환)
        var baseFileName = $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}";
        _baseFilePath = Path.Combine(options.OutputDirectory, baseFileName);

        if (_autoSplitEnabled)
        {
            _currentFilePath = $"{_baseFilePath}_Part{_segmentIndex:D2}.wav";
            _nextSplitTime = TimeSpan.FromMinutes(_autoSplitIntervalMinutes);
        }
        else
        {
            _currentFilePath = $"{_baseFilePath}.wav";
        }

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

            // WAV 헤더 공간 확보 - 헤더가 확실히 기록되었는지 확인
            // WaveFileWriter가 내부적으로 44바이트 헤더를 예약하지만,
            // 대용량 파일에서 Dispose 시 헤더 업데이트가 실패할 수 있으므로
            // 시작 시 Flush하여 헤더가 디스크에 기록되도록 함
            _writer.Flush();
            Debug.WriteLine($"[RecordingEngine] WAV 파일 생성: {_currentFilePath}");

            // 주기적 Flush 타이머 (5초마다) + 자동 분할 체크
            _flushTimer = new System.Threading.Timer(_ =>
            {
                try
                {
                    _writer?.Flush();

                    // 자동 분할 체크
                    if (_autoSplitEnabled && !_isPaused && State == RecordingState.Recording)
                    {
                        if (_stopwatch.Elapsed >= _nextSplitTime)
                        {
                            PerformFileSplit();
                        }
                    }
                }
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

        // 파일 안전하게 닫기 (WAV 헤더 업데이트 보장)
        string filePath = _currentFilePath;
        long bytesWritten = _bytesWritten;
        WaveFormat format = _outputFormat;

        lock (_writeLock)
        {
            if (_writer != null)
            {
                try
                {
                    _writer.Flush();
                    Debug.WriteLine($"[RecordingEngine] WAV 파일 Flush 완료, 크기: {_bytesWritten} bytes");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RecordingEngine] Flush 오류: {ex.Message}");
                }

                try
                {
                    _writer.Dispose();
                    Debug.WriteLine("[RecordingEngine] WAV 파일 Dispose 완료");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[RecordingEngine] Dispose 오류: {ex.Message}");
                }

                _writer = null;
            }
        }

        // 파일이 완전히 닫힐 때까지 잠시 대기
        Thread.Sleep(100);

        // WAV 헤더 검증 및 복구 (대용량 파일에서 헤더 손상 방지)
        try
        {
            RepairWavHeaderIfNeeded(filePath, bytesWritten, format);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingEngine] WAV 헤더 복구 실패: {ex.Message}");
        }

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
    /// 자동 분할: 현재 파일을 닫고 새 파일로 전환
    /// write lock 내에서 writer를 교체하여 데이터 손실 없이 분할
    /// </summary>
    private void PerformFileSplit()
    {
        var completedFilePath = _currentFilePath;
        var completedBytesWritten = _bytesWritten;
        var segmentDuration = TimeSpan.FromMinutes(_autoSplitIntervalMinutes);

        // 다음 세그먼트 파일 경로 생성
        _segmentIndex++;
        var newFilePath = $"{_baseFilePath}_Part{_segmentIndex:D2}.wav";

        Debug.WriteLine($"[RecordingEngine] 자동 분할: Part{_segmentIndex - 1:D2} -> Part{_segmentIndex:D2} (경과: {_stopwatch.Elapsed:hh\\:mm\\:ss})");

        lock (_writeLock)
        {
            try
            {
                // 1. 현재 파일 플러시 및 닫기
                if (_writer != null)
                {
                    _writer.Flush();
                    _writer.Dispose();
                    _writer = null;
                    Debug.WriteLine($"[RecordingEngine] 세그먼트 {_segmentIndex - 1} 닫기 완료: {completedBytesWritten} bytes");
                }

                // 2. 새 파일 열기
                _writer = new WaveFileWriter(newFilePath, _outputFormat);
                _writer.Flush();
                _currentFilePath = newFilePath;
                _bytesWritten = 0;

                Debug.WriteLine($"[RecordingEngine] 새 세그먼트 시작: {newFilePath}");
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RecordingEngine] 파일 분할 실패: {ex.Message}");
                OnError($"파일 분할 실패: {ex.Message}");
                return;
            }
        }

        // 다음 분할 시간 설정
        _nextSplitTime = _nextSplitTime.Add(TimeSpan.FromMinutes(_autoSplitIntervalMinutes));

        // 완료된 세그먼트 WAV 헤더 복구 (비동기적으로)
        _completedSegments.Add(completedFilePath);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                RepairWavHeaderIfNeeded(completedFilePath, completedBytesWritten, _outputFormat);
            }
            catch (Exception ex)
            {
                Debug.WriteLine($"[RecordingEngine] 세그먼트 WAV 헤더 복구 실패: {ex.Message}");
            }

            // 분할 완료 이벤트 발생
            SegmentCompleted?.Invoke(this, new SegmentCompletedEventArgs
            {
                FilePath = completedFilePath,
                SegmentIndex = _segmentIndex - 1,
                Duration = segmentDuration,
                FileSize = File.Exists(completedFilePath) ? new FileInfo(completedFilePath).Length : 0
            });
        });
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

    /// <summary>
    /// WAV 파일 헤더가 손상된 경우 복구
    /// WaveFileWriter.Dispose()가 헤더 업데이트에 실패한 경우 직접 복구
    /// </summary>
    private void RepairWavHeaderIfNeeded(string filePath, long dataSize, WaveFormat format)
    {
        if (!File.Exists(filePath)) return;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
        var header = new byte[44]; // 전체 WAV 헤더 크기
        fs.Read(header, 0, Math.Min(44, (int)fs.Length));

        // RIFF 마커 확인 (offset 0-3)
        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F')
        {
            Debug.WriteLine("[RecordingEngine] RIFF 마커 없음 - 복구 불가");
            return;
        }

        // WAVE 마커 확인 (offset 8-11) - 정확히 "WAVE" 문자열이어야 함
        bool hasValidWaveMarker = header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E';

        // fmt 청크 확인 (offset 12-15) - 정확히 "fmt " 문자열이어야 함
        bool hasValidFmtChunk = header[12] == 'f' && header[13] == 'm' && header[14] == 't' && header[15] == ' ';

        if (hasValidWaveMarker && hasValidFmtChunk)
        {
            Debug.WriteLine("[RecordingEngine] WAV 헤더 정상 (WAVE/fmt 확인됨)");
            return;
        }

        // 헤더가 손상된 패턴 로깅 (디버깅용)
        Debug.WriteLine($"[RecordingEngine] WAV 헤더 손상 감지:");
        Debug.WriteLine($"  - WAVE 마커: 0x{header[8]:X2} 0x{header[9]:X2} 0x{header[10]:X2} 0x{header[11]:X2} (expected: WAVE)");
        Debug.WriteLine($"  - fmt 청크: 0x{header[12]:X2} 0x{header[13]:X2} 0x{header[14]:X2} 0x{header[15]:X2} (expected: fmt )");
        Debug.WriteLine($"  - 헤더 hex: {BitConverter.ToString(header, 0, 20)}");

        fs.Close(); // 파일 스트림 닫기

        // 파일 복구: 새 파일에 올바른 헤더 + 원본 데이터 복사
        RepairWavFileWithCorrectHeader(filePath, format);
    }

    /// <summary>
    /// WAV 파일을 새로 생성하여 올바른 헤더와 함께 저장
    /// 대용량 파일(3GB+)도 효율적으로 처리 (청크 단위 복사)
    /// </summary>
    private void RepairWavFileWithCorrectHeader(string filePath, WaveFormat format)
    {
        var tempPath = filePath + ".repair.tmp";
        const int COPY_BUFFER_SIZE = 4 * 1024 * 1024; // 4MB 청크 (대용량 파일 처리 속도 향상)

        try
        {
            using (var srcFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, COPY_BUFFER_SIZE))
            using (var dstFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, COPY_BUFFER_SIZE))
            {
                // 손상된 파일에서 오디오 데이터 시작 위치 찾기
                // 패턴 1: RIFF + size(4) + 0x00... (헤더가 null로 채워짐) -> offset 44부터 데이터
                // 패턴 2: RIFF + size(4) + 오디오데이터 (헤더 없이 바로 데이터) -> offset 8부터 데이터

                // 헤더 영역 읽기
                var headerArea = new byte[64];
                srcFs.Read(headerArea, 0, Math.Min(64, (int)Math.Min(srcFs.Length, 64)));
                srcFs.Seek(0, SeekOrigin.Begin);

                long audioDataStart;

                // offset 8-43 영역이 대부분 0인지 확인 (null 헤더 패턴)
                int nullCount = 0;
                for (int i = 8; i < 44 && i < headerArea.Length; i++)
                {
                    if (headerArea[i] == 0) nullCount++;
                }

                if (nullCount > 30) // 36바이트 중 30바이트 이상이 0이면 null 헤더
                {
                    // 패턴 1: 헤더 영역이 null로 채워짐 - offset 44부터 실제 데이터
                    audioDataStart = 44;
                    Debug.WriteLine("[RecordingEngine] 복구 패턴: null 헤더 (offset 44부터 데이터)");
                }
                else
                {
                    // 패턴 2: 헤더 없이 바로 데이터 - offset 8부터 실제 데이터
                    audioDataStart = 8;
                    Debug.WriteLine("[RecordingEngine] 복구 패턴: 헤더 없음 (offset 8부터 데이터)");
                }

                long audioDataSize = srcFs.Length - audioDataStart;

                Debug.WriteLine($"[RecordingEngine] 복구: 원본={srcFs.Length / (1024.0 * 1024.0):F1}MB, 오디오 시작={audioDataStart}, 오디오={audioDataSize / (1024.0 * 1024.0):F1}MB");

                // 새 WAV 헤더 작성 (44바이트) - long 타입으로 대용량 지원
                WriteWavHeader(dstFs, audioDataSize, format);

                // 오디오 데이터 복사 (청크 단위로 효율적 처리)
                srcFs.Seek(audioDataStart, SeekOrigin.Begin);
                var buffer = new byte[COPY_BUFFER_SIZE];
                int bytesRead;
                long totalCopied = 0;
                var sw = Stopwatch.StartNew();

                while ((bytesRead = srcFs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dstFs.Write(buffer, 0, bytesRead);
                    totalCopied += bytesRead;

                    // 진행 상황 로깅 (500MB마다)
                    if (totalCopied % (500 * 1024 * 1024) < COPY_BUFFER_SIZE)
                    {
                        double percent = (double)totalCopied / audioDataSize * 100;
                        double mbPerSec = totalCopied / (1024.0 * 1024.0) / sw.Elapsed.TotalSeconds;
                        Debug.WriteLine($"[RecordingEngine] 복구 진행: {totalCopied / (1024 * 1024)}MB / {audioDataSize / (1024 * 1024)}MB ({percent:F1}%, {mbPerSec:F0}MB/s)");
                    }
                }

                dstFs.Flush();
                sw.Stop();
                Debug.WriteLine($"[RecordingEngine] 복구 완료: {totalCopied / (1024.0 * 1024.0):F1}MB, 소요시간: {sw.Elapsed.TotalSeconds:F1}초");
            }

            // 원본 파일을 백업하고 복구된 파일로 교체
            var backupPath = filePath + ".corrupted.bak";
            if (File.Exists(backupPath))
                File.Delete(backupPath);

            File.Move(filePath, backupPath);
            File.Move(tempPath, filePath);

            // 백업 파일 삭제 (복구 성공 시)
            File.Delete(backupPath);

            Debug.WriteLine($"[RecordingEngine] WAV 파일 복구 완료: {filePath}");
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[RecordingEngine] WAV 파일 복구 실패: {ex.Message}");

            // 임시 파일 정리
            try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
        }
    }

    /// <summary>
    /// WAV 헤더 작성 (44바이트 표준 PCM 헤더)
    /// 대용량 파일(2GB+)도 지원 - RF64 형식은 사용하지 않고 표준 WAV 최대값 사용
    /// </summary>
    private void WriteWavHeader(FileStream fs, long dataSize, WaveFormat format)
    {
        using var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        // WAV 파일의 크기 필드는 32비트이므로 최대 약 4GB까지 표현 가능
        // 하지만 signed int를 사용하는 일부 구현에서는 2GB 제한이 있음
        // 여기서는 uint로 최대 4GB까지 지원
        uint maxSize = uint.MaxValue - 36; // 약 4GB
        uint actualDataSize = (uint)Math.Min((ulong)dataSize, maxSize);
        uint fileSize = actualDataSize + 36; // 전체 크기 - 8 (RIFF/size 제외)

        Debug.WriteLine($"[RecordingEngine] WriteWavHeader: dataSize={dataSize}, actualDataSize={actualDataSize}");

        // RIFF 청크
        bw.Write(new[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        bw.Write(fileSize); // uint로 쓰기

        // WAVE 마커
        bw.Write(new[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });

        // fmt 청크
        bw.Write(new[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' });
        bw.Write(16); // fmt 청크 크기 (PCM은 16)
        bw.Write((short)1); // 오디오 포맷 (1 = PCM)
        bw.Write((short)format.Channels); // 채널 수
        bw.Write(format.SampleRate); // 샘플레이트
        bw.Write(format.AverageBytesPerSecond); // 바이트/초
        bw.Write((short)format.BlockAlign); // 블록 정렬
        bw.Write((short)format.BitsPerSample); // 비트/샘플

        // data 청크
        bw.Write(new[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' });
        bw.Write(actualDataSize); // uint로 쓰기

        bw.Flush();
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

public class SegmentCompletedEventArgs : EventArgs
{
    public string FilePath { get; init; } = string.Empty;
    public int SegmentIndex { get; init; }
    public TimeSpan Duration { get; init; }
    public long FileSize { get; init; }
}

public class ScreenRecordingCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public long FrameCount { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Warning { get; init; }
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
