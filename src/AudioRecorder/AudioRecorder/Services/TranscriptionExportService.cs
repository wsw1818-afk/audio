using System.IO;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using AudioRecorder.Models;

namespace AudioRecorder.Services;

/// <summary>
/// 녹취록 내보내기 서비스 (TXT, SRT, VTT, JSON)
/// </summary>
public class TranscriptionExportService
{
    /// <summary>
    /// 내보내기 형식
    /// </summary>
    public enum ExportFormat
    {
        TXT,    // 일반 텍스트 (화자+타임스탬프)
        SRT,    // SubRip 자막 형식
        VTT,    // WebVTT 자막 형식
        JSON    // 구조화 JSON
    }

    /// <summary>
    /// 결과를 파일로 내보내기
    /// </summary>
    public static async Task<bool> ExportAsync(TranscriptionResult result, string outputPath, ExportFormat format)
    {
        try
        {
            var content = format switch
            {
                ExportFormat.TXT => FormatAsTxt(result),
                ExportFormat.SRT => FormatAsSrt(result),
                ExportFormat.VTT => FormatAsVtt(result),
                ExportFormat.JSON => FormatAsJson(result),
                _ => FormatAsTxt(result)
            };

            var encoding = new UTF8Encoding(true); // BOM 포함 UTF-8
            await File.WriteAllTextAsync(outputPath, content, encoding);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 형식에 맞는 파일 확장자 반환
    /// </summary>
    public static string GetExtension(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.TXT => ".txt",
            ExportFormat.SRT => ".srt",
            ExportFormat.VTT => ".vtt",
            ExportFormat.JSON => ".json",
            _ => ".txt"
        };
    }

    /// <summary>
    /// 형식 표시 이름
    /// </summary>
    public static string GetDisplayName(ExportFormat format)
    {
        return format switch
        {
            ExportFormat.TXT => "텍스트 파일 (*.txt)",
            ExportFormat.SRT => "SRT 자막 (*.srt)",
            ExportFormat.VTT => "WebVTT 자막 (*.vtt)",
            ExportFormat.JSON => "JSON 데이터 (*.json)",
            _ => format.ToString()
        };
    }

    /// <summary>
    /// TXT 형식: 화자+타임스탬프 포함 일반 텍스트
    /// </summary>
    public static string FormatAsTxt(TranscriptionResult result)
    {
        var sb = new StringBuilder();

        // 헤더
        sb.AppendLine("=" .PadRight(60, '='));
        sb.AppendLine($"  녹취록 - {Path.GetFileName(result.SourceFilePath)}");
        sb.AppendLine($"  변환 일시: {result.CompletedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"  오디오 길이: {result.Duration:hh\\:mm\\:ss}");
        sb.AppendLine($"  엔진: {result.Engine} ({result.Model})");
        if (result.DetectedSpeakers.Count > 0)
            sb.AppendLine($"  감지된 화자: {string.Join(", ", result.DetectedSpeakers)}");
        sb.AppendLine("=".PadRight(60, '='));
        sb.AppendLine();

        // 본문
        string? lastSpeaker = null;

        foreach (var seg in result.Segments)
        {
            var timestamp = $"[{seg.StartTime:hh\\:mm\\:ss}]";
            var speaker = seg.SpeakerLabel;

            if (speaker != null && speaker != lastSpeaker)
            {
                if (lastSpeaker != null) sb.AppendLine(); // 화자 변경 시 빈 줄
                sb.AppendLine($"{timestamp} {speaker}:");
                sb.AppendLine($"  {seg.Text.Trim()}");
                lastSpeaker = speaker;
            }
            else if (speaker != null)
            {
                sb.AppendLine($"{timestamp}   {seg.Text.Trim()}");
            }
            else
            {
                sb.AppendLine($"{timestamp} {seg.Text.Trim()}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("---");
        sb.AppendLine($"처리 시간: {result.ProcessingTime:mm\\:ss}");

        return sb.ToString();
    }

    /// <summary>
    /// SRT 형식: SubRip 자막
    /// </summary>
    public static string FormatAsSrt(TranscriptionResult result)
    {
        var sb = new StringBuilder();

        for (int i = 0; i < result.Segments.Count; i++)
        {
            var seg = result.Segments[i];

            // 인덱스 (1부터)
            sb.AppendLine((i + 1).ToString());

            // 타임스탬프
            sb.AppendLine($"{FormatSrtTime(seg.StartTime)} --> {FormatSrtTime(seg.EndTime)}");

            // 텍스트 (화자 포함)
            if (!string.IsNullOrEmpty(seg.SpeakerLabel))
                sb.AppendLine($"[{seg.SpeakerLabel}] {seg.Text.Trim()}");
            else
                sb.AppendLine(seg.Text.Trim());

            sb.AppendLine(); // 빈 줄 구분
        }

        return sb.ToString();
    }

    /// <summary>
    /// VTT 형식: WebVTT 자막
    /// </summary>
    public static string FormatAsVtt(TranscriptionResult result)
    {
        var sb = new StringBuilder();

        // VTT 헤더
        sb.AppendLine("WEBVTT");
        sb.AppendLine($"NOTE 생성: AudioRecorder Pro, {result.CompletedAt:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine();

        for (int i = 0; i < result.Segments.Count; i++)
        {
            var seg = result.Segments[i];

            // 선택적 큐 ID
            sb.AppendLine($"cue-{i + 1}");

            // 타임스탬프
            sb.AppendLine($"{FormatVttTime(seg.StartTime)} --> {FormatVttTime(seg.EndTime)}");

            // 텍스트
            if (!string.IsNullOrEmpty(seg.SpeakerLabel))
                sb.AppendLine($"<v {seg.SpeakerLabel}>{seg.Text.Trim()}");
            else
                sb.AppendLine(seg.Text.Trim());

            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>
    /// JSON 형식: 구조화 데이터
    /// </summary>
    public static string FormatAsJson(TranscriptionResult result)
    {
        var exportData = new JsonExportData
        {
            SourceFile = Path.GetFileName(result.SourceFilePath),
            Language = result.Language,
            Duration = result.Duration.TotalSeconds,
            DurationFormatted = $"{result.Duration:hh\\:mm\\:ss}",
            Engine = result.Engine,
            Model = result.Model,
            CompletedAt = result.CompletedAt,
            ProcessingTimeSeconds = result.ProcessingTime.TotalSeconds,
            Speakers = result.DetectedSpeakers,
            Segments = result.Segments.Select(seg => new JsonSegmentData
            {
                Index = seg.Index,
                StartTime = seg.StartTime.TotalSeconds,
                EndTime = seg.EndTime.TotalSeconds,
                StartFormatted = $"{seg.StartTime:hh\\:mm\\:ss\\.fff}",
                EndFormatted = $"{seg.EndTime:hh\\:mm\\:ss\\.fff}",
                Text = seg.Text.Trim(),
                Speaker = seg.SpeakerLabel,
                Confidence = seg.Confidence,
                Words = seg.Words.Count > 0
                    ? seg.Words.Select(w => new JsonWordData
                    {
                        Word = w.Word,
                        StartTime = w.StartTime.TotalSeconds,
                        EndTime = w.EndTime.TotalSeconds,
                        Confidence = w.Confidence
                    }).ToList()
                    : null
            }).ToList()
        };

        var jsonOptions = new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };

        return JsonSerializer.Serialize(exportData, jsonOptions);
    }

    /// <summary>
    /// 클립보드용 텍스트 생성 (간결한 형식)
    /// </summary>
    public static string FormatForClipboard(TranscriptionResult result)
    {
        var sb = new StringBuilder();
        string? lastSpeaker = null;

        foreach (var seg in result.Segments)
        {
            var speaker = seg.SpeakerLabel;

            if (speaker != null && speaker != lastSpeaker)
            {
                if (sb.Length > 0) sb.AppendLine();
                sb.AppendLine($"[{seg.StartTime:hh\\:mm\\:ss}] {speaker}:");
                sb.AppendLine(seg.Text.Trim());
                lastSpeaker = speaker;
            }
            else
            {
                sb.AppendLine(seg.Text.Trim());
            }
        }

        return sb.ToString();
    }

    /// <summary>
    /// SRT 타임스탬프 형식: "HH:MM:SS,mmm"
    /// </summary>
    private static string FormatSrtTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00},{ts.Milliseconds:000}";
    }

    /// <summary>
    /// VTT 타임스탬프 형식: "HH:MM:SS.mmm"
    /// </summary>
    private static string FormatVttTime(TimeSpan ts)
    {
        return $"{(int)ts.TotalHours:00}:{ts.Minutes:00}:{ts.Seconds:00}.{ts.Milliseconds:000}";
    }

    // JSON 내보내기용 데이터 클래스들
    private class JsonExportData
    {
        [JsonPropertyName("source_file")]
        public string SourceFile { get; set; } = "";

        [JsonPropertyName("language")]
        public string Language { get; set; } = "";

        [JsonPropertyName("duration")]
        public double Duration { get; set; }

        [JsonPropertyName("duration_formatted")]
        public string DurationFormatted { get; set; } = "";

        [JsonPropertyName("engine")]
        public string Engine { get; set; } = "";

        [JsonPropertyName("model")]
        public string Model { get; set; } = "";

        [JsonPropertyName("completed_at")]
        public DateTime CompletedAt { get; set; }

        [JsonPropertyName("processing_time_seconds")]
        public double ProcessingTimeSeconds { get; set; }

        [JsonPropertyName("speakers")]
        public List<string> Speakers { get; set; } = new();

        [JsonPropertyName("segments")]
        public List<JsonSegmentData> Segments { get; set; } = new();
    }

    private class JsonSegmentData
    {
        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("start")]
        public double StartTime { get; set; }

        [JsonPropertyName("end")]
        public double EndTime { get; set; }

        [JsonPropertyName("start_formatted")]
        public string StartFormatted { get; set; } = "";

        [JsonPropertyName("end_formatted")]
        public string EndFormatted { get; set; } = "";

        [JsonPropertyName("text")]
        public string Text { get; set; } = "";

        [JsonPropertyName("speaker")]
        public string? Speaker { get; set; }

        [JsonPropertyName("confidence")]
        public float Confidence { get; set; }

        [JsonPropertyName("words")]
        public List<JsonWordData>? Words { get; set; }
    }

    private class JsonWordData
    {
        [JsonPropertyName("word")]
        public string Word { get; set; } = "";

        [JsonPropertyName("start")]
        public double StartTime { get; set; }

        [JsonPropertyName("end")]
        public double EndTime { get; set; }

        [JsonPropertyName("confidence")]
        public float Confidence { get; set; }
    }
}
