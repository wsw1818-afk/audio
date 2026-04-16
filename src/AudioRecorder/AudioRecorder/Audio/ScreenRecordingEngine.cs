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
/// Chrome DRM 캡처 → Enhanced DXGI → DXGI → GDI 순으로 자동 fallback
/// </summary>
public class ScreenRecordingEngine : IDisposable
{
    private readonly DeviceManager _deviceManager;
    private ScreenCaptureService? _gdiCapture;
    private DxgiScreenCaptureService? _dxgiCapture;
    private EnhancedDxgiCaptureService? _enhancedDxgiCapture;
    private ChromeDrmCaptureService? _chromeDrmCapture;
    private VideoEncoderService? _videoEncoder;
    private MouseClickHighlightService? _mouseClickHighlight;
    private RecordingBorderService? _recordingBorder;
    private WatermarkService? _watermark;
    private WebcamOverlayService? _webcamOverlay;
    private int _servicesInitializedFlag; // 0 = not yet, 1 = initialized (Interlocked)
    private readonly object _initLock = new();

    // 현재 사용 중인 캡처 방식
    private enum CaptureMode { None, GDI, DXGI, EnhancedDXGI, ChromeDRM }
    private CaptureMode _captureMode = CaptureMode.None;

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

    // 남부 포맷 (32-bit float, 48kHz, Stereo)
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
    private long _lastLevelUpdateTick;
    private const int LEVEL_UPDATE_INTERVAL_MS = 50;

    // 단독 오디오 녹음용 재사용 버퍼
    private byte[]? _soloAudioOutputBuffer;

    // 프레임 버퍼 풀링
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
    public long FrameCount => _captureMode switch
    {
        CaptureMode.ChromeDRM => _chromeDrmCapture?.FrameCount ?? 0,
        CaptureMode.EnhancedDXGI => _enhancedDxgiCapture?.FrameCount ?? 0,
        CaptureMode.DXGI => _dxgiCapture?.FrameCount ?? 0,
        _ => _gdiCapture?.FrameCount ?? 0
    };

    /// <summary>
    /// 현재 캡처 방식
    /// </summary>
    public string CaptureMethod => _captureMode switch
    {
        CaptureMode.ChromeDRM => "Chrome DRM",
        CaptureMode.EnhancedDXGI => "Enhanced DXGI",
        CaptureMode.DXGI => "DXGI",
        _ => "GDI"
    };

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
    public bool IsFFmpegAvailable => _videoEncoder?.IsFFmpegAvailable ?? VideoEncoderService.IsFFmpegInstalled();

    /// <summary>
    /// Chrome DRM 캡처 서비스 (외부에서 접근용, 지연 초기화 전에는 null)
    /// </summary>
    public ChromeDrmCaptureService? ChromeDrmCapture => _chromeDrmCapture;

    // 이벤트
    public event EventHandler<LevelEventArgs>? LevelUpdated;
    public event EventHandler<RecordingStateChangedEventArgs>? StateChanged;
    public event EventHandler<RecordingErrorEventArgs>? ErrorOccurred;
    public event EventHandler<ScreenRecordingCompletedEventArgs>? RecordingCompleted;

    public ScreenRecordingEngine(DeviceManager deviceManager)
    {
        _deviceManager = deviceManager;
        // 캡처 서비스는 실제 녹화 시작 시 지연 초기화 (앱 시작 속도 개선)
    }

    /// <summary>
    /// 캡처/인코딩 서비스 지연 초기화 (녹화 시작 시 1회만 실행)
    /// </summary>
    private void EnsureServicesInitialized()
    {
        // 빠른 경로: 이미 초기화된 경우 즉시 반환 (Volatile 읽기)
        if (System.Threading.Volatile.Read(ref _servicesInitializedFlag) == 1) return;

        lock (_initLock)
        {
            if (_servicesInitializedFlag == 1) return;

            _gdiCapture = new ScreenCaptureService();
        _dxgiCapture = new DxgiScreenCaptureService();
        _enhancedDxgiCapture = new EnhancedDxgiCaptureService();
        _chromeDrmCapture = new ChromeDrmCaptureService();
        _videoEncoder = new VideoEncoderService();
        _mouseClickHighlight = new MouseClickHighlightService();
        _recordingBorder = new RecordingBorderService();
        _watermark = new WatermarkService();
        _webcamOverlay = new WebcamOverlayService();

        // 캡처 서비스 이벤트 연결
        _gdiCapture.FrameAvailable += OnFrameAvailable;
        _gdiCapture.ErrorOccurred += OnCaptureError;
        _dxgiCapture.FrameAvailable += OnFrameAvailable;
        _dxgiCapture.ErrorOccurred += OnDxgiCaptureError;
        _enhancedDxgiCapture.FrameAvailable += OnEnhancedFrameAvailable;
        _enhancedDxgiCapture.ErrorOccurred += OnEnhancedCaptureError;
        _enhancedDxgiCapture.DrmDetected += OnDrmDetected;
        _chromeDrmCapture.FrameAvailable += OnChromeFrameAvailable;
        _chromeDrmCapture.ErrorOccurred += OnChromeCaptureError;
        _videoEncoder.EncodingCompleted += OnEncodingCompleted;

            // 모든 초기화가 끝난 뒤에만 플래그를 세운다 (부분 초기화 상태 노출 방지)
            System.Threading.Volatile.Write(ref _servicesInitializedFlag, 1);
        }
    }

    /// <summary>
    /// 초기화된 캡처 서비스가 null이 아님을 보장. 미초기화 시 명확한 예외를 던진다.
    /// </summary>
    private static T RequireService<T>(T? service, string name) where T : class
        => service ?? throw new InvalidOperationException(
            $"{name}가 초기화되지 않았습니다. Start()가 선행되어야 합니다.");

    /// <summary>
    /// 녹화 시작
    /// </summary>
    public void Start(ScreenRecordingOptions options)
    {
        if (State != RecordingState.Stopped || _isStopping)
            throw new InvalidOperationException("이미 녹화 중이거나 정지 작업이 진행 중입니다.");

        // 캡처/인코딩 서비스 지연 초기화
        EnsureServicesInitialized();

        var encoder = RequireService(_videoEncoder, nameof(VideoEncoderService));
        if (!encoder.IsFFmpegAvailable)
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

            // 화면 캡처 시작 - Chrome DRM → Enhanced DXGI → DXGI → GDI 순으로 시도
            int frameWidth, frameHeight;
            if (options.UseChromeDrmCapture && TryStartChromeDrmCapture(options, out frameWidth, out frameHeight))
            {
                _captureMode = CaptureMode.ChromeDRM;
                Debug.WriteLine("[ScreenRecording] Chrome DRM 캡처 모드로 시작");
            }
            else if (options.UseEnhancedDxgi && TryStartEnhancedDxgiCapture(options, out frameWidth, out frameHeight))
            {
                _captureMode = CaptureMode.EnhancedDXGI;
                Debug.WriteLine("[ScreenRecording] Enhanced DXGI 캡처 모드로 시작");
            }
            else if (TryStartDxgiCapture(options, out frameWidth, out frameHeight))
            {
                _captureMode = CaptureMode.DXGI;
                Debug.WriteLine("[ScreenRecording] DXGI 캡처 모드로 시작");
            }
            else
            {
                _captureMode = CaptureMode.GDI;
                _gdiCapture.Start(options.Region, options.FrameRate, options.ShowMouseCursor);
                frameWidth = _gdiCapture.FrameWidth;
                frameHeight = _gdiCapture.FrameHeight;
                Debug.WriteLine("[ScreenRecording] GDI 캡처 모드로 fallback");
            }

            // 비디오 인코더 시작
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
            if (_recordingBorder.IsEnabled && _captureMode != CaptureMode.ChromeDRM)
            {
                var borderBounds = options.Region.Type == CaptureRegionType.FullScreen
                    ? GetScreenBounds(options.Region.MonitorIndex)
                    : options.Region.Bounds;

                Debug.WriteLine($"[ScreenRecording] 테두리 영역 - X:{borderBounds.X}, Y:{borderBounds.Y}, W:{borderBounds.Width}, H:{borderBounds.Height}");

                if (borderBounds.Width > 0 && borderBounds.Height > 0)
                {
                    _recordingBorder.Show(borderBounds);
                    Debug.WriteLine("[ScreenRecording] 테두리 Show() 호출됨");
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
            _captureMode = CaptureMode.None;
            Cleanup();
            OnError($"녹화 시작 실패: {ex.Message}");
            throw;
        }
    }

    // 정지 작업 진행 중 플래그
    private volatile bool _isStopping;

    // 인코딩 진행 중 플래그
    private volatile bool _isEncoding;
    private CancellationTokenSource? _encodingCts;
    private Task? _encodingTask;

    /// <summary>
    /// 인코딩 진행 중 여부
    /// </summary>
    public bool IsEncoding => _isEncoding || (_encodingTask?.IsCompleted == false);

    /// <summary>
    /// 녹화 중지
    /// </summary>
    public async Task StopAsync()
    {
        if (State == RecordingState.Stopped || _isStopping)
            return;

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
            await StopCaptureAsync();

            Debug.WriteLine("[ScreenRecording] 오디오 캡처 중지...");
            StopAudioCapture();

            Debug.WriteLine("[ScreenRecording] 오디오 믹싱 스레드 종료 대기...");
            _audioDataEvent.Set();
            _audioMixingThread?.Join(2000);

            _stopwatch.Stop();
            var recordedDuration = _stopwatch.Elapsed;
            var recordedFrameCount = FrameCount;

            // 캡처 정지 완료
            Debug.WriteLine("[ScreenRecording] 캡처 정지 완료, 상태를 Stopped로 변경");
            Cleanup();
            State = RecordingState.Stopped;
            _isStopping = false;
            OnStateChanged();

            // 인코딩은 백그라운드에서 진행
            _isEncoding = true;
            _encodingCts = new CancellationTokenSource();
            _encodingTask = Task.Run(async () =>
            {
                Debug.WriteLine("[ScreenRecording] 백그라운드 태스크 실행 시작!");
                try
                {
                    await EncodeAndMuxAsync(recordedDuration, recordedFrameCount, _encodingCts.Token);
                }
                catch (OperationCanceledException)
                {
                    Debug.WriteLine("[ScreenRecording] 인코딩이 취소되었습니다.");
                }
                catch (Exception ex)
                {
                    Debug.WriteLine($"[ScreenRecording] 백그라운드 인코딩 예외: {ex.Message}");
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
                    _encodingCts?.Dispose();
                    _encodingCts = null;
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
    }

    private async Task StopCaptureAsync()
    {
        switch (_captureMode)
        {
            case CaptureMode.ChromeDRM:
                await _chromeDrmCapture.StopCaptureAsync();
                break;
            case CaptureMode.EnhancedDXGI:
                _enhancedDxgiCapture.Stop();
                break;
            case CaptureMode.DXGI:
                _dxgiCapture.Stop();
                break;
            case CaptureMode.GDI:
                _gdiCapture.Stop();
                break;
        }
    }

    private async Task EncodeAndMuxAsync(TimeSpan recordedDuration, long recordedFrameCount, CancellationToken ct = default)
    {
        var logDir = Path.GetDirectoryName(_finalOutputPath);
        if (string.IsNullOrEmpty(logDir))
        {
            logDir = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
        }
        var logPath = Path.Combine(logDir, "debug_mux.log");

        void Log(string msg)
        {
            Debug.WriteLine($"[ScreenRecording] {msg}");
            try { File.AppendAllText(logPath, $"{DateTime.Now:HH:mm:ss.fff} {msg}\n"); } catch { }
        }

        Log($"=== 백그라운드 인코딩 시작 ===");

        try
        {
            Log("비디오 인코더 중지 시작...");
            await _videoEncoder.StopEncodingAsync();
            Log("비디오 인코더 중지 완료");

            var actualVideoPath = _videoEncoder.OutputPath;
            Log($"actualVideoPath: {actualVideoPath}, exists: {File.Exists(actualVideoPath)}");

            var videoFileToUse = File.Exists(actualVideoPath) ? actualVideoPath :
                                 File.Exists(_videoPath) ? _videoPath : null;

            Log($"videoFileToUse: {videoFileToUse ?? "null"}");

            if (videoFileToUse != null && File.Exists(_audioPath))
            {
                var tempMuxOutput = Path.Combine(Path.GetDirectoryName(_finalOutputPath) ?? "",
                    $"mux_temp_{DateTime.Now:yyyyMMdd_HHmmss}.mp4");

                Log($"오디오/비디오 합성 시작...");
                var muxSuccess = await _videoEncoder.MuxAudioVideoAsync(videoFileToUse, _audioPath, tempMuxOutput, _options.AudioBitrate);
                Log($"합성 결과: {muxSuccess}");

                if (muxSuccess && File.Exists(tempMuxOutput))
                {
                    Log("합성 성공! 파일 정리 중...");
                    try { File.Delete(videoFileToUse); } catch { }
                    try { File.Delete(_audioPath); } catch { }

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
                    try { File.Delete(_audioPath); } catch { }
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
            Log($"예외 발생: {ex.Message}");
            OnError($"인코딩 실패: {ex.Message}");
            RecordingCompleted?.Invoke(this, new ScreenRecordingCompletedEventArgs
            {
                Success = false,
                ErrorMessage = ex.Message
            });
        }
        finally
        {
            // 임시 파일 정리 (성공/실패 모두)
            try { if (File.Exists(_audioPath)) File.Delete(_audioPath); } catch { }
            try { if (File.Exists(_videoPath)) File.Delete(_videoPath); } catch { }
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
        _audioWriter = new WaveFileWriter(_audioPath, _outputFormat);

        if (options.IncludeMicrophone)
        {
            InitializeMicCapture(options.MicrophoneDeviceId);
        }

        if (options.IncludeSystemAudio)
        {
            InitializeLoopbackCapture(options.OutputDeviceId);
        }

        if (options.IncludeMicrophone && options.IncludeSystemAudio)
        {
            _audioMixingThread = new Thread(AudioMixingLoop)
            {
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _audioMixingThread.Start();
        }

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

        _micLevelMeter.ProcessSamples(e.Buffer, e.BytesRecorded);
        ThrottledLevelUpdate();

        if (_isPaused) return;

        if (_options.IncludeSystemAudio)
        {
            _micBuffer.WriteFromBytes(e.Buffer, e.BytesRecorded);
            _audioDataEvent.Set();
        }
        else
        {
            WriteAudioToFile(e.Buffer, e.BytesRecorded, _options.MicrophoneVolume);
        }
    }

    private void OnLoopbackDataAvailable(object? sender, WaveInEventArgs e)
    {
        if (e.BytesRecorded == 0 || !_isRecording) return;

        _systemLevelMeter.ProcessSamples(e.Buffer, e.BytesRecorded);
        ThrottledLevelUpdate();

        if (_isPaused) return;

        if (_options.IncludeMicrophone)
        {
            _systemBuffer.WriteFromBytes(e.Buffer, e.BytesRecorded);
            _audioDataEvent.Set();
        }
        else
        {
            WriteAudioToFile(e.Buffer, e.BytesRecorded, _options.SystemVolume);
        }
    }

    private void AudioMixingLoop()
    {
        const int CHUNK_SIZE = 1920;
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
        if (_captureMode == CaptureMode.DXGI)
        {
            // DXGI: CopyCurrentFrameTo로 _frameBuffer에 직접 복사 (이중 복사 제거)
            ProcessFrameDirectCopy(e.Width, e.Height, buffer => _dxgiCapture.CopyCurrentFrameTo(buffer));
        }
        else
        {
            // GDI: GetCurrentFrameCopy 사용 (GDI는 CopyCurrentFrameTo 미지원)
            ProcessFrame(e.Width, e.Height, () => _gdiCapture.GetCurrentFrameCopy());
        }
    }

    private void OnEnhancedFrameAvailable(object? sender, FrameEventArgs e)
    {
        ProcessFrame(e.Width, e.Height, () => _enhancedDxgiCapture.GetCurrentFrameCopy());
    }

    private void OnChromeFrameAvailable(object? sender, ChromeFrameEventArgs e)
    {
        // Chrome은 이벤트 인자로 복사본이 전달됨
        ProcessFrame(e.Width, e.Height, () => e.FrameData);
    }

    /// <summary>
    /// 프레임 처리 - 직접 복사 경로 (DXGI 전용)
    /// CopyCurrentFrameTo를 사용하여 사전 할당된 _frameBuffer에 한 번만 복사.
    /// GetCurrentFrameCopy()의 new byte[] 할당 + 추가 BlockCopy를 제거하여 GC 압력 감소.
    /// </summary>
    private void ProcessFrameDirectCopy(int frameWidth, int frameHeight, Func<byte[], bool> copyFrameTo)
    {
        var frameNumber = Interlocked.Increment(ref _receivedFrameCount);
        if (frameNumber % 30 == 0)
        {
            Debug.WriteLine($"[ScreenRecording] 프레임 수신: {frameNumber}, isRecording: {_isRecording}, isPaused: {_isPaused}, 모드: {CaptureMethod}");
        }

        if (!_isRecording || _isPaused) return;

        _mouseClickHighlight.Update();

        var requiredSize = frameWidth * frameHeight * 4;
        if (_frameBuffer == null || _frameBufferSize < requiredSize)
        {
            _frameBuffer = new byte[requiredSize];
            _frameBufferSize = requiredSize;
        }

        // 사전 할당된 버퍼에 직접 복사 (단일 복사, new byte[] 할당 없음)
        if (!copyFrameTo(_frameBuffer)) return;

        ApplyEffectsAndEncode(frameWidth, frameHeight);
    }

    /// <summary>
    /// 프레임 처리 - 복사본 경로 (GDI, Enhanced DXGI, Chrome 등)
    /// getFrameData()가 반환한 복사본을 _frameBuffer에 BlockCopy.
    /// </summary>
    private void ProcessFrame(int frameWidth, int frameHeight, Func<byte[]?> getFrameData)
    {
        var frameNumber = Interlocked.Increment(ref _receivedFrameCount);
        if (frameNumber % 30 == 0)
        {
            Debug.WriteLine($"[ScreenRecording] 프레임 수신: {frameNumber}, isRecording: {_isRecording}, isPaused: {_isPaused}, 모드: {CaptureMethod}");
        }

        if (!_isRecording || _isPaused) return;

        _mouseClickHighlight.Update();

        var requiredSize = frameWidth * frameHeight * 4;
        if (_frameBuffer == null || _frameBufferSize < requiredSize)
        {
            _frameBuffer = new byte[requiredSize];
            _frameBufferSize = requiredSize;
        }

        byte[]? frameData = getFrameData();
        if (frameData == null) return;

        int copySize = Math.Min(frameData.Length, requiredSize);
        Buffer.BlockCopy(frameData, 0, _frameBuffer, 0, copySize);

        ApplyEffectsAndEncode(frameWidth, frameHeight);
    }

    /// <summary>
    /// 효과 적용 + 인코더 전달 (공통 로직)
    /// </summary>
    private void ApplyEffectsAndEncode(int frameWidth, int frameHeight)
    {
        if (_mouseClickHighlight.IsEnabled)
        {
            _mouseClickHighlight.DrawEffects(_frameBuffer!, frameWidth, frameHeight,
                _options.Region.Bounds.X, _options.Region.Bounds.Y);
        }

        if (_webcamOverlay.IsEnabled)
        {
            _webcamOverlay.DrawOverlay(_frameBuffer!, frameWidth, frameHeight);
        }

        if (_watermark.IsEnabled)
        {
            _watermark.DrawWatermark(_frameBuffer!, frameWidth, frameHeight);
        }

        _videoEncoder.WriteFrame(_frameBuffer!);
    }

    private void OnCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        OnError($"화면 캡처 오류: {e.Message}");
    }

    private void OnDxgiCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        Debug.WriteLine($"[ScreenRecording] DXGI 캡처 오류: {e.Message}");
        if (_isRecording)
        {
            OnError($"DXGI 캡처 오류: {e.Message}");
        }
    }

    private void OnEnhancedCaptureError(object? sender, CaptureErrorEventArgs e)
    {
        Debug.WriteLine($"[ScreenRecording] Enhanced DXGI 오류: {e.Message}");
        if (_isRecording)
        {
            OnError($"Enhanced DXGI 오류: {e.Message}");
        }
    }

    private void OnChromeCaptureError(object? sender, ChromeCaptureErrorEventArgs e)
    {
        Debug.WriteLine($"[ScreenRecording] Chrome DRM 오류: {e.Message}");
        if (_isRecording)
        {
            OnError($"Chrome DRM 오류: {e.Message}");
        }
    }

    private void OnDrmDetected(object? sender, DrmDetectedEventArgs e)
    {
        Debug.WriteLine($"[ScreenRecording] DRM 감지됨: {e.Message}");
        OnError($"DRM 감지: {e.Message}");
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

    private static System.Drawing.Rectangle GetScreenBounds(int monitorIndex)
    {
        var screens = System.Windows.Forms.Screen.AllScreens;
        if (monitorIndex >= 0 && monitorIndex < screens.Length)
        {
            return screens[monitorIndex].Bounds;
        }
        return System.Windows.Forms.Screen.PrimaryScreen?.Bounds
            ?? new System.Drawing.Rectangle(0, 0, 1920, 1080);
    }

    private bool TryStartChromeDrmCapture(ScreenRecordingOptions options, out int frameWidth, out int frameHeight)
    {
        frameWidth = 0;
        frameHeight = 0;

        try
        {
            // Task.Run으로 래핑하여 SynchronizationContext 데드락 방지
            var result = Task.Run(async () => await _chromeDrmCapture.StartCaptureAsync(
                options.FrameRate,
                options.ChromeDebugPort,
                options.ChromeTargetUrl));

            if (result.Wait(TimeSpan.FromSeconds(30)))
            {
                if (result.Result)
                {
                    frameWidth = _chromeDrmCapture.FrameWidth;
                    frameHeight = _chromeDrmCapture.FrameHeight;
                    return true;
                }
            }
            else
            {
                Debug.WriteLine("[ScreenRecording] Chrome DRM 캡처 시작 시간 초과");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenRecording] Chrome DRM 캡처 시작 실패: {ex.Message}");
        }

        return false;
    }

    private bool TryStartEnhancedDxgiCapture(ScreenRecordingOptions options, out int frameWidth, out int frameHeight)
    {
        frameWidth = 0;
        frameHeight = 0;

        try
        {
            _enhancedDxgiCapture.DrmBypassMode = true;
            _enhancedDxgiCapture.Start(options.Region, options.FrameRate, options.ShowMouseCursor);
            frameWidth = _enhancedDxgiCapture.FrameWidth;
            frameHeight = _enhancedDxgiCapture.FrameHeight;

            Thread.Sleep(100);
            var testFrame = _enhancedDxgiCapture.GetCurrentFrame();
            if (testFrame != null && !IsBlackFrame(testFrame))
            {
                return true;
            }

            Debug.WriteLine("[ScreenRecording] Enhanced DXGI 검은 화면, 다음 모드로 전환");
            _enhancedDxgiCapture.Stop();
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenRecording] Enhanced DXGI 시작 실패: {ex.Message}");
            try { _enhancedDxgiCapture.Stop(); } catch { }
            return false;
        }
    }

    private bool TryStartDxgiCapture(ScreenRecordingOptions options, out int frameWidth, out int frameHeight)
    {
        frameWidth = 0;
        frameHeight = 0;

        try
        {
            _dxgiCapture.Start(options.Region, options.FrameRate, options.ShowMouseCursor);
            frameWidth = _dxgiCapture.FrameWidth;
            frameHeight = _dxgiCapture.FrameHeight;

            Thread.Sleep(100);
            var testFrame = _dxgiCapture.GetCurrentFrame();
            if (testFrame != null && !IsBlackFrame(testFrame))
            {
                return true;
            }

            Debug.WriteLine("[ScreenRecording] DXGI 검은 화면, GDI로 전환");
            _dxgiCapture.Stop();
            return false;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[ScreenRecording] DXGI 시작 실패: {ex.Message}");
            try { _dxgiCapture.Stop(); } catch { }
            return false;
        }
    }

    private static bool IsBlackFrame(byte[] frameData)
    {
        if (frameData == null || frameData.Length < 1000)
            return true;

        int nonBlackPixels = 0;
        int sampleStep = frameData.Length / 1000;
        if (sampleStep < 4) sampleStep = 4;

        for (int i = 0; i < frameData.Length - 4; i += sampleStep)
        {
            byte b = frameData[i];
            byte g = frameData[i + 1];
            byte r = frameData[i + 2];

            if (r > 10 || g > 10 || b > 10)
            {
                nonBlackPixels++;
                if (nonBlackPixels > 50)
                    return false;
            }
        }

        return true;
    }

    private void OnStateChanged()
    {
        StateChanged?.Invoke(this, new RecordingStateChangedEventArgs { State = State });
    }

    private void OnError(string message)
    {
        Debug.WriteLine($"[ScreenRecording] 오류: {message}");
        ErrorOccurred?.Invoke(this, new RecordingErrorEventArgs { Message = message });
    }

    private void Cleanup()
    {
        try { _micCapture?.StopRecording(); } catch (Exception ex) { Debug.WriteLine($"[ScreenRecording] 마이크 중지 실패: {ex.Message}"); }
        try { _loopbackCapture?.StopRecording(); } catch (Exception ex) { Debug.WriteLine($"[ScreenRecording] 루프백 중지 실패: {ex.Message}"); }
        try { _silencePlayer?.Stop(); } catch (Exception ex) { Debug.WriteLine($"[ScreenRecording] 무음 재생 중지 실패: {ex.Message}"); }

        _audioWriter?.Dispose();
        _audioWriter = null;

        _micCapture?.Dispose();
        _micCapture = null;

        _loopbackCapture?.Dispose();
        _loopbackCapture = null;

        _silencePlayer?.Dispose();
        _silencePlayer = null;

        _chromeDrmCapture?.Dispose();
    }

    /// <summary>
    /// 비동기 Dispose (권장)
    /// </summary>
    public async Task DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        // 인코딩 Task 취소 및 대기
        try
        {
            _encodingCts?.Cancel();
            if (_encodingTask != null)
            {
                await _encodingTask.WaitAsync(TimeSpan.FromSeconds(30));
            }
        }
        catch { }

        await StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        // 이벤트 핸들러 해제 (지연 초기화되었을 때만)
        if (System.Threading.Volatile.Read(ref _servicesInitializedFlag) == 1)
        {
            if (_gdiCapture != null) { _gdiCapture.FrameAvailable -= OnFrameAvailable; _gdiCapture.ErrorOccurred -= OnCaptureError; }
            if (_dxgiCapture != null) { _dxgiCapture.FrameAvailable -= OnFrameAvailable; _dxgiCapture.ErrorOccurred -= OnDxgiCaptureError; }
            if (_enhancedDxgiCapture != null) { _enhancedDxgiCapture.FrameAvailable -= OnEnhancedFrameAvailable; _enhancedDxgiCapture.ErrorOccurred -= OnEnhancedCaptureError; _enhancedDxgiCapture.DrmDetected -= OnDrmDetected; }
            if (_chromeDrmCapture != null) { _chromeDrmCapture.FrameAvailable -= OnChromeFrameAvailable; _chromeDrmCapture.ErrorOccurred -= OnChromeCaptureError; }
            if (_videoEncoder != null) _videoEncoder.EncodingCompleted -= OnEncodingCompleted;
        }

        Cleanup();

        _gdiCapture?.Dispose();
        _dxgiCapture?.Dispose();
        _enhancedDxgiCapture?.Dispose();
        _chromeDrmCapture?.Dispose();
        _videoEncoder?.Dispose();
        _audioWriter?.Dispose();
        _micCapture?.Dispose();
        _loopbackCapture?.Dispose();
        _silencePlayer?.Dispose();
    }

    /// <summary>
    /// 동기 Dispose (UI 스레드에서 호출 시 주의)
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        // UI 스레드에서 호출 시 데드락 방지: Task.Run으로 컨텍스트 전환
        Task.Run(async () => await DisposeAsync()).GetAwaiter().GetResult();
    }

    #endregion
}
