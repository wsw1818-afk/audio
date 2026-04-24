using System.Diagnostics;
using System.IO;

namespace AudioRecorder.Services;

/// <summary>
/// 노이즈 제거 강도
/// </summary>
public enum NoiseReductionLevel
{
    Light,   // 약함 - 미세한 노이즈만 제거
    Medium,  // 보통 - 일반적인 배경 소음 제거
    Strong   // 강함 - 강한 노이즈 제거 (음성 왜곡 가능)
}

/// <summary>
/// 오디오 포맷 종류
/// </summary>
public enum AudioFormat
{
    WAV,        // 원본 (무손실)
    FLAC,       // 무손실 압축 (50-60% 크기 감소)
    MP3_320,    // 고품질 MP3 (320kbps) - 음악용
    MP3_192,    // 표준 MP3 (192kbps)
    MP3_128,    // 표준 MP3 (128kbps) - 음성용
    AAC_256,    // 고품질 AAC/M4A (256kbps)
    OGG_320     // Ogg Vorbis (320kbps)
}

/// <summary>
/// 고음질 오디오 변환 서비스 (FFmpeg 기반)
/// </summary>
public class AudioConversionService
{
    private readonly string _logPath;

    public event EventHandler<AudioConversionProgressEventArgs>? ProgressChanged;
    public event EventHandler<AudioConversionCompletedEventArgs>? ConversionCompleted;

    // 프로세스 수명 동안 FFmpeg 경로 1회만 탐색 (UI 폴링 비용 제거)
    private static readonly Lazy<string?> _cachedFFmpegPath = new(FindFFmpegUncached, isThreadSafe: true);

    public bool IsFFmpegAvailable => _cachedFFmpegPath.Value != null;

    public AudioConversionService()
    {
        // 로그 파일 경로 설정 (%AppData%/AudioRecorder/logs/ 하위에 저장)
        var appDataPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "AudioRecorder", "logs");
        if (!Directory.Exists(appDataPath)) Directory.CreateDirectory(appDataPath);
        _logPath = Path.Combine(appDataPath, "ffmpeg_conversion.log");
    }

    private void Log(string message)
    {
        var logMessage = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}";
        Debug.WriteLine(logMessage);
        try
        {
            File.AppendAllText(_logPath, logMessage + Environment.NewLine);
        }
        catch (Exception ex) { Debug.WriteLine($"[AudioConversion] {ex.Message}"); }
    }

    // 캐시된 경로 반환 (Lazy<>로 프로세스당 1회만 탐색)
    public string? FindFFmpeg() => _cachedFFmpegPath.Value;

    // 실제 탐색 로직 (Lazy 초기화 시 1회만 호출)
    private static string? FindFFmpegUncached()
    {
        // 1. 앱 폴더에서 찾기
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var localPath = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(localPath))
            return localPath;

        // 2. PATH 환경변수에서 찾기
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var dir in pathEnv.Split(Path.PathSeparator))
        {
            var ffmpegInPath = Path.Combine(dir, "ffmpeg.exe");
            if (File.Exists(ffmpegInPath))
                return ffmpegInPath;
        }

        // 3. 일반적인 설치 경로 확인
        var commonPaths = new[]
        {
            @"C:\ffmpeg\bin\ffmpeg.exe",
            @"C:\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe",
            @"C:\Program Files\ffmpeg\bin\ffmpeg.exe",
            @"C:\Program Files (x86)\ffmpeg\bin\ffmpeg.exe",
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ffmpeg", "bin", "ffmpeg.exe")
        };

        foreach (var path in commonPaths)
        {
            if (File.Exists(path))
                return path;
        }

        return null;
    }

    /// <summary>
    /// FFmpeg 인자로 사용되는 파일 경로에서 위험한 문자를 제거하여 명령어 인젝션을 방어
    /// </summary>
    private static string SanitizeFFmpegPath(string path)
    {
        if (string.IsNullOrEmpty(path)) return path;

        // 명령어 인젝션에 사용될 수 있는 위험 문자 제거
        char[] dangerousChars = { '"', '|', '&', ';', '`', '$', '(', ')' };
        var sanitized = path;
        foreach (var c in dangerousChars)
        {
            sanitized = sanitized.Replace(c.ToString(), "");
        }
        return sanitized;
    }

    /// <summary>
    /// 포맷별 FFmpeg 인코딩 옵션 생성
    /// </summary>
    private string GetEncodingOptions(AudioFormat format)
    {
        // -threads 1: 단일 스레드로 메모리 사용량 감소 (대용량 파일 안정성)
        // -reservoir 0: MP3 비트 저장소 비활성화 (스트리밍 안정성)
        return format switch
        {
            AudioFormat.FLAC => "-threads 1 -codec:a flac -compression_level 5",
            AudioFormat.MP3_320 => "-threads 1 -codec:a libmp3lame -b:a 320k -q:a 0",
            AudioFormat.MP3_192 => "-threads 1 -codec:a libmp3lame -b:a 192k",
            AudioFormat.MP3_128 => "-threads 1 -codec:a libmp3lame -b:a 128k",
            AudioFormat.AAC_256 => "-threads 1 -codec:a aac -b:a 256k",
            AudioFormat.OGG_320 => "-threads 1 -codec:a libvorbis -b:a 320k",
            _ => "-codec:a copy"
        };
    }

    /// <summary>
    /// 포맷별 파일 확장자
    /// </summary>
    public static string GetExtension(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.FLAC => ".flac",
            AudioFormat.MP3_320 or AudioFormat.MP3_192 or AudioFormat.MP3_128 => ".mp3",
            AudioFormat.AAC_256 => ".m4a",
            AudioFormat.OGG_320 => ".ogg",
            _ => ".wav"
        };
    }

    /// <summary>
    /// 포맷 표시 이름
    /// </summary>
    public static string GetDisplayName(AudioFormat format)
    {
        return format switch
        {
            AudioFormat.WAV => "WAV (무손실 원본)",
            AudioFormat.FLAC => "FLAC (무손실 압축)",
            AudioFormat.MP3_320 => "MP3 320kbps (음악용)",
            AudioFormat.MP3_192 => "MP3 192kbps (표준)",
            AudioFormat.MP3_128 => "MP3 128kbps (음성용)",
            AudioFormat.AAC_256 => "AAC/M4A 256kbps",
            AudioFormat.OGG_320 => "OGG Vorbis 320kbps",
            _ => format.ToString()
        };
    }

    /// <summary>
    /// 오디오 파일 변환 (대용량 파일 지원 - 최대 30분 타임아웃)
    /// </summary>
    public async Task<bool> ConvertAsync(string inputPath, AudioFormat format, string? outputPath = null)
    {
        var ffmpeg = FindFFmpeg();
        if (ffmpeg == null)
        {
            Log("[FFmpeg] FFmpeg를 찾을 수 없습니다.");
            ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = "FFmpeg를 찾을 수 없습니다. FFmpeg를 설치하거나 앱 폴더에 ffmpeg.exe를 복사하세요."
            });
            return false;
        }

        if (!File.Exists(inputPath))
        {
            Log($"[FFmpeg] 원본 파일 없음: {inputPath}");
            ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = $"원본 파일을 찾을 수 없습니다: {inputPath}"
            });
            return false;
        }

        var inputFileInfo = new FileInfo(inputPath);
        var inputSizeMB = inputFileInfo.Length / (1024.0 * 1024);
        Log($"[FFmpeg] 입력 파일 크기: {inputSizeMB:F1} MB");

        // WAV 파일인 경우 헤더 검증 및 복구 (대용량 파일 손상 방지)
        if (inputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                RepairWavHeaderIfNeeded(inputPath);
            }
            catch (Exception ex)
            {
                Log($"[FFmpeg] WAV 헤더 복구 실패: {ex.Message}");
            }
        }

        var extension = GetExtension(format);
        var output = outputPath ?? Path.ChangeExtension(inputPath, extension);
        var encodingOptions = GetEncodingOptions(format);
        var formatName = GetDisplayName(format);

        // 파일 크기에 따른 타임아웃 계산 (50MB당 1분, 최소 3분, 최대 60분)
        // 2시간 녹음 (약 2GB) = 약 43분 타임아웃
        var timeoutMinutes = Math.Clamp((int)(inputSizeMB / 50) + 3, 3, 60);
        var timeout = TimeSpan.FromMinutes(timeoutMinutes);
        Log($"[FFmpeg] 타임아웃: {timeoutMinutes}분 (파일 크기: {inputSizeMB:F0}MB)");

        try
        {
            ProgressChanged?.Invoke(this, new AudioConversionProgressEventArgs
            {
                Status = $"{formatName}로 변환 중...",
                Progress = 0
            });

            // 기존 출력 파일이 있으면 먼저 삭제
            if (File.Exists(output))
            {
                try { File.Delete(output); }
                catch (Exception ex)
                {
                    Log($"[FFmpeg] 기존 파일 삭제 실패: {ex.Message}");
                }
            }

            // 한글 경로 지원: 입력/출력 파일이 같은 폴더에 있으면 상대 경로 사용
            var workDir = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
            var inputFileName = SanitizeFFmpegPath(Path.GetFileName(inputPath));
            var outputFileName = SanitizeFFmpegPath(Path.GetFileName(output));

            // 출력 파일이 다른 폴더면 절대 경로 사용
            var outputDir = Path.GetDirectoryName(output);
            var useRelativePath = string.Equals(workDir, outputDir, StringComparison.OrdinalIgnoreCase);
            var outputArg = useRelativePath ? outputFileName : SanitizeFFmpegPath(output);

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                // -stats 제거하고 -loglevel error로 출력 최소화 (대용량 파일 안정성)
                // 한글 경로 지원: 작업 디렉토리 기준 상대 경로 사용
                Arguments = $"-i \"{inputFileName}\" {encodingOptions} -y -loglevel error \"{outputArg}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                // 한글 경로 지원을 위한 UTF-8 인코딩 설정
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                // 작업 디렉토리를 입력 파일 폴더로 설정 (상대 경로 사용)
                WorkingDirectory = workDir
            };

            Log($"[FFmpeg] 명령어: {ffmpeg} {startInfo.Arguments}");
            Log($"[FFmpeg] 작업 디렉토리: {startInfo.WorkingDirectory}");

            using var process = new Process { StartInfo = startInfo };
            using var cts = new CancellationTokenSource(timeout);

            var errorLines = new List<string>();
            process.ErrorDataReceived += (s, e) =>
            {
                if (!string.IsNullOrEmpty(e.Data))
                {
                    // 마지막 50줄만 유지 (메모리 절약)
                    if (errorLines.Count >= 50)
                        errorLines.RemoveAt(0);
                    errorLines.Add(e.Data);
                    Log($"[FFmpeg] {e.Data}");
                }
            };

            process.Start();

            // 프로세스 우선순위를 낮춰서 시스템 안정성 확보
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception ex) { Debug.WriteLine($"[AudioConversion] {ex.Message}"); }

            process.BeginErrorReadLine();
            // stdout도 읽어야 프로세스가 블록되지 않음
            process.BeginOutputReadLine();

            Log($"[FFmpeg] 프로세스 시작 (PID: {process.Id})");

            try
            {
                await process.WaitForExitAsync(cts.Token);
            }
            catch (OperationCanceledException)
            {
                Log($"[FFmpeg] 타임아웃 ({timeoutMinutes}분) - 프로세스 강제 종료");
                try { process.Kill(true); } catch (Exception ex) { Debug.WriteLine($"[AudioConversion] {ex.Message}"); }

                ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
                {
                    Success = false,
                    ErrorMessage = $"변환 시간 초과 ({timeoutMinutes}분). 파일이 너무 크거나 시스템이 바쁩니다."
                });
                return false;
            }

            var errorOutput = string.Join(Environment.NewLine, errorLines.TakeLast(10));
            Log($"[FFmpeg] ExitCode: {process.ExitCode}");
            if (!string.IsNullOrEmpty(errorOutput))
            {
                Log($"[FFmpeg] 마지막 출력:\n{errorOutput}");
            }

            // 파일이 완전히 쓰여질 때까지 잠시 대기 (대용량 파일 버퍼 플러시)
            await Task.Delay(100);

            if (process.ExitCode == 0 && File.Exists(output))
            {
                var outputSize = new FileInfo(output).Length;
                var inputSize = inputFileInfo.Length;

                Log($"[FFmpeg] 출력 파일 크기: {outputSize} bytes ({outputSize / (1024.0 * 1024):F1} MB)");

                // 출력 파일이 너무 작으면 실패로 처리
                // MP3 128kbps: 약 1MB/분, 최소 10KB 이상이어야 함
                var minOutputSize = Math.Max(1024, inputSize / 200); // 입력의 0.5% 이상
                if (outputSize < minOutputSize)
                {
                    Log($"[FFmpeg] 출력 파일이 너무 작음: {outputSize} bytes (최소 {minOutputSize} bytes 필요)");
                    try { File.Delete(output); } catch (Exception ex) { Debug.WriteLine($"[AudioConversion] {ex.Message}"); }
                    ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
                    {
                        Success = false,
                        ErrorMessage = $"변환된 파일이 손상되었습니다. (크기: {outputSize} bytes)"
                    });
                    return false;
                }

                var compressionRatio = (1 - (double)outputSize / inputSize) * 100;
                Log($"[FFmpeg] 변환 성공: {outputSize / (1024.0 * 1024):F1} MB (압축률: {compressionRatio:F1}%)");

                ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
                {
                    Success = true,
                    OutputPath = output,
                    Format = format,
                    OriginalSize = inputSize,
                    ConvertedSize = outputSize,
                    CompressionRatio = compressionRatio
                });
                return true;
            }
            else
            {
                Log($"[FFmpeg] 변환 실패 - ExitCode: {process.ExitCode}, FileExists: {File.Exists(output)}");
                ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
                {
                    Success = false,
                    ErrorMessage = $"변환 실패 (코드: {process.ExitCode}): {errorOutput}"
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            Log($"[FFmpeg] 예외 발생: {ex.Message}");
            ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = $"변환 중 오류: {ex.Message}"
            });
            return false;
        }
    }

    /// <summary>
    /// MP3 변환 (기존 호환)
    /// </summary>
    public Task<bool> ConvertToMp3Async(string wavPath, string? outputPath = null, int bitrate = 192)
    {
        var format = bitrate >= 256 ? AudioFormat.MP3_320 : AudioFormat.MP3_192;
        return ConvertAsync(wavPath, format, outputPath);
    }

    /// <summary>
    /// FLAC 변환 (무손실)
    /// </summary>
    public Task<bool> ConvertToFlacAsync(string wavPath, string? outputPath = null)
    {
        return ConvertAsync(wavPath, AudioFormat.FLAC, outputPath);
    }

    /// <summary>
    /// 노이즈 제거 (FFmpeg afftdn 필터 사용)
    /// </summary>
    public async Task<bool> RemoveNoiseAsync(string inputPath, string? outputPath = null, NoiseReductionLevel level = NoiseReductionLevel.Medium)
    {
        var ffmpeg = FindFFmpeg();
        if (ffmpeg == null)
        {
            Log("[FFmpeg] FFmpeg를 찾을 수 없습니다.");
            ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = "FFmpeg를 찾을 수 없습니다."
            });
            return false;
        }

        if (!File.Exists(inputPath))
        {
            Log($"[FFmpeg] 원본 파일 없음: {inputPath}");
            return false;
        }

        var output = outputPath ?? Path.Combine(
            Path.GetDirectoryName(inputPath) ?? "",
            Path.GetFileNameWithoutExtension(inputPath) + "_denoised" + Path.GetExtension(inputPath));

        // 노이즈 제거 강도 설정
        var noiseFloor = level switch
        {
            NoiseReductionLevel.Light => -25,
            NoiseReductionLevel.Medium => -30,
            NoiseReductionLevel.Strong => -40,
            _ => -30
        };

        var levelName = level switch
        {
            NoiseReductionLevel.Light => "약함",
            NoiseReductionLevel.Medium => "보통",
            NoiseReductionLevel.Strong => "강함",
            _ => "보통"
        };

        Log($"[FFmpeg] 노이즈 제거 시작: {inputPath} (강도: {levelName})");

        try
        {
            ProgressChanged?.Invoke(this, new AudioConversionProgressEventArgs
            {
                Status = $"노이즈 제거 중 ({levelName})...",
                Progress = 0
            });

            var workDir = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
            var inputFileName = SanitizeFFmpegPath(Path.GetFileName(inputPath));
            var outputFileName = SanitizeFFmpegPath(Path.GetFileName(output));

            // FFmpeg 노이즈 제거 필터
            // afftdn: FFT 기반 노이즈 제거
            // highpass: 저주파 노이즈 제거 (에어컨 등)
            // lowpass: 고주파 노이즈 제거 (전자기기 등)
            var filterChain = $"afftdn=nf={noiseFloor}:tn=1,highpass=f=80,lowpass=f=8000";

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{inputFileName}\" -af \"{filterChain}\" -y \"{outputFileName}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = workDir
            };

            Log($"[FFmpeg] 명령어: {ffmpeg} {startInfo.Arguments}");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(output))
            {
                var inputSize = new FileInfo(inputPath).Length;
                var outputSize = new FileInfo(output).Length;
                Log($"[FFmpeg] 노이즈 제거 완료: {output}");

                ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
                {
                    Success = true,
                    OutputPath = output,
                    OriginalSize = inputSize,
                    ConvertedSize = outputSize
                });
                return true;
            }

            Log($"[FFmpeg] 노이즈 제거 실패: ExitCode={process.ExitCode}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[FFmpeg] 노이즈 제거 오류: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 구간 추출 (특정 시간대만 잘라내기)
    /// </summary>
    public async Task<bool> ExtractSegmentAsync(string inputPath, TimeSpan startTime, TimeSpan endTime, string? outputPath = null)
    {
        var ffmpeg = FindFFmpeg();
        if (ffmpeg == null)
        {
            Log("[FFmpeg] FFmpeg를 찾을 수 없습니다.");
            return false;
        }

        if (!File.Exists(inputPath))
        {
            Log($"[FFmpeg] 원본 파일 없음: {inputPath}");
            return false;
        }

        var duration = endTime - startTime;
        if (duration.TotalSeconds <= 0)
        {
            Log("[FFmpeg] 잘못된 시간 범위");
            return false;
        }

        var output = outputPath ?? Path.Combine(
            Path.GetDirectoryName(inputPath) ?? "",
            Path.GetFileNameWithoutExtension(inputPath) + $"_{startTime:mm\\.ss}-{endTime:mm\\.ss}" + Path.GetExtension(inputPath));

        Log($"[FFmpeg] 구간 추출: {startTime:mm\\:ss} ~ {endTime:mm\\:ss}");

        try
        {
            ProgressChanged?.Invoke(this, new AudioConversionProgressEventArgs
            {
                Status = $"구간 추출 중 ({startTime:mm\\:ss} ~ {endTime:mm\\:ss})...",
                Progress = 0
            });

            var workDir = Path.GetDirectoryName(inputPath) ?? Environment.CurrentDirectory;
            var inputFileName = SanitizeFFmpegPath(Path.GetFileName(inputPath));
            var outputFileName = SanitizeFFmpegPath(Path.GetFileName(output));

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{inputFileName}\" -ss {startTime:hh\\:mm\\:ss\\.fff} -t {duration:hh\\:mm\\:ss\\.fff} -c copy -y \"{outputFileName}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardErrorEncoding = System.Text.Encoding.UTF8,
                WorkingDirectory = workDir
            };

            Log($"[FFmpeg] 명령어: {ffmpeg} {startInfo.Arguments}");

            using var process = new Process { StartInfo = startInfo };
            process.Start();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(output))
            {
                Log($"[FFmpeg] 구간 추출 완료: {output}");

                ConversionCompleted?.Invoke(this, new AudioConversionCompletedEventArgs
                {
                    Success = true,
                    OutputPath = output
                });
                return true;
            }

            Log($"[FFmpeg] 구간 추출 실패: ExitCode={process.ExitCode}");
            return false;
        }
        catch (Exception ex)
        {
            Log($"[FFmpeg] 구간 추출 오류: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// WAV 파일 헤더가 손상된 경우 복구
    /// 대용량 파일(3GB+)에서 헤더가 손상되는 경우 FFmpeg가 읽지 못하는 문제 해결
    /// </summary>
    private void RepairWavHeaderIfNeeded(string filePath)
    {
        if (!File.Exists(filePath)) return;

        using var fs = new FileStream(filePath, FileMode.Open, FileAccess.ReadWrite);
        if (fs.Length < 44) return; // WAV 헤더 최소 크기

        var header = new byte[44];
        fs.Read(header, 0, 44);

        // RIFF 마커 확인 (offset 0-3)
        bool hasRiffMarker = header[0] == 'R' && header[1] == 'I' && header[2] == 'F' && header[3] == 'F';

        // WAVE 마커 확인 (offset 8-11)
        bool hasWaveMarker = header[8] == 'W' && header[9] == 'A' && header[10] == 'V' && header[11] == 'E';

        // fmt 청크 확인 (offset 12-15)
        bool hasFmtChunk = header[12] == 'f' && header[13] == 'm' && header[14] == 't' && header[15] == ' ';

        if (hasRiffMarker && hasWaveMarker && hasFmtChunk)
        {
            Log("[FFmpeg] WAV 헤더 정상");
            return;
        }

        Log($"[FFmpeg] WAV 헤더 손상 감지 - RIFF:{hasRiffMarker}, WAVE:{hasWaveMarker}, fmt:{hasFmtChunk}");
        Log($"[FFmpeg] 헤더 hex: {BitConverter.ToString(header, 0, 20)}");

        fs.Close();

        // 파일 복구
        RepairWavFile(filePath);
    }

    /// <summary>
    /// WAV 파일을 새로 생성하여 올바른 헤더와 함께 저장
    /// </summary>
    private void RepairWavFile(string filePath)
    {
        var tempPath = filePath + ".repair.tmp";
        const int COPY_BUFFER_SIZE = 4 * 1024 * 1024; // 4MB 청크

        Log($"[FFmpeg] WAV 파일 복구 시작: {filePath}");

        try
        {
            using (var srcFs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, COPY_BUFFER_SIZE))
            using (var dstFs = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, COPY_BUFFER_SIZE))
            {
                // 헤더 영역 분석
                var headerArea = new byte[64];
                srcFs.Read(headerArea, 0, Math.Min(64, (int)Math.Min(srcFs.Length, 64)));
                srcFs.Seek(0, SeekOrigin.Begin);

                long audioDataStart;

                // offset 8-43 영역이 대부분 0인지 확인 (null 헤더 패턴)
                int nullCount = 0;
                for (int i = 8; i < 44 && i < headerArea.Length; i++)
                {
                    if (headerArea[i] == 0) nullCount++;
                }

                if (nullCount > 30)
                {
                    audioDataStart = 44;
                    Log("[FFmpeg] 복구 패턴: null 헤더 (offset 44부터 데이터)");
                }
                else
                {
                    audioDataStart = 8;
                    Log("[FFmpeg] 복구 패턴: 헤더 없음 (offset 8부터 데이터)");
                }

                long audioDataSize = srcFs.Length - audioDataStart;
                Log($"[FFmpeg] 복구: 원본={srcFs.Length / (1024.0 * 1024.0):F1}MB, 오디오={audioDataSize / (1024.0 * 1024.0):F1}MB");

                // 표준 WAV 헤더 작성 (48kHz, 16bit, Stereo 가정)
                WriteWavHeader(dstFs, audioDataSize, 48000, 16, 2);

                // 오디오 데이터 복사
                srcFs.Seek(audioDataStart, SeekOrigin.Begin);
                var buffer = new byte[COPY_BUFFER_SIZE];
                long totalCopied = 0;
                int bytesRead;

                while ((bytesRead = srcFs.Read(buffer, 0, buffer.Length)) > 0)
                {
                    dstFs.Write(buffer, 0, bytesRead);
                    totalCopied += bytesRead;

                    if (totalCopied % (100 * 1024 * 1024) == 0) // 100MB마다 로그
                    {
                        Log($"[FFmpeg] 복구 진행: {totalCopied / (1024.0 * 1024.0):F0}MB / {audioDataSize / (1024.0 * 1024.0):F0}MB");
                    }
                }

                dstFs.Flush();
            }

            // 원본 백업 후 복구 파일로 교체
            var backupPath = filePath + ".damaged";
            if (File.Exists(backupPath)) File.Delete(backupPath);
            File.Move(filePath, backupPath);
            File.Move(tempPath, filePath);

            Log($"[FFmpeg] WAV 파일 복구 완료 (손상된 원본: {backupPath})");
        }
        catch (Exception ex)
        {
            Log($"[FFmpeg] WAV 파일 복구 실패: {ex.Message}");
            if (File.Exists(tempPath)) File.Delete(tempPath);
            throw;
        }
    }

    /// <summary>
    /// WAV 헤더 작성 (44바이트 표준 PCM 헤더)
    /// </summary>
    private void WriteWavHeader(FileStream fs, long dataSize, int sampleRate, int bitsPerSample, int channels)
    {
        var blockAlign = channels * bitsPerSample / 8;
        var byteRate = sampleRate * blockAlign;

        // WAV 파일 최대 크기 제한 (4GB - 8)
        var maxSize = uint.MaxValue - 8;
        var fileSizeField = (uint)Math.Min(dataSize + 36, maxSize);
        var dataSizeField = (uint)Math.Min(dataSize, maxSize);

        using var bw = new BinaryWriter(fs, System.Text.Encoding.UTF8, leaveOpen: true);

        // RIFF 청크
        bw.Write(new char[] { 'R', 'I', 'F', 'F' });
        bw.Write(fileSizeField);
        bw.Write(new char[] { 'W', 'A', 'V', 'E' });

        // fmt 청크
        bw.Write(new char[] { 'f', 'm', 't', ' ' });
        bw.Write(16); // fmt 청크 크기
        bw.Write((short)1); // PCM 포맷
        bw.Write((short)channels);
        bw.Write(sampleRate);
        bw.Write(byteRate);
        bw.Write((short)blockAlign);
        bw.Write((short)bitsPerSample);

        // data 청크
        bw.Write(new char[] { 'd', 'a', 't', 'a' });
        bw.Write(dataSizeField);
    }
}

public class AudioConversionProgressEventArgs : EventArgs
{
    public string Status { get; init; } = "";
    public int Progress { get; init; }
}

public class AudioConversionCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? ErrorMessage { get; init; }
    public AudioFormat Format { get; init; }
    public long OriginalSize { get; init; }
    public long ConvertedSize { get; init; }
    public double CompressionRatio { get; init; }
}
