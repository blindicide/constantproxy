using ConstantProxy.Core.Presentation;

namespace ConstantProxy.Tests.Core;

public class DurationFormatterTests
{
    [Theory]
    [InlineData(0, "00:00:00")]
    [InlineData(59, "00:00:59")]
    [InlineData(3661, "01:01:01")]
    [InlineData(8077, "02:14:37")]
    [InlineData(90061, "1d 01:01:01")]
    [InlineData(-5, "00:00:00")]
    public void FormatsDurations(int seconds, string expected) =>
        Assert.Equal(expected, DurationFormatter.Format(TimeSpan.FromSeconds(seconds)));
}
