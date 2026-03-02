using System;

namespace AudioRecorder.Models;

/// <summary>
/// 녹음 중 북마크 정보
/// </summary>
public class BookmarkInfo
{
    public TimeSpan Position { get; set; }
    public string Label { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string PositionText => $"{Position:mm\\:ss}";
    public string DisplayText => string.IsNullOrEmpty(Label)
        ? $"📌 {PositionText}"
        : $"📌 {PositionText} - {Label}";
}
