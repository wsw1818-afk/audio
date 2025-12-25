using System;
using System.IO;
using System.Text.Json;

namespace AudioRecorder.Models;

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
                return JsonSerializer.Deserialize<AppSettings>(json) ?? new AppSettings();
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
