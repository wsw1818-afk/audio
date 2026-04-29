using AudioRecorder.Models;

namespace AudioRecorder.Tests.Models;

public class RecordingOptionsTests
{
    [Fact]
    public void GenerateFileName_DefaultTemplate_ReturnsDateTimeFormat()
    {
        // Arrange
        var options = new RecordingOptions
        {
            FileNameTemplate = "Recording_{datetime}",
            Format = RecordingFormat.WAV
        };

        // Act
        var fileName = options.GenerateFileName();

        // Assert
        Assert.StartsWith("Recording_", fileName);
        Assert.EndsWith(".wav", fileName);
        Assert.Matches(@"Recording_\d{8}_\d{6}\.wav", fileName);
    }

    [Fact]
    public void GenerateFileName_DateTemplate_ReturnsDateFormat()
    {
        // Arrange
        var options = new RecordingOptions
        {
            FileNameTemplate = "Meeting_{date}",
            Format = RecordingFormat.MP3_320
        };

        // Act
        var fileName = options.GenerateFileName();

        // Assert
        Assert.Matches(@"Meeting_\d{4}-\d{2}-\d{2}\.mp3", fileName);
    }

    [Fact]
    public void GenerateFileName_TimeTemplate_ReturnsTimeFormat()
    {
        // Arrange
        var options = new RecordingOptions
        {
            FileNameTemplate = "Audio_{time}",
            Format = RecordingFormat.FLAC
        };

        // Act
        var fileName = options.GenerateFileName();

        // Assert
        Assert.Matches(@"Audio_\d{6}\.flac", fileName);
    }

    [Fact]
    public void GenerateFileName_WithTitle_IncludesTitle()
    {
        // Arrange
        var options = new RecordingOptions
        {
            FileNameTemplate = "회의록_{date}_{title}",
            Title = "주간회의",
            Format = RecordingFormat.WAV
        };

        // Act
        var fileName = options.GenerateFileName();

        // Assert
        Assert.Contains("주간회의", fileName);
        Assert.Matches(@"회의록_\d{4}-\d{2}-\d{2}_주간회의\.wav", fileName);
    }

    [Fact]
    public void GenerateFileName_EmptyTitle_RemovesDoubleUnderscores()
    {
        // Arrange
        var options = new RecordingOptions
        {
            FileNameTemplate = "Recording_{date}_{title}",
            Title = "",
            Format = RecordingFormat.WAV
        };

        // Act
        var fileName = options.GenerateFileName();

        // Assert
        Assert.DoesNotContain("__", fileName);
    }

    [Fact]
    public void GenerateFileName_InvalidCharactersInTitle_SanitizesTitle()
    {
        // Arrange
        var options = new RecordingOptions
        {
            FileNameTemplate = "Meeting_{title}",
            Title = "회의<2024>:내용",
            Format = RecordingFormat.WAV
        };

        // Act
        var fileName = options.GenerateFileName();

        // Assert
        Assert.DoesNotContain("<", fileName);
        Assert.DoesNotContain(">", fileName);
        Assert.DoesNotContain(":", fileName);
    }

    [Fact]
    public void GenerateTempWavFileName_ReturnsValidTempName()
    {
        // Arrange
        var options = new RecordingOptions();

        // Act
        var tempFileName = options.GenerateTempWavFileName();

        // Assert
        Assert.StartsWith("Recording_", tempFileName);
        Assert.EndsWith("_temp.wav", tempFileName);
    }

    [Fact]
    public void GetFullPath_CombinesDirectoryAndFileName()
    {
        // Arrange
        var options = new RecordingOptions
        {
            OutputDirectory = @"C:\Recordings",
            FileNameTemplate = "Test_{datetime}",
            Format = RecordingFormat.WAV
        };

        // Act
        var fullPath = options.GetFullPath();

        // Assert
        Assert.StartsWith(@"C:\Recordings\Test_", fullPath);
        Assert.EndsWith(".wav", fullPath);
    }

    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var options = new RecordingOptions();

        // Assert
        Assert.True(options.RecordMicrophone);
        Assert.True(options.RecordSystemAudio);
        Assert.Equal(1.0f, options.MicrophoneVolume);
        Assert.Equal(1.0f, options.SystemVolume);
        Assert.Equal(48000, options.SampleRate);
        Assert.Equal(2, options.Channels);
        Assert.Equal(16, options.BitsPerSample);
        Assert.Equal(RecordingFormat.WAV, options.Format);
        Assert.Equal("Recording_{datetime}", options.FileNameTemplate);
    }

    [Theory]
    [InlineData(RecordingFormat.WAV, ".wav")]
    [InlineData(RecordingFormat.MP3_320, ".mp3")]
    [InlineData(RecordingFormat.MP3_128, ".mp3")]
    [InlineData(RecordingFormat.FLAC, ".flac")]
    public void GenerateFileName_DifferentFormats_ReturnsCorrectExtension(RecordingFormat format, string expectedExtension)
    {
        // Arrange
        var options = new RecordingOptions
        {
            FileNameTemplate = "Test",
            Format = format
        };

        // Act
        var fileName = options.GenerateFileName();

        // Assert
        Assert.EndsWith(expectedExtension, fileName);
    }
}
