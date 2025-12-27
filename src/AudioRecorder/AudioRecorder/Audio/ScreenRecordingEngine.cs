using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using AudioRecorder.Models;
using AudioRecorder.Services;

namespace AudioRecorder.Audio;

/// <summary>
/// 화면 녹화 엔진 - 화면 캡처 + 오디오 녹음 + 인코딩 통합
/// </summary>
public class ScreenRecordingEngine : IDisposable
{
    private readonly DeviceManager _deviceManager;
    private readonly ScreenCaptureService _screenCapture;
    private readonly VideoEncoderService _videoEncoder;

    // 오디오 캡처
    private WasapiCapture? _micCapture;
    private WasapiLoopbackCapture? _loopbackCapture;
    private WaveOutEvent? _silencePlayer;
    private WaveFileWriter? _audioWriter;

    // 레벨 미터
    private readonly LevelMeter _micLevelMeter = new();
    private readonly LevelMeter _systemLevelMeter = new();

    // 상태
    private readonly Stopwatch _stopwatch = new();
    private ScreenRecordingOptions _options = new();
    private string _videoPath = string.Empty;
    private string _audioPath = string.Empty;
    private string _finalOutputPath = string.Empty;
    private bool _disposed;
    private volatile bool _isRecording;
    private volatile bool _isPaused;

    // 내부 포맷 (32-bit float, 48kHz, Stereo)
    private readonly WaveFormat _internalFormat = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);
    // 출력 포맷 (16-bit PCM, 48kHz, Stereo)
    private readonly WaveFormat _outputFormat = new(48000, 16, 2);

    // 믹싱용 버퍼
    private readonly RingBuffer _micBuffer = new(48000);
    private readonly RingBuffer _systemBuffer = new(48000);
    private Thread? _audioMixingThread;
    private readonly AutoResetEvent _audioDataEvent = new(false);
    private readonly object _writeLock = new();

    // 레벨 업데이트 쓰로틀링
    private DateTime _lastLevelUpdate = DateTime.MinValue;
    private const int LEVEL_UPDATE_INTERVAL_MS = 50;

    /// <summary>
    /// 녹화 상태
    /// </summary>
    public RecordingState State { get; private set; } = RecordingState.Stopped;

    /// <summary>
    /// 경과 시간
    /// </summary>
    public TimeSpan ElapsedTime => _stopwatch.Elapsed;

    /// <summary>
    /// 캡처된 프레임 수
    /// </summary>
    public long FrameCount => _screenCapture.FrameCount;

    /// <summary>
    /// 현재 FPS
    /// </summary>
    public double CurrentFps => _stopwatch.Elapsed.TotalSeconds > 0
        ? _screenCapture.FrameCount / _stopwatch.Elapsed.TotalSeconds
        : 0;

    /// <summary>
    /// 출력 파일 경로
    /// </summary>
    public string OutputPath => _finalOutputPath;

    /// <summary>
    /// FFmpeg 사용 가능 여부
    /// </summary>
    public bool IsFFmpegAvailable => _videoEncoder.IsFFmpegAvailable;

    // 이벤트
    public event EventHandler<LevelEventArgs>? LevelUpdated;
    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecordingErrorEventArgs>? ErrorOccurred;
    public event EventHandler<ScreenRecordingCompletedEventArgs>? RecordingCompleted;

    public ScreenRecordingEngine(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
        _screenCapture = new ScreenCaptureService();
        _videoEncoder = new VideoEncoderService();

        _screenCapture.FrameAvailable += OnFrameAvailable;
        _screenCapture.ErrorOccurred += OnCaptureError;
        _videoEncoder.EncodingCompleted += OnEncodingCompleted;
    }

    /// <summary>
    /// 녹화 시작
    /// </summary>
    public void Start(ScreenRecordingOptions options)
    {
        if (State != RecordingState.Stopped || _isStopping)
            throw new InvalidOperationException("이미 녹화 중이거나 정지 작업이 진행 중입니다.");

        if (!_videoEncoder.IsFFmpegAvailable)
            throw new InvalidOperationException("FFmpeg를 찾을 수 없습니다. ffmpeg.exe를 앱 폴더에 복사하세요.");

        _options = options;

        // 출력 파일 경로 설정
        var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var extension = options.VideoFormat.GetExtension();
        _finalOutputPath = Path.Combine(options.OutputDirectory, $"ScreenRecording_{timestamp}{extension}");
        _videoPath = Path.Combine(options.OutputDirectory, $"temp_video_{timestamp}{extension}");
        _audioPath = Path.Combine(options.OutputDirectory, $"temp_audio_{timestamp}.wav");

        // 출력 디렉토리 확인
        if (!Directory.Exists(options.OutputDirectory))
        {
            Directory.CreateDirectory(options.OutputDirectory);
        }

        try
        {
            _isPaused = false;

            // 버퍼 초기화
            _micBuffer.Clear();
            _systemBuffer.Clear();
            _receivedFrameCount = 0;

            // 화면 캡처 먼저 초기화 (크기 정보 필요)
            _screenCapture.Start(options.Region, options.FrameRate, options.ShowMouseCursor);

            // 비디오 인코더 시작 (캡처 시작 후 크기 정보로)
            _videoEncoder.StartEncoding(
                _videoPath,
                _screenCapture.FrameWidth,
                _screenCapture.FrameHeight,
                options.FrameRate,
                options.VideoFormat,
                options.VideoBitrate,
                options.UseHardwareEncoding);

            // 인코더가 준비된 후 녹화 플래그 활성화
            _isRecording = true;

            // 오디오 녹음 시작
            if (options.IncludeMicrophone || options.IncludeSystemAudio)
            {
                StartAudioRecording(options);
            }

            _stopwatch.Restart();
            State = RecordingState.Recording;
            OnStateChanged();
        }
        catch (Exception ex)
        {
            Cleanup();
            OnError($"녹화 시작 실패: {ex.Message}");
            throw;
        }
    }

    // 정지 작업 진행 중 플래그
    private volatile bool _isStopping;

    // 인코딩 진행 중 플래그
    private volatile bool _isEncoding;

    /// <summary>
    /// 인코딩 진행 중 여부
    /// </summary>
    public bool IsEncoding => _isEncoding;

    /// <summary>
    /// 녹화 중지 - 캡처만 즉시 정지하고 인코딩은 백그라운드에서 진행
    /// </summary>
    public Task StopAsync()
    {
        if (State == RecordingState.Stopped || _isStopping)
            return Task.CompletedTask;

        Debug.WriteLine("[ScreenRecording] StopAsync 시작");
        _isStopping = true;
        _isRecording = false;
        _isPaused = false;
        State = RecordingState.Stopping;

        try
        {
            Debug.WriteLine("[ScreenRecording] 화면 캡처 중지...");
            _screenCapture.Stop();

            Debug.WriteLine("[ScreenRecording] 오디오 캡처 중지...");
            StopAudioCapture();

            Debug.WriteLine("[ScreenRecording] 오디오 믹싱 스레드 종료 대기...");
            _audioDataEvent.Set();
            _audioMixingThread?.Join(2000);

            _stopwatch.Stop();
            var recordedDuration = _stopwatch.Elapsed;
            var recordedFrameCount = _screenCapture.FrameCount;

            // 캡처 정지 완료 - 즉시 상태 변경하여 UI 활성화
            Debug.WriteLine("[ScreenRecording] 캡처 정지 완료, 상태를 Stopped로 변경");
            Cleanup();
            State = RecordingState.Stopped;
            _isStopping = false;
            OnStateChanged();

            // 인코딩은 백그라운드에서 진행
            Debug.WriteLine($"[ScreenRecording] 백그라운드 태스크 시작 준비 - duration: {recordedDuration}, frames: {recordedFrameCount}");
            Debug.WriteLine($"[ScreenRecording] _videoPath: {_videoPath}, _audioPath: {_audioPath}, _finalOutputPath: {_finalOutputPath}");

            // 로컬 변수로 복사 (클로저 문제 방지)
            var videoPath = _videoPath;
            var audioPath = _audioPath;
            var finalOutputPath = _finalOutputPath;

            _isEncoding = true;
            _ = Task.Run(async () =>
            {
                Debug.WriteLine("[ScreenRecording] 백그라운드 태스크 실행 시작!");
                try
                {
                    await EncodeAndMuxAsync(recordedDuration, recordedFrameCount);
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenRecording] 백그라운드 인코딩 예외: {ex.Message}\n{ex.StackTrace}");
                    OnError($"인코딩 실패: {ex.Message}");
                    RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
                    {
                        Success = false,
                        ErrorMessage = ex.Message
                    });
                }
                finally
                {
                    _isEncoding = false;
                    Debug.WriteLine("[ScreenRecording] 백그라운드 태스크 종료");
                }
            });
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenRecording] StopAsync 예외: {ex.Message}");
            OnError($"녹화 중지 실패: {ex.Message}");
            Cleanup();
            State = RecordingState.Stopped;
            _isStopping = false;
            _isEncoding = false;
            OnStateChanged();

            RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// 백그라운드에서 인코딩 및 합성 수행
    /// </summary>
    private async Task EncodeAndMuxAsync(TimeSpan recordedDuration, long recordedFrameCount)
    {
        // 디버그 로그 파일 - 명시적 경로 사용
        var logDir = Path.GetDirectoryName(_finalOutputPath);
        if (string.IsNullOrEmpty(logDir))
        {
            logDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }
        var logPath = Path.Combine(logDir, "debug_mux.log");

        void Log(string msg)
        {
            Debug.WriteLine($"[ScreenRecording] {msg}");
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch (Exception ex) { Debug.WriteLine($"Log write error: {ex.Message}"); }
        }

        Log($"=== 백그라운드 인코딩 시작 === (finalOutputPath: {_finalOutputPath})");

        try
        {
            Log("비디오 인코더 중지 시작...");
            await _videoEncoder.StopEncodingAsync();
            Log("비디오 인코더 중지 완료");

            // VideoEncoderService가 실제로 출력한 경로 사용
            var actualVideoPath = _videoEncoder.OutputPath;
            Log($"actualVideoPath: {actualVideoPath}, exists: {File.Exists(actualVideoPath)}");
            Log($"_videoPath: {_videoPath}, exists: {File.Exists(_videoPath)}");
            Log($"_audioPath: {_audioPath}, exists: {File.Exists(_audioPath)}");

            var videoFileToUse = File.Exists(actualVideoPath) ? actualVideoPath :
                                 File.Exists(_videoPath) ? _videoPath : null;

            Log($"videoFileToUse: {videoFileToUse ?? "null"}");

            if (videoFileToUse != null && File.Exists(_audioPath))
            {
                // 합성 전에 임시 출력 파일 경로 생성
                var tempMuxOutput = Path.Combine(Path.GetDirectoryName(_finalOutputPath) ?? "",
                    $"mux_temp_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                Log($"tempMuxOutput: {tempMuxOutput}");
                Log("오디오/비디오 합성 시작...");
                var muxSuccess = await _videoEncoder.MuxAudioVideoAsync(videoFileToUse, _audioPath, tempMuxOutput);
                Log($"합성 결과: {muxSuccess}, tempMuxOutput exists: {File.Exists(tempMuxOutput)}");

                if (muxSuccess && File.Exists(tempMuxOutput))
                {
                    Log("합성 성공! 임시 파일 정리 중...");
                    try { File.Delete(videoFileToUse); } catch { }
                    try { File.Delete(_audioPath); } catch { }

                    // 임시 합성 파일을 최종 경로로 이동
                    File.Move(tempMuxOutput, _finalOutputPath, true);
                    Log($"완료: {_finalOutputPath}");

                    RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
                    {
                        Success = true,
                        OutputPath = _finalOutputPath,
                        Duration = recordedDuration,
                        FrameCount = recordedFrameCount
                    });
                }
                else
                {
                    Log("합성 실패! 비디오만 저장...");
                    try { File.Delete(tempMuxOutput); } catch { }
                    if (videoFileToUse != _finalOutputPath)
                    {
                        File.Move(videoFileToUse, _finalOutputPath, true);
                    }

                    RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
                    {
                        Success = true,
                        OutputPath = _finalOutputPath,
                        Duration = recordedDuration,
                        FrameCount = recordedFrameCount,
                        Warning = "오디오 합성 실패 - 비디오만 저장됨"
                    });
                }
            }
            else if (videoFileToUse != null)
            {
                Log("오디오 파일 없음, 비디오만 저장...");
                if (videoFileToUse != _finalOutputPath)
                {
                    File.Move(videoFileToUse, _finalOutputPath, true);
                }

                RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
                {
                    Success = true,
                    OutputPath = _finalOutputPath,
                    Duration = recordedDuration,
                    FrameCount = recordedFrameCount
                });
            }
            else
            {
                Log("비디오 파일 없음!");
                RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
                {
                    Success = false,
                    ErrorMessage = "녹화 파일을 찾을 수 없습니다."
                });
            }
        }
        catch (Exception ex)
        {
            Log($"예외 발생: {ex.Message}\n{ex.StackTrace}");
            OnError($"인코딩 실패: {ex.Message}");
            RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }

        Log("=== 백그라운드 인코딩 완료 ===");
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

    #region 오디오 녹음

    private void StartAudioRecording(ScreenRecordingOptions options)
    {
        // 오디오 파일 라이터 생성
        _audioWriter = new WaveFileWriter(_audioPath, _outputFormat);

        // 마이크 캡처
        if (options.IncludeMicrophone)
        {
            InitializeMicCapture(options.MicrophoneDeviceId);
        }

        // 시스템 오디오 캡처
        if (options.IncludeSystemAudio)
        {
            InitializeLoopbackCapture(options.OutputDeviceId);
        }

        // 믹싱 스레드 시작 (동시 녹음 시)
        if (options.IncludeMicrophone && options.IncludeSystemAudio)
        {
            _audioMixingThread = new Thread(AudioMixingLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _audioMixingThread.Start();
        }

        // 캡처 시작
        _micCapture?.StartRecording();
        _loopbackCapture?.StartRecording();
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
            OnError("마이크 장치를 찾을 수 없습니다.");
            return;
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
            OnError("출력 장치를 찾을 수 없습니다.");
            return;
        }

        _loopbackCapture = new WasapiLoopbackCapture(device);

        // 무음 재생으로 Loopback 활성화
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
        if (e.BytesRecorded == 0 || !_isRecording) return;

        // 레벨 미터 업데이트
        _micLevelMeter.ProcessSamples(e.Buffer, e.BytesRecorded);
        ThrottledLevelUpdate();

        if (_isPaused) return;

        // 동시 녹음 모드
        if (_options.IncludeSystemAudio)
        {
            _micBuffer.WriteFromBytes(e.Buffer, e.BytesRecorded);
            _audioDataEvent.Set();
        }
        else
        {
            // 마이크 단독 녹음
            WriteAudioToFile(e.Buffer, e.BytesRecorded, _options.MicrophoneVolume);
        }
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || !_isRecording) return;

        // 레벨 미터 업데이트
        _systemLevelMeter.ProcessSamples(e.Buffer, e.BytesRecorded);
        ThrottledLevelUpdate();

        if (_isPaused) return;

        // 동시 녹음 모드
        if (_options.IncludeMicrophone)
        {
            _systemBuffer.WriteFromBytes(e.Buffer, e.BytesRecorded);
            _audioDataEvent.Set();
        }
        else
        {
            // 시스템 오디오 단독 녹음
            WriteAudioToFile(e.Buffer, e.BytesRecorded, _options.SystemVolume);
        }
    }

    private void AudioMixingLoop()
    {
        const int CHUNK_SIZE = 1920; // 20ms at 48kHz stereo
        var micData = new float[CHUNK_SIZE];
        var sysData = new float[CHUNK_SIZE];
        var mixBuffer = new float[CHUNK_SIZE];
        var outputBuffer = new byte[CHUNK_SIZE * 2];

        while (_isRecording)
        {
            if (_isPaused)
            {
                _audioDataEvent.WaitOne(100);
                continue;
            }

            int minAvailable = Math.Min(_micBuffer.Count, _systemBuffer.Count);

            if (minAvailable >= CHUNK_SIZE)
            {
                int micRead = _micBuffer.Read(micData, 0, CHUNK_SIZE);
                int sysRead = _systemBuffer.Read(sysData, 0, CHUNK_SIZE);
                int samplesToMix = Math.Min(micRead, sysRead);

                float micVol = _options.MicrophoneVolume;
                float sysVol = _options.SystemVolume;

                for (int i = 0; i < samplesToMix; i++)
                {
                    float mixed = micData[i] * micVol + sysData[i] * sysVol;
                    mixBuffer[i] = Math.Clamp(mixed, -1.0f, 1.0f);
                }

                // 16-bit PCM 변환 및 파일 쓰기
                var shortSpan = MemoryMarshal.Cast<byte, short>(outputBuffer.AsSpan(0, samplesToMix * 2));
                for (int i = 0; i < samplesToMix; i++)
                {
                    shortSpan[i] = (short)(mixBuffer[i] * 32767);
                }

                lock (_writeLock)
                {
                    _audioWriter?.Write(outputBuffer, 0, samplesToMix * 2);
                }
            }
            else
            {
                _audioDataEvent.WaitOne(20);
            }
        }
    }

    private void WriteAudioToFile(byte[] buffer, int bytesRecorded, float volume)
    {
        if (_audioWriter == null) return;

        int sampleCount = bytesRecorded / 4;
        var outputBuffer = new byte[sampleCount * 2];

        var floatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
        var shortSpan = MemoryMarshal.Cast<byte, short>(outputBuffer);

        for (int i = 0; i < sampleCount; i++)
        {
            float sample = Math.Clamp(floatSpan[i] * volume, -1.0f, 1.0f);
            shortSpan[i] = (short)(sample * 32767);
        }

        lock (_writeLock)
        {
            _audioWriter?.Write(outputBuffer, 0, outputBuffer.Length);
        }
    }

    private void StopAudioCapture()
    {
        try { _micCapture?.StopRecording(); } catch { }
        try { _loopbackCapture?.StopRecording(); } catch { }
        try { _silencePlayer?.Stop(); } catch { }

        _audioWriter?.Dispose();
        _audioWriter = null;
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

    private void ThrottledLevelUpdate()
    {
        var now = DateTime.Now;
        if ((now - _lastLevelUpdate).TotalMilliseconds >= LEVEL_UPDATE_INTERVAL_MS)
        {
            _lastLevelUpdate = now;
            LevelUpdated?.Invoke(this, new LevelEventArgs
            {
                MicLevel = _micLevelMeter.PeakLevel,
                MicLevelDb = _micLevelMeter.LevelDb,
                SystemLevel = _systemLevelMeter.PeakLevel,
                SystemLevelDb = _systemLevelMeter.LevelDb
            });
        }
    }

    #endregion

    #region 화면 캡처 이벤트

    private long _receivedFrameCount;

    private void OnFrameAvailable(object? sender, FrameEventArgs e)
    {
        _receivedFrameCount++;
        if (_receivedFrameCount % 30 == 0)
        {
            Debug.WriteLine($"[ScreenRecording] 프레임 수신: {_receivedFrameCount}, isRecording: {_isRecording}, isPaused: {_isPaused}");
        }

        if (!_isRecording || _isPaused) return;

        var frameData = _screenCapture.GetCurrentFrame();
        if (frameData != null)
        {
            _videoEncoder.WriteFrame(frameData);
        }
        else
        {
            Debug.WriteLine($"[ScreenRecording] 프레임 데이터가 null입니다!");
        }
    }

    private void OnCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        OnError($"화면 캡처 오류: {e.Message}");
    }

    private void OnEncodingCompleted(object? sender, EncodingCompletedEventArgs e)
    {
        if (!e.Success)
        {
            OnError($"인코딩 실패: {e.ErrorMessage}");
        }
    }

    #endregion

    #region 정리

    private void Cleanup()
    {
        _isRecording = false;

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

        Cleanup();
        _screenCapture.Dispose();
        _videoEncoder.Dispose();
        _audioDataEvent.Dispose();
    }

    #endregion
}

/// <summary>
/// 화면 녹화 완료 이벤트 인자
/// </summary>
public class ScreenRecordingCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public long FrameCount { get; init; }
    public string? ErrorMessage { get; init; }
    public string? Warning { get; init; }
}
