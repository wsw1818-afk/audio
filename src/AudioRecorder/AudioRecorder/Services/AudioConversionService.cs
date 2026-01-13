using System.Diagnostics;
using System.IO;

namespace AudioRecorder.Services;

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
    private string? _ffmpegPath;
    private readonly string _logPath;

    public event EventHandler<AudioConversionProgressEventArgs>? ProgressChanged;
    public event EventHandler<AudioConversionCompletedEventArgs>? ConversionCompleted;

    public bool IsFFmpegAvailable => FindFFmpeg() != null;

    public AudioConversionService()
    {
        // 로그 파일 경로 설정 (바탕화면에 저장 - 쉽게 확인 가능)
        var desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        _logPath = Path.Combine(desktopPath, "ffmpeg_conversion.log");
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

        // 1. 앱 폴더에서 찾기
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var localPath = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(localPath))
        {
            _ffmpegPath = localPath;
            return _ffmpegPath;
        }

        // 2. PATH 환경변수에서 찾기
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
            {
                _ffmpegPath = path;
                return _ffmpegPath;
            }
        }

        return null;
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

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                // -stats 제거하고 -loglevel error로 출력 최소화 (대용량 파일 안정성)
                Arguments = $"-i \"{inputPath}\" {encodingOptions} -y -loglevel error \"{output}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
                // 작업 디렉토리를 출력 폴더로 설정
                WorkingDirectory = Path.GetDirectoryName(output) ?? Environment.CurrentDirectory
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
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

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
                try { process.Kill(true); } catch { }

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
                    try { File.Delete(output); } catch { }
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
