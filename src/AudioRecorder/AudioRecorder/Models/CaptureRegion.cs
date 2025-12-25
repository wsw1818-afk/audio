using System.Drawing;

namespace AudioRecorder.Models;

/// <summary>
/// 캡처 영역 정보
/// </summary>
public class CaptureRegion
{
    /// <summary>
    /// 캡처 유형
    /// </summary>
    public CaptureRegionType Type { get; set; } = CaptureRegionType.FullScreen;

    /// <summary>
    /// 캡처 영역 (사용자 지정 영역용)
    /// </summary>
    public Rectangle Bounds { get; set; }

    /// <summary>
    /// 선택된 창 핸들 (Window 유형용)
    /// </summary>
    public IntPtr WindowHandle { get; set; }

    /// <summary>
    /// 선택된 창 제목
    /// </summary>
    public string? WindowTitle { get; set; }

    /// <summary>
    /// 모니터 인덱스 (FullScreen용)
    /// </summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>
    /// 영역이 유효한지 확인
    /// </summary>
    public bool IsValid => Type switch
    {
        CaptureRegionType.FullScreen => true,
        CaptureRegionType.Window => WindowHandle != IntPtr.Zero,
        CaptureRegionType.CustomRegion => Bounds.Width > 0 && Bounds.Height > 0,
        _ => false
    };

    public override string ToString() => Type switch
    {
        CaptureRegionType.FullScreen => $"전체 화면 (모니터 {MonitorIndex + 1})",
        CaptureRegionType.Window => WindowTitle ?? "선택된 창",
        CaptureRegionType.CustomRegion => $"영역 ({Bounds.Width}x{Bounds.Height})",
        _ => "알 수 없음"
    };
}
