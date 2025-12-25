using System;
using System.Diagnostics;
using System.IO;
using System.Threading.Tasks;

namespace AudioRecorder.Services;

public class Mp3ConversionService
{
    private string? _ffmpegPath;

    public event EventHandler<Mp3ConversionProgressEventArgs>? ProgressChanged;
    public event EventHandler<Mp3ConversionCompletedEventArgs>? ConversionCompleted;

    public bool IsFFmpegAvailable => FindFFmpeg() != null;

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

    public async Task<bool> ConvertToMp3Async(string wavPath, string? outputPath = null, int bitrate = 192)
    {
        var ffmpeg = FindFFmpeg();
        if (ffmpeg == null)
        {
            ConversionCompleted?.Invoke(this, new Mp3ConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = "FFmpeg를 찾을 수 없습니다. FFmpeg를 설치하거나 앱 폴더에 ffmpeg.exe를 복사하세요."
            });
            return false;
        }

        if (!File.Exists(wavPath))
        {
            ConversionCompleted?.Invoke(this, new Mp3ConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = $"원본 파일을 찾을 수 없습니다: {wavPath}"
            });
            return false;
        }

        var mp3Path = outputPath ?? Path.ChangeExtension(wavPath, ".mp3");

        try
        {
            ProgressChanged?.Invoke(this, new Mp3ConversionProgressEventArgs
            {
                Status = "변환 시작...",
                Progress = 0
            });

            var startInfo = new ProcessStartInfo
            {
                FileName = ffmpeg,
                Arguments = $"-i \"{wavPath}\" -codec:a libmp3lame -b:a {bitrate}k -y \"{mp3Path}\"",
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = startInfo };
            process.Start();

            // FFmpeg 출력 읽기 (진행률 파싱)
            var errorOutput = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode == 0 && File.Exists(mp3Path))
            {
                var mp3Size = new FileInfo(mp3Path).Length;
                var wavSize = new FileInfo(wavPath).Length;
                var compressionRatio = (1 - (double)mp3Size / wavSize) * 100;

                ConversionCompleted?.Invoke(this, new Mp3ConversionCompletedEventArgs
                {
                    Success = true,
                    OutputPath = mp3Path,
                    OriginalSize = wavSize,
                    ConvertedSize = mp3Size,
                    CompressionRatio = compressionRatio
                });
                return true;
            }
            else
            {
                ConversionCompleted?.Invoke(this, new Mp3ConversionCompletedEventArgs
                {
                    Success = false,
                    ErrorMessage = $"변환 실패: {errorOutput}"
                });
                return false;
            }
        }
        catch (Exception ex)
        {
            ConversionCompleted?.Invoke(this, new Mp3ConversionCompletedEventArgs
            {
                Success = false,
                ErrorMessage = $"변환 중 오류: {ex.Message}"
            });
            return false;
        }
    }
}

public class Mp3ConversionProgressEventArgs : EventArgs
{
    public string Status { get; init; } = "";
    public int Progress { get; init; }
}

public class Mp3ConversionCompletedEventArgs : EventArgs
{
    public bool Success { get; init; }
    public string? OutputPath { get; init; }
    public string? ErrorMessage { get; init; }
    public long OriginalSize { get; init; }
    public long ConvertedSize { get; init; }
    public double CompressionRatio { get; init; }
}
