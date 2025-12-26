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
    private readonly DeviceManager _deviceManager;
    private readonly RecordingEngine _recordingEngine;
    private readonly ScreenRecordingEngine _screenRecordingEngine;
    private readonly AudioPlayer _audioPlayer;
    private readonly AudioConversionService _conversionService;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _playbackTimer;
    private readonly AppSettings _settings;
    private bool _disposed;
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
        RecordingFormat.MP3_320
    };

    // 화면 녹화 모드
    [ObservableProperty]
    private RecordingMode _currentRecordingMode = RecordingMode.AudioOnly;

    [ObservableProperty]
    private CaptureRegion? _selectedCaptureRegion;

    [ObservableProperty]
    private VideoFormat _selectedVideoFormat = VideoFormat.MP4_H264;

    [ObservableProperty]
    private int _selectedFrameRate = 30;

    [ObservableProperty]
    private string _captureRegionText = "전체 화면";

    [ObservableProperty]
    private bool _showMouseCursor = true;

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
    public bool IsScreenRecordingAvailable => _screenRecordingEngine.IsFFmpegAvailable;

    // 재생 상태
    [ObservableProperty]
    private bool _isPlaying;

    [ObservableProperty]
    private string _playbackTime = "00:00 / 00:00";

    [ObservableProperty]
    private double _playbackPosition;

    [ObservableProperty]
    private double _playbackDuration = 1;

    public MainViewModel()
    {
        _deviceManager = new DeviceManager();
        _recordingEngine = new RecordingEngine(_deviceManager);
        _screenRecordingEngine = new ScreenRecordingEngine(_deviceManager);
        _audioPlayer = new AudioPlayer();
        _conversionService = new AudioConversionService();
        _settings = AppSettings.Load();

        // 설정 적용
        _outputDirectory = _settings.OutputDirectory;
        _recordMicrophone = _settings.RecordMicrophone;
        _recordSystemAudio = _settings.RecordSystem;
        _micVolume = _settings.MicrophoneVolume;
        _systemVolume = _settings.SystemVolume;
        _selectedRecordingFormat = _settings.RecordingFormat;

        // 녹음 타이머 설정 (UI 업데이트용)
        _timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(50)
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
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;

        // 화면 녹화 이벤트 연결
        _screenRecordingEngine.LevelUpdated += OnLevelUpdated;
        _screenRecordingEngine.StateChanged += OnScreenRecordingStateChanged;
        _screenRecordingEngine.ErrorOccurred += OnErrorOccurred;
        _screenRecordingEngine.RecordingCompleted += OnScreenRecordingCompleted;

        // 기본 캡처 영역 설정 (전체 화면)
        _selectedCaptureRegion = new CaptureRegion { Type = CaptureRegionType.FullScreen };

        // 장치 목록 로드
        LoadDevices();

        // 최근 파일 목록 로드
        LoadRecentFiles();

        // 초기 모드 UI 업데이트 (바인딩 초기화를 위해)
        OnPropertyChanged(nameof(IsAudioOnlyMode));
        OnPropertyChanged(nameof(IsScreenRecordingMode));
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
        if (!RecordMicrophone && !RecordSystemAudio)
        {
            StatusText = "마이크 또는 시스템 오디오 중 하나 이상을 선택하세요";
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
            Format = SelectedRecordingFormat
        };

        _recordingEngine.Start(options);
        _timer.Start();

        // 수동으로 상태 업데이트
        RecordingState = RecordingState.Recording;
        var formatName = SelectedRecordingFormat.GetDisplayName();
        StatusText = $"녹음 중... ({formatName})";
    }

    private void StartScreenRecording()
    {
        System.Diagnostics.Debug.WriteLine($"[ViewModel] StartScreenRecording 호출됨");

        if (SelectedCaptureRegion == null)
        {
            SelectedCaptureRegion = new CaptureRegion { Type = CaptureRegionType.FullScreen };
        }

        var options = new ScreenRecordingOptions
        {
            Region = SelectedCaptureRegion,
            FrameRate = SelectedFrameRate,
            VideoFormat = SelectedVideoFormat,
            OutputDirectory = OutputDirectory,
            IncludeMicrophone = RecordMicrophone,
            IncludeSystemAudio = RecordSystemAudio,
            MicrophoneVolume = MicVolume,
            SystemVolume = SystemVolume,
            MicrophoneDeviceId = SelectedInputDevice?.Id,
            OutputDeviceId = SelectedOutputDevice?.Id,
            ShowMouseCursor = ShowMouseCursor
        };

        System.Diagnostics.Debug.WriteLine($"[ViewModel] 화면 녹화 옵션 - 영역: {options.Region.Type}, FPS: {options.FrameRate}, 출력: {options.OutputDirectory}");

        try
        {
            _screenRecordingEngine.Start(options);
            _timer.Start();

            // 수동으로 상태 업데이트 (이벤트가 비동기로 처리될 수 있음)
            RecordingState = RecordingState.Recording;
            StatusText = $"화면 녹화 중... ({CaptureRegionText}, {SelectedFrameRate}fps)";
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

    private async Task StopAudioRecordingAsync()
    {
        var wavFilePath = _recordingEngine.CurrentFilePath;
        var targetFormat = _recordingEngine.TargetFormat;
        var duration = _recordingEngine.ElapsedTime;

        _recordingEngine.Stop();

        if (!File.Exists(wavFilePath))
        {
            StatusText = "녹음 파일을 찾을 수 없습니다.";
            return;
        }

        string finalFilePath = wavFilePath;

        // WAV가 아닌 포맷이면 변환 수행
        if (targetFormat != RecordingFormat.WAV)
        {
            var formatName = targetFormat.GetDisplayName();
            StatusText = $"{formatName}로 변환 중...";

            var audioFormat = targetFormat == RecordingFormat.FLAC
                ? AudioFormat.FLAC
                : AudioFormat.MP3_320;

            var targetExtension = targetFormat.GetExtension();
            finalFilePath = Path.ChangeExtension(wavFilePath, targetExtension);

            var success = await _conversionService.ConvertAsync(wavFilePath, audioFormat, finalFilePath);

            if (success && File.Exists(finalFilePath))
            {
                // 변환 성공 시 원본 WAV 삭제
                try { File.Delete(wavFilePath); } catch { }
                StatusText = $"{formatName} 변환 완료";
            }
            else
            {
                // 변환 실패 시 WAV 유지
                finalFilePath = wavFilePath;
                StatusText = "변환 실패, WAV로 저장됨";
            }
        }
        else
        {
            StatusText = "녹음 완료";
        }

        // 최근 파일 목록에 추가
        if (File.Exists(finalFilePath))
        {
            var fileInfo = new FileInfo(finalFilePath);
            var recording = new RecordingInfo
            {
                FilePath = finalFilePath,
                RecordedAt = DateTime.Now,
                Duration = duration,
                FileSize = fileInfo.Length
            };

            RecentFiles.Insert(0, recording);
            if (RecentFiles.Count > 10)
                RecentFiles.RemoveAt(RecentFiles.Count - 1);

            var sizeMb = recording.FileSize / (1024.0 * 1024);
            if (sizeMb >= 1)
                FileInfo = $"저장됨: {recording.FileName} ({sizeMb:F1} MB)";
            else
                FileInfo = $"저장됨: {recording.FileName} ({recording.FileSize / 1024.0:F1} KB)";
        }
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
    }

    [RelayCommand]
    private void PlayWithDefaultApp(RecordingInfo? recording)
    {
        if (recording != null && File.Exists(recording.FilePath))
        {
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
        await _conversionService.ConvertAsync(recording.FilePath, format);
        _conversionService.ConversionCompleted -= OnConversionCompleted;
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

            // 파일 크기 업데이트
            if (RecordingState == RecordingState.Recording && File.Exists(_recordingEngine.CurrentFilePath))
            {
                try
                {
                    var fileInfo = new FileInfo(_recordingEngine.CurrentFilePath);
                    var sizeMb = fileInfo.Length / (1024.0 * 1024);
                    FileInfo = $"파일: {sizeMb:F1} MB";
                }
                catch { }
            }
        }
    }

    private void OnLevelUpdated(object? sender, LevelEventArgs e)
    {
        // UI 스레드에서 업데이트
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            MicLevel = e.MicLevel;
            SystemLevel = e.SystemLevel;
            MicLevelDb = e.MicLevelDb <= -60 ? "-∞ dB" : $"{e.MicLevelDb:F0} dB";
            SystemLevelDb = e.SystemLevelDb <= -60 ? "-∞ dB" : $"{e.SystemLevelDb:F0} dB";
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
            }
            else
            {
                StatusText = e.ErrorMessage ?? "녹화 실패";
            }
        });
    }

    partial void OnCurrentRecordingModeChanged(RecordingMode value)
    {
        OnPropertyChanged(nameof(IsScreenRecordingMode));
        OnPropertyChanged(nameof(IsAudioOnlyMode));
    }

    #region 화면 녹화 커맨드

    [RelayCommand(CanExecute = nameof(CanSwitchMode))]
    private void SwitchToAudioMode()
    {
        CurrentRecordingMode = RecordingMode.AudioOnly;
        StatusText = "오디오 녹음 모드";
        OnPropertyChanged(nameof(IsScreenRecordingMode));
        OnPropertyChanged(nameof(IsAudioOnlyMode));
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
        OnPropertyChanged(nameof(IsScreenRecordingMode));
        OnPropertyChanged(nameof(IsAudioOnlyMode));
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

        SaveSettings();

        _timer.Stop();
        _playbackTimer.Stop();
        _audioPlayer.Dispose();
        _recordingEngine.Dispose();
        _screenRecordingEngine.Dispose();
    }
}
