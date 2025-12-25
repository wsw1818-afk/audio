namespace AudioRecorder.Models;

/// <summary>
/// 녹음 저장 포맷
/// </summary>
public enum RecordingFormat
{
    /// <summary>WAV - 무손실 원본 (가장 큰 파일)</summary>
    WAV,

    /// <summary>FLAC - 무손실 압축 (50-60% 크기 감소)</summary>
    FLAC,

    /// <summary>MP3 320kbps - 고품질 손실 압축</summary>
    MP3_320
}

/// <summary>
/// RecordingFormat 확장 메서드
/// </summary>
public static class RecordingFormatExtensions
{
    public static string GetDisplayName(this RecordingFormat format) => format switch
    {
        RecordingFormat.WAV => "WAV (무손실)",
        RecordingFormat.FLAC => "FLAC (무손실 압축)",
        RecordingFormat.MP3_320 => "MP3 320kbps",
        _ => format.ToString()
    };

    public static string GetExtension(this RecordingFormat format) => format switch
    {
        RecordingFormat.WAV => ".wav",
        RecordingFormat.FLAC => ".flac",
        RecordingFormat.MP3_320 => ".mp3",
        _ => ".wav"
    };

    public static string GetDescription(this RecordingFormat format) => format switch
    {
        RecordingFormat.WAV => "가장 높은 음질, 큰 파일",
        RecordingFormat.FLAC => "무손실 압축, 중간 크기",
        RecordingFormat.MP3_320 => "고품질 압축, 작은 파일",
        _ => ""
    };
}
