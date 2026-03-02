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
    /// 비디오 CRF 값 (0-51, 낮을수록 고화질)
    /// </summary>
    public int VideoCrf { get; set; } = 23; // 기본 보통 화질

    /// <summary>
    /// 인코딩 프리셋 (ultrafast, superfast, veryfast, faster, fast, medium, slow, slower, veryslow)
    /// </summary>
    public string EncoderPreset { get; set; } = "fast";

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

    // ========== 1단계: UI 옵션 ==========

    /// <summary>
    /// 마우스 클릭 강조 표시 여부
    /// </summary>
    public bool HighlightMouseClicks { get; set; } = false;

    /// <summary>
    /// 녹화 시작 전 카운트다운 (초, 0이면 비활성화)
    /// </summary>
    public int CountdownSeconds { get; set; } = 0;

    /// <summary>
    /// 녹화 영역 테두리 표시 여부
    /// </summary>
    public bool ShowRecordingBorder { get; set; } = true;

    // ========== 2단계: FFmpeg 옵션 ==========

    /// <summary>
    /// 출력 해상도 (null이면 원본)
    /// </summary>
    public string? OutputResolution { get; set; } = null; // "1920x1080", "1280x720", "854x480"

    /// <summary>
    /// 오디오 비트레이트 (kbps)
    /// </summary>
    public int AudioBitrate { get; set; } = 192; // 128, 192, 320

    // ========== 3단계: 고급 기능 ==========

    /// <summary>
    /// 웹캠 오버레이 활성화 여부
    /// </summary>
    public bool EnableWebcamOverlay { get; set; } = false;

    /// <summary>
    /// 웹캠 위치 (TopLeft, TopRight, BottomLeft, BottomRight)
    /// </summary>
    public string WebcamPosition { get; set; } = "BottomRight";

    /// <summary>
    /// 웹캠 크기 (Small, Medium, Large)
    /// </summary>
    public string WebcamSize { get; set; } = "Small";

    /// <summary>
    /// 워터마크 텍스트 (null이면 비활성화)
    /// </summary>
    public string? WatermarkText { get; set; } = null;

    /// <summary>
    /// 워터마크 위치
    /// </summary>
    public string WatermarkPosition { get; set; } = "BottomRight";

    /// <summary>
    /// 예약 녹화 시작 시간 (null이면 비활성화)
    /// </summary>
    public DateTime? ScheduledStartTime { get; set; } = null;

    /// <summary>
    /// 예약 녹화 종료 시간 (null이면 수동 종료)
    /// </summary>
    public DateTime? ScheduledEndTime { get; set; } = null;

    // ========== 4단계: DRM 캡처 옵션 ==========

    /// <summary>
    /// Chrome DRM 캡처 모드 사용 여부
    /// </summary>
    public bool UseChromeDrmCapture { get; set; } = false;

    /// <summary>
    /// Chrome 디버그 포트 (기본 9222)
    /// </summary>
    public int ChromeDebugPort { get; set; } = 9222;

    /// <summary>
    /// Chrome 자동 실행 여부
    /// </summary>
    public bool AutoLaunchChrome { get; set; } = true;

    /// <summary>
    /// 캡처할 Chrome URL (null이면 현재 탭)
    /// </summary>
    public string? ChromeTargetUrl { get; set; } = null;

    /// <summary>
    /// 향상된 DXGI 캡처 사용 (DRM 우회 모드)
    /// </summary>
    public bool UseEnhancedDxgi { get; set; } = false;
}
