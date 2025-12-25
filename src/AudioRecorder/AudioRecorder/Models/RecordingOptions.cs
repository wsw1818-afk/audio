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

    public string GenerateFileName()
    {
        var extension = Format.GetExtension();
        return $"Recording_{DateTime.Now:yyyyMMdd_HHmmss}{extension}";
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
