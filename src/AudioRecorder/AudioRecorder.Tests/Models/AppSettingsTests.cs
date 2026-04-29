using AudioRecorder.Models;

namespace AudioRecorder.Tests.Models;

public class AppSettingsTests
{
    [Fact]
    public void DefaultValues_AreCorrect()
    {
        // Arrange & Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.MyMusic), settings.OutputDirectory);
        Assert.Null(settings.LastMicrophoneId);
        Assert.Null(settings.LastSystemDeviceId);
        Assert.Equal(1.0f, settings.MicrophoneVolume);
        Assert.Equal(1.0f, settings.SystemVolume);
        Assert.True(settings.RecordMicrophone);
        Assert.True(settings.RecordSystem);
        Assert.Equal(20, settings.MaxRecentFiles);
        Assert.Equal(RecordingFormat.WAV, settings.RecordingFormat);
        Assert.Equal(CloseAction.MinimizeToTray, settings.CloseAction);
    }

    [Fact]
    public void VideoCompressionSettings_DefaultValues()
    {
        // Arrange & Act
        var settings = new AppSettings();

        // Assert
        Assert.False(settings.AutoCompressVideo);
        Assert.Equal(VideoCompressionQuality.None, settings.VideoCompressionQuality);
    }

    [Fact]
    public void ScreenRecordingSettings_DefaultValues()
    {
        // Arrange & Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal("고화질", settings.VideoQuality);
        Assert.True(settings.ShowMouseCursor);
        Assert.False(settings.HighlightMouseClicks);
        Assert.Equal(3, settings.CountdownSeconds);
        Assert.True(settings.ShowRecordingBorder);
        Assert.Equal("원본", settings.Resolution);
        Assert.Equal("192 kbps", settings.AudioBitrate);
    }

    [Fact]
    public void FileNameTemplateSettings_DefaultValues()
    {
        // Arrange & Act
        var settings = new AppSettings();

        // Assert
        Assert.Equal("Recording_{datetime}", settings.FileNameTemplate);
        Assert.Equal("", settings.DefaultTitle);
    }

    [Theory]
    [InlineData(VideoCompressionQuality.None)]
    [InlineData(VideoCompressionQuality.High)]
    [InlineData(VideoCompressionQuality.Normal)]
    public void VideoCompressionQuality_CanBeSet(VideoCompressionQuality quality)
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        settings.VideoCompressionQuality = quality;

        // Assert
        Assert.Equal(quality, settings.VideoCompressionQuality);
    }

    [Theory]
    [InlineData(RecordingFormat.WAV)]
    [InlineData(RecordingFormat.MP3_320)]
    [InlineData(RecordingFormat.MP3_128)]
    [InlineData(RecordingFormat.FLAC)]
    public void RecordingFormat_CanBeSet(RecordingFormat format)
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        settings.RecordingFormat = format;

        // Assert
        Assert.Equal(format, settings.RecordingFormat);
    }

    [Fact]
    public void MicrophoneVolume_CanBeSet()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        settings.MicrophoneVolume = 0.5f;

        // Assert
        Assert.Equal(0.5f, settings.MicrophoneVolume);
    }

    [Fact]
    public void SystemVolume_CanBeSet()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        settings.SystemVolume = 0.75f;

        // Assert
        Assert.Equal(0.75f, settings.SystemVolume);
    }

    [Fact]
    public void CountdownSeconds_CanBeSet()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        settings.CountdownSeconds = 5;

        // Assert
        Assert.Equal(5, settings.CountdownSeconds);
    }

    [Fact]
    public void FileNameTemplate_CanBeSet()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        settings.FileNameTemplate = "회의록_{date}_{title}";

        // Assert
        Assert.Equal("회의록_{date}_{title}", settings.FileNameTemplate);
    }

    [Fact]
    public void CloseAction_CanBeSet()
    {
        // Arrange
        var settings = new AppSettings();

        // Act
        settings.CloseAction = CloseAction.ExitImmediately;

        // Assert
        Assert.Equal(CloseAction.ExitImmediately, settings.CloseAction);
    }
}
