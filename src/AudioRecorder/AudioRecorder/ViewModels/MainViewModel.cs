using System.Collections.ObjectModel;
using System.IO;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using AudioRecorder.Audio;
using AudioRecorder.Models;
using AudioRecorder.Services;

namespace AudioRecorder.ViewModels;

public partial class MainViewModel : ObservableObject, IDisposable
{
    private static readonly HashSet<string> AllowedMediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".wav", ".mp3", ".flac", ".aac", ".ogg", ".m4a",
        ".mp4", ".avi", ".mkv", ".mov", ".wmv", ".webm"
    };

    private static bool IsAllowedMediaFile(string filePath)
    {
        var ext = Path.GetExtension(filePath);
        return AllowedMediaExtensions.Contains(ext);
    }

    private readonly DeviceManager _deviceManager;
    private readonly RecordingEngine _recordingEngine;
    private readonly ScreenRecordingEngine _screenRecordingEngine;
    private readonly AudioPlayer _audioPlayer;
    private readonly AudioConversionService _conversionService;
    private readonly VideoConversionService _videoConversionService;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _playbackTimer;
    private readonly AppSettings _settings;
    private bool _disposed;
    // 세그먼트 백그라운드 변환 취소용 (앱 종료 시 진행 중 변환을 신속히 중단)
    private readonly CancellationTokenSource _segmentConvertCts = new();
    private readonly List<Task> _segmentConvertTasks = new();
    private readonly object _segmentConvertLock = new();
    private bool _isStoppingScreenRecording; // 화면 녹화 정지 진행 중 플래그

    // 녹음 상태
    [ObservableProperty]
    private RecordingState _recordingState = RecordingState.Stopped;

    [ObservableProperty]
    private string _elapsedTime = "00:00:00";

    [ObservableProperty]
    private string _statusText = "준비";

    [ObservableProperty]
    private string _fileInfo = "";

    // 장치 목록
    [ObservableProperty]
    private ObservableCollection<AudioDevice> _inputDevices = new();

    [ObservableProperty]
    private ObservableCollection<AudioDevice> _outputDevices = new();

    [ObservableProperty]
    private AudioDevice? _selectedInputDevice;

    [ObservableProperty]
    private AudioDevice? _selectedOutputDevice;

    // 녹음 옵션
    [ObservableProperty]
    private bool _recordMicrophone = true;

    [ObservableProperty]
    private bool _recordSystemAudio = true;

    [ObservableProperty]
    private float _micVolume = 0.8f;

    [ObservableProperty]
    private float _systemVolume = 1.0f;

    [ObservableProperty]
    private string _outputDirectory;

    // 레벨 미터
    [ObservableProperty]
    private float _micLevel;

    [ObservableProperty]
    private float _systemLevel;

    [ObservableProperty]
    private string _micLevelDb = "-∞ dB";

    [ObservableProperty]
    private string _systemLevelDb = "-∞ dB";

    // 최근 파일
    [ObservableProperty]
    private ObservableCollection<RecordingInfo> _recentFiles = new();

    [ObservableProperty]
    private RecordingInfo? _selectedRecentFile;

    // 옵션 패널
    [ObservableProperty]
    private bool _isOptionsExpanded = false;

    // 녹음 포맷
    [ObservableProperty]
    private RecordingFormat _selectedRecordingFormat = RecordingFormat.WAV;

    public IReadOnlyList<RecordingFormat> AvailableFormats { get; } = new[]
    {
        RecordingFormat.WAV,
        RecordingFormat.FLAC,
        RecordingFormat.MP3_320,
        RecordingFormat.MP3_128
    };

    // 화면 녹화 모드
    [ObservableProperty]
    private RecordingMode _currentRecordingMode = RecordingMode.AudioOnly;

    [ObservableProperty]
    private CaptureRegion? _selectedCaptureRegion;

    [ObservableProperty]
    private VideoFormat _selectedVideoFormat = VideoFormat.MP4_H264;

    [ObservableProperty]
    private string _selectedVideoQuality = "고화질";

    // 품질 옵션 (FPS + CRF 통합) - 3개
    // 무손실: 60fps, CRF 10 (거의 무손실, 파일 큼)
    // 최고 화질: 60fps, CRF 15 (부드럽고 선명)
    // 고화질: 30fps, CRF 20 (일반적인 고화질)
    public IReadOnlyList<string> VideoQualityOptions { get; } = new[] { "고화질", "최고 화질", "무손실" };

    // 품질을 FPS로 변환
    public int GetFpsFromQuality() => SelectedVideoQuality switch
    {
        "무손실" => 60,
        "최고 화질" => 60,
        "고화질" => 30,
        _ => 30
    };

    // 품질을 CRF 값으로 변환
    public int GetCrfFromQuality() => SelectedVideoQuality switch
    {
        "무손실" => 10,
        "최고 화질" => 15,
        "고화질" => 20,
        _ => 20
    };

    // 품질에 따른 프리셋
    public string GetPresetFromQuality() => SelectedVideoQuality switch
    {
        "무손실" => "medium",
        "최고 화질" => "medium",
        "고화질" => "fast",
        _ => "fast"
    };

    [ObservableProperty]
    private string _captureRegionText = "전체 화면";

    [ObservableProperty]
    private bool _showMouseCursor = true;

    // ========== 1단계: UI 옵션 ==========
    [ObservableProperty]
    private bool _highlightMouseClicks = false;

    [ObservableProperty]
    private int _countdownSeconds = 3;

    [ObservableProperty]
    private bool _showRecordingBorder = true;

    public IReadOnlyList<int> CountdownOptions { get; } = new[] { 0, 3, 5, 10 };

    // ========== 2단계: FFmpeg 옵션 ==========
    [ObservableProperty]
    private string _selectedResolution = "원본";

    [ObservableProperty]
    private string _selectedAudioBitrate = "192 kbps";

    public IReadOnlyList<string> ResolutionOptions { get; } = new[] { "원본", "1080p", "720p", "480p" };
    public IReadOnlyList<string> AudioBitrateOptions { get; } = new[] { "128 kbps", "192 kbps", "320 kbps" };

    // ========== 3단계: 고급 기능 ==========
    [ObservableProperty]
    private bool _enableWebcamOverlay = false;

    [ObservableProperty]
    private string _selectedWebcamPosition = "우하단";

    [ObservableProperty]
    private string _selectedWebcamSize = "소";

    [ObservableProperty]
    private string _watermarkText = "";

    [ObservableProperty]
    private string _selectedWatermarkPosition = "우하단";

    [ObservableProperty]
    private bool _enableScheduledRecording = false;

    [ObservableProperty]
    private DateTime _scheduledStartTime = DateTime.Now.AddMinutes(5);

    [ObservableProperty]
    private int _scheduledDurationMinutes = 10;

    // ========== 일반 설정 ==========
    [ObservableProperty]
    private CloseAction _closeAction = CloseAction.MinimizeToTray;

    public IReadOnlyList<string> CloseActionOptions { get; } = new[] { "트레이로 최소화", "즉시 종료" };

    // CloseAction을 문자열로 변환 (UI 바인딩용)
    public string SelectedCloseActionText
    {
        get => CloseAction switch
        {
            CloseAction.MinimizeToTray => "트레이로 최소화",
            CloseAction.ExitImmediately => "즉시 종료",
            _ => "트레이로 최소화"
        };
        set
        {
            CloseAction = value switch
            {
                "즉시 종료" => CloseAction.ExitImmediately,
                _ => CloseAction.MinimizeToTray
            };
            OnPropertyChanged();
        }
    }

    public IReadOnlyList<string> PositionOptions { get; } = new[] { "좌상단", "우상단", "좌하단", "우하단" };
    public IReadOnlyList<string> WebcamSizeOptions { get; } = new[] { "소", "중", "대" };

    // ========== 자동 동영상 압축 설정 ==========
    [ObservableProperty]
    private VideoCompressionQuality _autoVideoCompression = VideoCompressionQuality.None;

    // 수동 압축 진행 상태
    [ObservableProperty]
    private bool _isCompressingVideo;

    [ObservableProperty]
    private string _compressionStatus = "";

    [ObservableProperty]
    private int _compressionProgress;

    public IReadOnlyList<string> VideoCompressionOptions { get; } = new[] { "압축 안함", "최고 품질 (H.265)", "보통 품질 (H.265, 용량↓)" };

    public string SelectedVideoCompressionText
    {
        get => AutoVideoCompression switch
        {
            VideoCompressionQuality.High => "최고 품질 (H.265)",
            VideoCompressionQuality.Normal => "보통 품질 (H.265, 용량↓)",
            _ => "압축 안함"
        };
        set
        {
            AutoVideoCompression = value switch
            {
                "최고 품질 (H.265)" => VideoCompressionQuality.High,
                "보통 품질 (H.265, 용량↓)" => VideoCompressionQuality.Normal,
                _ => VideoCompressionQuality.None
            };
            OnPropertyChanged();
        }
    }

    [ObservableProperty]
    private long _frameCount;

    [ObservableProperty]
    private double _currentFps;

    public IReadOnlyList<VideoFormat> AvailableVideoFormats { get; } = new[]
    {
        VideoFormat.MP4_H264,
        VideoFormat.WebM_VP9,
        VideoFormat.MKV_H264
    };

    public IReadOnlyList<int> AvailableFrameRates { get; } = new[] { 15, 24, 30, 60 };

    public bool IsScreenRecordingMode => CurrentRecordingMode == RecordingMode.ScreenWithAudio;
    public bool IsAudioOnlyMode => CurrentRecordingMode == RecordingMode.AudioOnly;
    public string RecentFilesHeader => IsScreenRecordingMode ? "최근 녹화" : "최근 녹음";
    public bool IsScreenRecordingAvailable => _screenRecordingEngine.IsFFmpegAvailable;

    // 캐싱된 필터링 결과 (LINQ 재계산 방지)
    private List<RecordingInfo>? _cachedFilteredFiles;
    private RecordingMode _lastFilterMode;
    private int _lastRecentFilesCount;

    // 현재 모드에 맞는 파일만 필터링하여 표시 (캐싱)
    public IEnumerable<RecordingInfo> FilteredRecentFiles
    {
        get
        {
            // 캐시 무효화 조건: 모드 변경 또는 파일 수 변경
            if (_cachedFilteredFiles == null ||
                _lastFilterMode != CurrentRecordingMode ||
                _lastRecentFilesCount != RecentFiles.Count)
            {
                _cachedFilteredFiles = IsScreenRecordingMode
                    ? RecentFiles.Where(r => r.IsVideo).ToList()
                    : RecentFiles.Where(r => !r.IsVideo).ToList();
                _lastFilterMode = CurrentRecordingMode;
                _lastRecentFilesCount = RecentFiles.Count;
            }
            return _cachedFilteredFiles;
        }
    }

    // 빈 상태 메시지
    public string EmptyFilesMessage => IsScreenRecordingMode ? "녹화 파일이 없습니다" : "녹음 파일이 없습니다";
    // 캐시 리스트의 Count만 체크 — 이중 필터링 방지 (FilteredRecentFiles 게터 우회)
    public bool HasNoFilteredFiles => (_cachedFilteredFiles ?? (IEnumerable<RecordingInfo>)FilteredRecentFiles).Count() == 0;

    // 재생 상태
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _playbackTime = "00:00 / 00:00";

    [ObservableProperty]
    private double _playbackPosition;

    [ObservableProperty]
    private double _playbackDuration = 1;

    // 배속 재생
    [ObservableProperty]
    private double _playbackSpeed = 1.0;

    [ObservableProperty]
    private string _playbackSpeedText = "1.0x";

    public IReadOnlyList<double> PlaybackSpeedOptions { get; } = new[] { 0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0 };

    // 북마크 (녹음 중 중요 시점 표시)
    [ObservableProperty]
    private ObservableCollection<BookmarkInfo> _bookmarks = new();

    [ObservableProperty]
    private string _currentRecordingPath = "";

    // 파일명 템플릿
    [ObservableProperty]
    private string _fileNameTemplate = "Recording_{datetime}";

    [ObservableProperty]
    private string _recordingTitle = "";

    // 노이즈 제거
    [ObservableProperty]
    private bool _isProcessingAudio;

    [ObservableProperty]
    private string _audioProcessingStatus = "";

    public IReadOnlyList<string> NoiseReductionOptions { get; } = new[] { "약함", "보통", "강함" };

    // 구간 추출
    [ObservableProperty]
    private TimeSpan _extractStartTime = TimeSpan.Zero;

    [ObservableProperty]
    private TimeSpan _extractEndTime = TimeSpan.Zero;

    // 자동 분할 녹음
    [ObservableProperty]
    private bool _autoSplitEnabled = false;

    [ObservableProperty]
    private int _autoSplitIntervalMinutes = 60;

    public IReadOnlyList<int> AutoSplitIntervalOptions { get; } = new[] { 10, 20, 30, 60, 90, 120, 180, 240 };

    public MainViewModel()
    {
        _deviceManager = new DeviceManager();
        _recordingEngine = new RecordingEngine(_deviceManager);
        _screenRecordingEngine = new ScreenRecordingEngine(_deviceManager);
        _audioPlayer = new AudioPlayer();
        _conversionService = new AudioConversionService();
        _videoConversionService = new VideoConversionService();
        _settings = AppSettings.Load();

        // 설정 적용
        _outputDirectory = _settings.OutputDirectory;
        _recordMicrophone = _settings.RecordMicrophone;
        _recordSystemAudio = _settings.RecordSystem;
        _micVolume = _settings.MicrophoneVolume;
        _systemVolume = _settings.SystemVolume;
        _selectedRecordingFormat = _settings.RecordingFormat;
        _closeAction = _settings.CloseAction;
        _autoVideoCompression = _settings.VideoCompressionQuality;

        // 자동 분할 설정 적용
        _autoSplitEnabled = _settings.AutoSplitEnabled;
        _autoSplitIntervalMinutes = _settings.AutoSplitIntervalMinutes;

        // 화면 녹화 설정 적용
        _selectedVideoQuality = _settings.VideoQuality;
        _showMouseCursor = _settings.ShowMouseCursor;
        _highlightMouseClicks = _settings.HighlightMouseClicks;
        _countdownSeconds = _settings.CountdownSeconds;
        _showRecordingBorder = _settings.ShowRecordingBorder;
        _selectedResolution = _settings.Resolution;
        _selectedAudioBitrate = _settings.AudioBitrate;

        // 녹음 타이머 설정 (UI 업데이트용) - 100ms로 최적화
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(100)
        };
        _timer.Tick += OnTimerTick;

        // 재생 타이머 설정
        _playbackTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _playbackTimer.Tick += OnPlaybackTimerTick;

        // 오디오 녹음 이벤트 연결
        _recordingEngine.LevelUpdated += OnLevelUpdated;
        _recordingEngine.StateChanged += OnStateChanged;
        _recordingEngine.ErrorOccurred += OnErrorOccurred;
        _recordingEngine.SegmentCompleted += OnSegmentCompleted;
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;

        // 화면 녹화 이벤트 연결
        _screenRecordingEngine.LevelUpdated += OnLevelUpdated;
        _screenRecordingEngine.StateChanged += OnScreenRecordingStateChanged;
        _screenRecordingEngine.ErrorOccurred += OnErrorOccurred;
        _screenRecordingEngine.RecordingCompleted += OnScreenRecordingCompleted;

        // 기본 캡처 영역 설정 (전체 화면)
        _selectedCaptureRegion = new CaptureRegion { Type = CaptureRegionType.FullScreen };

        // 초기 모드 UI 업데이트 (바인딩 초기화를 위해)
        OnPropertyChanged(nameof(IsAudioOnlyMode));
        OnPropertyChanged(nameof(IsScreenRecordingMode));
        OnPropertyChanged(nameof(RecentFilesHeader));
        OnPropertyChanged(nameof(FilteredRecentFiles));
        OnPropertyChanged(nameof(EmptyFilesMessage));
        OnPropertyChanged(nameof(HasNoFilteredFiles));

        // UI 스레드 외부에서 COM 장치 열거 + 파일 I/O 수행 → 창이 즉시 뜸
        _ = Task.Run(() =>
        {
            try
            {
                // 1) DeviceManager 미리 준비 (COM 호출 수백ms, 결과만 받아 UI로 반영)
                var inputs = _deviceManager.GetInputDevices();
                var outputs = _deviceManager.GetOutputDevices();

                // 2) UI 스레드에서 ObservableCollection 일괄 반영
                System.Windows.Application.Current?.Dispatcher.InvokeAsync(() =>
                {
                    InputDevices.Clear();
                    foreach (var d in inputs)
                    {
                        InputDevices.Add(d);
                        if (d.IsDefault) SelectedInputDevice = d;
                    }
                    OutputDevices.Clear();
                    foreach (var d in outputs)
                    {
                        OutputDevices.Add(d);
                        if (d.IsDefault) SelectedOutputDevice = d;
                    }

                    // 최근 파일 로드 (JSON 파일, 100ms 이내)
                    LoadRecentFiles();
                }, System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[MainViewModel] 비동기 초기화 실패: {ex.Message}");
            }
        });
    }

    private void LoadRecentFiles()
    {
        var recentFilesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder",
            "recent_files.json");

        try
        {
            if (File.Exists(recentFilesPath))
            {
                var json = File.ReadAllText(recentFilesPath);

                // 직접 JSON 파싱하여 RecordingInfo 생성 (이전 형식 호환성)
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                foreach (var element in doc.RootElement.EnumerateArray().Take(_settings.MaxRecentFiles))
                {
                    try
                    {
                        var filePath = element.GetProperty("FilePath").GetString() ?? "";
                        if (!File.Exists(filePath)) continue;

                        var recording = new RecordingInfo
                        {
                            FilePath = filePath,
                            RecordedAt = element.TryGetProperty("RecordedAt", out var rat)
                                ? rat.GetDateTime() : DateTime.Now,
                            Duration = element.TryGetProperty("Duration", out var dur)
                                ? TimeSpan.Parse(dur.GetString() ?? "00:00:00") : TimeSpan.Zero,
                            FileSize = element.TryGetProperty("FileSize", out var fs)
                                ? fs.GetInt64() : new FileInfo(filePath).Length
                        };
                        RecentFiles.Add(recording);
                    }
                    catch { }
                }

                // 필터링된 목록 업데이트 (캐시 무효화)
                _cachedFilteredFiles = null;
                OnPropertyChanged(nameof(FilteredRecentFiles));
                OnPropertyChanged(nameof(HasNoFilteredFiles));
            }
        }
        catch { }
    }

    private void SaveRecentFiles()
    {
        var recentFilesPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder",
            "recent_files.json");

        try
        {
            var directory = Path.GetDirectoryName(recentFilesPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            // 필요한 속성만 저장 (이전 형식 속성 제외)
            var dataToSave = RecentFiles.Select(r => new
            {
                r.FilePath,
                r.RecordedAt,
                Duration = r.Duration.ToString(),
                r.FileSize
            }).ToList();

            var options = new System.Text.Json.JsonSerializerOptions { WriteIndented = true };
            var json = System.Text.Json.JsonSerializer.Serialize(dataToSave, options);
            File.WriteAllText(recentFilesPath, json);
        }
        catch { }
    }

    public void SaveSettings()
    {
        _settings.OutputDirectory = OutputDirectory;
        _settings.RecordMicrophone = RecordMicrophone;
        _settings.RecordSystem = RecordSystemAudio;
        _settings.MicrophoneVolume = MicVolume;
        _settings.SystemVolume = SystemVolume;
        _settings.LastMicrophoneId = SelectedInputDevice?.Id;
        _settings.LastSystemDeviceId = SelectedOutputDevice?.Id;
        _settings.RecordingFormat = SelectedRecordingFormat;
        _settings.CloseAction = CloseAction;
        _settings.VideoCompressionQuality = AutoVideoCompression;

        // 자동 분할 설정 저장
        _settings.AutoSplitEnabled = AutoSplitEnabled;
        _settings.AutoSplitIntervalMinutes = AutoSplitIntervalMinutes;

        // 화면 녹화 설정 저장
        _settings.VideoQuality = SelectedVideoQuality;
        _settings.ShowMouseCursor = ShowMouseCursor;
        _settings.HighlightMouseClicks = HighlightMouseClicks;
        _settings.CountdownSeconds = CountdownSeconds;
        _settings.ShowRecordingBorder = ShowRecordingBorder;
        _settings.Resolution = SelectedResolution;
        _settings.AudioBitrate = SelectedAudioBitrate;

        _settings.Save();

        SaveRecentFiles();
    }

    private void LoadDevices()
    {
        InputDevices.Clear();
        OutputDevices.Clear();

        foreach (var device in _deviceManager.GetInputDevices())
        {
            InputDevices.Add(device);
            if (device.IsDefault)
                SelectedInputDevice = device;
        }

        foreach (var device in _deviceManager.GetOutputDevices())
        {
            OutputDevices.Add(device);
            if (device.IsDefault)
                SelectedOutputDevice = device;
        }
    }

    [RelayCommand(CanExecute = nameof(CanStartRecording))]
    private void StartRecording()
    {
        System.Diagnostics.Debug.WriteLine($"[ViewModel] StartRecording() 호출됨 - Mode: {CurrentRecordingMode}, Mic: {RecordMicrophone}, Sys: {RecordSystemAudio}");

        if (!RecordMicrophone && !RecordSystemAudio)
        {
            StatusText = "마이크 또는 시스템 오디오 중 하나 이상을 선택하세요";
            System.Diagnostics.Debug.WriteLine("[ViewModel] StartRecording() - 오디오 소스 없음, 반환");
            return;
        }

        try
        {
            if (CurrentRecordingMode == RecordingMode.ScreenWithAudio)
            {
                // 화면 녹화 모드
                StartScreenRecording();
            }
            else
            {
                // 오디오 녹음 모드
                StartAudioRecording();
            }

            // 녹음/녹화 중에는 모드 전환 비활성화
            SwitchToAudioModeCommand.NotifyCanExecuteChanged();
            SwitchToScreenModeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSwitchModeBinding));
        }
        catch (Exception ex)
        {
            StatusText = $"녹음/녹화 시작 실패: {ex.Message}";
        }
    }

    private void StartAudioRecording()
    {
        var options = new RecordingOptions
        {
            RecordMicrophone = RecordMicrophone,
            RecordSystemAudio = RecordSystemAudio,
            MicrophoneDeviceId = SelectedInputDevice?.Id,
            OutputDeviceId = SelectedOutputDevice?.Id,
            MicrophoneVolume = MicVolume,
            SystemVolume = SystemVolume,
            OutputDirectory = OutputDirectory,
            Format = SelectedRecordingFormat,
            AutoSplitEnabled = AutoSplitEnabled,
            AutoSplitIntervalMinutes = AutoSplitIntervalMinutes
        };

        _recordingEngine.Start(options);
        _timer.Start();

        // 수동으로 상태 업데이트
        RecordingState = RecordingState.Recording;
        var formatName = SelectedRecordingFormat.GetDisplayName();
        var splitInfo = AutoSplitEnabled ? $", {AutoSplitIntervalMinutes}분 분할" : "";
        StatusText = $"녹음 중... ({formatName}{splitInfo})";
    }

    private async void StartScreenRecording()
    {
        System.Diagnostics.Debug.WriteLine($"[ViewModel] StartScreenRecording 호출됨");

        if (SelectedCaptureRegion == null)
        {
            SelectedCaptureRegion = new CaptureRegion { Type = CaptureRegionType.FullScreen };
        }

        var options = new ScreenRecordingOptions
        {
            Region = SelectedCaptureRegion,
            FrameRate = GetFpsFromQuality(),
            VideoFormat = SelectedVideoFormat,
            VideoCrf = GetCrfFromQuality(),
            EncoderPreset = GetPresetFromQuality(),
            OutputDirectory = OutputDirectory,
            IncludeMicrophone = RecordMicrophone,
            IncludeSystemAudio = RecordSystemAudio,
            MicrophoneVolume = MicVolume,
            SystemVolume = SystemVolume,
            MicrophoneDeviceId = SelectedInputDevice?.Id,
            OutputDeviceId = SelectedOutputDevice?.Id,
            ShowMouseCursor = ShowMouseCursor,
            // 1단계 옵션
            HighlightMouseClicks = HighlightMouseClicks,
            CountdownSeconds = CountdownSeconds,
            ShowRecordingBorder = ShowRecordingBorder
        };

        System.Diagnostics.Debug.WriteLine($"[ViewModel] 화면 녹화 옵션 - 영역: {options.Region.Type}, FPS: {options.FrameRate}, 출력: {options.OutputDirectory}");

        try
        {
            // 카운트다운 표시 (0보다 크면)
            if (CountdownSeconds > 0)
            {
                StatusText = $"녹화 시작 대기 중... ({CountdownSeconds}초)";
                var countdownWindow = new Views.CountdownWindow(CountdownSeconds);
                var result = await countdownWindow.StartCountdownAsync();

                if (!result)
                {
                    StatusText = "녹화가 취소되었습니다.";
                    return;
                }
            }

            _screenRecordingEngine.Start(options);
            _timer.Start();

            // 수동으로 상태 업데이트 (이벤트가 비동기로 처리될 수 있음)
            RecordingState = RecordingState.Recording;
            StatusText = $"화면 녹화 중... ({CaptureRegionText}, {GetFpsFromQuality()}fps)";
            System.Diagnostics.Debug.WriteLine($"[ViewModel] 화면 녹화 시작 완료 - StatusText: {StatusText}");
        }
        catch (Exception ex)
        {
            StatusText = $"화면 녹화 실패: {ex.Message}";
            System.Diagnostics.Debug.WriteLine($"[ViewModel] 화면 녹화 실패: {ex.Message}");
            throw;
        }
    }

    private bool CanStartRecording() => RecordingState == RecordingState.Stopped && !_isStoppingScreenRecording;

    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    private async Task StopRecordingAsync()
    {
        System.Diagnostics.Debug.WriteLine("[ViewModel] StopRecordingAsync 시작");
        _isStoppingScreenRecording = true;
        StartRecordingCommand.NotifyCanExecuteChanged();
        try
        {
            _timer.Stop();

            if (CurrentRecordingMode == RecordingMode.ScreenWithAudio)
            {
                // 화면 녹화 중지 - StopAsync는 캡처만 정지하고 즉시 반환
                // 인코딩은 백그라운드에서 진행되며 RecordingCompleted 이벤트로 결과 통보
                StatusText = "녹화 정지 중...";
                System.Diagnostics.Debug.WriteLine("[ViewModel] StopAsync 호출 전");
                await _screenRecordingEngine.StopAsync();
                System.Diagnostics.Debug.WriteLine("[ViewModel] StopAsync 완료 - 캡처 정지됨, 인코딩은 백그라운드에서 진행");

                // StopAsync 이후 상태는 이미 Stopped으로 변경됨 (ScreenRecordingEngine에서 처리)
                // 버튼 활성화
                _isStoppingScreenRecording = false;
                RecordingState = RecordingState.Stopped;
                StatusText = "인코딩 중... (백그라운드)";
                StartRecordingCommand.NotifyCanExecuteChanged();
                StopRecordingCommand.NotifyCanExecuteChanged();
                SwitchToAudioModeCommand.NotifyCanExecuteChanged();
                SwitchToScreenModeCommand.NotifyCanExecuteChanged();
                OnPropertyChanged(nameof(CanSwitchModeBinding));
                System.Diagnostics.Debug.WriteLine("[ViewModel] 버튼 활성화 완료");
            }
            else
            {
                // 오디오 녹음 중지
                await StopAudioRecordingAsync();
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[ViewModel] StopRecordingAsync 예외: {ex.Message}");
            StatusText = $"녹음/녹화 중지 실패: {ex.Message}";
            _isStoppingScreenRecording = false;
            RecordingState = RecordingState.Stopped;
            StartRecordingCommand.NotifyCanExecuteChanged();
            StopRecordingCommand.NotifyCanExecuteChanged();
            SwitchToAudioModeCommand.NotifyCanExecuteChanged();
            SwitchToScreenModeCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(CanSwitchModeBinding));
        }
    }

    private Task StopAudioRecordingAsync()
    {
        var wavFilePath = _recordingEngine.CurrentFilePath;
        var targetFormat = _recordingEngine.TargetFormat;
        var duration = _recordingEngine.ElapsedTime;

        _recordingEngine.Stop();

        // 즉시 버튼 활성화 (UI 응답성 개선)
        _isStoppingScreenRecording = false;
        RecordingState = RecordingState.Stopped;
        StartRecordingCommand.NotifyCanExecuteChanged();
        StopRecordingCommand.NotifyCanExecuteChanged();
        PauseRecordingCommand.NotifyCanExecuteChanged();
        ResumeRecordingCommand.NotifyCanExecuteChanged();
        SwitchToAudioModeCommand.NotifyCanExecuteChanged();
        SwitchToScreenModeCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(nameof(CanSwitchModeBinding));

        if (!File.Exists(wavFilePath))
        {
            StatusText = "녹음 파일을 찾을 수 없습니다.";
            return Task.CompletedTask;
        }

        // WAV 포맷이면 즉시 완료
        if (targetFormat == RecordingFormat.WAV)
        {
            AddToRecentFiles(wavFilePath, duration);
            StatusText = "녹음 완료";
            return Task.CompletedTask;
        }

        // WAV가 아닌 포맷은 백그라운드에서 변환
        var formatName = targetFormat.GetDisplayName();
        var fileSizeMB = new FileInfo(wavFilePath).Length / (1024.0 * 1024);
        System.Diagnostics.Debug.WriteLine($"[StopRecording] WAV 파일 크기: {fileSizeMB:F1} MB");
        StatusText = $"{formatName}로 변환 중... (백그라운드)";

        // 로컬 변수로 복사 (클로저 문제 방지)
        var localWavPath = wavFilePath;
        var localTargetFormat = targetFormat;
        var localDuration = duration;

        // 클로저에서 사용할 formatName도 캡처
        var localFormatName = formatName;

        _ = Task.Run(async () =>
        {
            try
            {
                // WAV 파일이 완전히 닫힐 때까지 대기 (파일 핸들 해제 확인)
                var waitStart = DateTime.Now;
                var maxWaitSeconds = 10;
                while ((DateTime.Now - waitStart).TotalSeconds < maxWaitSeconds)
                {
                    try
                    {
                        // 파일을 열어볼 수 있으면 녹음 완료
                        using (var fs = new FileStream(localWavPath, FileMode.Open, FileAccess.Read, FileShare.None))
                        {
                            var size = fs.Length;
                            System.Diagnostics.Debug.WriteLine($"[AudioConvert] WAV 파일 준비됨: {size / (1024.0 * 1024):F1} MB");
                            break;
                        }
                    }
                    catch (IOException)
                    {
                        // 파일이 아직 사용 중 - 대기
                        System.Diagnostics.Debug.WriteLine("[AudioConvert] WAV 파일 대기 중...");
                        await Task.Delay(500);
                    }
                }

                var audioFormat = localTargetFormat switch
                {
                    RecordingFormat.FLAC => AudioFormat.FLAC,
                    RecordingFormat.MP3_128 => AudioFormat.MP3_128,
                    _ => AudioFormat.MP3_320
                };

                var targetExtension = localTargetFormat.GetExtension();
                var finalFilePath = Path.ChangeExtension(localWavPath, targetExtension);

                System.Diagnostics.Debug.WriteLine($"[AudioConvert] 변환 시작: {localWavPath} -> {finalFilePath} (Format: {audioFormat})");

                var success = await _conversionService.ConvertAsync(localWavPath, audioFormat, finalFilePath);

                System.Diagnostics.Debug.WriteLine($"[AudioConvert] 변환 결과: success={success}, fileExists={File.Exists(finalFilePath)}");

                // UI 스레드에서 결과 처리
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (success && File.Exists(finalFilePath))
                    {
                        // 변환 성공 시 원본 WAV 삭제
                        try { File.Delete(localWavPath); } catch { }
                        AddToRecentFiles(finalFilePath, localDuration);
                        StatusText = $"{localFormatName} 변환 완료";
                    }
                    else
                    {
                        // 변환 실패 시 WAV 유지
                        AddToRecentFiles(localWavPath, localDuration);
                        StatusText = "변환 실패, WAV로 저장됨";
                    }
                });
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[AudioConvert] 예외 발생: {ex.Message}");
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    AddToRecentFiles(localWavPath, localDuration);
                    StatusText = $"변환 오류: {ex.Message}";
                });
            }
        });

        return Task.CompletedTask;
    }

    /// <summary>
    /// 최근 파일 목록에 추가 (UI 스레드에서 호출)
    /// </summary>
    private void AddToRecentFiles(string filePath, TimeSpan duration)
    {
        if (!File.Exists(filePath)) return;

        var fileInfo = new FileInfo(filePath);
        var recording = new RecordingInfo
        {
            FilePath = filePath,
            RecordedAt = DateTime.Now,
            Duration = duration,
            FileSize = fileInfo.Length
        };

        RecentFiles.Insert(0, recording);
        if (RecentFiles.Count > 10)
            RecentFiles.RemoveAt(RecentFiles.Count - 1);

        // 필터링된 목록 업데이트 (캐시 무효화)
        _cachedFilteredFiles = null;
        OnPropertyChanged(nameof(FilteredRecentFiles));
        OnPropertyChanged(nameof(HasNoFilteredFiles));

        var sizeMb = recording.FileSize / (1024.0 * 1024);
        if (sizeMb >= 1)
            FileInfo = $"저장됨: {recording.FileName} ({sizeMb:F1} MB)";
        else
            FileInfo = $"저장됨: {recording.FileName} ({recording.FileSize / 1024.0:F1} KB)";

        SaveRecentFiles();
    }

    private bool CanStopRecording() => RecordingState == RecordingState.Recording || RecordingState == RecordingState.Paused;

    [RelayCommand(CanExecute = nameof(CanPauseRecording))]
    private void PauseRecording()
    {
        if (CurrentRecordingMode == RecordingMode.ScreenWithAudio)
        {
            _screenRecordingEngine.Pause();
        }
        else
        {
            _recordingEngine.Pause();
        }
        StatusText = "일시정지";
    }

    private bool CanPauseRecording() => RecordingState == RecordingState.Recording;

    [RelayCommand(CanExecute = nameof(CanResumeRecording))]
    private void ResumeRecording()
    {
        if (CurrentRecordingMode == RecordingMode.ScreenWithAudio)
        {
            _screenRecordingEngine.Resume();
            StatusText = "화면 녹화 중...";
        }
        else
        {
            _recordingEngine.Resume();
            StatusText = "녹음 중...";
        }
    }

    private bool CanResumeRecording() => RecordingState == RecordingState.Paused;

    [RelayCommand]
    private void RefreshDevices()
    {
        LoadDevices();
        StatusText = "장치 목록 새로고침 완료";
    }

    [RelayCommand]
    private void ToggleOptions()
    {
        IsOptionsExpanded = !IsOptionsExpanded;
    }

    [RelayCommand]
    private void BrowseOutputDirectory()
    {
        // Windows Vista+ 스타일 폴더 선택 다이얼로그
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "녹음 파일 저장 폴더 선택",
            InitialDirectory = OutputDirectory
        };

        if (dialog.ShowDialog() == true)
        {
            OutputDirectory = dialog.FolderName;
            StatusText = $"저장 경로 변경: {OutputDirectory}";
        }
    }

    [RelayCommand]
    private void OpenOutputDirectory()
    {
        if (Directory.Exists(OutputDirectory))
        {
            System.Diagnostics.Process.Start("explorer.exe", OutputDirectory);
        }
    }

    [RelayCommand]
    private void PlayFile(RecordingInfo? recording)
    {
        if (recording == null || !File.Exists(recording.FilePath))
            return;

        // 비디오 파일은 기본 앱으로 열기
        if (recording.IsVideo)
        {
            if (!IsAllowedMediaFile(recording.FilePath))
            {
                StatusText = $"지원하지 않는 파일 형식입니다: {Path.GetExtension(recording.FilePath)}";
                return;
            }

            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = recording.FilePath,
                    UseShellExecute = true
                });
                StatusText = $"재생 중: {recording.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = $"재생 실패: {ex.Message}";
            }
            return;
        }

        // 오디오 파일은 내장 플레이어로 재생
        try
        {
            if (IsPlaying)
                _audioPlayer.Stop();

            _audioPlayer.Play(recording.FilePath);
            PlaybackDuration = _audioPlayer.TotalDuration.TotalSeconds;
            IsPlaying = true;
            _playbackTimer.Start();
            StatusText = $"재생 중: {recording.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"재생 실패: {ex.Message}";
        }
    }

    [RelayCommand]
    private void StopPlayback()
    {
        _playbackTimer.Stop();
        IsPlaying = false;
        PlaybackPosition = 0;
        PlaybackTime = "00:00 / 00:00";
        StatusText = "준비";
        _audioPlayer.Stop();
    }

    [RelayCommand]
    private void PausePlayback()
    {
        // 재생 중이 아니면 무시
        if (!IsPlaying) return;

        try
        {
            if (_audioPlayer.IsPlaying)
            {
                _audioPlayer.Pause();
                StatusText = "재생 일시정지";
            }
            else if (_audioPlayer.IsPaused)
            {
                _audioPlayer.Resume();
                StatusText = "재생 중...";
            }
        }
        catch
        {
            // Stop 중에 호출되면 무시
        }
    }

    [RelayCommand]
    private void OpenInExplorer(RecordingInfo? recording)
    {
        if (recording == null || !File.Exists(recording.FilePath))
            return;

        System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{recording.FilePath}\"");
    }

    [RelayCommand]
    private void DeleteRecording(RecordingInfo? recording)
    {
        if (recording == null)
            return;

        // 확인 다이얼로그 - 데이터 손실 방지
        var confirm = System.Windows.MessageBox.Show(
            $"이 파일을 삭제하시겠습니까?\n\n{recording.FileName}\n\n삭제된 파일은 복구할 수 없습니다.",
            "파일 삭제 확인",
            System.Windows.MessageBoxButton.YesNo,
            System.Windows.MessageBoxImage.Warning,
            System.Windows.MessageBoxResult.No);

        if (confirm != System.Windows.MessageBoxResult.Yes)
            return;

        // 재생 중이면 중지
        if (IsPlaying && SelectedRecentFile == recording)
        {
            StopPlayback();
        }

        // 파일 삭제
        if (File.Exists(recording.FilePath))
        {
            try
            {
                File.Delete(recording.FilePath);
                StatusText = $"삭제됨: {recording.FileName}";
            }
            catch (Exception ex)
            {
                StatusText = $"삭제 실패: {ex.Message}";
                return;
            }
        }

        // 목록에서 제거
        RecentFiles.Remove(recording);

        // 필터링된 목록 업데이트 (캐시 무효화)
        _cachedFilteredFiles = null;
        OnPropertyChanged(nameof(FilteredRecentFiles));
        OnPropertyChanged(nameof(HasNoFilteredFiles));
    }

    [RelayCommand]
    private void PlayWithDefaultApp(RecordingInfo? recording)
    {
        if (recording != null && File.Exists(recording.FilePath))
        {
            if (!IsAllowedMediaFile(recording.FilePath))
            {
                StatusText = $"지원하지 않는 파일 형식입니다: {Path.GetExtension(recording.FilePath)}";
                return;
            }

            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = recording.FilePath,
                UseShellExecute = true
            });
        }
    }


    [RelayCommand]
    private async Task ConvertToMp3Async(RecordingInfo? recording)
    {
        await ConvertToFormatAsync(recording, AudioFormat.MP3_320);
    }

    [RelayCommand]
    private async Task ConvertToFlacAsync(RecordingInfo? recording)
    {
        await ConvertToFormatAsync(recording, AudioFormat.FLAC);
    }

    [RelayCommand]
    private async Task ConvertToAacAsync(RecordingInfo? recording)
    {
        await ConvertToFormatAsync(recording, AudioFormat.AAC_256);
    }

    [RelayCommand]
    private async Task CompressVideoHighAsync(RecordingInfo? recording)
    {
        await CompressVideoAsync(recording, VideoQuality.High);
    }

    [RelayCommand]
    private async Task CompressVideoNormalAsync(RecordingInfo? recording)
    {
        await CompressVideoAsync(recording, VideoQuality.Normal);
    }

    [RelayCommand]
    private void CancelVideoCompression()
    {
        if (IsCompressingVideo)
        {
            _videoConversionService.Cancel();
            StatusText = "압축 취소 중...";
        }
    }

    // ========== 배속 재생 명령 ==========
    [RelayCommand]
    private void SetPlaybackSpeed(double speed)
    {
        PlaybackSpeed = speed;
        _audioPlayer.PlaybackSpeed = speed;
        PlaybackSpeedText = $"{speed:F2}x";
    }

    [RelayCommand]
    private void IncreasePlaybackSpeed()
    {
        var currentIndex = Array.IndexOf(PlaybackSpeedOptions.ToArray(), PlaybackSpeed);
        if (currentIndex < PlaybackSpeedOptions.Count - 1)
            SetPlaybackSpeed(PlaybackSpeedOptions[currentIndex + 1]);
    }

    [RelayCommand]
    private void DecreasePlaybackSpeed()
    {
        var currentIndex = Array.IndexOf(PlaybackSpeedOptions.ToArray(), PlaybackSpeed);
        if (currentIndex > 0)
            SetPlaybackSpeed(PlaybackSpeedOptions[currentIndex - 1]);
    }

    // ========== 되감기/앞으로 명령 ==========
    [RelayCommand]
    private void Rewind5() => _audioPlayer.Rewind5();

    [RelayCommand]
    private void Rewind10() => _audioPlayer.Rewind10();

    [RelayCommand]
    private void Forward5() => _audioPlayer.Forward5();

    [RelayCommand]
    private void Forward10() => _audioPlayer.Forward10();

    // ========== 북마크 명령 ==========
    [RelayCommand]
    private void AddBookmark()
    {
        if (RecordingState != RecordingState.Recording) return;

        var elapsed = _recordingEngine.ElapsedTime;
        var bookmark = new BookmarkInfo
        {
            Position = elapsed,
            Label = $"북마크 {Bookmarks.Count + 1}"
        };
        Bookmarks.Add(bookmark);
        StatusText = $"📌 북마크 추가: {bookmark.PositionText}";
    }

    [RelayCommand]
    private void SeekToBookmark(BookmarkInfo? bookmark)
    {
        if (bookmark == null) return;
        _audioPlayer.Seek(bookmark.Position);
    }

    [RelayCommand]
    private void RemoveBookmark(BookmarkInfo? bookmark)
    {
        if (bookmark != null)
            Bookmarks.Remove(bookmark);
    }

    [RelayCommand]
    private void ClearBookmarks()
    {
        Bookmarks.Clear();
    }

    // ========== 노이즈 제거 명령 ==========
    [RelayCommand]
    private async Task RemoveNoiseAsync(RecordingInfo? recording)
    {
        await RemoveNoiseWithLevelAsync(recording, "보통");
    }

    [RelayCommand]
    private async Task RemoveNoiseLightAsync(RecordingInfo? recording)
    {
        await RemoveNoiseWithLevelAsync(recording, "약함");
    }

    [RelayCommand]
    private async Task RemoveNoiseStrongAsync(RecordingInfo? recording)
    {
        await RemoveNoiseWithLevelAsync(recording, "강함");
    }

    private async Task RemoveNoiseWithLevelAsync(RecordingInfo? recording, string levelName)
    {
        if (recording == null || !File.Exists(recording.FilePath)) return;
        if (recording.IsVideo)
        {
            StatusText = "오디오 파일만 노이즈 제거가 가능합니다.";
            return;
        }
        if (IsProcessingAudio)
        {
            StatusText = "이미 처리 중입니다.";
            return;
        }

        var level = levelName switch
        {
            "약함" => NoiseReductionLevel.Light,
            "강함" => NoiseReductionLevel.Strong,
            _ => NoiseReductionLevel.Medium
        };

        IsProcessingAudio = true;
        AudioProcessingStatus = $"노이즈 제거 중 ({levelName})...";
        StatusText = AudioProcessingStatus;

        try
        {
            var result = await _conversionService.RemoveNoiseAsync(recording.FilePath, null, level);
            if (result)
            {
                StatusText = "✅ 노이즈 제거 완료";
                LoadRecentFiles();
            }
            else
            {
                StatusText = "❌ 노이즈 제거 실패";
            }
        }
        finally
        {
            IsProcessingAudio = false;
            AudioProcessingStatus = "";
        }
    }

    // ========== 구간 추출 명령 ==========
    [RelayCommand]
    private async Task ExtractSegmentAsync(RecordingInfo? recording)
    {
        if (recording == null || !File.Exists(recording.FilePath)) return;
        if (IsProcessingAudio)
        {
            StatusText = "이미 처리 중입니다.";
            return;
        }

        // 현재 재생 위치를 끝 시간으로 설정
        if (ExtractEndTime == TimeSpan.Zero && _audioPlayer.TotalDuration > TimeSpan.Zero)
        {
            ExtractEndTime = _audioPlayer.TotalDuration;
        }

        if (ExtractStartTime >= ExtractEndTime)
        {
            StatusText = "시작 시간이 끝 시간보다 작아야 합니다.";
            return;
        }

        IsProcessingAudio = true;
        AudioProcessingStatus = $"구간 추출 중 ({ExtractStartTime:mm\\:ss} ~ {ExtractEndTime:mm\\:ss})...";
        StatusText = AudioProcessingStatus;

        try
        {
            var result = await _conversionService.ExtractSegmentAsync(recording.FilePath, ExtractStartTime, ExtractEndTime);
            if (result)
            {
                StatusText = "✅ 구간 추출 완료";
                LoadRecentFiles();
            }
            else
            {
                StatusText = "❌ 구간 추출 실패";
            }
        }
        finally
        {
            IsProcessingAudio = false;
            AudioProcessingStatus = "";
        }
    }

    [RelayCommand]
    private void SetExtractStart()
    {
        ExtractStartTime = _audioPlayer.CurrentPosition;
        StatusText = $"시작 지점 설정: {ExtractStartTime:mm\\:ss}";
    }

    [RelayCommand]
    private void SetExtractEnd()
    {
        ExtractEndTime = _audioPlayer.CurrentPosition;
        StatusText = $"끝 지점 설정: {ExtractEndTime:mm\\:ss}";
    }

    private async Task CompressVideoAsync(RecordingInfo? recording, VideoQuality quality)
    {
        if (recording == null || !File.Exists(recording.FilePath))
            return;

        if (!recording.IsVideo)
        {
            StatusText = "동영상 파일만 압축할 수 있습니다.";
            return;
        }

        if (!_videoConversionService.IsFFmpegAvailable)
        {
            StatusText = "FFmpeg가 설치되어 있지 않습니다.";
            return;
        }

        if (IsCompressingVideo)
        {
            StatusText = "이미 압축이 진행 중입니다.";
            return;
        }

        IsCompressingVideo = true;
        CompressionProgress = 0;
        CompressionStatus = "압축 준비 중...";

        var qualityName = VideoConversionService.GetDisplayName(quality);
        StatusText = $"{qualityName}로 압축 중: {recording.FileName}...";

        _videoConversionService.ProgressChanged += OnVideoCompressionProgress;
        _videoConversionService.ConversionCompleted += OnVideoCompressionCompleted;

        try
        {
            await _videoConversionService.CompressAsync(recording.FilePath, quality);
        }
        finally
        {
            _videoConversionService.ProgressChanged -= OnVideoCompressionProgress;
            _videoConversionService.ConversionCompleted -= OnVideoCompressionCompleted;
            IsCompressingVideo = false;
            CompressionProgress = 0;
            CompressionStatus = "";
        }
    }

    private void OnVideoCompressionProgress(object? sender, VideoConversionProgressEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            CompressionProgress = e.Progress;
            CompressionStatus = e.Status;
            StatusText = e.Status;
        });
    }

    private void OnVideoCompressionCompleted(object? sender, VideoConversionCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (e.Success)
            {
                var savedMb = (e.OriginalSize - e.ConvertedSize) / (1024.0 * 1024);
                StatusText = $"압축 완료! ({e.CompressionRatio:F0}% 압축, {savedMb:F1}MB 절약)";

                // 압축된 파일을 목록에 추가
                if (!string.IsNullOrEmpty(e.OutputPath) && File.Exists(e.OutputPath))
                {
                    var fileInfo = new FileInfo(e.OutputPath);
                    var compressedInfo = new RecordingInfo
                    {
                        FilePath = e.OutputPath,
                        RecordedAt = DateTime.Now,
                        Duration = TimeSpan.Zero, // 동영상 길이는 나중에 재생 시 확인
                        FileSize = fileInfo.Length
                    };
                    RecentFiles.Insert(0, compressedInfo);
                    _cachedFilteredFiles = null;
                    OnPropertyChanged(nameof(FilteredRecentFiles));
                    OnPropertyChanged(nameof(HasNoFilteredFiles));
                    SaveRecentFiles();
                }
            }
            else
            {
                StatusText = e.ErrorMessage ?? "압축 실패";
            }
        });
    }

    private async Task ConvertToFormatAsync(RecordingInfo? recording, AudioFormat format)
    {
        if (recording == null || !File.Exists(recording.FilePath))
            return;

        if (!recording.FilePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "WAV 파일만 변환할 수 있습니다.";
            return;
        }

        if (!_conversionService.IsFFmpegAvailable)
        {
            StatusText = "FFmpeg가 설치되어 있지 않습니다. ffmpeg.exe를 앱 폴더에 복사하세요.";
            return;
        }

        var formatName = AudioConversionService.GetDisplayName(format);
        StatusText = $"{formatName}로 변환 중: {recording.FileName}...";

        _conversionService.ConversionCompleted += OnConversionCompleted;
        try
        {
            await _conversionService.ConvertAsync(recording.FilePath, format);
        }
        finally
        {
            _conversionService.ConversionCompleted -= OnConversionCompleted;
        }
    }

    private void OnConversionCompleted(object? sender, AudioConversionCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (e.Success)
            {
                var formatName = AudioConversionService.GetDisplayName(e.Format);
                if (e.CompressionRatio > 0)
                {
                    var savedMb = (e.OriginalSize - e.ConvertedSize) / (1024.0 * 1024);
                    StatusText = $"{formatName} 변환 완료! ({e.CompressionRatio:F0}% 압축, {savedMb:F1}MB 절약)";
                }
                else
                {
                    StatusText = $"{formatName} 변환 완료!";
                }
            }
            else
            {
                StatusText = e.ErrorMessage ?? "변환 실패";
            }
        });
    }

    public bool IsFFmpegAvailable => _conversionService.IsFFmpegAvailable;

    private void OnPlaybackTimerTick(object? sender, EventArgs e)
    {
        if (!IsPlaying) return;

        try
        {
            if (_audioPlayer.IsPlaying)
            {
                PlaybackPosition = _audioPlayer.CurrentPosition.TotalSeconds;
                var current = _audioPlayer.CurrentPosition;
                var total = _audioPlayer.TotalDuration;
                PlaybackTime = $"{current:mm\\:ss} / {total:mm\\:ss}";
            }
        }
        catch { }
    }

    private void OnPlaybackStopped(object? sender, EventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            IsPlaying = false;
            _playbackTimer.Stop();
            PlaybackPosition = 0;
            PlaybackTime = "00:00 / 00:00";
            StatusText = "재생 완료";
        });
    }

    partial void OnMicVolumeChanged(float value)
    {
        _recordingEngine.MicVolume = value;
    }

    partial void OnSystemVolumeChanged(float value)
    {
        _recordingEngine.SystemVolume = value;
    }

    private void OnTimerTick(object? sender, EventArgs e)
    {
        if (CurrentRecordingMode == RecordingMode.ScreenWithAudio)
        {
            // 화면 녹화 모드
            ElapsedTime = _screenRecordingEngine.ElapsedTime.ToString(@"hh\:mm\:ss");
            FrameCount = _screenRecordingEngine.FrameCount;
            CurrentFps = _screenRecordingEngine.CurrentFps;
            FileInfo = $"프레임: {FrameCount:N0} | FPS: {CurrentFps:F1}";
        }
        else
        {
            // 오디오 녹음 모드
            ElapsedTime = _recordingEngine.ElapsedTime.ToString(@"hh\:mm\:ss");

            // 파일 크기 업데이트 (파일 I/O 없이 추적된 바이트 수 사용)
            if (RecordingState == RecordingState.Recording)
            {
                var sizeMb = _recordingEngine.BytesWritten / (1024.0 * 1024);
                if (AutoSplitEnabled && _recordingEngine.SegmentIndex > 0)
                {
                    FileInfo = $"Part {_recordingEngine.SegmentIndex} | {sizeMb:F1} MB";
                }
                else
                {
                    FileInfo = $"파일: {sizeMb:F1} MB";
                }
            }
        }
    }

    // 레벨 업데이트 캐싱 (문자열 생성 최소화)
    private string _lastMicLevelDbText = "-∞ dB";
    private string _lastSystemLevelDbText = "-∞ dB";
    private int _lastMicLevelDbRounded = -100;
    // UI 업데이트 쓰로틀 (레벨 미터가 초당 수백번 호출되어 Dispatcher 큐 포화 방지)
    private long _lastLevelUiUpdateTicks;
    private const long LevelUiUpdateIntervalTicks = TimeSpan.TicksPerMillisecond * 50; // 50ms = 20fps
    private int _lastSystemLevelDbRounded = -100;

    private void OnLevelUpdated(object? sender, LevelEventArgs e)
    {
        // 50ms 쓰로틀: 오디오 콜백이 초당 수백번 호출되어도 UI는 20fps로 제한
        var nowTicks = DateTime.UtcNow.Ticks;
        if (nowTicks - _lastLevelUiUpdateTicks < LevelUiUpdateIntervalTicks)
            return;
        _lastLevelUiUpdateTicks = nowTicks;

        // 문자열 생성은 메인 스레드 외부에서 미리 처리
        int micDbRounded = e.MicLevelDb <= -60 ? -100 : (int)e.MicLevelDb;
        int sysDbRounded = e.SystemLevelDb <= -60 ? -100 : (int)e.SystemLevelDb;

        // dB 값이 변경된 경우에만 문자열 생성
        string micDbText = _lastMicLevelDbText;
        string sysDbText = _lastSystemLevelDbText;

        if (micDbRounded != _lastMicLevelDbRounded)
        {
            micDbText = micDbRounded == -100 ? "-∞ dB" : $"{micDbRounded} dB";
            _lastMicLevelDbRounded = micDbRounded;
            _lastMicLevelDbText = micDbText;
        }

        if (sysDbRounded != _lastSystemLevelDbRounded)
        {
            sysDbText = sysDbRounded == -100 ? "-∞ dB" : $"{sysDbRounded} dB";
            _lastSystemLevelDbRounded = sysDbRounded;
            _lastSystemLevelDbText = sysDbText;
        }

        // 캡처된 값으로 UI 업데이트 (Background 우선순위)
        var micLevel = e.MicLevel;
        var sysLevel = e.SystemLevel;

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            () =>
            {
                MicLevel = micLevel;
                SystemLevel = sysLevel;
                MicLevelDb = micDbText;
                SystemLevelDb = sysDbText;
            });
    }

    private void OnStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RecordingState = e.State;
            StartRecordingCommand.NotifyCanExecuteChanged();
            StopRecordingCommand.NotifyCanExecuteChanged();
            PauseRecordingCommand.NotifyCanExecuteChanged();
            ResumeRecordingCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnErrorOccurred(object? sender, RecordingErrorEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusText = e.Message;
        });
    }

    private void OnSegmentCompleted(object? sender, SegmentCompletedEventArgs e)
    {
        var targetFormat = _recordingEngine.TargetFormat;
        var segmentIndex = e.SegmentIndex;
        var sizeMb = e.FileSize / (1024.0 * 1024);

        // WAV 포맷이면 그냥 목록에 추가
        if (targetFormat == RecordingFormat.WAV)
        {
            System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                AddToRecentFiles(e.FilePath, e.Duration);
                StatusText = $"녹음 중... (Part {segmentIndex} 저장: {sizeMb:F1}MB, Part {segmentIndex + 1} 녹음 중)";
            });
            return;
        }

        // WAV 외 포맷은 백그라운드에서 변환 후 추가
        var localWavPath = e.FilePath;
        var localDuration = e.Duration;
        var audioFormat = targetFormat switch
        {
            RecordingFormat.FLAC => AudioFormat.FLAC,
            RecordingFormat.MP3_128 => AudioFormat.MP3_128,
            _ => AudioFormat.MP3_320
        };
        var targetExtension = targetFormat.GetExtension();
        var formatName = targetFormat.GetDisplayName();

        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            StatusText = $"녹음 중... (Part {segmentIndex} {formatName} 변환 중, Part {segmentIndex + 1} 녹음 중)";
        });

        var token = _segmentConvertCts.Token;
        var convertTask = Task.Run(async () =>
        {
            try
            {
                // WAV 파일이 닫힐 때까지 대기 (취소 감시)
                var waitStart = DateTime.Now;
                while ((DateTime.Now - waitStart).TotalSeconds < 10)
                {
                    token.ThrowIfCancellationRequested();
                    try
                    {
                        using var fs = new FileStream(localWavPath, FileMode.Open, FileAccess.Read, FileShare.None);
                        break;
                    }
                    catch (IOException) { await Task.Delay(500, token); }
                }

                var finalFilePath = Path.ChangeExtension(localWavPath, targetExtension);
                token.ThrowIfCancellationRequested();
                var success = await _conversionService.ConvertAsync(localWavPath, audioFormat, finalFilePath);

                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    if (success && File.Exists(finalFilePath))
                    {
                        try { File.Delete(localWavPath); } catch { }
                        AddToRecentFiles(finalFilePath, localDuration);
                        StatusText = $"녹음 중... (Part {segmentIndex} {formatName} 저장 완료, Part {segmentIndex + 1} 녹음 중)";
                    }
                    else
                    {
                        AddToRecentFiles(localWavPath, localDuration);
                        StatusText = $"녹음 중... (Part {segmentIndex} 변환 실패, WAV로 저장)";
                    }
                });
            }
            catch (OperationCanceledException)
            {
                // 앱 종료로 인한 취소 — 원본 WAV는 보존 (고아 파일 방지)
                System.Diagnostics.Debug.WriteLine($"[SegmentConvert] Part {segmentIndex} 취소됨. WAV 보존: {localWavPath}");
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine($"[SegmentConvert] 예외: {ex.Message}");
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
                {
                    AddToRecentFiles(localWavPath, localDuration);
                });
            }
        }, token);

        lock (_segmentConvertLock)
        {
            _segmentConvertTasks.Add(convertTask);
            // 주기적으로 완료된 Task 정리 (메모리 누수 방지)
            _segmentConvertTasks.RemoveAll(t => t.IsCompleted);
        }
    }

    private void OnScreenRecordingStateChanged(object? sender, RecordingStateChangedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            RecordingState = e.State;
            StartRecordingCommand.NotifyCanExecuteChanged();
            StopRecordingCommand.NotifyCanExecuteChanged();
            PauseRecordingCommand.NotifyCanExecuteChanged();
            ResumeRecordingCommand.NotifyCanExecuteChanged();
        });
    }

    private void OnScreenRecordingCompleted(object? sender, ScreenRecordingCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (e.Success)
            {
                var fileInfo = new FileInfo(e.OutputPath);
                var sizeMb = fileInfo.Length / (1024.0 * 1024);

                StatusText = e.Warning ?? $"녹화 완료: {sizeMb:F1} MB";
                FileInfo = $"저장됨: {Path.GetFileName(e.OutputPath)}";

                // 최근 파일 목록에 추가
                var recording = new RecordingInfo
                {
                    FilePath = e.OutputPath,
                    RecordedAt = DateTime.Now,
                    Duration = e.Duration,
                    FileSize = fileInfo.Length
                };

                RecentFiles.Insert(0, recording);
                if (RecentFiles.Count > 10)
                    RecentFiles.RemoveAt(RecentFiles.Count - 1);

                // 필터링된 목록 업데이트 (캐시 무효화)
                _cachedFilteredFiles = null;
                OnPropertyChanged(nameof(FilteredRecentFiles));
                OnPropertyChanged(nameof(HasNoFilteredFiles));

                // 최근 파일 목록 저장
                SaveRecentFiles();

                // 자동 동영상 압축 실행
                if (AutoVideoCompression != VideoCompressionQuality.None &&
                    _videoConversionService.IsFFmpegAvailable)
                {
                    _ = AutoCompressVideoAsync(e.OutputPath, recording);
                }
            }
            else
            {
                StatusText = e.ErrorMessage ?? "녹화 실패";
            }
        });
    }

    /// <summary>
    /// 자동 동영상 압축 (녹화 완료 후 백그라운드 실행)
    /// </summary>
    private async Task AutoCompressVideoAsync(string videoPath, RecordingInfo recording)
    {
        try
        {
            var quality = AutoVideoCompression == VideoCompressionQuality.High
                ? VideoQuality.High
                : VideoQuality.Normal;

            var qualityName = VideoConversionService.GetDisplayName(quality);
            StatusText = $"자동 압축 중... ({qualityName})";

            _videoConversionService.ProgressChanged += OnVideoCompressionProgress;

            var success = await _videoConversionService.CompressAsync(videoPath, quality);

            _videoConversionService.ProgressChanged -= OnVideoCompressionProgress;

            if (success)
            {
                // 압축 완료 후 파일 목록 갱신
                var compressedPath = Path.Combine(
                    Path.GetDirectoryName(videoPath) ?? "",
                    Path.GetFileNameWithoutExtension(videoPath) + "_compressed" + Path.GetExtension(videoPath));

                if (File.Exists(compressedPath))
                {
                    var compressedInfo = new FileInfo(compressedPath);
                    var originalSize = recording.FileSize;
                    var compressedSize = compressedInfo.Length;
                    var ratio = (1 - (double)compressedSize / originalSize) * 100;

                    StatusText = $"압축 완료! {originalSize / (1024.0 * 1024):F1}MB → {compressedSize / (1024.0 * 1024):F1}MB ({ratio:F0}% 감소)";

                    // 원본 삭제 후 압축 파일로 교체
                    try { File.Delete(videoPath); } catch { }

                    System.Windows.Application.Current?.Dispatcher.Invoke(() =>
                    {
                        // 목록에서 원본을 압축 파일로 교체
                        var existingIndex = -1;
                        for (int i = 0; i < RecentFiles.Count; i++)
                        {
                            if (RecentFiles[i].FilePath == recording.FilePath)
                            {
                                existingIndex = i;
                                break;
                            }
                        }

                        var compressedRecording = new RecordingInfo
                        {
                            FilePath = compressedPath,
                            RecordedAt = recording.RecordedAt,
                            Duration = recording.Duration,
                            FileSize = compressedSize
                        };

                        if (existingIndex >= 0)
                        {
                            RecentFiles[existingIndex] = compressedRecording;
                        }
                        else
                        {
                            RecentFiles.Insert(0, compressedRecording);
                            if (RecentFiles.Count > 10)
                                RecentFiles.RemoveAt(RecentFiles.Count - 1);
                        }

                        _cachedFilteredFiles = null;
                        OnPropertyChanged(nameof(FilteredRecentFiles));
                        SaveRecentFiles();
                    });
                }
            }
            else
            {
                StatusText = "자동 압축 실패";
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AutoCompress] 오류: {ex.Message}");
            StatusText = "자동 압축 오류";
        }
    }

    // RecordingState가 변경될 때 CanSwitchModeBinding 갱신
    partial void OnRecordingStateChanged(RecordingState value)
    {
        System.Diagnostics.Debug.WriteLine($"[ViewModel] RecordingState changed to: {value}");
        OnPropertyChanged(nameof(CanSwitchModeBinding));
        SwitchToAudioModeCommand.NotifyCanExecuteChanged();
        SwitchToScreenModeCommand.NotifyCanExecuteChanged();
    }

    partial void OnCurrentRecordingModeChanged(RecordingMode value)
    {
        // 모드 변경 시 캐시 무효화
        _cachedFilteredFiles = null;
        OnPropertyChanged(nameof(IsScreenRecordingMode));
        OnPropertyChanged(nameof(IsAudioOnlyMode));
        OnPropertyChanged(nameof(RecentFilesHeader));
        OnPropertyChanged(nameof(FilteredRecentFiles));
        OnPropertyChanged(nameof(EmptyFilesMessage));
        OnPropertyChanged(nameof(HasNoFilteredFiles));
    }

    #region 화면 녹화 커맨드

    [RelayCommand(CanExecute = nameof(CanSwitchMode))]
    private void SwitchToAudioMode()
    {
        CurrentRecordingMode = RecordingMode.AudioOnly;
        StatusText = "오디오 녹음 모드";
        // OnCurrentRecordingModeChanged에서 캐시 무효화 및 PropertyChanged 처리
    }

    private bool CanSwitchMode() => RecordingState == RecordingState.Stopped && !_isStoppingScreenRecording;

    // XAML 바인딩용 프로퍼티
    public bool CanSwitchModeBinding => CanSwitchMode();

    [RelayCommand(CanExecute = nameof(CanSwitchMode))]
    private void SwitchToScreenMode()
    {
        if (!_screenRecordingEngine.IsFFmpegAvailable)
        {
            StatusText = "FFmpeg가 필요합니다. ffmpeg.exe를 앱 폴더에 복사하세요.";
            return;
        }

        CurrentRecordingMode = RecordingMode.ScreenWithAudio;
        StatusText = "화면 녹화 모드";
        // OnCurrentRecordingModeChanged에서 캐시 무효화 및 PropertyChanged 처리
    }

    [RelayCommand]
    private void SelectFullScreen()
    {
        SelectedCaptureRegion = new CaptureRegion
        {
            Type = CaptureRegionType.FullScreen,
            MonitorIndex = 0
        };
        CaptureRegionText = "전체 화면";
        StatusText = "전체 화면 선택됨";
    }

    [RelayCommand]
    private void SelectMonitor()
    {
        var dialog = new Views.MonitorPickerDialog();
        dialog.Owner = System.Windows.Application.Current.MainWindow;

        if (dialog.ShowDialog() == true && dialog.SelectedRegion != null)
        {
            SelectedCaptureRegion = dialog.SelectedRegion;
            CaptureRegionText = dialog.SelectedMonitor?.DisplayName ?? $"모니터 {dialog.SelectedRegion.MonitorIndex + 1}";
            StatusText = $"모니터 선택됨: {CaptureRegionText}";
        }
    }

    [RelayCommand]
    private void SelectWindow()
    {
        var dialog = new Views.WindowPickerDialog();
        dialog.Owner = System.Windows.Application.Current.MainWindow;

        if (dialog.ShowDialog() == true && dialog.SelectedRegion != null)
        {
            SelectedCaptureRegion = dialog.SelectedRegion;
            CaptureRegionText = SelectedCaptureRegion.WindowTitle ?? "선택된 창";
            StatusText = $"창 선택됨: {CaptureRegionText}";
        }
    }

    [RelayCommand]
    private void SelectRegion()
    {
        // 메인 창 최소화
        var mainWindow = System.Windows.Application.Current.MainWindow;
        var previousState = mainWindow.WindowState;
        mainWindow.WindowState = System.Windows.WindowState.Minimized;

        // 잠시 대기 후 영역 선택 창 표시
        Task.Delay(300).ContinueWith(_ =>
        {
            System.Windows.Application.Current.Dispatcher.Invoke(() =>
            {
                var selector = new Views.RegionSelectorWindow();

                if (selector.ShowDialog() == true && selector.SelectedRegion != null)
                {
                    SelectedCaptureRegion = selector.SelectedRegion;
                    CaptureRegionText = $"영역 ({SelectedCaptureRegion.Bounds.Width}x{SelectedCaptureRegion.Bounds.Height})";
                    StatusText = $"영역 선택됨: {CaptureRegionText}";
                }

                // 메인 창 복원
                mainWindow.WindowState = previousState;
            });
        });
    }

    #endregion

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        // 진행 중인 세그먼트 변환 취소 + 최대 3초 대기 (고아 WAV 방지)
        try
        {
            _segmentConvertCts.Cancel();
            Task[] pending;
            lock (_segmentConvertLock)
            {
                pending = _segmentConvertTasks.ToArray();
            }
            if (pending.Length > 0)
            {
                try { Task.WaitAll(pending, TimeSpan.FromSeconds(3)); } catch { }
            }
        }
        catch { }
        finally
        {
            _segmentConvertCts.Dispose();
        }

        SaveSettings();

        // 타이머 정리
        _timer.Stop();
        _timer.Tick -= OnTimerTick;
        _playbackTimer.Stop();
        _playbackTimer.Tick -= OnPlaybackTimerTick;

        // 이벤트 핸들러 정리 (메모리 누수 방지)
        _recordingEngine.LevelUpdated -= OnLevelUpdated;
        _recordingEngine.StateChanged -= OnStateChanged;
        _recordingEngine.ErrorOccurred -= OnErrorOccurred;
        _recordingEngine.SegmentCompleted -= OnSegmentCompleted;
        _audioPlayer.PlaybackStopped -= OnPlaybackStopped;
        _screenRecordingEngine.LevelUpdated -= OnLevelUpdated;
        _screenRecordingEngine.StateChanged -= OnScreenRecordingStateChanged;
        _screenRecordingEngine.ErrorOccurred -= OnErrorOccurred;
        _screenRecordingEngine.RecordingCompleted -= OnScreenRecordingCompleted;

        // 리소스 정리
        _audioPlayer.Dispose();
        _recordingEngine.Dispose();
        _screenRecordingEngine.Dispose();
        _deviceManager.Dispose();
    }
}
