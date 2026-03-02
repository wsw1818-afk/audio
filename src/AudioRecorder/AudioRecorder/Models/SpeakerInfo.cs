namespace AudioRecorder.Models;

/// <summary>
/// 화자 정보
/// </summary>
public class SpeakerInfo
{
    /// <summary>
    /// 화자 ID (0부터)
    /// </summary>
    public int Id { get; set; }

    /// <summary>
    /// 화자 레이블 ("화자 1", "화자 2" 등)
    /// </summary>
    public string Label { get; set; } = "";

    /// <summary>
    /// 총 발화 시간
    /// </summary>
    public TimeSpan TotalSpeakingTime { get; set; }

    /// <summary>
    /// 발화 구간 수
    /// </summary>
    public int SegmentCount { get; set; }

    /// <summary>
    /// UI 표시용 색상 (hex)
    /// </summary>
    public string Color { get; set; } = "#FFB347";

    /// <summary>
    /// 기본 화자 색상 팔레트
    /// </summary>
    public static readonly string[] DefaultColors =
    {
        "#FFB347",  // 주황
        "#87CEEB",  // 하늘
        "#98FB98",  // 연두
        "#DDA0DD",  // 보라
        "#F0E68C",  // 노랑
        "#FF6B6B",  // 빨강
        "#20B2AA",  // 청록
        "#FFA07A",  // 연어
    };

    /// <summary>
    /// ID 기반 기본 색상 반환
    /// </summary>
    public static string GetDefaultColor(int speakerId)
    {
        return DefaultColors[speakerId % DefaultColors.Length];
    }
}
