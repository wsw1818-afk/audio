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
    private readonly AudioPlayer _audioPlayer;
    private readonly Mp3ConversionService _mp3Service;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _playbackTimer;
    private readonly AppSettings _settings;
    private bool _disposed;

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
        _audioPlayer = new AudioPlayer();
        _mp3Service = new Mp3ConversionService();
        _settings = AppSettings.Load();

        // 설정 적용
        _outputDirectory = _settings.OutputDirectory;
        _recordMicrophone = _settings.RecordMicrophone;
        _recordSystemAudio = _settings.RecordSystem;
        _micVolume = _settings.MicrophoneVolume;
        _systemVolume = _settings.SystemVolume;

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

        // 이벤트 연결
        _recordingEngine.LevelUpdated += OnLevelUpdated;
        _recordingEngine.StateChanged += OnStateChanged;
        _recordingEngine.ErrorOccurred += OnErrorOccurred;
        _audioPlayer.PlaybackStopped += OnPlaybackStopped;

        // 장치 목록 로드
        LoadDevices();

        // 최근 파일 목록 로드
        LoadRecentFiles();
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
            var options = new RecordingOptions
            {
                RecordMicrophone = RecordMicrophone,
                RecordSystemAudio = RecordSystemAudio,
                MicrophoneDeviceId = SelectedInputDevice?.Id,
                OutputDeviceId = SelectedOutputDevice?.Id,
                MicrophoneVolume = MicVolume,
                SystemVolume = SystemVolume,
                OutputDirectory = OutputDirectory
            };

            _recordingEngine.Start(options);
            _timer.Start();
            StatusText = "녹음 중...";
        }
        catch (Exception ex)
        {
            StatusText = $"녹음 시작 실패: {ex.Message}";
        }
    }

    private bool CanStartRecording() => RecordingState == RecordingState.Stopped;

    [RelayCommand(CanExecute = nameof(CanStopRecording))]
    private void StopRecording()
    {
        try
        {
            var filePath = _recordingEngine.CurrentFilePath;
            _recordingEngine.Stop();
            _timer.Stop();
            StatusText = "녹음 완료";

            // 최근 파일 목록에 추가
            if (File.Exists(filePath))
            {
                var fileInfo = new FileInfo(filePath);
                var recording = new RecordingInfo
                {
                    FilePath = filePath,
                    RecordedAt = DateTime.Now,
                    Duration = _recordingEngine.ElapsedTime,
                    FileSize = fileInfo.Length
                };

                RecentFiles.Insert(0, recording);
                if (RecentFiles.Count > 10)
                    RecentFiles.RemoveAt(RecentFiles.Count - 1);

                var sizeKb = recording.FileSize / 1024.0;
                FileInfo = $"저장됨: {recording.FileName} ({sizeKb:F1} KB)";
            }
        }
        catch (Exception ex)
        {
            StatusText = $"녹음 중지 실패: {ex.Message}";
        }
    }

    private bool CanStopRecording() => RecordingState != RecordingState.Stopped;

    [RelayCommand(CanExecute = nameof(CanPauseRecording))]
    private void PauseRecording()
    {
        _recordingEngine.Pause();
        StatusText = "일시정지";
    }

    private bool CanPauseRecording() => RecordingState == RecordingState.Recording;

    [RelayCommand(CanExecute = nameof(CanResumeRecording))]
    private void ResumeRecording()
    {
        _recordingEngine.Resume();
        StatusText = "녹음 중...";
    }

    private bool CanResumeRecording() => RecordingState == RecordingState.Paused;

    [RelayCommand]
    private void RefreshDevices()
    {
        LoadDevices();
        StatusText = "장치 목록 새로고침 완료";
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
        if (recording == null || !File.Exists(recording.FilePath))
            return;

        if (!recording.FilePath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            StatusText = "WAV 파일만 MP3로 변환할 수 있습니다.";
            return;
        }

        if (!_mp3Service.IsFFmpegAvailable)
        {
            StatusText = "FFmpeg가 설치되어 있지 않습니다. ffmpeg.exe를 앱 폴더에 복사하세요.";
            return;
        }

        StatusText = $"MP3 변환 중: {recording.FileName}...";

        _mp3Service.ConversionCompleted += OnMp3ConversionCompleted;
        await _mp3Service.ConvertToMp3Async(recording.FilePath);
        _mp3Service.ConversionCompleted -= OnMp3ConversionCompleted;
    }

    private void OnMp3ConversionCompleted(object? sender, Mp3ConversionCompletedEventArgs e)
    {
        System.Windows.Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            if (e.Success)
            {
                var savedMb = (e.OriginalSize - e.ConvertedSize) / (1024.0 * 1024);
                StatusText = $"MP3 변환 완료! ({e.CompressionRatio:F0}% 압축, {savedMb:F1}MB 절약)";
            }
            else
            {
                StatusText = e.ErrorMessage ?? "MP3 변환 실패";
            }
        });
    }

    public bool IsFFmpegAvailable => _mp3Service.IsFFmpegAvailable;

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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SaveSettings();

        _timer.Stop();
        _playbackTimer.Stop();
        _audioPlayer.Dispose();
        _recordingEngine.Dispose();
    }
}
