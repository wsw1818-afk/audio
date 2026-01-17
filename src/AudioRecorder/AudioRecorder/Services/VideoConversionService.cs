using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace AudioRecorder.Services;

/// <summary>
/// 동영상 압축 품질
/// </summary>
public enum VideoQuality
{
    High,   // 최고 품질 (CRF 23)
    Normal  // 보통 품질 (CRF 28) - 용량 우선
}

/// <summary>
/// 인코더 타입
/// </summary>
public enum VideoEncoder
{
    Auto,       // 자동 선택 (NVENC > QSV > CPU Fast)
    NVENC,      // NVIDIA GPU 하드웨어 인코딩 (가장 빠름)
    QSV,        // Intel Quick Sync 하드웨어 인코딩
    CPUFast,    // CPU 빠른 프리셋 (ultrafast)
    CPUQuality  // CPU 품질 우선 (medium) - 기존 방식
}

/// <summary>
/// 동영상 압축 서비스 (FFmpeg H.265/HEVC 기반)
/// GPU 하드웨어 가속 지원 (NVENC, QSV)
/// </summary>
public class VideoConversionService
{
    private string? _ffmpegPath;
    private readonly string _logPath;
    private Process? _currentProcess;
    private bool _isCancelled;

    // GPU 인코더 사용 가능 여부 캐시
    private bool? _nvencAvailable;
    private bool? _qsvAvailable;
    private VideoEncoder _preferredEncoder = VideoEncoder.Auto;

    public event EventHandler<VideoConversionProgressEventArgs>? ProgressChanged;
    public event EventHandler<VideoConversionCompletedEventArgs>? ConversionCompleted;

    public bool IsFFmpegAvailable => FindFFmpeg() != null;
    public bool IsConverting => _currentProcess != null && !_currentProcess.HasExited;

    /// <summary>
    /// 현재 사용 가능한 인코더 정보
    /// </summary>
    public string AvailableEncoderInfo { get; private set; } = "";

    /// <summary>
    /// 선호 인코더 설정 (기본: Auto)
    /// </summary>
    public VideoEncoder PreferredEncoder
    {
        get => _preferredEncoder;
        set => _preferredEncoder = value;
    }

    public VideoConversionService()
    {
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        _logPath = Path.Combine(desktopPath, "video_conversion.log");
    }

    private void Log(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Debug.WriteLine(logMessage);
        try
        {
            File.AppendAllText(_logPath, logMessage + Environment.NewLine);
        }
        catch { }
    }

    public string? FindFFmpeg()
    {
        if (_ffmpegPath != null && File.Exists(_ffmpegPath))
            return _ffmpegPath;

        // 1. 앱 폴더
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var localPath = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(localPath))
        {
            _ffmpegPath = localPath;
            return _ffmpegPath;
        }

        // 2. PATH 환경변수
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var ffmpegInPath = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(ffmpegInPath))
            {
                _ffmpegPath = ffmpegInPath;
                return _ffmpegPath;
            }
        }

        // 3. 일반적인 설치 경로
        var commonPaths = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe"
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
            {
                _ffmpegPath = path;
                return _ffmpegPath;
            }
        }

        return null;
    }

    /// <summary>
    /// 품질별 표시 이름
    /// </summary>
    public static string GetDisplayName(VideoQuality quality)
    {
        return quality switch
        {
            VideoQuality.High => "최고 품질 (H.265)",
            VideoQuality.Normal => "보통 품질 (H.265, 용량↓)",
            _ => quality.ToString()
        };
    }

    /// <summary>
    /// 인코더별 표시 이름
    /// </summary>
    public static string GetEncoderDisplayName(VideoEncoder encoder)
    {
        return encoder switch
        {
            VideoEncoder.Auto => "자동 (GPU 우선)",
            VideoEncoder.NVENC => "NVIDIA GPU (NVENC)",
            VideoEncoder.QSV => "Intel GPU (Quick Sync)",
            VideoEncoder.CPUFast => "CPU 빠름 (ultrafast)",
            VideoEncoder.CPUQuality => "CPU 품질 (medium)",
            _ => encoder.ToString()
        };
    }

    /// <summary>
    /// NVENC 사용 가능 여부 확인 (NVIDIA GPU)
    /// </summary>
    public async Task<bool> IsNvencAvailableAsync()
    {
        if (_nvencAvailable.HasValue) return _nvencAvailable.Value;

        var ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return false;

        try
        {
            // FFmpeg에서 NVENC 인코더 확인
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            _nvencAvailable = output.Contains("hevc_nvenc");
            Log($"[Video] NVENC 사용 가능: {_nvencAvailable}");
            return _nvencAvailable.Value;
        }
        catch (Exception ex)
        {
            Log($"[Video] NVENC 확인 실패: {ex.Message}");
            _nvencAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// Intel Quick Sync 사용 가능 여부 확인
    /// </summary>
    public async Task<bool> IsQsvAvailableAsync()
    {
        if (_qsvAvailable.HasValue) return _qsvAvailable.Value;

        var ffmpeg = FindFFmpeg();
        if (ffmpeg == null) return false;

        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = "-hide_banner -encoders",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync();
            await process.WaitForExitAsync();

            _qsvAvailable = output.Contains("hevc_qsv");
            Log($"[Video] QSV 사용 가능: {_qsvAvailable}");
            return _qsvAvailable.Value;
        }
        catch (Exception ex)
        {
            Log($"[Video] QSV 확인 실패: {ex.Message}");
            _qsvAvailable = false;
            return false;
        }
    }

    /// <summary>
    /// 사용 가능한 인코더 초기화 및 정보 업데이트
    /// </summary>
    public async Task InitializeEncodersAsync()
    {
        var nvenc = await IsNvencAvailableAsync();
        var qsv = await IsQsvAvailableAsync();

        var info = new List<string>();
        if (nvenc) info.Add("NVENC (NVIDIA)");
        if (qsv) info.Add("QSV (Intel)");
        info.Add("CPU");

        AvailableEncoderInfo = string.Join(", ", info);
        Log($"[Video] 사용 가능한 인코더: {AvailableEncoderInfo}");
    }

    /// <summary>
    /// 최적의 인코더 자동 선택
    /// </summary>
    private async Task<VideoEncoder> GetBestEncoderAsync()
    {
        if (_preferredEncoder != VideoEncoder.Auto)
        {
            // 지정된 인코더가 사용 가능한지 확인
            switch (_preferredEncoder)
            {
                case VideoEncoder.NVENC when await IsNvencAvailableAsync():
                    return VideoEncoder.NVENC;
                case VideoEncoder.QSV when await IsQsvAvailableAsync():
                    return VideoEncoder.QSV;
                case VideoEncoder.CPUFast:
                case VideoEncoder.CPUQuality:
                    return _preferredEncoder;
                default:
                    // 사용 불가능하면 Auto로 폴백
                    Log($"[Video] 지정된 인코더 {_preferredEncoder} 사용 불가, 자동 선택으로 전환");
                    break;
            }
        }

        // 자동 선택: NVENC > QSV > CPU Fast
        if (await IsNvencAvailableAsync()) return VideoEncoder.NVENC;
        if (await IsQsvAvailableAsync()) return VideoEncoder.QSV;
        return VideoEncoder.CPUFast; // CPU 기본값을 빠른 프리셋으로 변경
    }

    /// <summary>
    /// 인코더와 품질에 따른 FFmpeg 옵션 생성
    /// </summary>
    private string GetEncodingOptions(VideoEncoder encoder, VideoQuality quality)
    {
        // 품질에 따른 설정
        // NVENC/QSV는 CRF 대신 CQ (Constant Quality) 사용
        // CQ 값은 CRF와 유사하게 동작 (낮을수록 고품질)
        var (crf, cq, audioBitrate) = quality switch
        {
            VideoQuality.High => (23, 23, "128k"),
            VideoQuality.Normal => (28, 28, "96k"),
            _ => (28, 28, "96k")
        };

        return encoder switch
        {
            // NVIDIA NVENC - 가장 빠름 (10배 이상)
            VideoEncoder.NVENC => $"-c:v hevc_nvenc -preset p4 -rc constqp -qp {cq} -c:a aac -b:a {audioBitrate}",

            // Intel Quick Sync - 빠름
            VideoEncoder.QSV => $"-c:v hevc_qsv -preset medium -global_quality {cq} -c:a aac -b:a {audioBitrate}",

            // CPU 빠른 프리셋 - 3배 빠름 (파일 약간 커짐)
            VideoEncoder.CPUFast => $"-c:v libx265 -preset ultrafast -crf {crf} -c:a aac -b:a {audioBitrate}",

            // CPU 품질 프리셋 - 기존 방식 (느리지만 최적 압축)
            VideoEncoder.CPUQuality => $"-c:v libx265 -preset medium -crf {crf} -c:a aac -b:a {audioBitrate}",

            _ => $"-c:v libx265 -preset ultrafast -crf {crf} -c:a aac -b:a {audioBitrate}"
        };
    }

    /// <summary>
    /// 품질별 FFmpeg 인코딩 옵션 (기존 호환용 - 자동 인코더 선택)
    /// </summary>
    private async Task<(string options, VideoEncoder usedEncoder)> GetEncodingOptionsAsync(VideoQuality quality)
    {
        var encoder = await GetBestEncoderAsync();
        var options = GetEncodingOptions(encoder, quality);
        return (options, encoder);
    }

    /// <summary>
    /// 동영상 압축 변환
    /// </summary>
    public async Task<bool> CompressAsync(string inputPath, VideoQuality quality, string? outputPath = null)
    {
        _isCancelled = false;

        var ffmpeg = FindFFmpeg();
        if (ffmpeg == null)
        {
            Log("[Video] FFmpeg를 찾을 수 없습니다.");
            ConversionCompleted?.Invoke(this, new VideoConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = "FFmpeg를 찾을 수 없습니다."
            });
            return false;
        }

        if (!File.Exists(inputPath))
        {
            Log($"[Video] 원본 파일 없음: {inputPath}");
            ConversionCompleted?.Invoke(this, new VideoConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = $"원본 파일을 찾을 수 없습니다: {inputPath}"
            });
            return false;
        }

        var inputFileInfo = new FileInfo(inputPath);
        var inputSizeMB = inputFileInfo.Length / (1024.0 * 1024);
        Log($"[Video] 입력 파일: {inputPath}");
        Log($"[Video] 입력 파일 크기: {inputSizeMB:F1} MB");

        // 출력 파일명 생성 (_compressed 접미사)
        var dir = Path.GetDirectoryName(inputPath) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(inputPath);
        var ext = Path.GetExtension(inputPath);
        var output = outputPath ?? Path.Combine(dir, $"{nameWithoutExt}_compressed{ext}");

        // 자동 인코더 선택 및 옵션 생성
        var (encodingOptions, usedEncoder) = await GetEncodingOptionsAsync(quality);
        var qualityName = GetDisplayName(quality);
        var encoderName = GetEncoderDisplayName(usedEncoder);

        // 타임아웃: GPU는 빠르므로 짧게, CPU는 길게
        // NVENC/QSV: 100MB당 1분, CPU: 100MB당 5분
        var baseMinutes = usedEncoder is VideoEncoder.NVENC or VideoEncoder.QSV ? 1 : 5;
        var timeoutMinutes = Math.Clamp((int)(inputSizeMB / 100) * baseMinutes + 5, 5, 180);
        var timeout = TimeSpan.FromMinutes(timeoutMinutes);
        Log($"[Video] 품질: {qualityName}, 인코더: {encoderName}");
        Log($"[Video] 타임아웃: {timeoutMinutes}분");

        try
        {
            ProgressChanged?.Invoke(this, new VideoConversionProgressEventArgs
            {
                Status = $"{encoderName}로 압축 중...",
                Progress = 0,
                CurrentTime = TimeSpan.Zero,
                TotalDuration = TimeSpan.Zero
            });

            // 기존 출력 파일 삭제
            if (File.Exists(output))
            {
                try { File.Delete(output); }
                catch (Exception ex)
                {
                    Log($"[Video] 기존 파일 삭제 실패: {ex.Message}");
                }
            }

            // 먼저 영상 길이 확인
            var duration = await GetVideoDurationAsync(ffmpeg, inputPath);
            Log($"[Video] 영상 길이: {duration}");

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{inputPath}\" {encodingOptions} -y -progress pipe:1 \"{output}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                WorkingDirectory = Path.GetDirectoryName(output) ?? Environment.CurrentDirectory,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            Log($"[Video] 명령어: {ffmpeg} {startInfo.Arguments}");

            using var process = new Process { StartInfo = startInfo };
            _currentProcess = process;
            using var cts = new CancellationTokenSource(timeout);

            var errorLines = new List<string>();

            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    if (errorLines.Count >= 50) errorLines.RemoveAt(0);
                    errorLines.Add(e.Data);
                }
            };

            process.OutputDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    // progress 파싱 (out_time_ms=xxx)
                    if (e.Data.StartsWith("out_time_ms="))
                    {
                        if (long.TryParse(e.Data.Substring(12), out var microseconds))
                        {
                            var currentTime = TimeSpan.FromMilliseconds(microseconds / 1000.0);
                            var progress = duration.TotalSeconds > 0
                                ? (int)(currentTime.TotalSeconds / duration.TotalSeconds * 100)
                                : 0;
                            progress = Math.Clamp(progress, 0, 99);

                            ProgressChanged?.Invoke(this, new VideoConversionProgressEventArgs
                            {
                                Status = $"압축 중... {progress}%",
                                Progress = progress,
                                CurrentTime = currentTime,
                                TotalDuration = duration
                            });
                        }
                    }
                }
            };

            process.Start();
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            process.BeginErrorReadLine();
            process.BeginOutputReadLine();

            Log($"[Video] 프로세스 시작 (PID: {process.Id})");

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                if (_isCancelled)
                {
                    Log("[Video] 사용자가 취소함");
                }
                else
                {
                    Log($"[Video] 타임아웃 ({timeoutMinutes}분)");
                }
                try { process.Kill(true); } catch { }

                ConversionCompleted?.Invoke(this, new VideoConversionCompletedEventArgs
                {
                    Success = false,
                    ErrorMessage = _isCancelled ? "사용자가 취소했습니다." : $"변환 시간 초과 ({timeoutMinutes}분)"
                });
                return false;
            }
            finally
            {
                _currentProcess = null;
            }

            var errorOutput = string.Join(Environment.NewLine, errorLines.TakeLast(10));
            Log($"[Video] ExitCode: {process.ExitCode}");

            if (process.ExitCode == 0 && File.Exists(output))
            {
                var outputSize = new FileInfo(output).Length;
                var inputSize = inputFileInfo.Length;
                var compressionRatio = (1 - (double)outputSize / inputSize) * 100;

                Log($"[Video] 변환 성공: {outputSize / (1024.0 * 1024):F1} MB (압축률: {compressionRatio:F1}%)");

                ProgressChanged?.Invoke(this, new VideoConversionProgressEventArgs
                {
                    Status = "완료!",
                    Progress = 100,
                    CurrentTime = duration,
                    TotalDuration = duration
                });

                ConversionCompleted?.Invoke(this, new VideoConversionCompletedEventArgs
                {
                    Success = true,
                    OutputPath = output,
                    Quality = quality,
                    OriginalSize = inputSize,
                    ConvertedSize = outputSize,
                    CompressionRatio = compressionRatio
                });
                return true;
            }
            else
            {
                Log($"[Video] 변환 실패 - ExitCode: {process.ExitCode}");
                if (!string.IsNullOrEmpty(errorOutput))
                {
                    Log($"[Video] 오류:\n{errorOutput}");
                }

                ConversionCompleted?.Invoke(this, new VideoConversionCompletedEventArgs
                {
                    Success = false,
                    ErrorMessage = $"변환 실패 (코드: {process.ExitCode})"
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"[Video] 예외 발생: {ex.Message}");
            ConversionCompleted?.Invoke(this, new VideoConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = $"변환 중 오류: {ex.Message}"
            });
            return false;
        }
    }

    /// <summary>
    /// 영상 길이 확인
    /// </summary>
    private async Task<TimeSpan> GetVideoDurationAsync(string ffmpegPath, string inputPath)
    {
        try
        {
            var ffprobePath = Path.Combine(Path.GetDirectoryName(ffmpegPath) ?? "", "ffprobe.exe");
            var executable = File.Exists(ffprobePath) ? ffprobePath : ffmpegPath;
            var args = executable == ffprobePath
                ? $"-v error -show_entries format=duration -of default=noprint_wrappers=1:nokey=1 \"{inputPath}\""
                : $"-i \"{inputPath}\" -f null -";

            var startInfo = new ProcessStartInfo
            {
                FileName = executable,
                Arguments = args,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardErrorEncoding = Encoding.UTF8,
                StandardOutputEncoding = Encoding.UTF8
            };

            using var process = new Process { StartInfo = startInfo };
            var output = new StringBuilder();
            var error = new StringBuilder();

            process.OutputDataReceived += (s, e) => { if (e.Data != null) output.AppendLine(e.Data); };
            process.ErrorDataReceived += (s, e) => { if (e.Data != null) error.AppendLine(e.Data); };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            await process.WaitForExitAsync();

            // ffprobe 출력에서 duration 파싱
            if (double.TryParse(output.ToString().Trim(), out var seconds))
            {
                return TimeSpan.FromSeconds(seconds);
            }

            // ffmpeg 오류 출력에서 Duration 파싱
            var durationMatch = Regex.Match(error.ToString(), @"Duration: (\d{2}):(\d{2}):(\d{2})\.(\d{2})");
            if (durationMatch.Success)
            {
                return new TimeSpan(0,
                    int.Parse(durationMatch.Groups[1].Value),
                    int.Parse(durationMatch.Groups[2].Value),
                    int.Parse(durationMatch.Groups[3].Value),
                    int.Parse(durationMatch.Groups[4].Value) * 10);
            }
        }
        catch (Exception ex)
        {
            Log($"[Video] 영상 길이 확인 실패: {ex.Message}");
        }

        return TimeSpan.Zero;
    }

    /// <summary>
    /// 변환 취소
    /// </summary>
    public void Cancel()
    {
        _isCancelled = true;
        try
        {
            _currentProcess?.Kill(true);
        }
        catch { }
    }
}

public class VideoConversionProgressEventArgs : EventArgs
{
    public string Status { get; init; } = "";
    public int Progress { get; init; }
    public TimeSpan CurrentTime { get; init; }
    public TimeSpan TotalDuration { get; init; }
}

public class VideoConversionCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? ErrorMessage { get; init; }
    public VideoQuality Quality { get; init; }
    public long OriginalSize { get; init; }
    public long ConvertedSize { get; init; }
    public double CompressionRatio { get; init; }
}
