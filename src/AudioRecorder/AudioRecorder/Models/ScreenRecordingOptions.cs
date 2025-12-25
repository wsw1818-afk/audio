namespace AudioRecorder.Models;

/// <summary>
/// 화면 녹화 옵션
/// </summary>
public class ScreenRecordingOptions
{
    /// <summary>
    /// 캡처 영역 정보
    /// </summary>
    public CaptureRegion Region { get; set; } = new();

    /// <summary>
    /// 프레임 레이트 (FPS)
    /// </summary>
    public int FrameRate { get; set; } = 30;

    /// <summary>
    /// 비디오 비트레이트 (bps)
    /// </summary>
    public int VideoBitrate { get; set; } = 8_000_000; // 8 Mbps

    /// <summary>
    /// 비디오 출력 포맷
    /// </summary>
    public VideoFormat VideoFormat { get; set; } = VideoFormat.MP4_H264;

    /// <summary>
    /// 출력 디렉토리
    /// </summary>
    public string OutputDirectory { get; set; } = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);

    /// <summary>
    /// 마이크 녹음 포함 여부
    /// </summary>
    public bool IncludeMicrophone { get; set; } = true;

    /// <summary>
    /// 시스템 오디오 포함 여부
    /// </summary>
    public bool IncludeSystemAudio { get; set; } = true;

    /// <summary>
    /// 마이크 볼륨 (0.0 ~ 1.0)
    /// </summary>
    public float MicrophoneVolume { get; set; } = 0.8f;

    /// <summary>
    /// 시스템 오디오 볼륨 (0.0 ~ 1.0)
    /// </summary>
    public float SystemVolume { get; set; } = 1.0f;

    /// <summary>
    /// 마이크 장치 ID
    /// </summary>
    public string? MicrophoneDeviceId { get; set; }

    /// <summary>
    /// 출력 장치 ID
    /// </summary>
    public string? OutputDeviceId { get; set; }

    /// <summary>
    /// 마우스 커서 표시 여부
    /// </summary>
    public bool ShowMouseCursor { get; set; } = true;

    /// <summary>
    /// 하드웨어 인코딩 사용 여부 (NVENC, QSV 등)
    /// </summary>
    public bool UseHardwareEncoding { get; set; } = true;
}
