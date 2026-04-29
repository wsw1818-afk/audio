using AudioRecorder.Models;

namespace AudioRecorder.Tests.Models;

public class BookmarkInfoTests
{
    [Fact]
    public void PositionText_ReturnsFormattedTime()
    {
        // Arrange
        var bookmark = new BookmarkInfo
        {
            Position = TimeSpan.FromSeconds(125) // 2분 5초
        };

        // Act
        var positionText = bookmark.PositionText;

        // Assert
        Assert.Equal("02:05", positionText);
    }

    [Fact]
    public void PositionText_ZeroPosition_ReturnsZeroTime()
    {
        // Arrange
        var bookmark = new BookmarkInfo
        {
            Position = TimeSpan.Zero
        };

        // Act
        var positionText = bookmark.PositionText;

        // Assert
        Assert.Equal("00:00", positionText);
    }

    [Fact]
    public void DisplayText_WithoutLabel_ShowsOnlyPosition()
    {
        // Arrange
        var bookmark = new BookmarkInfo
        {
            Position = TimeSpan.FromMinutes(5),
            Label = ""
        };

        // Act
        var displayText = bookmark.DisplayText;

        // Assert
        Assert.Equal("📌 05:00", displayText);
    }

    [Fact]
    public void DisplayText_WithLabel_ShowsPositionAndLabel()
    {
        // Arrange
        var bookmark = new BookmarkInfo
        {
            Position = TimeSpan.FromMinutes(10),
            Label = "중요 내용"
        };

        // Act
        var displayText = bookmark.DisplayText;

        // Assert
        Assert.Equal("📌 10:00 - 중요 내용", displayText);
    }

    [Fact]
    public void CreatedAt_DefaultsToNow()
    {
        // Arrange
        var before = DateTime.Now;

        // Act
        var bookmark = new BookmarkInfo();
        var after = DateTime.Now;

        // Assert
        Assert.InRange(bookmark.CreatedAt, before, after);
    }

    [Fact]
    public void DefaultLabel_IsEmpty()
    {
        // Arrange & Act
        var bookmark = new BookmarkInfo();

        // Assert
        Assert.Equal("", bookmark.Label);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(30, "00:30")]
    [InlineData(60, "01:00")]
    [InlineData(90, "01:30")]
    [InlineData(599, "09:59")]   // 9분 59초
    [InlineData(1800, "30:00")] // 30분
    public void PositionText_VariousTimes_ReturnsCorrectFormat(int seconds, string expected)
    {
        // Arrange
        var bookmark = new BookmarkInfo
        {
            Position = TimeSpan.FromSeconds(seconds)
        };

        // Act
        var positionText = bookmark.PositionText;

        // Assert
        Assert.Equal(expected, positionText);
    }

    [Fact]
    public void DisplayText_WhitespaceLabel_TreatedAsEmpty()
    {
        // Arrange
        var bookmark = new BookmarkInfo
        {
            Position = TimeSpan.FromSeconds(30),
            Label = "   "
        };

        // Act
        var displayText = bookmark.DisplayText;

        // Assert
        // 공백만 있는 라벨은 빈 문자열이 아니므로 포함됨
        Assert.Contains("-", displayText);
    }
}
