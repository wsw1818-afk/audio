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
/// DXGI 우선 사용, 실패 시 GDI로 자동 fallback
/// </summary>
public class ScreenRecordingEngine : IDisposable
{
    private readonly DeviceManager _deviceManager;
    private readonly ScreenCaptureService _gdiCapture;
    private readonly DxgiScreenCaptureService _dxgiCapture;
    private readonly VideoEncoderService _videoEncoder;
    private readonly MouseClickHighlightService _mouseClickHighlight;
    private readonly RecordingBorderService _recordingBorder;
    private readonly WatermarkService _watermark;
    private readonly WebcamOverlayService _webcamOverlay;

    // 현재 사용 중인 캡처 방식
    private bool _useDxgi = true;
    private bool _dxgiInitialized = false;

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

    // 레벨 업데이트 쓰로틀링 (Environment.TickCount64 사용 - DateTime.Now보다 빠름)
    private long _lastLevelUpdateTick;
    private const int LEVEL_UPDATE_INTERVAL_MS = 50;

    // 단독 오디오 녹음용 재사용 버퍼
    private byte[]? _soloAudioOutputBuffer;

    // 프레임 버퍼 풀링 (메모리 재사용)
    private byte[]? _frameBuffer;
    private int _frameBufferSize;

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
    public long FrameCount => _useDxgi ? _dxgiCapture.FrameCount : _gdiCapture.FrameCount;

    /// <summary>
    /// 현재 캡처 방식
    /// </summary>
    public string CaptureMethod => _useDxgi ? "DXGI" : "GDI";

    /// <summary>
    /// 현재 FPS
    /// </summary>
    public double CurrentFps => _stopwatch.Elapsed.TotalSeconds > 0
        ? FrameCount / _stopwatch.Elapsed.TotalSeconds
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
        _gdiCapture = new ScreenCaptureService();
        _dxgiCapture = new DxgiScreenCaptureService();
        _videoEncoder = new VideoEncoderService();
        _mouseClickHighlight = new MouseClickHighlightService();
        _recordingBorder = new RecordingBorderService();
        _watermark = new WatermarkService();
        _webcamOverlay = new WebcamOverlayService();

        // 두 캡처 서비스 모두 이벤트 연결
        _gdiCapture.FrameAvailable += OnFrameAvailable;
        _gdiCapture.ErrorOccurred += OnCaptureError;
        _dxgiCapture.FrameAvailable += OnFrameAvailable;
        _dxgiCapture.ErrorOccurred += OnDxgiCaptureError;
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

            // 마우스 클릭 강조 설정
            _mouseClickHighlight.IsEnabled = options.HighlightMouseClicks;

            // 녹화 영역 테두리 설정
            _recordingBorder.IsEnabled = options.ShowRecordingBorder;

            // 워터마크 설정
            if (!string.IsNullOrWhiteSpace(options.WatermarkText))
            {
                _watermark.Text = options.WatermarkText;
                _watermark.Position = options.WatermarkPosition;
                _watermark.IsEnabled = true;
            }
            else
            {
                _watermark.IsEnabled = false;
            }

            // 웹캠 오버레이 설정
            _webcamOverlay.IsEnabled = options.EnableWebcamOverlay;
            _webcamOverlay.Position = options.WebcamPosition;
            _webcamOverlay.Size = options.WebcamSize;

            // 화면 캡처 시작 - DXGI 우선, 실패 시 GDI fallback
            int frameWidth, frameHeight;
            if (TryStartDxgiCapture(options, out frameWidth, out frameHeight))
            {
                _useDxgi = true;
                _dxgiInitialized = true;
                Debug.WriteLine("[ScreenRecording] DXGI 캡처 모드로 시작");
            }
            else
            {
                _useDxgi = false;
                _dxgiInitialized = false;
                _gdiCapture.Start(options.Region, options.FrameRate, options.ShowMouseCursor);
                frameWidth = _gdiCapture.FrameWidth;
                frameHeight = _gdiCapture.FrameHeight;
                Debug.WriteLine("[ScreenRecording] GDI 캡처 모드로 fallback");
            }

            // 비디오 인코더 시작 (캡처 시작 후 크기 정보로)
            _videoEncoder.StartEncoding(
                _videoPath,
                frameWidth,
                frameHeight,
                options.FrameRate,
                options.VideoFormat,
                options.VideoBitrate,
                options.UseHardwareEncoding,
                options.VideoCrf,
                options.OutputResolution,
                options.EncoderPreset);

            // 인코더가 준비된 후 녹화 플래그 활성화
            _isRecording = true;

            // 녹화 영역 테두리 표시
            Debug.WriteLine($"[ScreenRecording] 테두리 설정 - IsEnabled: {_recordingBorder.IsEnabled}, RegionType: {options.Region.Type}");
            if (_recordingBorder.IsEnabled)
            {
                // 전체 화면일 경우 화면 크기로 Bounds 계산
                var borderBounds = options.Region.Type == CaptureRegionType.FullScreen
                    ? GetScreenBounds(options.Region.MonitorIndex)
                    : options.Region.Bounds;

                Debug.WriteLine($"[ScreenRecording] 테두리 영역 - X:{borderBounds.X}, Y:{borderBounds.Y}, W:{borderBounds.Width}, H:{borderBounds.Height}");

                if (borderBounds.Width > 0 && borderBounds.Height > 0)
                {
                    _recordingBorder.Show(borderBounds);
                    Debug.WriteLine("[ScreenRecording] 테두리 Show() 호출됨");
                }
                else
                {
                    Debug.WriteLine("[ScreenRecording] 테두리 영역이 0이라 표시 안 함");
                }
            }

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

        // 녹화 영역 테두리 숨기기
        _recordingBorder.Hide();

        try
        {
            Debug.WriteLine($"[ScreenRecording] 화면 캡처 중지... (모드: {CaptureMethod})");
            if (_useDxgi)
                _dxgiCapture.Stop();
            else
                _gdiCapture.Stop();

            Debug.WriteLine("[ScreenRecording] 오디오 캡처 중지...");
            StopAudioCapture();

            Debug.WriteLine("[ScreenRecording] 오디오 믹싱 스레드 종료 대기...");
            _audioDataEvent.Set();
            _audioMixingThread?.Join(2000);

            _stopwatch.Stop();
            var recordedDuration = _stopwatch.Elapsed;
            var recordedFrameCount = FrameCount;

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
                Log($"오디오/비디오 합성 시작... (audio bitrate: {_options.AudioBitrate}kbps)");
                var muxSuccess = await _videoEncoder.MuxAudioVideoAsync(videoFileToUse, _audioPath, tempMuxOutput, _options.AudioBitrate);
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
        int outputSize = sampleCount * 2;

        // 버퍼 재사용 (GC 압력 최소화)
        if (_soloAudioOutputBuffer == null || _soloAudioOutputBuffer.Length < outputSize)
        {
            _soloAudioOutputBuffer = new byte[outputSize];
        }

        var floatSpan = MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, bytesRecorded));
        var shortSpan = MemoryMarshal.Cast<byte, short>(_soloAudioOutputBuffer.AsSpan(0, outputSize));

        for (int i = 0; i < sampleCount; i++)
        {
            float sample = Math.Clamp(floatSpan[i] * volume, -1.0f, 1.0f);
            shortSpan[i] = (short)(sample * 32767);
        }

        lock (_writeLock)
        {
            _audioWriter?.Write(_soloAudioOutputBuffer, 0, outputSize);
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
        long now = Environment.TickCount64;
        if (now - _lastLevelUpdateTick >= LEVEL_UPDATE_INTERVAL_MS)
        {
            _lastLevelUpdateTick = now;
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
            Debug.WriteLine($"[ScreenRecording] 프레임 수신: {_receivedFrameCount}, isRecording: {_isRecording}, isPaused: {_isPaused}, 모드: {CaptureMethod}");
        }

        if (!_isRecording || _isPaused) return;

        // 마우스 클릭 상태 업데이트
        _mouseClickHighlight.Update();

        // 프레임 크기 정보
        var frameWidth = _useDxgi ? _dxgiCapture.FrameWidth : _gdiCapture.FrameWidth;
        var frameHeight = _useDxgi ? _dxgiCapture.FrameHeight : _gdiCapture.FrameHeight;

        // 효과 적용 필요 여부 확인
        bool needsEffects = _mouseClickHighlight.IsEnabled || _webcamOverlay.IsEnabled || _watermark.IsEnabled;

        if (needsEffects)
        {
            // 효과 적용 시: 버퍼에 복사 후 수정
            var requiredSize = frameWidth * frameHeight * 4; // BGRA
            if (_frameBuffer == null || _frameBufferSize < requiredSize)
            {
                _frameBuffer = new byte[requiredSize];
                _frameBufferSize = requiredSize;
                Debug.WriteLine($"[ScreenRecording] 프레임 버퍼 재할당: {requiredSize} bytes");
            }

            // 캡처 서비스에서 버퍼로 직접 복사
            bool copied = _useDxgi
                ? _dxgiCapture.CopyCurrentFrameTo(_frameBuffer)
                : _gdiCapture.CopyCurrentFrameTo(_frameBuffer);

            if (!copied)
            {
                Debug.WriteLine($"[ScreenRecording] 프레임 복사 실패!");
                return;
            }

            // 마우스 클릭 강조 효과 적용
            if (_mouseClickHighlight.IsEnabled)
            {
                _mouseClickHighlight.DrawEffects(
                    _frameBuffer,
                    frameWidth,
                    frameHeight,
                    _options.Region.Bounds.X,
                    _options.Region.Bounds.Y);
            }

            // 웹캠 오버레이 적용
            if (_webcamOverlay.IsEnabled)
            {
                _webcamOverlay.DrawOverlay(_frameBuffer, frameWidth, frameHeight);
            }

            // 워터마크 적용
            if (_watermark.IsEnabled)
            {
                _watermark.DrawWatermark(_frameBuffer, frameWidth, frameHeight);
            }

            _videoEncoder.WriteFrame(_frameBuffer);
        }
        else
        {
            // 효과 없음: 직접 전달 (zero-copy)
            var frameData = _useDxgi ? _dxgiCapture.GetCurrentFrame() : _gdiCapture.GetCurrentFrame();
            if (frameData != null)
            {
                _videoEncoder.WriteFrame(frameData);
            }
            else
            {
                Debug.WriteLine($"[ScreenRecording] 프레임 데이터가 null입니다!");
            }
        }
    }

    private void OnCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        OnError($"화면 캡처 오류: {e.Message}");
    }

    private void OnDxgiCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        Debug.WriteLine($"[ScreenRecording] DXGI 캡처 오류: {e.Message}");
        // DXGI 오류 발생 시 GDI로 자동 전환은 녹화 시작 시에만 수행
        // 녹화 중 오류는 일반 오류로 처리
        if (_isRecording)
        {
            OnError($"DXGI 캡처 오류: {e.Message}");
        }
    }

    private void OnEncodingCompleted(object? sender, EncodingCompletedEventArgs e)
    {
        if (!e.Success)
        {
            OnError($"인코딩 실패: {e.ErrorMessage}");
        }
    }

    #endregion

    #region 헬퍼 메서드

    /// <summary>
    /// 지정된 모니터 인덱스의 화면 영역을 반환
    /// </summary>
    private static System.Drawing.Rectangle GetScreenBounds(int monitorIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (monitorIndex >= 0 && monitorIndex < screens.Length)
        {
            return screens[monitorIndex].Bounds;
        }
        // 기본값: 주 모니터
        return System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
    }

    /// <summary>
    /// DXGI 캡처 시작 시도
    /// </summary>
    private bool TryStartDxgiCapture(ScreenRecordingOptions options, out int frameWidth, out int frameHeight)
    {
        frameWidth = 0;
        frameHeight = 0;

        try
        {
            _dxgiCapture.Start(options.Region, options.FrameRate, options.ShowMouseCursor);
            frameWidth = _dxgiCapture.FrameWidth;
            frameHeight = _dxgiCapture.FrameHeight;

            // 첫 프레임 캡처 테스트 (검은 화면 감지)
            Thread.Sleep(100); // DXGI 초기화 대기
            var testFrame = _dxgiCapture.GetCurrentFrame();
            if (testFrame != null && !IsBlackFrame(testFrame))
            {
                return true;
            }

            // 검은 화면이면 DXGI 실패로 처리
            Debug.WriteLine("[ScreenRecording] DXGI 캡처 결과가 검은 화면, GDI로 전환");
            _dxgiCapture.Stop();
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenRecording] DXGI 캡처 시작 실패: {ex.Message}");
            try { _dxgiCapture.Stop(); } catch { }
            return false;
        }
    }

    /// <summary>
    /// 프레임이 검은 화면(또는 거의 검은 화면)인지 확인
    /// </summary>
    private static bool IsBlackFrame(byte[] frameData)
    {
        if (frameData == null || frameData.Length < 1000)
            return true;

        // 샘플링으로 빠르게 검사 (전체 검사는 느림)
        int nonBlackPixels = 0;
        int sampleStep = frameData.Length / 1000; // 약 1000개 샘플
        if (sampleStep < 4) sampleStep = 4;

        for (int i = 0; i < frameData.Length - 4; i += sampleStep)
        {
            // BGRA 형식: B, G, R, A
            byte b = frameData[i];
            byte g = frameData[i + 1];
            byte r = frameData[i + 2];

            // 픽셀이 완전히 검은색(0,0,0)이 아니면 카운트
            if (r > 10 || g > 10 || b > 10)
            {
                nonBlackPixels++;
                if (nonBlackPixels > 50) // 5% 이상이 검은색이 아니면 유효한 프레임
                    return false;
            }
        }

        return true; // 대부분 검은색
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

        // 버퍼 정리 (메모리 해제)
        _soloAudioOutputBuffer = null;
        _frameBuffer = null;
        _frameBufferSize = 0;
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
        _gdiCapture.Dispose();
        _dxgiCapture.Dispose();
        _videoEncoder.Dispose();
        _mouseClickHighlight.Dispose();
        _recordingBorder.Dispose();
        _watermark.Dispose();
        _webcamOverlay.Dispose();
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
