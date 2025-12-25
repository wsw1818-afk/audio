namespace AudioRecorder.Models;

/// <summary>
/// 녹화 모드 (오디오 전용 / 화면 녹화)
/// </summary>
public enum RecordingMode
{
    /// <summary>
    /// 오디오만 녹음 (기존 기능)
    /// </summary>
    AudioOnly,

    /// <summary>
    /// 화면 + 오디오 녹화
    /// </summary>
    ScreenWithAudio
}

/// <summary>
/// 화면 캡처 영역 유형
/// </summary>
public enum CaptureRegionType
{
    /// <summary>
    /// 전체 화면 (주 모니터)
    /// </summary>
    FullScreen,

    /// <summary>
    /// 특정 창
    /// </summary>
    Window,

    /// <summary>
    /// 사용자 지정 영역 (드래그 선택)
    /// </summary>
    CustomRegion
}

/// <summary>
/// 비디오 출력 포맷
/// </summary>
public enum VideoFormat
{
    /// <summary>
    /// MP4 (H.264 코덱) - 가장 호환성이 좋음
    /// </summary>
    MP4_H264,

    /// <summary>
    /// WebM (VP9 코덱) - 웹 친화적
    /// </summary>
    WebM_VP9,

    /// <summary>
    /// MKV (H.264 코덱) - 유연한 컨테이너
    /// </summary>
    MKV_H264
}

public static class VideoFormatExtensions
{
    public static string GetDisplayName(this VideoFormat format) => format switch
    {
        VideoFormat.MP4_H264 => "MP4 (H.264)",
        VideoFormat.WebM_VP9 => "WebM (VP9)",
        VideoFormat.MKV_H264 => "MKV (H.264)",
        _ => format.ToString()
    };

    public static string GetExtension(this VideoFormat format) => format switch
    {
        VideoFormat.MP4_H264 => ".mp4",
        VideoFormat.WebM_VP9 => ".webm",
        VideoFormat.MKV_H264 => ".mkv",
        _ => ".mp4"
    };

    public static string GetFFmpegCodec(this VideoFormat format) => format switch
    {
        VideoFormat.MP4_H264 => "libx264",
        VideoFormat.WebM_VP9 => "libvpx-vp9",
        VideoFormat.MKV_H264 => "libx264",
        _ => "libx264"
    };
}
