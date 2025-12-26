using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using AudioRecorder.Models;

namespace AudioRecorder.Services;

/// <summary>
/// FFmpeg 기반 비디오 인코더 서비스
/// </summary>
public class VideoEncoderService : IDisposable
{
    private Process? _ffmpegProcess;
    private BinaryWriter? _pipeWriter;
    private readonly object _writeLock = new();
    private bool _isEncoding;
    private bool _disposed;
    private string _outputPath = string.Empty;
    private string _tempAudioPath = string.Empty;

    /// <summary>
    /// FFmpeg 실행 파일 경로
    /// </summary>
    public string FFmpegPath { get; set; }

    /// <summary>
    /// FFmpeg 사용 가능 여부
    /// </summary>
    public bool IsFFmpegAvailable => !string.IsNullOrEmpty(FFmpegPath) && File.Exists(FFmpegPath);

    /// <summary>
    /// 인코딩 중 여부
    /// </summary>
    public bool IsEncoding => _isEncoding;

    /// <summary>
    /// 출력 파일 경로
    /// </summary>
    public string OutputPath => _outputPath;

    /// <summary>
    /// 인코딩 진행 이벤트
    /// </summary>
    public event EventHandler<EncodingProgressEventArgs>? ProgressUpdated;

    /// <summary>
    /// 인코딩 완료 이벤트
    /// </summary>
    public event EventHandler<EncodingCompletedEventArgs>? EncodingCompleted;

    public VideoEncoderService()
    {
        // FFmpeg 경로 찾기
        FFmpegPath = FindFFmpeg();
    }

    /// <summary>
    /// FFmpeg 경로 찾기
    /// </summary>
    private static string FindFFmpeg()
    {
        // 1. 앱 폴더
        var appDir = AppDomain.CurrentDomain.BaseDirectory;
        var ffmpegInApp = Path.Combine(appDir, "ffmpeg.exe");
        if (File.Exists(ffmpegInApp))
            return ffmpegInApp;

        // 2. 전역 설치 경로
        var globalPath = @"C:\ffmpeg\ffmpeg-master-latest-win64-gpl\bin\ffmpeg.exe";
        if (File.Exists(globalPath))
            return globalPath;

        // 3. PATH에서 찾기
        var pathEnv = Environment.GetEnvironmentVariable("PATH") ?? "";
        foreach (var path in pathEnv.Split(';'))
        {
            var ffmpegPath = Path.Combine(path.Trim(), "ffmpeg.exe");
            if (File.Exists(ffmpegPath))
                return ffmpegPath;
        }

        return string.Empty;
    }

    /// <summary>
    /// 비디오 인코딩 시작 (파이프 기반 실시간 인코딩)
    /// </summary>
    public void StartEncoding(string outputPath, int width, int height, int frameRate,
        VideoFormat format, int bitrate = 8_000_000, bool useHardwareEncoding = true)
    {
        if (!IsFFmpegAvailable)
            throw new InvalidOperationException("FFmpeg를 찾을 수 없습니다.");

        if (_isEncoding)
            throw new InvalidOperationException("이미 인코딩 중입니다.");

        _outputPath = outputPath;
        _isEncoding = true;

        // 임시 오디오 파일 경로 (나중에 합성용)
        _tempAudioPath = Path.Combine(Path.GetDirectoryName(outputPath) ?? "",
            $"temp_audio_{DateTime.Now:yyyyMMdd_HHmmss}.wav");

        // 소프트웨어 인코딩 (libx264) 사용 - 호환성 최우선
        var arguments = $"-y -f rawvideo -pix_fmt bgra -s {width}x{height} -r {frameRate} -i - " +
                        $"-c:v libx264 -preset fast -b:v {bitrate} " +
                        $"-pix_fmt yuv420p " +
                        $"-movflags +faststart " +
                        $"\"{outputPath}\"";

        Debug.WriteLine($"[VideoEncoder] FFmpeg 시작: {FFmpegPath}");
        Debug.WriteLine($"[VideoEncoder] 인자: {arguments}");

        if (!TryStartFFmpeg(arguments))
        {
            _isEncoding = false;
            throw new InvalidOperationException("FFmpeg 프로세스를 시작할 수 없습니다.");
        }
    }

    /// <summary>
    /// FFmpeg 프로세스 시작 시도
    /// </summary>
    private bool TryStartFFmpeg(string arguments)
    {
        try
        {
            Debug.WriteLine($"[VideoEncoder] FFmpeg 시작 시도 - 경로: {FFmpegPath}, 존재: {File.Exists(FFmpegPath)}");

            var startInfo = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardError = true,
                RedirectStandardOutput = true
            };

            _ffmpegProcess = Process.Start(startInfo);
            if (_ffmpegProcess == null)
            {
                Debug.WriteLine("[VideoEncoder] FFmpeg 프로세스가 null입니다!");
                return false;
            }

            Debug.WriteLine($"[VideoEncoder] FFmpeg 프로세스 시작됨: PID={_ffmpegProcess.Id}");

            _pipeWriter = new BinaryWriter(_ffmpegProcess.StandardInput.BaseStream);

            // 에러 출력 모니터링 (별도 스레드)
            Task.Run(() => MonitorFFmpegOutput());

            return true;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoEncoder] FFmpeg 시작 실패: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    /// <summary>
    /// FFmpeg 출력 모니터링
    /// </summary>
    private void MonitorFFmpegOutput()
    {
        try
        {
            if (_ffmpegProcess == null) return;

            using var reader = _ffmpegProcess.StandardError;
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                Debug.WriteLine($"[FFmpeg] {line}");

                // 진행률 파싱 (frame=xxx)
                if (line.Contains("frame="))
                {
                    var frameMatch = System.Text.RegularExpressions.Regex.Match(line, @"frame=\s*(\d+)");
                    if (frameMatch.Success && int.TryParse(frameMatch.Groups[1].Value, out int frame))
                    {
                        ProgressUpdated?.Invoke(this, new EncodingProgressEventArgs { FrameNumber = frame });
                    }
                }
            }
        }
        catch { }
    }

    private long _writtenFrameCount;

    /// <summary>
    /// 프레임 쓰기
    /// </summary>
    public void WriteFrame(byte[] frameData)
    {
        if (!_isEncoding || _pipeWriter == null)
        {
            Debug.WriteLine($"[VideoEncoder] WriteFrame 스킵 - isEncoding: {_isEncoding}, pipeWriter: {_pipeWriter != null}");
            return;
        }

        try
        {
            lock (_writeLock)
            {
                _pipeWriter.Write(frameData);
                _writtenFrameCount++;
                if (_writtenFrameCount % 30 == 0) // 매 30프레임마다 로그
                {
                    Debug.WriteLine($"[VideoEncoder] 프레임 {_writtenFrameCount}개 전송 완료 ({frameData.Length} bytes)");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"[VideoEncoder] 프레임 쓰기 실패: {ex.Message}");
        }
    }

    /// <summary>
    /// 인코딩 중지 및 완료
    /// </summary>
    public async Task StopEncodingAsync()
    {
        if (!_isEncoding) return;

        _isEncoding = false;

        try
        {
            // 파이프 닫기
            _pipeWriter?.Flush();
            _pipeWriter?.Close();
            _pipeWriter = null;

            // FFmpeg 프로세스 종료 대기
            if (_ffmpegProcess != null)
            {
                await _ffmpegProcess.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));

                var exitCode = _ffmpegProcess.ExitCode;
                _ffmpegProcess.Dispose();
                _ffmpegProcess = null;

                EncodingCompleted?.Invoke(this, new EncodingCompletedEventArgs
                {
                    Success = exitCode == 0,
                    OutputPath = _outputPath,
                    ErrorMessage = exitCode != 0 ? $"FFmpeg 종료 코드: {exitCode}" : null
                });
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"인코딩 중지 실패: {ex.Message}");
            EncodingCompleted?.Invoke(this, new EncodingCompletedEventArgs
            {
                Success = false,
                OutputPath = _outputPath,
                ErrorMessage = ex.Message
            });
        }
    }

    /// <summary>
    /// 오디오와 비디오 합성
    /// </summary>
    public async Task<bool> MuxAudioVideoAsync(string videoPath, string audioPath, string outputPath)
    {
        if (!IsFFmpegAvailable || !File.Exists(videoPath) || !File.Exists(audioPath))
            return false;

        try
        {
            var arguments = $"-y -i \"{videoPath}\" -i \"{audioPath}\" " +
                           $"-c:v copy -c:a aac -b:a 192k " +
                           $"-map 0:v:0 -map 1:a:0 " +
                           $"-shortest " +
                           $"\"{outputPath}\"";

            var startInfo = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                Arguments = arguments,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true
            };

            using var process = Process.Start(startInfo);
            if (process == null) return false;

            // 30초 타임아웃으로 합성 완료 대기
            await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
            return process.ExitCode == 0;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"오디오/비디오 합성 실패: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 코덱 결정
    /// </summary>
    private static string GetCodec(VideoFormat format, bool useHardwareEncoding)
    {
        if (useHardwareEncoding)
        {
            // NVIDIA NVENC 우선 사용
            return format switch
            {
                VideoFormat.MP4_H264 => "h264_nvenc",
                VideoFormat.MKV_H264 => "h264_nvenc",
                VideoFormat.WebM_VP9 => "libvpx-vp9", // VP9는 NVENC 미지원
                _ => "h264_nvenc"
            };
        }

        return format.GetFFmpegCodec();
    }

    /// <summary>
    /// 임시 오디오 파일 경로
    /// </summary>
    public string TempAudioPath => _tempAudioPath;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _pipeWriter?.Dispose();
        _ffmpegProcess?.Dispose();

        // 임시 파일 정리
        if (File.Exists(_tempAudioPath))
        {
            try { File.Delete(_tempAudioPath); } catch { }
        }
    }
}

/// <summary>
/// 인코딩 진행 이벤트 인자
/// </summary>
public class EncodingProgressEventArgs : EventArgs
{
    public int FrameNumber { get; init; }
}

/// <summary>
/// 인코딩 완료 이벤트 인자
/// </summary>
public class EncodingCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public string OutputPath { get; init; } = string.Empty;
    public string? ErrorMessage { get; init; }
}
