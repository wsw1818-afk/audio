using System.Diagnostics;
using System.IO;
using AudioRecorder.Models;

namespace AudioRecorder.Services;

/// <summary>
/// STT 통합 관리 서비스
/// Whisper 로컬/API + 화자 분리를 조합하여 최종 결과 생성
/// </summary>
public class SpeechToTextService : IDisposable
{
    private readonly WhisperLocalEngine _localEngine;
    private readonly SpeakerDiarizationService _diarization;
    private readonly string _logPath;
    private bool _disposed;

    /// <summary>
    /// 진행 상태 변경 이벤트
    /// </summary>
    public event EventHandler<SttProgressEventArgs>? ProgressChanged;

    /// <summary>
    /// 변환 완료 이벤트
    /// </summary>
    public event EventHandler<TranscriptionCompletedEventArgs>? TranscriptionCompleted;

    public SpeechToTextService(AudioConversionService audioConversion)
    {
        _localEngine = new WhisperLocalEngine(audioConversion);
        _diarization = new SpeakerDiarizationService(audioConversion);

        // 이벤트 전파
        _localEngine.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);
        _diarization.ProgressChanged += (s, e) => ProgressChanged?.Invoke(this, e);

        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder", "logs");
        if (!Directory.Exists(logDir)) Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "stt_service.log");
    }

    private void Log(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Debug.WriteLine(logMessage);
        try { File.AppendAllText(_logPath, logMessage + Environment.NewLine); }
        catch { }
    }

    /// <summary>
    /// Whisper 로컬 엔진 사용 가능 여부
    /// </summary>
    public bool IsLocalEngineAvailable => _localEngine.IsAvailable;

    /// <summary>
    /// NVIDIA GPU(CUDA) 사용 중 여부
    /// </summary>
    public bool IsGpuAccelerated => _localEngine.IsNvidiaGpuAvailable;

    /// <summary>
    /// 모델 다운로드 여부 확인
    /// </summary>
    public bool IsModelDownloaded(WhisperModelSize modelSize)
        => _localEngine.IsModelDownloaded(modelSize);

    /// <summary>
    /// 모델 다운로드
    /// </summary>
    public Task<bool> DownloadModelAsync(WhisperModelSize modelSize, CancellationToken cancellationToken = default)
        => _localEngine.DownloadModelAsync(modelSize, cancellationToken);

    /// <summary>
    /// 오디오 파일을 텍스트로 변환 (전체 파이프라인)
    /// </summary>
    public async Task<TranscriptionResult?> TranscribeAsync(
        string audioPath, SttEngineOptions options, CancellationToken cancellationToken = default)
    {
        Log($"STT 시작: {audioPath}");
        Log($"  모델: {options.ModelSize}, 언어: {options.Language}");
        Log($"  화자분리: {options.EnableDiarization}, 단어타임스탬프: {options.EnableWordTimestamps}");

        var stopwatch = Stopwatch.StartNew();

        try
        {
            // 1단계: 음성 인식 (로컬 Whisper)
            Log("Whisper 로컬 엔진 사용");

            // 모델 확인/다운로드
            if (!_localEngine.IsModelDownloaded(options.ModelSize))
            {
                Log($"모델 다운로드 필요: {options.ModelSize}");
                ProgressChanged?.Invoke(this, new SttProgressEventArgs
                {
                    Status = $"모델 다운로드 중...",
                    Progress = 0,
                    Phase = SttPhase.Downloading
                });

                var downloaded = await _localEngine.DownloadModelAsync(options.ModelSize, cancellationToken);
                if (!downloaded)
                {
                    Log("모델 다운로드 실패");
                    TranscriptionCompleted?.Invoke(this, new TranscriptionCompletedEventArgs
                    {
                        Success = false,
                        ErrorMessage = "Whisper 모델 다운로드에 실패했습니다."
                    });
                    return null;
                }
            }

            TranscriptionResult? result = await _localEngine.TranscribeAsync(audioPath, options, cancellationToken);

            if (result == null)
            {
                Log("음성 인식 실패");
                TranscriptionCompleted?.Invoke(this, new TranscriptionCompletedEventArgs
                {
                    Success = false,
                    ErrorMessage = "음성 인식에 실패했습니다. 오디오 파일과 설정을 확인하세요."
                });
                return null;
            }

            // 2단계: 화자 분리 (선택)
            if (options.EnableDiarization && result.Segments.Count > 0)
            {
                Log("화자 분리 시작");
                await _diarization.DiarizeAsync(audioPath, result, options.MaxSpeakers, cancellationToken);
            }

            stopwatch.Stop();
            result.ProcessingTime = stopwatch.Elapsed;

            Log($"STT 완료: {result.Segments.Count}개 구간, " +
                $"{result.DetectedSpeakers.Count}명 화자, " +
                $"소요시간: {stopwatch.Elapsed:mm\\:ss}");

            TranscriptionCompleted?.Invoke(this, new TranscriptionCompletedEventArgs
            {
                Success = true,
                Result = result
            });

            return result;
        }
        catch (OperationCanceledException)
        {
            Log("STT 취소됨");
            TranscriptionCompleted?.Invoke(this, new TranscriptionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = "변환이 취소되었습니다."
            });
            return null;
        }
        catch (Exception ex)
        {
            Log($"STT 오류: {ex.Message}");
            TranscriptionCompleted?.Invoke(this, new TranscriptionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = $"변환 중 오류 발생: {ex.Message}"
            });
            return null;
        }
    }

    /// <summary>
    /// 현재 진행 중인 변환 취소
    /// </summary>
    public void Cancel()
    {
        _localEngine.Cancel();
    }

    /// <summary>
    /// 지원되는 오디오 파일 확장자
    /// </summary>
    public static bool IsSupportedAudioFile(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        return ext is ".wav" or ".mp3" or ".m4a" or ".flac" or ".ogg" or ".mp4" or ".mkv" or ".webm" or ".aac";
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _localEngine.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// 변환 완료 이벤트 인자
/// </summary>
public class TranscriptionCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public TranscriptionResult? Result { get; init; }
    public string? ErrorMessage { get; init; }
}
