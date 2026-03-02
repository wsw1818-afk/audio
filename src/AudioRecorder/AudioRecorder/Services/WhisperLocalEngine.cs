using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using AudioRecorder.Models;

namespace AudioRecorder.Services;

/// <summary>
/// whisper.cpp 로컬 실행 엔진
/// whisper.cpp 바이너리를 프로세스로 실행하여 STT 수행
/// </summary>
public class WhisperLocalEngine : IDisposable
{
    private readonly string _modelsDir;
    private readonly string _logPath;
    private readonly AudioConversionService _audioConversion;
    private Process? _currentProcess;
    private bool _disposed;

    /// <summary>
    /// 진행 상태 변경 이벤트
    /// </summary>
    public event EventHandler<SttProgressEventArgs>? ProgressChanged;

    public WhisperLocalEngine(AudioConversionService audioConversion)
    {
        _audioConversion = audioConversion;

        // 모델 저장 경로
        _modelsDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder", "whisper-models");
        if (!Directory.Exists(_modelsDir))
            Directory.CreateDirectory(_modelsDir);

        // 로그 경로
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "AudioRecorder", "logs");
        if (!Directory.Exists(logDir))
            Directory.CreateDirectory(logDir);
        _logPath = Path.Combine(logDir, "whisper_local.log");
    }

    private void Log(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Debug.WriteLine(logMessage);
        try { File.AppendAllText(_logPath, logMessage + Environment.NewLine); }
        catch { /* 로그 실패 무시 */ }
    }

    /// <summary>
    /// NVIDIA GPU 사용 가능 여부
    /// </summary>
    public bool IsNvidiaGpuAvailable { get; private set; }

    /// <summary>
    /// whisper.cpp 실행 파일 경로 찾기 (GPU 감지 시 CUDA 버전 우선)
    /// </summary>
    public string? FindWhisperExecutable()
    {
        var appDir = AppDomain.CurrentDomain.BaseDirectory;

        // 1. NVIDIA GPU 감지 → whisper-cuda 폴더 우선
        if (HasNvidiaGpu())
        {
            var cudaPath = Path.Combine(appDir, "whisper-cuda", "whisper-cli.exe");
            if (File.Exists(cudaPath))
            {
                IsNvidiaGpuAvailable = true;
                Log("NVIDIA GPU 감지 → CUDA 버전 사용");
                return cudaPath;
            }
        }

        // 2. 앱 폴더 (CPU 버전)
        IsNvidiaGpuAvailable = false;
        foreach (var name in new[] { "whisper-cli.exe", "main.exe", "whisper.exe" })
        {
            var localPath = Path.Combine(appDir, name);
            if (File.Exists(localPath))
            {
                Log("CPU 버전 사용");
                return localPath;
            }
        }

        // 3. PATH 환경변수
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            foreach (var name in new[] { "whisper-cli.exe", "whisper.exe", "main.exe" })
            {
                var fullPath = Path.Combine(dir, name);
                if (File.Exists(fullPath)) return fullPath;
            }
        }

        return null;
    }

    /// <summary>
    /// NVIDIA GPU 존재 여부 확인 (nvidia-smi 실행)
    /// </summary>
    private bool HasNvidiaGpu()
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name --format=csv,noheader",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            };
            using var process = Process.Start(startInfo);
            if (process == null) return false;
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            var hasGpu = process.ExitCode == 0 && !string.IsNullOrWhiteSpace(output);
            if (hasGpu) Log($"GPU 감지: {output.Trim()}");
            return hasGpu;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// whisper.cpp 사용 가능 여부
    /// </summary>
    public bool IsAvailable => FindWhisperExecutable() != null;

    /// <summary>
    /// 모델 파일이 존재하는지 확인
    /// </summary>
    public bool IsModelDownloaded(WhisperModelSize modelSize)
    {
        var options = new SttEngineOptions { ModelSize = modelSize };
        var modelPath = Path.Combine(_modelsDir, options.GetModelFileName());
        return File.Exists(modelPath);
    }

    /// <summary>
    /// 모델 파일 경로 반환
    /// </summary>
    public string GetModelPath(WhisperModelSize modelSize)
    {
        var options = new SttEngineOptions { ModelSize = modelSize };
        return Path.Combine(_modelsDir, options.GetModelFileName());
    }

    /// <summary>
    /// 모델 다운로드
    /// </summary>
    public async Task<bool> DownloadModelAsync(WhisperModelSize modelSize, CancellationToken cancellationToken = default)
    {
        var options = new SttEngineOptions { ModelSize = modelSize };
        var modelPath = Path.Combine(_modelsDir, options.GetModelFileName());
        var url = options.GetModelDownloadUrl();
        var expectedSizeMB = SttEngineOptions.GetModelSizeMB(modelSize);

        if (File.Exists(modelPath))
        {
            var existingSize = new FileInfo(modelPath).Length / (1024 * 1024);
            if (existingSize >= expectedSizeMB * 0.9) // 90% 이상이면 유효
            {
                Log($"모델 이미 존재: {modelPath} ({existingSize}MB)");
                return true;
            }
            // 불완전한 파일 삭제
            File.Delete(modelPath);
        }

        Log($"모델 다운로드 시작: {url} → {modelPath}");
        ProgressChanged?.Invoke(this, new SttProgressEventArgs
        {
            Status = $"모델 다운로드 중 ({SttEngineOptions.GetModelDisplayName(modelSize)})...",
            Progress = 0,
            Phase = SttPhase.Downloading
        });

        try
        {
            using var httpClient = new HttpClient();
            httpClient.Timeout = TimeSpan.FromMinutes(30);

            using var response = await httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var totalBytes = response.Content.Headers.ContentLength ?? expectedSizeMB * 1024 * 1024;
            var tempPath = modelPath + ".downloading";

            using (var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken))
            using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 8192))
            {
                var buffer = new byte[8192];
                long totalRead = 0;
                int bytesRead;
                int lastReportedProgress = -1;

                while ((bytesRead = await contentStream.ReadAsync(buffer, cancellationToken)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken);
                    totalRead += bytesRead;

                    var progress = (int)(totalRead * 100 / totalBytes);
                    if (progress != lastReportedProgress)
                    {
                        lastReportedProgress = progress;
                        var downloadedMB = totalRead / (1024.0 * 1024);
                        var totalMB = totalBytes / (1024.0 * 1024);
                        ProgressChanged?.Invoke(this, new SttProgressEventArgs
                        {
                            Status = $"모델 다운로드 중... {downloadedMB:F0}MB / {totalMB:F0}MB",
                            Progress = progress,
                            Phase = SttPhase.Downloading
                        });
                    }
                }
            }

            // 다운로드 완료 → 이름 변경
            if (File.Exists(modelPath)) File.Delete(modelPath);
            File.Move(tempPath, modelPath);

            Log($"모델 다운로드 완료: {modelPath} ({new FileInfo(modelPath).Length / (1024.0 * 1024):F0}MB)");
            return true;
        }
        catch (OperationCanceledException)
        {
            Log("모델 다운로드 취소됨");
            var tempPath = modelPath + ".downloading";
            if (File.Exists(tempPath)) File.Delete(tempPath);
            return false;
        }
        catch (Exception ex)
        {
            Log($"모델 다운로드 실패: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 오디오 파일을 텍스트로 변환
    /// </summary>
    public async Task<TranscriptionResult?> TranscribeAsync(
        string audioPath, SttEngineOptions options, CancellationToken cancellationToken = default)
    {
        var whisperExe = FindWhisperExecutable();
        if (whisperExe == null)
        {
            Log("whisper.cpp 실행 파일을 찾을 수 없습니다");
            return null;
        }

        var modelPath = GetModelPath(options.ModelSize);
        if (!File.Exists(modelPath))
        {
            Log($"모델 파일 없음: {modelPath}");
            ProgressChanged?.Invoke(this, new SttProgressEventArgs
            {
                Status = "모델 다운로드가 필요합니다",
                Progress = 0,
                Phase = SttPhase.Error
            });
            return null;
        }

        // whisper.cpp는 16kHz WAV만 지원 → FFmpeg로 변환
        var wavPath = await PrepareAudioAsync(audioPath, cancellationToken);
        if (wavPath == null) return null;

        var stopwatch = Stopwatch.StartNew();

        try
        {
            ProgressChanged?.Invoke(this, new SttProgressEventArgs
            {
                Status = "음성 인식 중...",
                Progress = 10,
                Phase = SttPhase.Transcribing
            });

            // whisper.cpp 실행 인자 구성
            var outputJsonPath = Path.Combine(Path.GetTempPath(), $"whisper_output_{Guid.NewGuid():N}");
            var args = BuildWhisperArgs(whisperExe, wavPath, modelPath, options, outputJsonPath);

            Log($"whisper.cpp 실행: {whisperExe} {args}");

            var startInfo = new ProcessStartInfo
            {
                FileName = whisperExe,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
            };

            using var process = new Process { StartInfo = startInfo };
            _currentProcess = process;

            var outputLines = new List<string>();
            var errorLines = new List<string>();

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    outputLines.Add(e.Data);
                    // 진행률 파싱 (whisper.cpp는 타임스탬프 출력)
                    ParseProgress(e.Data);
                }
            };

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    errorLines.Add(e.Data);
                    Log($"[whisper stderr] {e.Data}");
                }
            };

            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // 타임아웃: 30분 (대용량 파일 + CPU 처리 고려)
            var timeoutMs = 30 * 60 * 1000;

            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            linkedCts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

            try
            {
                await process.WaitForExitAsync(linkedCts.Token);
            }
            catch (OperationCanceledException)
            {
                Log("whisper.cpp 실행 취소/타임아웃");
                try { process.Kill(true); } catch { }
                return null;
            }
            finally
            {
                _currentProcess = null;
            }

            stopwatch.Stop();

            if (process.ExitCode != 0)
            {
                Log($"whisper.cpp 실패: ExitCode={process.ExitCode}");
                Log($"stderr: {string.Join(Environment.NewLine, errorLines.TakeLast(5))}");
                return null;
            }

            // 결과 파싱 (JSON 출력 파일)
            var result = ParseWhisperOutput(outputJsonPath, audioPath, options, stopwatch.Elapsed);

            // 임시 파일 정리
            CleanupTempFiles(wavPath, audioPath, outputJsonPath);

            ProgressChanged?.Invoke(this, new SttProgressEventArgs
            {
                Status = "변환 완료",
                Progress = 100,
                Phase = SttPhase.Completed
            });

            return result;
        }
        catch (Exception ex)
        {
            Log($"TranscribeAsync 예외: {ex.Message}");
            return null;
        }
        finally
        {
            // 변환용 임시 WAV 정리
            if (wavPath != audioPath && File.Exists(wavPath))
            {
                try { File.Delete(wavPath); } catch { }
            }
        }
    }

    /// <summary>
    /// 현재 실행 중인 프로세스 취소
    /// </summary>
    public void Cancel()
    {
        if (_currentProcess != null && !_currentProcess.HasExited)
        {
            try { _currentProcess.Kill(true); } catch { }
        }
    }

    /// <summary>
    /// 오디오를 whisper.cpp 호환 16kHz mono WAV로 변환
    /// </summary>
    private async Task<string?> PrepareAudioAsync(string audioPath, CancellationToken cancellationToken)
    {
        // 이미 16kHz WAV인지 확인 → 확장자로만 판단 (실제 샘플레이트는 FFmpeg가 처리)
        var ext = Path.GetExtension(audioPath).ToLowerInvariant();

        ProgressChanged?.Invoke(this, new SttProgressEventArgs
        {
            Status = "오디오 전처리 중...",
            Progress = 5,
            Phase = SttPhase.Preprocessing
        });

        // FFmpeg로 16kHz mono WAV 변환
        var ffmpeg = _audioConversion.FindFFmpeg();
        if (ffmpeg == null)
        {
            Log("FFmpeg를 찾을 수 없습니다");
            return null;
        }

        var tempWav = Path.Combine(Path.GetTempPath(), $"whisper_input_{Guid.NewGuid():N}.wav");

        var startInfo = new ProcessStartInfo
        {
            FileName = ffmpeg,
            Arguments = $"-i \"{audioPath}\" -ar 16000 -ac 1 -c:a pcm_s16le -y \"{tempWav}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        using var process = new Process { StartInfo = startInfo };
        process.Start();

        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(true); } catch { }
            if (File.Exists(tempWav)) File.Delete(tempWav);
            return null;
        }

        if (process.ExitCode == 0 && File.Exists(tempWav))
        {
            Log($"오디오 전처리 완료: {tempWav} ({new FileInfo(tempWav).Length / 1024}KB)");
            return tempWav;
        }

        Log($"오디오 전처리 실패: ExitCode={process.ExitCode}");
        return null;
    }

    /// <summary>
    /// whisper.cpp 실행 인자 구성
    /// </summary>
    private string BuildWhisperArgs(string whisperExe, string wavPath, string modelPath,
        SttEngineOptions options, string outputPath)
    {
        var args = new List<string>
        {
            $"-m \"{modelPath}\"",
            $"-f \"{wavPath}\"",
            "-oj",                              // JSON 출력
            $"-of \"{outputPath}\"",            // 출력 파일 경로
            "-pp",                              // 진행률 표시
            "-bs 3",                            // beam search 3 (정확도+속도 균형)
            "-bo 3",                            // best-of 3 (후보 중 최선 선택)
        };

        // 언어 설정
        if (options.Language != "auto")
        {
            args.Add($"-l {options.Language}");
        }
        else
        {
            args.Add("-l auto");
        }

        // 단어별 타임스탬프: JSON의 tokens에서 이미 제공됨
        // -ml 옵션은 사용하지 않음 (세그먼트를 문장 단위로 유지)

        // 스레드 수 (CPU 코어의 3/4 사용 - 속도 향상)
        var threads = Math.Max(2, Environment.ProcessorCount * 3 / 4);
        args.Add($"-t {threads}");

        return string.Join(" ", args);
    }

    /// <summary>
    /// whisper.cpp 진행률 파싱
    /// </summary>
    private void ParseProgress(string line)
    {
        // whisper.cpp 출력 형식: "[00:00:00.000 --> 00:00:03.000]   텍스트"
        if (line.StartsWith("[") && line.Contains("-->"))
        {
            // 타임스탬프에서 진행률 추정 (대략적)
            try
            {
                var endIdx = line.IndexOf("-->") + 4;
                var endTime = line.Substring(endIdx, 12).Trim();
                if (TimeSpan.TryParse(endTime, CultureInfo.InvariantCulture, out var ts))
                {
                    var text = line.Contains("]") ? line[(line.LastIndexOf(']') + 1)..].Trim() : "";
                    ProgressChanged?.Invoke(this, new SttProgressEventArgs
                    {
                        Status = $"인식 중... {ts:hh\\:mm\\:ss} - {(text.Length > 30 ? text[..30] + "..." : text)}",
                        Progress = 50, // 정확한 진행률은 알 수 없음
                        Phase = SttPhase.Transcribing
                    });
                }
            }
            catch { /* 파싱 실패 무시 */ }
        }
    }

    /// <summary>
    /// whisper.cpp JSON 출력 파싱
    /// </summary>
    private TranscriptionResult? ParseWhisperOutput(string outputBasePath, string audioPath,
        SttEngineOptions options, TimeSpan processingTime)
    {
        var jsonPath = outputBasePath + ".json";
        if (!File.Exists(jsonPath))
        {
            Log($"JSON 출력 파일 없음: {jsonPath}");
            return null;
        }

        try
        {
            var json = File.ReadAllText(jsonPath);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var result = new TranscriptionResult
            {
                SourceFilePath = audioPath,
                Engine = "whisper-local",
                Model = options.ModelSize.ToString().ToLowerInvariant(),
                ProcessingTime = processingTime,
                CompletedAt = DateTime.Now,
            };

            // 언어 감지 결과
            if (root.TryGetProperty("result", out var resultObj) &&
                resultObj.TryGetProperty("language", out var langProp))
            {
                result.Language = langProp.GetString() ?? "ko";
            }

            // 세그먼트 파싱
            if (root.TryGetProperty("transcription", out var transcription))
            {
                int segIdx = 0;
                foreach (var item in transcription.EnumerateArray())
                {
                    var segment = new TranscriptionSegment
                    {
                        Index = segIdx++,
                    };

                    // 타임스탬프
                    if (item.TryGetProperty("timestamps", out var timestamps))
                    {
                        if (timestamps.TryGetProperty("from", out var from))
                            segment.StartTime = ParseTimestamp(from.GetString());
                        if (timestamps.TryGetProperty("to", out var to))
                            segment.EndTime = ParseTimestamp(to.GetString());
                    }

                    // 오프셋 기반 (다른 출력 형식)
                    if (item.TryGetProperty("offsets", out var offsets))
                    {
                        if (offsets.TryGetProperty("from", out var fromMs))
                            segment.StartTime = TimeSpan.FromMilliseconds(fromMs.GetInt64());
                        if (offsets.TryGetProperty("to", out var toMs))
                            segment.EndTime = TimeSpan.FromMilliseconds(toMs.GetInt64());
                    }

                    // 텍스트
                    if (item.TryGetProperty("text", out var textProp))
                        segment.Text = textProp.GetString()?.Trim() ?? "";

                    // 단어별 타임스탬프 (tokens)
                    if (item.TryGetProperty("tokens", out var tokens))
                    {
                        foreach (var token in tokens.EnumerateArray())
                        {
                            var word = new WordTimestamp();

                            if (token.TryGetProperty("text", out var wordText))
                                word.Word = wordText.GetString() ?? "";

                            if (token.TryGetProperty("offsets", out var wordOffsets))
                            {
                                if (wordOffsets.TryGetProperty("from", out var wFrom))
                                    word.StartTime = TimeSpan.FromMilliseconds(wFrom.GetInt64());
                                if (wordOffsets.TryGetProperty("to", out var wTo))
                                    word.EndTime = TimeSpan.FromMilliseconds(wTo.GetInt64());
                            }

                            if (token.TryGetProperty("p", out var prob))
                                word.Confidence = (float)prob.GetDouble();

                            if (!string.IsNullOrWhiteSpace(word.Word))
                                segment.Words.Add(word);
                        }
                    }

                    if (!string.IsNullOrWhiteSpace(segment.Text))
                        result.Segments.Add(segment);
                }
            }

            // 전체 길이 계산
            if (result.Segments.Count > 0)
            {
                result.Duration = result.Segments[^1].EndTime;
            }

            Log($"파싱 완료: {result.Segments.Count}개 구간, 언어={result.Language}");
            return result;
        }
        catch (Exception ex)
        {
            Log($"JSON 파싱 실패: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// "HH:mm:ss.fff" 형식 타임스탬프 파싱
    /// </summary>
    private static TimeSpan ParseTimestamp(string? timestamp)
    {
        if (string.IsNullOrEmpty(timestamp)) return TimeSpan.Zero;

        if (TimeSpan.TryParse(timestamp, CultureInfo.InvariantCulture, out var result))
            return result;

        return TimeSpan.Zero;
    }

    /// <summary>
    /// 임시 파일 정리
    /// </summary>
    private void CleanupTempFiles(string wavPath, string originalPath, string outputBasePath)
    {
        // 변환용 WAV 정리
        if (wavPath != originalPath && File.Exists(wavPath))
        {
            try { File.Delete(wavPath); } catch { }
        }

        // whisper 출력 파일 정리
        foreach (var ext in new[] { ".json", ".txt", ".srt", ".vtt" })
        {
            var path = outputBasePath + ext;
            if (File.Exists(path))
            {
                try { File.Delete(path); } catch { }
            }
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            Cancel();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }
}

/// <summary>
/// STT 진행 상태
/// </summary>
public enum SttPhase
{
    Downloading,    // 모델 다운로드 중
    Preprocessing,  // 오디오 전처리 중
    Transcribing,   // 음성 인식 중
    Diarizing,      // 화자 분리 중
    Completed,      // 완료
    Error           // 오류
}

/// <summary>
/// STT 진행 이벤트 인자
/// </summary>
public class SttProgressEventArgs : EventArgs
{
    public string Status { get; init; } = "";
    public int Progress { get; init; }
    public SttPhase Phase { get; init; }
}
