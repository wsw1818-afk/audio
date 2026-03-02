namespace AudioRecorder.Models;

/// <summary>
/// STT 엔진 종류
/// </summary>
public enum SttEngineType
{
    /// <summary>
    /// 로컬 Whisper.cpp (오프라인, 무료)
    /// </summary>
    WhisperLocal
}

/// <summary>
/// Whisper 모델 크기
/// </summary>
public enum WhisperModelSize
{
    LargeV3Turbo    // 1.6GB, Large-v3급 정확도 + 8배 빠름
}

/// <summary>
/// STT 변환 옵션
/// </summary>
public class SttEngineOptions
{
    /// <summary>
    /// STT 엔진 선택
    /// </summary>
    public SttEngineType Engine { get; set; } = SttEngineType.WhisperLocal;

    /// <summary>
    /// Whisper 모델 크기 (로컬 전용)
    /// </summary>
    public WhisperModelSize ModelSize { get; set; } = WhisperModelSize.LargeV3Turbo;

    /// <summary>
    /// 인식 언어 ("auto"이면 자동 감지)
    /// </summary>
    public string Language { get; set; } = "auto";

    /// <summary>
    /// 화자 분리 활성화 여부
    /// </summary>
    public bool EnableDiarization { get; set; } = true;

    /// <summary>
    /// 최대 화자 수 (화자 분리 시)
    /// </summary>
    public int MaxSpeakers { get; set; } = 4;

    /// <summary>
    /// 단어별 타임스탬프 활성화 여부
    /// </summary>
    public bool EnableWordTimestamps { get; set; } = true;

    /// <summary>
    /// 모델 파일명 반환
    /// </summary>
    public string GetModelFileName()
    {
        return "ggml-large-v3-turbo.bin";
    }

    /// <summary>
    /// 모델 표시 이름
    /// </summary>
    public static string GetModelDisplayName(WhisperModelSize size)
    {
        return "Large-v3-turbo (1.6GB, 고정확+고속)";
    }

    /// <summary>
    /// 모델 다운로드 URL (Hugging Face)
    /// </summary>
    public string GetModelDownloadUrl()
    {
        var fileName = GetModelFileName();
        return $"https://huggingface.co/ggerganov/whisper.cpp/resolve/main/{fileName}";
    }

    /// <summary>
    /// 모델 예상 파일 크기 (MB)
    /// </summary>
    public static long GetModelSizeMB(WhisperModelSize size)
    {
        return 1620; // Large-v3-turbo: 1.6GB
    }
}
