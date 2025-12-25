using System.Drawing;

namespace AudioRecorder.Models;

/// <summary>
/// 창 정보 (창 선택용)
/// </summary>
public class WindowInfo
{
    /// <summary>
    /// 창 핸들
    /// </summary>
    public IntPtr Handle { get; set; }

    /// <summary>
    /// 창 제목
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// 프로세스 이름
    /// </summary>
    public string ProcessName { get; set; } = string.Empty;

    /// <summary>
    /// 창 크기
    /// </summary>
    public Rectangle Bounds { get; set; }

    /// <summary>
    /// 썸네일 이미지 (미리보기용)
    /// </summary>
    public System.Windows.Media.ImageSource? Thumbnail { get; set; }

    /// <summary>
    /// 표시용 텍스트
    /// </summary>
    public string DisplayText => string.IsNullOrEmpty(Title)
        ? ProcessName
        : $"{Title} ({ProcessName})";

    public override string ToString() => DisplayText;
}
