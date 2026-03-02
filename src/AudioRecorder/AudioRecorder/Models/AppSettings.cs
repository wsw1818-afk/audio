using System;
using System.IO;
using System.Text.Json;

namespace AudioRecorder.Models;

/// <summary>
/// 자동 동영상 압축 품질
/// </summary>
public enum VideoCompressionQuality
{
    None,   // 압축 안함
    High,   // 최고 품질 (CRF 23)
    Normal  // 보통 품질 (CRF 28)
}

public class AppSettings
{
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public string? LastMicrophoneId { get; set; }
    public string? LastSystemDeviceId { get; set; }
    public float MicrophoneVolume { get; set; } = 1.0f;
    public float SystemVolume { get; set; } = 1.0f;
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystem { get; set; } = true;
    public int MaxRecentFiles { get; set; } = 20;
    public RecordingFormat RecordingFormat { get; set; } = RecordingFormat.WAV;
    public CloseAction CloseAction { get; set; } = CloseAction.MinimizeToTray;

    // 자동 동영상 압축 설정
    public bool AutoCompressVideo { get; set; } = false;
    public VideoCompressionQuality VideoCompressionQuality { get; set; } = VideoCompressionQuality.None;

    // 화면 녹화 설정
    public string VideoQuality { get; set; } = "고화질";
    public bool ShowMouseCursor { get; set; } = true;
    public bool HighlightMouseClicks { get; set; } = false;
    public int CountdownSeconds { get; set; } = 3;
    public bool ShowRecordingBorder { get; set; } = true;
    public string Resolution { get; set; } = "원본";
    public string AudioBitrate { get; set; } = "192 kbps";

    // 자동 분할 녹음 설정
    public bool AutoSplitEnabled { get; set; } = false;
    public int AutoSplitIntervalMinutes { get; set; } = 60;

    // 파일명 템플릿 설정
    // 사용 가능한 변수: {date}, {time}, {datetime}, {title}, {n}
    // 예: "회의록_{date}_{title}" → "회의록_2026-01-21_주간회의.mp3"
    public string FileNameTemplate { get; set; } = "Recording_{datetime}";
    public string DefaultTitle { get; set; } = "";

    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "AudioRecorder",
        "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();

                // 역직렬화 후 범위 검증 (조작된 설정 파일 방어)
                settings.MicrophoneVolume = Math.Clamp(settings.MicrophoneVolume, 0f, 2f);
                settings.SystemVolume = Math.Clamp(settings.SystemVolume, 0f, 2f);
                settings.MaxRecentFiles = Math.Clamp(settings.MaxRecentFiles, 1, 100);
                settings.CountdownSeconds = Math.Clamp(settings.CountdownSeconds, 0, 60);
                settings.AutoSplitIntervalMinutes = Math.Clamp(settings.AutoSplitIntervalMinutes, 10, 480);

                return settings;
            }
        }
        catch (Exception)
        {
            // 설정 파일 손상 시 기본값 사용
        }

        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions { WriteIndented = true };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception)
        {
            // 저장 실패 시 무시
        }
    }
}
