using System.Text;

namespace AudioRecorder.Models;

/// <summary>
/// STT 변환 전체 결과
/// </summary>
public class TranscriptionResult
{
    /// <summary>
    /// 원본 오디오 파일 경로
    /// </summary>
    public string SourceFilePath { get; set; } = "";

    /// <summary>
    /// 감지된 언어 코드 ("ko", "en", "ja" 등)
    /// </summary>
    public string Language { get; set; } = "ko";

    /// <summary>
    /// 오디오 전체 길이
    /// </summary>
    public TimeSpan Duration { get; set; }

    /// <summary>
    /// 구간별 텍스트 목록
    /// </summary>
    public List<TranscriptionSegment> Segments { get; set; } = new();

    /// <summary>
    /// 감지된 화자 목록 ("화자 1", "화자 2" 등)
    /// </summary>
    public List<string> DetectedSpeakers { get; set; } = new();

    /// <summary>
    /// 사용된 엔진 ("whisper-local", "whisper-api")
    /// </summary>
    public string Engine { get; set; } = "";

    /// <summary>
    /// 사용된 모델 ("small", "medium" 등)
    /// </summary>
    public string Model { get; set; } = "";

    /// <summary>
    /// 변환 소요 시간
    /// </summary>
    public TimeSpan ProcessingTime { get; set; }

    /// <summary>
    /// 변환 완료 시각
    /// </summary>
    public DateTime CompletedAt { get; set; } = DateTime.Now;

    /// <summary>
    /// 전체 텍스트 (타임스탬프/화자 없이)
    /// </summary>
    public string FullText
    {
        get
        {
            var sb = new StringBuilder();
            foreach (var seg in Segments)
            {
                if (sb.Length > 0) sb.Append(' ');
                sb.Append(seg.Text.Trim());
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// 화자+타임스탬프 포맷 텍스트
    /// 짧은 세그먼트를 병합하여 자연스러운 문장 단위로 출력
    /// </summary>
    public string FormattedText
    {
        get
        {
            // 1단계: 짧은 세그먼트를 병합하여 문장 단위로 묶기
            var merged = MergeShortSegments(Segments);

            // 2단계: 포맷팅
            var sb = new StringBuilder();
            string? lastSpeaker = null;

            foreach (var seg in merged)
            {
                var timestamp = $"[{seg.StartTime:hh\\:mm\\:ss}]";
                var speaker = seg.SpeakerLabel ?? "";

                if (speaker != lastSpeaker && !string.IsNullOrEmpty(speaker))
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine($"{timestamp} {speaker}:");
                    sb.AppendLine($"  {seg.Text.Trim()}");
                    lastSpeaker = speaker;
                }
                else
                {
                    sb.AppendLine($"{timestamp} {seg.Text.Trim()}");
                }
            }

            return sb.ToString();
        }
    }

    /// <summary>
    /// 짧은 세그먼트를 이전 세그먼트에 병합 (문장부호 또는 시간 간격 기준)
    /// </summary>
    private static List<TranscriptionSegment> MergeShortSegments(List<TranscriptionSegment> segments)
    {
        if (segments.Count == 0) return segments;

        var merged = new List<TranscriptionSegment>();
        TranscriptionSegment? current = null;

        foreach (var seg in segments)
        {
            var text = seg.Text.Trim();
            if (string.IsNullOrEmpty(text)) continue;

            if (current == null)
            {
                current = new TranscriptionSegment
                {
                    Index = seg.Index,
                    StartTime = seg.StartTime,
                    EndTime = seg.EndTime,
                    Text = text,
                    SpeakerLabel = seg.SpeakerLabel,
                    Confidence = seg.Confidence,
                };
                continue;
            }

            // 같은 화자이고 시간 간격이 2초 이내면 병합
            var gap = seg.StartTime - current.EndTime;
            var sameSpeaker = seg.SpeakerLabel == current.SpeakerLabel;
            var shortGap = gap.TotalSeconds <= 2.0;

            // 현재 세그먼트가 문장 종결(. ! ? 등)로 끝나면 새 세그먼트 시작
            var currentEndsWithPunctuation = current.Text.Length > 0 &&
                ".!?。！？".Contains(current.Text[^1]);

            if (sameSpeaker && shortGap && !currentEndsWithPunctuation)
            {
                // 병합: 텍스트 이어붙이기
                current.Text = current.Text + " " + text;
                current.EndTime = seg.EndTime;
            }
            else
            {
                // 새 세그먼트
                merged.Add(current);
                current = new TranscriptionSegment
                {
                    Index = seg.Index,
                    StartTime = seg.StartTime,
                    EndTime = seg.EndTime,
                    Text = text,
                    SpeakerLabel = seg.SpeakerLabel,
                    Confidence = seg.Confidence,
                };
            }
        }

        if (current != null) merged.Add(current);
        return merged;
    }
}

/// <summary>
/// 구간별 텍스트 (문장 단위)
/// </summary>
public class TranscriptionSegment
{
    /// <summary>
    /// 구간 인덱스 (0부터)
    /// </summary>
    public int Index { get; set; }

    /// <summary>
    /// 시작 시간
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// 종료 시간
    /// </summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>
    /// 텍스트 내용
    /// </summary>
    public string Text { get; set; } = "";

    /// <summary>
    /// 화자 레이블 ("화자 1", "화자 2" 등, null이면 미분류)
    /// </summary>
    public string? SpeakerLabel { get; set; }

    /// <summary>
    /// 인식 신뢰도 (0.0 ~ 1.0)
    /// </summary>
    public float Confidence { get; set; } = 1.0f;

    /// <summary>
    /// 단어별 타임스탬프 (활성화 시)
    /// </summary>
    public List<WordTimestamp> Words { get; set; } = new();

    /// <summary>
    /// 구간 길이
    /// </summary>
    public TimeSpan Duration => EndTime - StartTime;
}

/// <summary>
/// 단어별 타임스탬프
/// </summary>
public class WordTimestamp
{
    /// <summary>
    /// 단어
    /// </summary>
    public string Word { get; set; } = "";

    /// <summary>
    /// 시작 시간
    /// </summary>
    public TimeSpan StartTime { get; set; }

    /// <summary>
    /// 종료 시간
    /// </summary>
    public TimeSpan EndTime { get; set; }

    /// <summary>
    /// 인식 신뢰도 (0.0 ~ 1.0)
    /// </summary>
    public float Confidence { get; set; } = 1.0f;
}
