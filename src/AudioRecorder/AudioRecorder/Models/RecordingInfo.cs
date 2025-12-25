using System.ComponentModel;
using System.IO;

namespace AudioRecorder.Models;

/// <summary>
/// 녹음 파일 정보 - XAML 바인딩 전용
/// WPF TwoWay 바인딩 문제 방지를 위해 모든 속성에 Bindable(false) 또는 읽기 전용 설정
/// </summary>
public class RecordingInfo
{
    // 내부 데이터 (바인딩에서 제외)
    [Bindable(false)]
    [Browsable(false)]
    public string FilePath { get; set; } = string.Empty;

    [Bindable(false)]
    [Browsable(false)]
    public DateTime RecordedAt { get; set; }

    [Bindable(false)]
    [Browsable(false)]
    public TimeSpan Duration { get; set; }

    [Bindable(false)]
    [Browsable(false)]
    public long FileSize { get; set; }

    // XAML 바인딩용 속성 (읽기 전용)
    [Bindable(BindableSupport.Yes, BindingDirection.OneWay)]
    public string FileName => Path.GetFileName(FilePath);

    [Bindable(BindableSupport.Yes, BindingDirection.OneWay)]
    public string FileDetails
    {
        get
        {
            var dateStr = RecordedAt.ToString("yyyy-MM-dd HH:mm");
            var durationStr = Duration.ToString(@"mm\:ss");
            string sizeStr;
            if (FileSize < 1024) sizeStr = $"{FileSize} B";
            else if (FileSize < 1024 * 1024) sizeStr = $"{FileSize / 1024.0:F1} KB";
            else sizeStr = $"{FileSize / (1024.0 * 1024):F1} MB";

            return $"{dateStr} | {durationStr} | {sizeStr}";
        }
    }
}
