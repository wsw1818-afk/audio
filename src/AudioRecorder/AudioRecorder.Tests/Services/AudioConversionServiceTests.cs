using System.IO;
using AudioRecorder.Services;

namespace AudioRecorder.Tests.Services;

public class AudioConversionServiceTests
{
    [Theory]
    [InlineData(AudioFormat.WAV, ".wav")]
    [InlineData(AudioFormat.FLAC, ".flac")]
    [InlineData(AudioFormat.MP3_320, ".mp3")]
    [InlineData(AudioFormat.MP3_192, ".mp3")]
    [InlineData(AudioFormat.MP3_128, ".mp3")]
    [InlineData(AudioFormat.AAC_256, ".m4a")]
    [InlineData(AudioFormat.OGG_320, ".ogg")]
    public void GetExtension_ReturnsCorrectExtension(AudioFormat format, string expected)
    {
        // Act
        var extension = AudioConversionService.GetExtension(format);

        // Assert
        Assert.Equal(expected, extension);
    }

    [Theory]
    [InlineData(AudioFormat.WAV, "WAV (무손실 원본)")]
    [InlineData(AudioFormat.FLAC, "FLAC (무손실 압축)")]
    [InlineData(AudioFormat.MP3_320, "MP3 320kbps (음악용)")]
    [InlineData(AudioFormat.MP3_192, "MP3 192kbps (표준)")]
    [InlineData(AudioFormat.MP3_128, "MP3 128kbps (음성용)")]
    [InlineData(AudioFormat.AAC_256, "AAC/M4A 256kbps")]
    [InlineData(AudioFormat.OGG_320, "OGG Vorbis 320kbps")]
    public void GetDisplayName_ReturnsCorrectName(AudioFormat format, string expected)
    {
        // Act
        var displayName = AudioConversionService.GetDisplayName(format);

        // Assert
        Assert.Equal(expected, displayName);
    }

    [Fact]
    public void FindFFmpeg_ReturnsNullOrValidPath()
    {
        // Arrange
        var service = new AudioConversionService();

        // Act
        var ffmpegPath = service.FindFFmpeg();

        // Assert
        // FFmpeg가 설치되어 있으면 경로 반환, 없으면 null
        if (ffmpegPath != null)
        {
            Assert.True(File.Exists(ffmpegPath));
            Assert.EndsWith("ffmpeg.exe", ffmpegPath, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void IsFFmpegAvailable_ReturnsBoolean()
    {
        // Arrange
        var service = new AudioConversionService();

        // Act
        var isAvailable = service.IsFFmpegAvailable;

        // Assert
        // 결과는 FFmpeg 설치 여부에 따라 다름
        Assert.IsType<bool>(isAvailable);
    }

    [Fact]
    public async Task ConvertAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var service = new AudioConversionService();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".wav");
        var completed = false;
        var success = false;
        string? errorMessage = null;

        service.ConversionCompleted += (s, e) =>
        {
            completed = true;
            success = e.Success;
            errorMessage = e.ErrorMessage;
        };

        // Act
        var result = await service.ConvertAsync(nonExistentPath, AudioFormat.MP3_192);

        // Assert
        Assert.False(result);
        Assert.True(completed);
        Assert.False(success);
        Assert.NotNull(errorMessage);
        Assert.Contains("찾을 수 없습니다", errorMessage);
    }

    [Fact]
    public async Task RemoveNoiseAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var service = new AudioConversionService();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".wav");

        // Act
        var result = await service.RemoveNoiseAsync(nonExistentPath, null, NoiseReductionLevel.Medium);

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExtractSegmentAsync_NonExistentFile_ReturnsFalse()
    {
        // Arrange
        var service = new AudioConversionService();
        var nonExistentPath = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString() + ".wav");

        // Act
        var result = await service.ExtractSegmentAsync(
            nonExistentPath,
            TimeSpan.FromSeconds(0),
            TimeSpan.FromSeconds(10));

        // Assert
        Assert.False(result);
    }

    [Fact]
    public async Task ExtractSegmentAsync_InvalidTimeRange_ReturnsFalse()
    {
        // Arrange
        var service = new AudioConversionService();
        var tempFile = Path.Combine(Path.GetTempPath(), "test_extract.wav");

        try
        {
            // 빈 파일 생성 (테스트용)
            File.WriteAllBytes(tempFile, new byte[100]);

            // Act - 종료 시간이 시작 시간보다 앞인 경우
            var result = await service.ExtractSegmentAsync(
                tempFile,
                TimeSpan.FromSeconds(10),
                TimeSpan.FromSeconds(5)); // end < start

            // Assert
            Assert.False(result);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(NoiseReductionLevel.Light)]
    [InlineData(NoiseReductionLevel.Medium)]
    [InlineData(NoiseReductionLevel.Strong)]
    public void NoiseReductionLevel_HasAllValues(NoiseReductionLevel level)
    {
        // Assert
        Assert.True(Enum.IsDefined(typeof(NoiseReductionLevel), level));
    }

    [Theory]
    [InlineData(AudioFormat.WAV)]
    [InlineData(AudioFormat.FLAC)]
    [InlineData(AudioFormat.MP3_320)]
    [InlineData(AudioFormat.MP3_192)]
    [InlineData(AudioFormat.MP3_128)]
    [InlineData(AudioFormat.AAC_256)]
    [InlineData(AudioFormat.OGG_320)]
    public void AudioFormat_HasAllValues(AudioFormat format)
    {
        // Assert
        Assert.True(Enum.IsDefined(typeof(AudioFormat), format));
    }

    [Fact]
    public void AudioConversionProgressEventArgs_HasCorrectProperties()
    {
        // Arrange & Act
        var args = new AudioConversionProgressEventArgs
        {
            Status = "변환 중...",
            Progress = 50
        };

        // Assert
        Assert.Equal("변환 중...", args.Status);
        Assert.Equal(50, args.Progress);
    }

    [Fact]
    public void AudioConversionCompletedEventArgs_SuccessCase()
    {
        // Arrange & Act
        var args = new AudioConversionCompletedEventArgs
        {
            Success = true,
            OutputPath = @"C:\output.mp3",
            Format = AudioFormat.MP3_192,
            OriginalSize = 10000000,
            ConvertedSize = 1000000,
            CompressionRatio = 90.0
        };

        // Assert
        Assert.True(args.Success);
        Assert.Equal(@"C:\output.mp3", args.OutputPath);
        Assert.Equal(AudioFormat.MP3_192, args.Format);
        Assert.Equal(10000000, args.OriginalSize);
        Assert.Equal(1000000, args.ConvertedSize);
        Assert.Equal(90.0, args.CompressionRatio);
        Assert.Null(args.ErrorMessage);
    }

    [Fact]
    public void AudioConversionCompletedEventArgs_FailureCase()
    {
        // Arrange & Act
        var args = new AudioConversionCompletedEventArgs
        {
            Success = false,
            ErrorMessage = "변환 실패"
        };

        // Assert
        Assert.False(args.Success);
        Assert.Equal("변환 실패", args.ErrorMessage);
        Assert.Null(args.OutputPath);
    }
}
