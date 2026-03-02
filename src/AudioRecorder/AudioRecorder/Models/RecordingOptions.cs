using System.IO;

namespace AudioRecorder.Models;

/// <summary>
/// 녹음 옵션 설정
/// </summary>
public class RecordingOptions
{
    public bool RecordMicrophone { get; set; } = true;
    public bool RecordSystemAudio { get; set; } = true;
    public string? MicrophoneDeviceId { get; set; }
    public string? OutputDeviceId { get; set; }
    public float MicrophoneVolume { get; set; } = 1.0f;
    public float SystemVolume { get; set; } = 1.0f;
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
    public int SampleRate { get; set; } = 48000;
    public int Channels { get; set; } = 2;
    public int BitsPerSample { get; set; } = 16;
    public RecordingFormat Format { get; set; } = RecordingFormat.WAV;

    // 자동 분할 녹음 설정
    public bool AutoSplitEnabled { get; set; } = false;
    public int AutoSplitIntervalMinutes { get; set; } = 60;

    // 파일명 템플릿 설정
    // 사용 가능한 변수: {date}, {time}, {datetime}, {title}, {n}
    public string FileNameTemplate { get; set; } = "Recording_{datetime}";
    public string Title { get; set; } = "";

    public string GenerateFileName()
    {
        var extension = Format.GetExtension();
        var fileName = GenerateFileNameFromTemplate();
        return fileName + extension;
    }

    private string GenerateFileNameFromTemplate()
    {
        var now = DateTime.Now;
        var result = FileNameTemplate
            .Replace("{date}", now.ToString("yyyy-MM-dd"))
            .Replace("{time}", now.ToString("HHmmss"))
            .Replace("{datetime}", now.ToString("yyyyMMdd_HHmmss"))
            .Replace("{title}", SanitizeFileName(Title))
            .Replace("{n}", ""); // 순번은 나중에 처리

        // 제목이 비어있으면 {title} 부분 정리
        if (string.IsNullOrWhiteSpace(Title))
        {
            result = result.Replace("__", "_").Replace("_.", ".");
        }

        // 파일명에 사용할 수 없는 문자 제거
        return SanitizeFileName(result);
    }

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        var invalid = Path.GetInvalidFileNameChars();
        return string.Join("", name.Split(invalid));
    }

    /// <summary>
    /// 임시 WAV 파일명 생성 (FLAC/MP3 변환용)
    /// </summary>
    public string GenerateTempWavFileName()
    {
        return $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}_temp.wav";
    }

    public string GetFullPath()
    {
        return Path.Combine(OutputDirectory, GenerateFileName());
    }
}
